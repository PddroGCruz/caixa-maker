using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Cam.Raster;

/// <summary>Contorno fechado extraído com relação hierárquica de 2 níveis (externo vs. furo interno).</summary>
internal sealed record TracedContour(
    IReadOnlyList<Point2D> Points,
    bool IsHole,
    int ParentIndex,
    double AreaPx2);

/// <summary>
/// Implementa rastreamento topológico de contornos binários pelo algoritmo clássico de Suzuki-Abe (1985)
/// com hierarquia de dois níveis (<c>RETR_CCOMP</c>) e simplificação poligonal fechada Ramer-Douglas-Peucker.
/// </summary>
internal static class BinaryContourTracer
{
    // Vizinhança 8-conectada em ordem horária a partir de Oeste (esquerda):
    // 0:(-1,0), 1:(-1,-1), 2:(0,-1), 3:(1,-1), 4:(1,0), 5:(1,1), 6:(0,1), 7:(-1,1)
    private static readonly int[] Dx = [-1, -1, 0, 1, 1, 1, 0, -1];
    private static readonly int[] Dy = [0, -1, -1, -1, 0, 1, 1, 1];

    /// <summary>
    /// Extrai todos os contornos externos e seus furos diretos (hierarquia 2 níveis) de uma máscara binária.
    /// </summary>
    public static List<TracedContour> FindContoursTwoLevel(bool[,] foregroundMask)
    {
        int height = foregroundMask.GetLength(0);
        int width = foregroundMask.GetLength(1);
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        // Borda zero de 1 pixel para eliminar verificações de limites no rastreamento de vizinhança
        int paddedW = width + 2;
        int paddedH = height + 2;
        int[,] grid = new int[paddedH, paddedW];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (foregroundMask[y, x])
                {
                    grid[y + 1, x + 1] = 1;
                }
            }
        }

        int nbd = 1;
        var rawContours = new List<RawContourRecord>();

        for (int r = 1; r <= height; r++)
        {
            int lnbd = 1;
            for (int c = 1; c <= width; c++)
            {
                int val = grid[r, c];
                if (val == 0)
                {
                    continue;
                }

                bool isOuter = val == 1 && grid[r, c - 1] == 0;
                bool isHole = val >= 1 && grid[r, c + 1] == 0;

                if (isOuter)
                {
                    nbd++;
                    int parentNbd = DetermineParent(rawContours, lnbd, newContourIsHole: false);
                    List<Point2D> pts = TraceBorder(grid, r, c, r, c - 1, nbd);
                    rawContours.Add(new RawContourRecord(nbd, IsHole: false, parentNbd, pts));
                }
                else if (isHole)
                {
                    nbd++;
                    if (val > 1)
                    {
                        lnbd = val;
                    }

                    int parentNbd = DetermineParent(rawContours, lnbd, newContourIsHole: true);
                    List<Point2D> pts = TraceBorder(grid, r, c, r, c + 1, nbd);
                    rawContours.Add(new RawContourRecord(nbd, IsHole: true, parentNbd, pts));
                }

                if (grid[r, c] != 1)
                {
                    lnbd = Math.Abs(grid[r, c]);
                }
            }
        }

        // Constrói saída mapeando parentNbd -> índice de 2 níveis (furos apontam para seu pai externo)
        var nbdToOutputIndex = new Dictionary<int, int>();
        var result = new List<TracedContour>(rawContours.Count);

        for (int i = 0; i < rawContours.Count; i++)
        {
            RawContourRecord rec = rawContours[i];
            int parentIdx = -1;
            bool effectiveHole = false;

            if (rec.ParentNbd > 1 && nbdToOutputIndex.TryGetValue(rec.ParentNbd, out int directParentIdx))
            {
                if (rec.IsHole)
                {
                    // Furo direto de um componente externo (ou furo de nível mais profundo colapsado para 2 níveis)
                    effectiveHole = true;
                    parentIdx = result[directParentIdx].IsHole
                        ? result[directParentIdx].ParentIndex
                        : directParentIdx;
                }
                else
                {
                    // Ilha interna dentro de um furo -> tratada como novo contorno externo de nível 0
                    effectiveHole = false;
                    parentIdx = -1;
                }
            }
            else if (rec.IsHole)
            {
                effectiveHole = true;
                parentIdx = -1;
            }

            List<Point2D> compressed = CompressCollinear(rec.Points);
            double area = Math.Abs(SignedArea(compressed));
            int outIndex = result.Count;
            nbdToOutputIndex[rec.Nbd] = outIndex;
            result.Add(new TracedContour(compressed, effectiveHole, parentIdx, area));
        }

        return result;
    }

    /// <summary>
    /// Simplifica um polígono fechado usando Ramer-Douglas-Peucker mantendo orientação fechada consistente.
    /// </summary>
    public static List<Point2D> SimplifyClosedPolygon(IReadOnlyList<Point2D> polygon, double epsilon)
    {
        if (polygon.Count <= 3 || epsilon <= 0.0)
        {
            return polygon.ToList();
        }

        // Encontra o par de pontos mais distante para ancorar a abertura do ciclo fechado
        int idxA = 0;
        double maxDist2 = -1.0;
        for (int i = 1; i < polygon.Count; i++)
        {
            double d2 = DistSq(polygon[0], polygon[i]);
            if (d2 > maxDist2)
            {
                maxDist2 = d2;
                idxA = i;
            }
        }

        int idxB = 0;
        maxDist2 = -1.0;
        for (int i = 0; i < polygon.Count; i++)
        {
            double d2 = DistSq(polygon[idxA], polygon[i]);
            if (d2 > maxDist2)
            {
                maxDist2 = d2;
                idxB = i;
            }
        }

        if (idxA == idxB)
        {
            return polygon.ToList();
        }

        var chain1 = new List<Point2D>();
        for (int i = idxA; ; i = (i + 1) % polygon.Count)
        {
            chain1.Add(polygon[i]);
            if (i == idxB)
            {
                break;
            }
        }

        var chain2 = new List<Point2D>();
        for (int i = idxB; ; i = (i + 1) % polygon.Count)
        {
            chain2.Add(polygon[i]);
            if (i == idxA)
            {
                break;
            }
        }

        List<Point2D> simp1 = RdpOpen(chain1, epsilon);
        List<Point2D> simp2 = RdpOpen(chain2, epsilon);

        var merged = new List<Point2D>(simp1.Count + simp2.Count);
        merged.AddRange(simp1.Take(simp1.Count - 1));
        merged.AddRange(simp2.Take(simp2.Count - 1));

        return PruneEndJointCollinear(merged, epsilon);
    }

    public static double SignedArea(IReadOnlyList<Point2D> pts)
    {
        if (pts.Count < 3)
        {
            return 0.0;
        }

        double sum = 0.0;
        for (int i = 0; i < pts.Count; i++)
        {
            Point2D a = pts[i];
            Point2D b = pts[(i + 1) % pts.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum * 0.5;
    }

    private static int DetermineParent(List<RawContourRecord> records, int lnbd, bool newContourIsHole)
    {
        if (lnbd <= 1)
        {
            return 1;
        }

        RawContourRecord? neighbor = records.FirstOrDefault(r => r.Nbd == lnbd);
        if (neighbor is null)
        {
            return 1;
        }

        // Regra exata da Tabela 1 de Suzuki & Abe (1985)
        return (neighbor.IsHole == newContourIsHole)
            ? neighbor.ParentNbd
            : neighbor.Nbd;
    }

    private static List<Point2D> TraceBorder(int[,] grid, int startR, int startC, int emptyR, int emptyC, int nbd)
    {
        var points = new List<Point2D>();
        int startDir = DirectionBetween(startR, startC, emptyR, emptyC);

        // Passo 3.1 (Suzuki-Abe): busca horária a partir de emptyDir pelo primeiro vizinho não-zero (p1)
        int p1R = -1;
        int p1C = -1;
        for (int step = 1; step < 8; step++)
        {
            int dir = (startDir + step) % 8;
            int nr = startR + Dy[dir];
            int nc = startC + Dx[dir];
            if (grid[nr, nc] != 0)
            {
                p1R = nr;
                p1C = nc;
                break;
            }
        }

        if (p1R < 0)
        {
            // Pixel isolado
            grid[startR, startC] = -nbd;
            points.Add(new Point2D(startC - 1.0, startR - 1.0));
            return points;
        }

        int curR = startR;
        int curC = startC;
        int prevR = p1R;
        int prevC = p1C;
        int firstNextR = -1;
        int firstNextC = -1;

        int maxIter = grid.Length * 4;
        for (int iter = 0; iter < maxIter; iter++)
        {
            int fromPrevDir = DirectionBetween(curR, curC, prevR, prevC);
            int nextR = -1;
            int nextC = -1;
            bool crossedRightZero = false;

            // Passo 3.3: busca anti-horária (índice decrescente) pelo próximo pixel da borda
            for (int step = 1; step <= 8; step++)
            {
                int dir = (fromPrevDir - step + 8) % 8;
                int nr = curR + Dy[dir];
                int nc = curC + Dx[dir];
                if (dir == 4 && grid[nr, nc] == 0)
                {
                    crossedRightZero = true;
                }

                if (grid[nr, nc] != 0)
                {
                    nextR = nr;
                    nextC = nc;
                    break;
                }
            }

            if (crossedRightZero)
            {
                grid[curR, curC] = -nbd;
            }
            else if (grid[curR, curC] == 1)
            {
                grid[curR, curC] = nbd;
            }

            if (nextR < 0)
            {
                break;
            }

            if (iter == 0)
            {
                firstNextR = nextR;
                firstNextC = nextC;
            }
            else if (curR == startR && curC == startC && nextR == firstNextR && nextC == firstNextC)
            {
                break;
            }

            points.Add(new Point2D(curC - 1.0, curR - 1.0));

            prevR = curR;
            prevC = curC;
            curR = nextR;
            curC = nextC;
        }

        return points;
    }

    private static int DirectionBetween(int fromR, int fromC, int toR, int toC)
    {
        int dc = Math.Sign(toC - fromC);
        int dr = Math.Sign(toR - fromR);
        for (int i = 0; i < 8; i++)
        {
            if (Dx[i] == dc && Dy[i] == dr)
            {
                return i;
            }
        }

        return 0;
    }

    private static List<Point2D> CompressCollinear(List<Point2D> pts)
    {
        if (pts.Count <= 2)
        {
            return pts;
        }

        var result = new List<Point2D>(pts.Count);
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            Point2D prev = pts[(i - 1 + n) % n];
            Point2D cur = pts[i];
            Point2D next = pts[(i + 1) % n];
            double cross = ((cur.X - prev.X) * (next.Y - cur.Y)) - ((cur.Y - prev.Y) * (next.X - cur.X));
            if (Math.Abs(cross) > 1e-9)
            {
                result.Add(cur);
            }
        }

        return result.Count >= 3 ? result : pts;
    }

    private static List<Point2D> RdpOpen(List<Point2D> pts, double epsilon)
    {
        if (pts.Count <= 2)
        {
            return pts;
        }

        double maxDist = 0.0;
        int maxIdx = 0;
        Point2D start = pts[0];
        Point2D end = pts[^1];

        for (int i = 1; i < pts.Count - 1; i++)
        {
            double d = PointToSegmentDistance(pts[i], start, end);
            if (d > maxDist)
            {
                maxDist = d;
                maxIdx = i;
            }
        }

        if (maxDist > epsilon)
        {
            List<Point2D> left = RdpOpen(pts.GetRange(0, maxIdx + 1), epsilon);
            List<Point2D> right = RdpOpen(pts.GetRange(maxIdx, pts.Count - maxIdx), epsilon);
            var combined = new List<Point2D>(left.Count + right.Count - 1);
            combined.AddRange(left.Take(left.Count - 1));
            combined.AddRange(right);
            return combined;
        }

        return [start, end];
    }

    private static List<Point2D> PruneEndJointCollinear(List<Point2D> pts, double epsilon)
    {
        var current = new List<Point2D>(pts);
        bool changed = true;
        while (changed && current.Count > 3)
        {
            changed = false;
            for (int i = 0; i < current.Count && current.Count > 3; i++)
            {
                Point2D prev = current[(i - 1 + current.Count) % current.Count];
                Point2D cur = current[i];
                Point2D next = current[(i + 1) % current.Count];
                if (PointToSegmentDistance(cur, prev, next) <= epsilon)
                {
                    current.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }

        return current;
    }

    private static double PointToSegmentDistance(Point2D p, Point2D a, Point2D b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double len2 = (dx * dx) + (dy * dy);
        if (len2 <= 1e-18)
        {
            return Hypot(p.X - a.X, p.Y - a.Y);
        }

        double t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / len2, 0.0, 1.0);
        double projX = a.X + (t * dx);
        double projY = a.Y + (t * dy);
        return Hypot(p.X - projX, p.Y - projY);
    }

    private static double Hypot(double dx, double dy) => Math.Sqrt((dx * dx) + (dy * dy));

    private static double DistSq(Point2D a, Point2D b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private sealed record RawContourRecord(int Nbd, bool IsHole, int ParentNbd, List<Point2D> Points);
}
