using System.Numerics;
using CaixaMaker.Core.Geometry;

namespace CaixaMaker.App.Viewport3D;

/// <summary>Resultado da triangulação 2D de um polígono complexo com furos.</summary>
internal sealed record TriangulatedPolygon2D(
    IReadOnlyList<Vector2> OuterLoop,
    IReadOnlyList<IReadOnlyList<Vector2>> HoleLoops,
    IReadOnlyList<(Vector2 A, Vector2 B, Vector2 C)> Triangles);

/// <summary>
/// Triangulador 2D para polígonos côncavos (dentes Finger Joint) com furos internos
/// retangulares e circulares, utilizando ponte de Eberly e Ear Clipping robusto.
/// </summary>
internal static class PolygonTriangulator2D
{
    private const float Eps = 1e-5f;

    public static TriangulatedPolygon2D Triangulate(
        Polygon2D outerContour,
        IReadOnlyList<Polygon2D> holes,
        IReadOnlyList<Circle2D> circularHoles,
        Func<Point2D, Vector2> toPlane2D,
        int circleSegments = 32)
    {
        List<Vector2> outer = CleanLoop(outerContour.Vertices.Select(toPlane2D));
        if (SignedArea(outer) < 0f)
        {
            outer.Reverse();
        }

        var cleanHoles = new List<List<Vector2>>(holes.Count + circularHoles.Count);

        foreach (Polygon2D hole in holes)
        {
            List<Vector2> cleaned = CleanLoop(hole.Vertices.Select(toPlane2D));
            if (cleaned.Count < 3 || MathF.Abs(SignedArea(cleaned)) < 1e-4f)
            {
                continue;
            }

            if (SignedArea(cleaned) > 0f)
            {
                cleaned.Reverse(); // Furos devem ter orientação horária (CW)
            }

            cleanHoles.Add(cleaned);
        }

        foreach (Circle2D circle in circularHoles)
        {
            var pts = new List<Vector2>(circleSegments);
            for (int i = 0; i < circleSegments; i++)
            {
                double angle = -2.0 * Math.PI * i / circleSegments; // CW em relação a CCW outer
                double px = circle.CenterX + circle.Radius * Math.Cos(angle);
                double py = circle.CenterY + circle.Radius * Math.Sin(angle);
                pts.Add(toPlane2D(new Point2D(px, py)));
            }

            List<Vector2> cleaned = CleanLoop(pts);
            if (SignedArea(cleaned) > 0f)
            {
                cleaned.Reverse();
            }

            cleanHoles.Add(cleaned);
        }

        // Mescla cada furo ao contorno externo via pontes da direita para a esquerda
        var workingPoly = new List<Vector2>(outer);
        List<(List<Vector2> Loop, float MaxX)> sortedHoles = cleanHoles
            .Select(h => (Loop: h, MaxX: h.Max(p => p.X)))
            .OrderByDescending(x => x.MaxX)
            .ToList();

        foreach ((List<Vector2> hole, _) in sortedHoles)
        {
            workingPoly = SpliceHole(workingPoly, hole);
        }

        List<(Vector2 A, Vector2 B, Vector2 C)> triangles = ClipEars(workingPoly);
        return new TriangulatedPolygon2D(
            outer,
            cleanHoles.Cast<IReadOnlyList<Vector2>>().ToArray(),
            triangles);
    }

