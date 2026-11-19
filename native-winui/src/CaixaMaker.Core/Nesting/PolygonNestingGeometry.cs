namespace CaixaMaker.Core.Nesting;

/// <summary>
/// Operações geométricas determinísticas 2D (mm) para normalização, rotação rígida,
/// detecção de colisões <i>True-Shape</i>, cálculo de distância mínima entre contornos e encaixe em furos.
/// </summary>
public static class PolygonNestingGeometry
{
    private const double DuplicateEps = 1e-6;
    private const double CollinearEps = 1e-7;
    private const double DistanceTolerance = 1e-4;

    /// <summary>Calcula a área assinada pelo algoritmo de Shoelace em mm².</summary>
    public static double SignedArea(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 3)
        {
            return 0.0;
        }

        double sum = 0.0;
        for (int i = 0; i < points.Count; i++)
        {
            (double x1, double y1) = points[i];
            (double x2, double y2) = points[(i + 1) % points.Count];
            sum += (x1 * y2) - (x2 * y1);
        }

        return sum * 0.5;
    }

    /// <summary>Calcula a área absoluta de um polígono fechado em mm².</summary>
    public static double AbsArea(IReadOnlyList<(double X, double Y)> points)
        => Math.Abs(SignedArea(points));

    /// <summary>Remove duplicados/fechamento redundante, pontos colineares intermediários e normaliza orientação.</summary>
    public static IReadOnlyList<(double X, double Y)> NormalizeClosedPolygon(
        IEnumerable<(double X, double Y)> rawPoints,
        bool ensureCcw = true)
    {
        var pts = new List<(double X, double Y)>();
        foreach ((double x, double y) in rawPoints)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            {
                continue;
            }

            if (pts.Count == 0 || DistSq(pts[^1], (x, y)) > DuplicateEps * DuplicateEps)
            {
                pts.Add((x, y));
            }
        }

        if (pts.Count >= 2 && DistSq(pts[^1], pts[0]) <= DuplicateEps * DuplicateEps)
        {
            pts.RemoveAt(pts.Count - 1);
        }

        if (pts.Count < 3)
        {
            return pts;
        }

        // Remove vértices estritamente colineares mantendo integralmente quinas e dentes
        var simplified = new List<(double X, double Y)>(pts.Count);
        int count = pts.Count;
        for (int i = 0; i < count; i++)
        {
            (double X, double Y) prev = pts[(i - 1 + count) % count];
            (double X, double Y) curr = pts[i];
            (double X, double Y) next = pts[(i + 1) % count];

            double v1x = curr.X - prev.X;
            double v1y = curr.Y - prev.Y;
            double v2x = next.X - curr.X;
            double v2y = next.Y - curr.Y;
            double cross = (v1x * v2y) - (v1y * v2x);
            double dot = (v1x * v2x) + (v1y * v2y);
            double scale = Math.Sqrt((v1x * v1x) + (v1y * v1y)) * Math.Sqrt((v2x * v2x) + (v2y * v2y));

            if (scale > 1e-12 && Math.Abs(cross) <= CollinearEps * scale && dot > 0.0)
            {
                continue;
            }

            simplified.Add(curr);
        }

        if (simplified.Count < 3)
        {
            simplified = pts;
        }

        double signedArea = SignedArea(simplified);
        if ((ensureCcw && signedArea < 0.0) || (!ensureCcw && signedArea > 0.0))
        {
            simplified.Reverse();
        }

        return simplified;
    }

    /// <summary>Limpa vértices consecutivos duplicados em uma polilinha aberta.</summary>
    public static IReadOnlyList<(double X, double Y)> NormalizeOpenPolyline(
        IEnumerable<(double X, double Y)> rawPoints)
    {
        var pts = new List<(double X, double Y)>();
        foreach ((double x, double y) in rawPoints)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            {
                continue;
            }

            if (pts.Count == 0 || DistSq(pts[^1], (x, y)) > DuplicateEps * DuplicateEps)
            {
                pts.Add((x, y));
            }
        }

        return pts;
    }

    /// <summary>Calcula a caixa delimitadora alinhada aos eixos em mm.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) ComputeBounds(
        IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count == 0)
        {
            return (0.0, 0.0, 0.0, 0.0);
        }

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        for (int i = 0; i < points.Count; i++)
        {
            (double x, double y) = points[i];
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>Calcula seno e cosseno exatos para múltiplos de 90° e trigonométricos para outros ângulos.</summary>
    public static (double Cos, double Sin) GetCosSin(double angleDeg)
    {
        double norm = ((angleDeg % 360.0) + 360.0) % 360.0;
        if (Math.Abs(norm - 0.0) < 1e-9 || Math.Abs(norm - 360.0) < 1e-9) return (1.0, 0.0);
        if (Math.Abs(norm - 90.0) < 1e-9) return (0.0, 1.0);
        if (Math.Abs(norm - 180.0) < 1e-9) return (-1.0, 0.0);
        if (Math.Abs(norm - 270.0) < 1e-9) return (0.0, -1.0);

        double rad = norm * (Math.PI / 180.0);
        return (Math.Cos(rad), Math.Sin(rad));
    }

    /// <summary>Rotaciona rigidamente uma peça inteira e normaliza sua origem local para <c>(0, 0)</c>.</summary>
    public static (
        IReadOnlyList<(double X, double Y)> OuterContour,
        IReadOnlyList<NestingPolyline2D> InternalFeatures,
        IReadOnlyList<NestingRasterOverlay> RasterOverlays,
        double WidthMm,
        double HeightMm)
        RotateAndNormalizeToZero(NestingPart part, double rotationDeg)
    {
        ArgumentNullException.ThrowIfNull(part);
        (double cos, double sin) = GetCosSin(rotationDeg);

        var rawOuter = new (double X, double Y)[part.OuterContour.Count];
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        for (int i = 0; i < part.OuterContour.Count; i++)
        {
            (double ox, double oy) = part.OuterContour[i];
            double rx = (ox * cos) - (oy * sin);
            double ry = (ox * sin) + (oy * cos);
            rawOuter[i] = (rx, ry);
            if (rx < minX) minX = rx;
            if (ry < minY) minY = ry;
            if (rx > maxX) maxX = rx;
            if (ry > maxY) maxY = ry;
        }

        if (rawOuter.Length == 0)
        {
            minX = minY = maxX = maxY = 0.0;
        }

        for (int i = 0; i < rawOuter.Length; i++)
        {
            rawOuter[i] = (rawOuter[i].X - minX, rawOuter[i].Y - minY);
        }

        var rotatedFeatures = new NestingPolyline2D[part.InternalFeatures.Count];
        for (int f = 0; f < part.InternalFeatures.Count; f++)
        {
            NestingPolyline2D src = part.InternalFeatures[f];
            var dstPts = new (double X, double Y)[src.Points.Count];
            for (int p = 0; p < src.Points.Count; p++)
            {
                (double fx, double fy) = src.Points[p];
                double rx = (fx * cos) - (fy * sin) - minX;
                double ry = (fx * sin) + (fy * cos) - minY;
                dstPts[p] = (rx, ry);
            }

            rotatedFeatures[f] = src with { Points = dstPts };
        }

        var rotatedRasters = new NestingRasterOverlay[part.RasterOverlays.Count];
        for (int r = 0; r < part.RasterOverlays.Count; r++)
        {
            NestingRasterOverlay src = part.RasterOverlays[r];
            double cx = src.X + (src.WidthMm * 0.5);
            double cy = src.Y + (src.HeightMm * 0.5);
            double ncx = (cx * cos) - (cy * sin) - minX;
            double ncy = (cx * sin) + (cy * cos) - minY;
            double newAngle = ((src.RotationDeg + rotationDeg) % 360.0 + 360.0) % 360.0;
            rotatedRasters[r] = src with
            {
                X = ncx - (src.WidthMm * 0.5),
                Y = ncy - (src.HeightMm * 0.5),
                RotationDeg = newAngle,
            };
        }

        return (rawOuter, rotatedFeatures, rotatedRasters, Math.Max(0.0, maxX - minX), Math.Max(0.0, maxY - minY));
    }

    /// <summary>Translada uma sequência de vértices por <c>(dx, dy)</c>.</summary>
    public static (double X, double Y)[] TranslatePoints(
        IReadOnlyList<(double X, double Y)> points,
        double dx,
        double dy)
    {
        var result = new (double X, double Y)[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            result[i] = (points[i].X + dx, points[i].Y + dy);
        }

        return result;
    }

    /// <summary>Teste ponto-em-polígono por ray-casting considerando bordas como inclusivas.</summary>
    public static bool IsPointInPolygon((double X, double Y) point, IReadOnlyList<(double X, double Y)> polygon)
    {
        int n = polygon.Count;
        if (n < 3)
        {
            return false;
        }

        for (int i = 0; i < n; i++)
        {
            if (PointToSegmentDistanceSq(point, polygon[i], polygon[(i + 1) % n]) <= 1e-12)
            {
                return true;
            }
        }

        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            (double xi, double yi) = polygon[i];
            (double xj, double yj) = polygon[j];

            bool crosses = ((yi > point.Y) != (yj > point.Y)) &&
                           (point.X < (((xj - xi) * (point.Y - yi)) / (yj - yi)) + xi);
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Calcula um ponto interno representativo do polígono (preferindo centroide quando contido).</summary>
    public static (double X, double Y) GetInteriorTestPoint(IReadOnlyList<(double X, double Y)> polygon)
    {
        if (polygon.Count == 0)
        {
            return (0.0, 0.0);
        }

        double signedArea = SignedArea(polygon);
        if (Math.Abs(signedArea) > 1e-8)
        {
            double cx = 0.0;
            double cy = 0.0;
            for (int i = 0; i < polygon.Count; i++)
            {
                (double x0, double y0) = polygon[i];
                (double x1, double y1) = polygon[(i + 1) % polygon.Count];
                double cross = (x0 * y1) - (x1 * y0);
                cx += (x0 + x1) * cross;
                cy += (y0 + y1) * cross;
            }

            double factor = 1.0 / (6.0 * signedArea);
            var centroid = (cx * factor, cy * factor);
            if (IsPointInPolygon(centroid, polygon))
            {
                return centroid;
            }
        }

        (double minX, double minY, double maxX, double maxY) = ComputeBounds(polygon);
        var boxMid = ((minX + maxX) * 0.5, (minY + maxY) * 0.5);
        if (IsPointInPolygon(boxMid, polygon))
        {
            return boxMid;
        }

        // Scanline horizontal no ponto médio Y para polígonos côncavos / em L
        double midY = (minY + maxY) * 0.5;
        var xs = new List<double>();
        for (int i = 0; i < polygon.Count; i++)
        {
            (double x1, double y1) = polygon[i];
            (double x2, double y2) = polygon[(i + 1) % polygon.Count];
            if ((y1 <= midY && y2 > midY) || (y2 <= midY && y1 > midY))
            {
                double ix = x1 + (((midY - y1) / (y2 - y1)) * (x2 - x1));
                xs.Add(ix);
            }
        }

        if (xs.Count >= 2)
        {
            xs.Sort();
            return ((xs[0] + xs[1]) * 0.5, midY);
        }

        return polygon[0];
    }

    /// <summary>Verifica se dois polígonos fechados se sobrepõem ou se contêm mutuamente.</summary>
    public static bool PolygonsIntersect(
        IReadOnlyList<(double X, double Y)> polyA,
        IReadOnlyList<(double X, double Y)> polyB)
    {
        if (polyA.Count < 3 || polyB.Count < 3)
        {
            return false;
        }

        (double aMinX, double aMinY, double aMaxX, double aMaxY) = ComputeBounds(polyA);
        (double bMinX, double bMinY, double bMaxX, double bMaxY) = ComputeBounds(polyB);
        if (aMaxX < bMinX - 1e-7 || bMaxX < aMinX - 1e-7 || aMaxY < bMinY - 1e-7 || bMaxY < aMinY - 1e-7)
        {
            return false;
        }

        for (int i = 0; i < polyA.Count; i++)
        {
            (double X, double Y) a1 = polyA[i];
            (double X, double Y) a2 = polyA[(i + 1) % polyA.Count];
            double segMinX = Math.Min(a1.X, a2.X) - 1e-7;
            double segMaxX = Math.Max(a1.X, a2.X) + 1e-7;
            double segMinY = Math.Min(a1.Y, a2.Y) - 1e-7;
            double segMaxY = Math.Max(a1.Y, a2.Y) + 1e-7;
            if (segMaxX < bMinX || segMinX > bMaxX || segMaxY < bMinY || segMinY > bMaxY)
            {
                continue;
            }

            for (int j = 0; j < polyB.Count; j++)
            {
                if (SegmentsIntersect(a1, a2, polyB[j], polyB[(j + 1) % polyB.Count]))
                {
                    return true;
                }
            }
        }

        return IsPointInPolygon(GetInteriorTestPoint(polyA), polyB)
            || IsPointInPolygon(GetInteriorTestPoint(polyB), polyA);
    }

    /// <summary>
    /// Verifica se dois polígonos fechados estão estritamente disjuntos e respeitam a distância mínima
    /// <paramref name="requiredSpacingMm"/> entre suas fronteiras.
    /// </summary>
    public static bool IsMinimumSpacingSatisfied(
        IReadOnlyList<(double X, double Y)> polyA,
        IReadOnlyList<(double X, double Y)> polyB,
        double requiredSpacingMm)
    {
        if (PolygonsIntersect(polyA, polyB))
        {
            return false;
        }

        if (requiredSpacingMm <= DistanceTolerance)
        {
            return true;
        }

        double minAllowSq = Math.Pow(Math.Max(0.0, requiredSpacingMm - DistanceTolerance), 2);
        return MinDistanceSquared(polyA, polyB, minAllowSq) >= minAllowSq;
    }

    /// <summary>
    /// Verifica se <paramref name="innerPoly"/> está inteiramente contido dentro de um furo fechado
    /// <paramref name="holePoly"/> mantendo distância mínima <paramref name="requiredSpacingMm"/> da borda do furo.
    /// </summary>
    public static bool IsInsidePolygonWithClearance(
        IReadOnlyList<(double X, double Y)> innerPoly,
        IReadOnlyList<(double X, double Y)> holePoly,
        double requiredSpacingMm)
    {
        if (innerPoly.Count < 3 || holePoly.Count < 3)
        {
            return false;
        }

        for (int i = 0; i < innerPoly.Count; i++)
        {
            if (!IsPointInPolygon(innerPoly[i], holePoly))
            {
                return false;
            }
        }

        // Nenhuma aresta pode cruzar a borda do furo
        for (int i = 0; i < innerPoly.Count; i++)
        {
            (double X, double Y) a1 = innerPoly[i];
            (double X, double Y) a2 = innerPoly[(i + 1) % innerPoly.Count];
            for (int j = 0; j < holePoly.Count; j++)
            {
                if (SegmentsIntersect(a1, a2, holePoly[j], holePoly[(j + 1) % holePoly.Count]))
                {
                    return false;
                }
            }
        }

        // Também garante que nenhum ponto do contorno do furo caia dentro da peça (concavidades invertidas)
        if (IsPointInPolygon(GetInteriorTestPoint(holePoly), innerPoly) && AbsArea(holePoly) <= AbsArea(innerPoly))
        {
            return false;
        }

        if (requiredSpacingMm <= DistanceTolerance)
        {
            return true;
        }

        double minAllowSq = Math.Pow(Math.Max(0.0, requiredSpacingMm - DistanceTolerance), 2);
        return MinDistanceSquared(innerPoly, holePoly, minAllowSq) >= minAllowSq;
    }

    /// <summary>
    /// Verifica se <paramref name="innerPoly"/> está estritamente contido no interior de <paramref name="outerPoly"/>
    /// (área estritamente menor, caixa delimitadora contida, todos os vértices internos e sem cruzamento de arestas),
    /// usado para agrupar subcaminhos concêntricos em caminhos compostos (<c>fill-rule="evenodd"</c>).
    /// </summary>
    public static bool IsPolygonStrictlyContainedIn(
        IReadOnlyList<(double X, double Y)> innerPoly,
        IReadOnlyList<(double X, double Y)> outerPoly)
    {
        if (innerPoly.Count < 3 || outerPoly.Count < 3)
        {
            return false;
        }

        double innerArea = AbsArea(innerPoly);
        double outerArea = AbsArea(outerPoly);
        if (innerArea <= 1e-6 || outerArea <= innerArea + 1e-4)
        {
            return false;
        }

        (double iMinX, double iMinY, double iMaxX, double iMaxY) = ComputeBounds(innerPoly);
        (double oMinX, double oMinY, double oMaxX, double oMaxY) = ComputeBounds(outerPoly);
        if (iMinX < oMinX - 1e-5 || iMaxX > oMaxX + 1e-5 || iMinY < oMinY - 1e-5 || iMaxY > oMaxY + 1e-5)
        {
            return false;
        }

        return IsInsidePolygonWithClearance(innerPoly, outerPoly, requiredSpacingMm: 0.0);
    }

    /// <summary>Calcula a distância euclidiana mínima real entre duas polilinhas/polígonos fechados.</summary>
    public static double MinDistanceBetweenPolygons(
        IReadOnlyList<(double X, double Y)> polyA,
        IReadOnlyList<(double X, double Y)> polyB)
    {
        if (PolygonsIntersect(polyA, polyB))
        {
            return 0.0;
        }

        return Math.Sqrt(MinDistanceSquared(polyA, polyB, double.PositiveInfinity));
    }

    private static double MinDistanceSquared(
        IReadOnlyList<(double X, double Y)> polyA,
        IReadOnlyList<(double X, double Y)> polyB,
        double earlyExitBelowSq)
    {
        (double aMinX, double aMinY, double aMaxX, double aMaxY) = ComputeBounds(polyA);
        (double bMinX, double bMinY, double bMaxX, double bMaxY) = ComputeBounds(polyB);
        double minDistSq = double.PositiveInfinity;
        double pad = double.IsPositiveInfinity(earlyExitBelowSq) ? 1e9 : Math.Sqrt(earlyExitBelowSq) + 1e-3;
        int nA = polyA.Count;
        int nB = polyB.Count;

        for (int i = 0; i < nA; i++)
        {
            (double X, double Y) a1 = polyA[i];
            (double X, double Y) a2 = polyA[(i + 1) % nA];
            if (Math.Max(a1.X, a2.X) < bMinX - pad || Math.Min(a1.X, a2.X) > bMaxX + pad ||
                Math.Max(a1.Y, a2.Y) < bMinY - pad || Math.Min(a1.Y, a2.Y) > bMaxY + pad)
            {
                continue;
            }

            for (int j = 0; j < nB; j++)
            {
                double dSq = PointToSegmentDistanceSq(polyB[j], a1, a2);
                if (dSq < minDistSq)
                {
                    minDistSq = dSq;
                    if (minDistSq < earlyExitBelowSq)
                    {
                        return minDistSq;
                    }
                }
            }
        }

        for (int j = 0; j < nB; j++)
        {
            (double X, double Y) b1 = polyB[j];
            (double X, double Y) b2 = polyB[(j + 1) % nB];
            if (Math.Max(b1.X, b2.X) < aMinX - pad || Math.Min(b1.X, b2.X) > aMaxX + pad ||
                Math.Max(b1.Y, b2.Y) < aMinY - pad || Math.Min(b1.Y, b2.Y) > aMaxY + pad)
            {
                continue;
            }

            for (int i = 0; i < nA; i++)
            {
                double dSq = PointToSegmentDistanceSq(polyA[i], b1, b2);
                if (dSq < minDistSq)
                {
                    minDistSq = dSq;
                    if (minDistSq < earlyExitBelowSq)
                    {
                        return minDistSq;
                    }
                }
            }
        }

        return minDistSq;
    }

    public static bool SegmentsIntersect(
        (double X, double Y) a1,
        (double X, double Y) a2,
        (double X, double Y) b1,
        (double X, double Y) b2)
    {
        const double eps = 1e-9;

        if (Math.Max(a1.X, a2.X) < Math.Min(b1.X, b2.X) - eps ||
            Math.Max(b1.X, b2.X) < Math.Min(a1.X, a2.X) - eps ||
            Math.Max(a1.Y, a2.Y) < Math.Min(b1.Y, b2.Y) - eps ||
            Math.Max(b1.Y, b2.Y) < Math.Min(a1.Y, a2.Y) - eps)
        {
            return false;
        }

        double o1 = Cross(a1, a2, b1);
        double o2 = Cross(a1, a2, b2);
        double o3 = Cross(b1, b2, a1);
        double o4 = Cross(b1, b2, a2);

        if (((o1 > eps && o2 < -eps) || (o1 < -eps && o2 > eps)) &&
            ((o3 > eps && o4 < -eps) || (o3 < -eps && o4 > eps)))
        {
            return true;
        }

        if (Math.Abs(o1) <= eps && OnSegment(a1, b1, a2, eps)) return true;
        if (Math.Abs(o2) <= eps && OnSegment(a1, b2, a2, eps)) return true;
        if (Math.Abs(o3) <= eps && OnSegment(b1, a1, b2, eps)) return true;
        if (Math.Abs(o4) <= eps && OnSegment(b1, a2, b2, eps)) return true;

        return false;
    }

    public static double PointToSegmentDistanceSq(
        (double X, double Y) p,
        (double X, double Y) a,
        (double X, double Y) b)
    {
        double vx = b.X - a.X;
        double vy = b.Y - a.Y;
        double lenSq = (vx * vx) + (vy * vy);
        if (lenSq <= 1e-18)
        {
            return DistSq(p, a);
        }

        double t = (((p.X - a.X) * vx) + ((p.Y - a.Y) * vy)) / lenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        double px = a.X + (t * vx);
        double py = a.Y + (t * vy);
        return DistSq(p, (px, py));
    }

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static bool OnSegment((double X, double Y) a, (double X, double Y) p, (double X, double Y) b, double eps)
        => p.X <= Math.Max(a.X, b.X) + eps && p.X >= Math.Min(a.X, b.X) - eps
        && p.Y <= Math.Max(a.Y, b.Y) + eps && p.Y >= Math.Min(a.Y, b.Y) - eps;

    private static double DistSq((double X, double Y) a, (double X, double Y) b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }
}