    internal static float SignedArea(IReadOnlyList<Vector2> poly)
    {
        if (poly.Count < 3) return 0f;
        double sum = 0.0;
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i];
            Vector2 b = poly[(i + 1) % poly.Count];
            sum += ((double)a.X * b.Y) - ((double)b.X * a.Y);
        }

        return (float)(sum * 0.5);
    }

    private static List<Vector2> CleanLoop(IEnumerable<Vector2> source)
    {
        var pts = new List<Vector2>();
        foreach (Vector2 p in source)
        {
            if (pts.Count == 0 || Vector2.DistanceSquared(pts[^1], p) > 1e-8f)
            {
                pts.Add(p);
            }
        }

        if (pts.Count > 2 && Vector2.DistanceSquared(pts[0], pts[^1]) <= 1e-8f)
        {
            pts.RemoveAt(pts.Count - 1);
        }

        // Remove vértices estritamente colineares intermediários sem alterar área
        bool removed = true;
        while (removed && pts.Count > 3)
        {
            removed = false;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 prev = pts[(i - 1 + pts.Count) % pts.Count];
                Vector2 curr = pts[i];
                Vector2 next = pts[(i + 1) % pts.Count];
                Vector2 d1 = curr - prev;
                Vector2 d2 = next - curr;
                float cross = MathF.Abs(Cross(d1, d2));
                float scale = d1.Length() * d2.Length();
                if (scale > 1e-8f && cross <= 1e-5f * scale && Vector2.Dot(d1, d2) > 0f)
                {
                    pts.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }

        return pts;
    }

    private static List<Vector2> SpliceHole(List<Vector2> poly, List<Vector2> hole)
    {
        int mIdx = 0;
        for (int i = 1; i < hole.Count; i++)
        {
            if (hole[i].X > hole[mIdx].X ||
                (MathF.Abs(hole[i].X - hole[mIdx].X) < Eps && hole[i].Y < hole[mIdx].Y))
            {
                mIdx = i;
            }
        }

        Vector2 m = hole[mIdx];

        // Raio horizontal +X partindo de M até a aresta mais próxima do polígono externo
        float bestX = float.PositiveInfinity;
        int bestEdgeA = -1;
        int bestEdgeB = -1;
        Vector2 bestIntersect = default;

        for (int i = 0; i < poly.Count; i++)
        {
            int j = (i + 1) % poly.Count;
            Vector2 a = poly[i];
            Vector2 b = poly[j];

            if ((a.Y <= m.Y && b.Y > m.Y) || (b.Y <= m.Y && a.Y > m.Y))
            {
                float t = (m.Y - a.Y) / (b.Y - a.Y);
                float ix = a.X + t * (b.X - a.X);
                if (ix >= m.X - Eps && ix < bestX)
                {
                    bestX = ix;
                    bestEdgeA = i;
                    bestEdgeB = j;
                    bestIntersect = new Vector2(ix, m.Y);
                }
            }
            else if (MathF.Abs(a.Y - m.Y) < Eps && a.X >= m.X - Eps && a.X < bestX)
            {
                bestX = a.X;
                bestEdgeA = i;
                bestEdgeB = j;
                bestIntersect = a;
            }
        }

        int visibleIdx;
        if (bestEdgeA < 0)
        {
            visibleIdx = FindClosestVertex(poly, m);
        }
        else
        {
            Vector2 a = poly[bestEdgeA];
            Vector2 b = poly[bestEdgeB];
            int pIdx;
            if (Vector2.DistanceSquared(bestIntersect, a) < 1e-6f)
            {
                pIdx = bestEdgeA;
            }
            else if (Vector2.DistanceSquared(bestIntersect, b) < 1e-6f)
            {
                pIdx = bestEdgeB;
            }
            else
            {
                pIdx = a.X >= b.X ? bestEdgeA : bestEdgeB;
            }

            Vector2 p = poly[pIdx];
            visibleIdx = pIdx;

            if (MathF.Abs(Cross(bestIntersect - m, p - m)) > Eps)
            {
                float bestCos = -2f;
                float bestDistSq = float.PositiveInfinity;

                for (int i = 0; i < poly.Count; i++)
                {
                    if (i == pIdx) continue;
                    Vector2 candidate = poly[i];
                    if (candidate.X < m.X - Eps) continue;
                    if (!IsReflex(poly, i)) continue;

                    if (PointInTriangleUnorientedInclusive(candidate, m, bestIntersect, p))
                    {
                        Vector2 dir = candidate - m;
                        float distSq = dir.LengthSquared();
                        if (distSq < 1e-8f) continue;
                        float cosAngle = dir.X / MathF.Sqrt(distSq);
                        if (cosAngle > bestCos + 1e-6f ||
                            (MathF.Abs(cosAngle - bestCos) <= 1e-6f && distSq < bestDistSq))
                        {
                            bestCos = cosAngle;
                            bestDistSq = distSq;
                            visibleIdx = i;
                        }
                    }
                }
            }
        }

        // Seleciona ocorrência correta caso vértice já tenha sido ponte anterior e garante diagonal sem auto-interseção
        visibleIdx = SelectBestSectorOccurrence(poly, visibleIdx, m);
        if (!IsBridgeClear(poly, visibleIdx, m))
        {
            visibleIdx = FindClosestClearVisibleVertex(poly, m, visibleIdx);
        }

        var merged = new List<Vector2>(poly.Count + hole.Count + 2);
        for (int i = 0; i <= visibleIdx; i++)
        {
            merged.Add(poly[i]);
        }

        for (int k = 0; k < hole.Count; k++)
        {
            merged.Add(hole[(mIdx + k) % hole.Count]);
        }

        merged.Add(hole[mIdx]);
        merged.Add(poly[visibleIdx]);

        for (int i = visibleIdx + 1; i < poly.Count; i++)
        {
            merged.Add(poly[i]);
        }

        return merged;
    }

    private static int SelectBestSectorOccurrence(List<Vector2> poly, int targetIdx, Vector2 fromHolePoint)
    {
        Vector2 target = poly[targetIdx];
        Vector2 dirToHole = fromHolePoint - target;
        int bestIdx = targetIdx;

        for (int i = 0; i < poly.Count; i++)
        {
            if (Vector2.DistanceSquared(poly[i], target) > 1e-6f) continue;

            Vector2 prev = poly[(i - 1 + poly.Count) % poly.Count];
            Vector2 next = poly[(i + 1) % poly.Count];
            if (IsInInteriorCone(prev - target, next - target, dirToHole))
            {
                return i;
            }

            bestIdx = i;
        }

        return bestIdx;
    }

    private static bool IsInInteriorCone(Vector2 toPrev, Vector2 toNext, Vector2 dir)
    {
        // Para polígono CCW: interior fica à esquerda da aresta de entrada eIn = -toPrev e da aresta de saída eOut = toNext
        Vector2 eIn = -toPrev;
        Vector2 eOut = toNext;
        if (Cross(eIn, eOut) >= 0f)
        {
            return Cross(eIn, dir) >= -Eps && Cross(eOut, dir) >= -Eps;
        }

        return Cross(eIn, dir) >= -Eps || Cross(eOut, dir) >= -Eps;
    }

    private static int FindClosestClearVisibleVertex(List<Vector2> poly, Vector2 fromHolePoint, int fallbackIdx)
    {
        int bestIdx = fallbackIdx;
        float bestDistSq = float.PositiveInfinity;

        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 target = poly[i];
            Vector2 prev = poly[(i - 1 + poly.Count) % poly.Count];
            Vector2 next = poly[(i + 1) % poly.Count];
            if (!IsInInteriorCone(prev - target, next - target, fromHolePoint - target))
            {
                continue;
            }

            if (!IsBridgeClear(poly, i, fromHolePoint))
            {
                continue;
            }

            float distSq = Vector2.DistanceSquared(target, fromHolePoint);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    private static bool IsBridgeClear(List<Vector2> poly, int polyIdx, Vector2 holePoint)
    {
        Vector2 a = poly[polyIdx];
        int n = poly.Count;

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            if (i == polyIdx || j == polyIdx)
            {
                continue;
            }

            Vector2 c = poly[i];
            Vector2 d = poly[j];
            if (Vector2.DistanceSquared(c, a) < 1e-6f || Vector2.DistanceSquared(d, a) < 1e-6f)
            {
                continue;
            }

            if (SegmentsProperlyIntersect(a, holePoint, c, d))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SegmentsProperlyIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        float d1 = Cross(p2 - p1, q1 - p1);
        float d2 = Cross(p2 - p1, q2 - p1);
        float d3 = Cross(q2 - q1, p1 - q1);
        float d4 = Cross(q2 - q1, p2 - q1);

        if (((d1 > Eps && d2 < -Eps) || (d1 < -Eps && d2 > Eps)) &&
            ((d3 > Eps && d4 < -Eps) || (d3 < -Eps && d4 > Eps)))
        {
            return true;
        }

        return false;
    }

    private static bool PointInTriangleUnorientedInclusive(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        => Cross(b - a, c - a) >= 0f
            ? PointInTriangleInclusive(p, a, b, c)
            : PointInTriangleInclusive(p, a, c, b);

    private static int FindClosestVertex(List<Vector2> poly, Vector2 point)
    {
        int best = 0;
        float bestDistSq = float.PositiveInfinity;
        for (int i = 0; i < poly.Count; i++)
        {
            float d = Vector2.DistanceSquared(poly[i], point);
            if (d < bestDistSq)
            {
                bestDistSq = d;
                best = i;
            }
        }
        return best;
    }

    private static bool IsReflex(List<Vector2> poly, int idx)
    {
        Vector2 prev = poly[(idx - 1 + poly.Count) % poly.Count];
        Vector2 curr = poly[idx];
        Vector2 next = poly[(idx + 1) % poly.Count];
        return Cross(curr - prev, next - curr) < -Eps;
    }

    private static List<(Vector2 A, Vector2 B, Vector2 C)> ClipEars(List<Vector2> input)
    {
        var poly = new List<Vector2>(input);
        var triangles = new List<(Vector2 A, Vector2 B, Vector2 C)>(Math.Max(0, poly.Count - 2));

        int guard = 0;
        int maxIterations = Math.Max(64, poly.Count * poly.Count);

        while (poly.Count > 3 && guard++ < maxIterations)
        {
            int earIdx = FindEarIndex(poly, strict: true);
            if (earIdx < 0)
            {
                earIdx = FindEarIndex(poly, strict: false);
            }

            if (earIdx < 0)
            {
                break;
            }

            int n = poly.Count;
            Vector2 prev = poly[(earIdx - 1 + n) % n];
            Vector2 curr = poly[earIdx];
            Vector2 next = poly[(earIdx + 1) % n];

            if (MathF.Abs(Cross(curr - prev, next - prev)) > 1e-6f)
            {
                triangles.Add((prev, curr, next));
            }

            poly.RemoveAt(earIdx);
        }

        if (poly.Count == 3 && MathF.Abs(Cross(poly[1] - poly[0], poly[2] - poly[0])) > 1e-6f)
        {
            triangles.Add((poly[0], poly[1], poly[2]));
        }

        return triangles;
    }

    private static int FindEarIndex(List<Vector2> poly, bool strict)
    {
        int n = poly.Count;
        int bestFallback = -1;
        float bestArea = float.NegativeInfinity;

        for (int i = 0; i < n; i++)
        {
            Vector2 prev = poly[(i - 1 + n) % n];
            Vector2 curr = poly[i];
            Vector2 next = poly[(i + 1) % n];
            float cross = Cross(curr - prev, next - curr);

            if (strict)
            {
                if (cross <= 1e-6f) continue;

                bool containsOther = false;
                for (int j = 0; j < n; j++)
                {
                    if (j == (i - 1 + n) % n || j == i || j == (i + 1) % n) continue;
                    Vector2 p = poly[j];
                    if (Vector2.DistanceSquared(p, prev) < 1e-7f ||
                        Vector2.DistanceSquared(p, curr) < 1e-7f ||
                        Vector2.DistanceSquared(p, next) < 1e-7f)
                    {
                        continue;
                    }

                    if (PointInTriangleInclusive(p, prev, curr, next))
                    {
                        containsOther = true;
                        break;
                    }
                }

                if (!containsOther)
                {
                    return i;
                }
            }
            else if (cross >= -1e-5f)
            {
                float score = cross;
                if (score > bestArea)
                {
                    bestArea = score;
                    bestFallback = i;
                }
            }
        }

        return bestFallback;
    }

    private static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

    private static bool PointInTriangleInclusive(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float c1 = Cross(b - a, p - a);
        float c2 = Cross(c - b, p - b);
        float c3 = Cross(a - c, p - c);
        return c1 >= -1e-5f && c2 >= -1e-5f && c3 >= -1e-5f;
    }
}
