using System.Globalization;
using CaixaMaker.Cam.Svg;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Nesting;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.Cam.Nesting;

/// <summary>
/// Helpers geométricos compartilhados entre os importadores (DXF/SVG) e o adaptador de chapas da caixa,
/// evitando duplicação de achatamento de curvas, costura de contornos e agrupamento hierárquico.
/// </summary>
internal static class NestingImportHelper
{
    public const double DefaultChordToleranceMm = 0.02;
    public const double DefaultStitchToleranceMm = 0.05;

    internal sealed record RawImportedPolyline(
        IReadOnlyList<(double X, double Y)> Points,
        bool IsClosed,
        NestingSemanticLayer Layer,
        double StrokeWidthMm,
        string? SourceLayerName,
        bool UsedDefaultZeroFallback = false,
        string? CompoundGroupId = null,
        bool IsFilled = true);

    public static IReadOnlyList<(double X, double Y)> DiscretizeCircle(
        double cx,
        double cy,
        double radius,
        double chordToleranceMm = DefaultChordToleranceMm)
    {
        double r = Math.Max(1e-4, radius);
        double arg = Math.Clamp(1.0 - (chordToleranceMm / r), -1.0, 1.0);
        double maxDTheta = 2.0 * Math.Acos(arg);
        int segments = double.IsNaN(maxDTheta) || maxDTheta <= 1e-6
            ? 128
            : Math.Clamp((int)Math.Ceiling((2.0 * Math.PI) / maxDTheta), 36, 128);

        var pts = new (double X, double Y)[segments];
        for (int i = 0; i < segments; i++)
        {
            double angle = (2.0 * Math.PI * i) / segments;
            pts[i] = (cx + (r * Math.Cos(angle)), cy + (r * Math.Sin(angle)));
        }

        return pts;
    }

    public static void AppendArcPoints(
        List<(double X, double Y)> target,
        double cx,
        double cy,
        double radius,
        double startRad,
        double deltaRad,
        double chordToleranceMm = DefaultChordToleranceMm)
    {
        double r = Math.Max(1e-4, radius);
        double arg = Math.Clamp(1.0 - (chordToleranceMm / r), -1.0, 1.0);
        double maxDTheta = 2.0 * Math.Acos(arg);
        int steps = double.IsNaN(maxDTheta) || maxDTheta <= 1e-6
            ? 32
            : Math.Max(4, (int)Math.Ceiling(Math.Abs(deltaRad) / maxDTheta));

        for (int i = 1; i <= steps; i++)
        {
            double theta = startRad + ((deltaRad * i) / steps);
            target.Add((cx + (r * Math.Cos(theta)), cy + (r * Math.Sin(theta))));
        }
    }

    public static List<(double X, double Y)> FlattenSvgSubpath(
        SvgSubpath subpath,
        AffineMatrix2D? transform = null,
        double chordToleranceMm = DefaultChordToleranceMm)
    {
        AffineMatrix2D m = transform ?? AffineMatrix2D.Identity;
        var pts = new List<(double X, double Y)>();

        void Push(Point2D p)
        {
            Point2D tp = m.Transform(p);
            if (pts.Count == 0 || DistSq(pts[^1], (tp.X, tp.Y)) > 1e-10)
            {
                pts.Add((tp.X, tp.Y));
            }
        }

        foreach (SvgPathSegment seg in subpath.Segments)
        {
            switch (seg.Kind)
            {
                case SvgSegmentKind.Move:
                    Push(seg.End);
                    break;

                case SvgSegmentKind.Line:
                    if (pts.Count == 0)
                    {
                        Push(seg.Start);
                    }

                    Push(seg.End);
                    break;

                case SvgSegmentKind.Quad:
                {
                    if (pts.Count == 0)
                    {
                        Push(seg.Start);
                    }

                    Point2D p0 = seg.Start;
                    Point2D p3 = seg.End;
                    Point2D c1 = new(
                        p0.X + ((2.0 / 3.0) * (seg.Control1.X - p0.X)),
                        p0.Y + ((2.0 / 3.0) * (seg.Control1.Y - p0.Y)));
                    Point2D c2 = new(
                        p3.X + ((2.0 / 3.0) * (seg.Control1.X - p3.X)),
                        p3.Y + ((2.0 / 3.0) * (seg.Control1.Y - p3.Y)));
                    FlattenCubicAdaptive(m.Transform(p0), m.Transform(c1), m.Transform(c2), m.Transform(p3), chordToleranceMm, 0, pts);
                    break;
                }

                case SvgSegmentKind.Cubic:
                    if (pts.Count == 0)
                    {
                        Push(seg.Start);
                    }

                    FlattenCubicAdaptive(m.Transform(seg.Start), m.Transform(seg.Control1), m.Transform(seg.Control2), m.Transform(seg.End), chordToleranceMm, 0, pts);
                    break;

                case SvgSegmentKind.Arc:
                    if (pts.Count == 0)
                    {
                        Push(seg.Start);
                    }

                    foreach (SvgPathSegment cubic in SvgDocumentParser.ArcToCubicBeziers(seg))
                    {
                        FlattenCubicAdaptive(
                            m.Transform(cubic.Start),
                            m.Transform(cubic.Control1),
                            m.Transform(cubic.Control2),
                            m.Transform(cubic.End),
                            chordToleranceMm,
                            0,
                            pts);
                    }
                    break;
            }
        }

        if (subpath.IsClosed && pts.Count >= 2 && DistSq(pts[^1], pts[0]) <= 1e-8)
        {
            pts.RemoveAt(pts.Count - 1);
        }

        return pts;
    }

    private static void FlattenCubicAdaptive(
        Point2D p0,
        Point2D c1,
        Point2D c2,
        Point2D p3,
        double tolMm,
        int depth,
        List<(double X, double Y)> output)
    {
        double d1 = PointLineDistance(c1, p0, p3);
        double d2 = PointLineDistance(c2, p0, p3);
        if ((d1 <= tolMm && d2 <= tolMm && depth >= 1) || depth >= 8)
        {
            if (output.Count == 0 || DistSq(output[^1], (p3.X, p3.Y)) > 1e-10)
            {
                output.Add((p3.X, p3.Y));
            }

            return;
        }

        Point2D p01 = Mid(p0, c1);
        Point2D p12 = Mid(c1, c2);
        Point2D p23 = Mid(c2, p3);
        Point2D p012 = Mid(p01, p12);
        Point2D p123 = Mid(p12, p23);
        Point2D p0123 = Mid(p012, p123);

        FlattenCubicAdaptive(p0, p01, p012, p0123, tolMm, depth + 1, output);
        FlattenCubicAdaptive(p0123, p123, p23, p3, tolMm, depth + 1, output);
    }

    /// <summary>Costura entidades abertas (LINE/ARC) na mesma camada cujas pontas estejam dentro de <paramref name="toleranceMm"/>.</summary>
    public static List<RawImportedPolyline> StitchOpenPolylines(
        IReadOnlyList<RawImportedPolyline> input,
        double toleranceMm = DefaultStitchToleranceMm)
    {
        var result = new List<RawImportedPolyline>();
        var openBuckets = new Dictionary<(NestingSemanticLayer Layer, string? RawLayer, string? CompoundGroupId, bool IsFilled), List<List<(double X, double Y)>>>();
        var strokeWidths = new Dictionary<(NestingSemanticLayer, string?, string?, bool), double>();

        foreach (RawImportedPolyline item in input)
        {
            if (item.Points.Count < 2)
            {
                continue;
            }

            if (item.IsClosed && item.Points.Count >= 3)
            {
                result.Add(item);
                continue;
            }

            // Verifica se já está fechada geometricamente
            if (item.Points.Count >= 3 && Dist(item.Points[0], item.Points[^1]) <= toleranceMm)
            {
                IReadOnlyList<(double X, double Y)> closedPts = item.Points.Take(item.Points.Count - 1).ToArray();
                if (closedPts.Count >= 3)
                {
                    result.Add(item with { Points = closedPts, IsClosed = true });
                    continue;
                }
            }

            var key = (item.Layer, item.SourceLayerName, item.CompoundGroupId, item.IsFilled);
            if (!openBuckets.TryGetValue(key, out List<List<(double X, double Y)>>? bucket))
            {
                bucket = new List<List<(double X, double Y)>>();
                openBuckets[key] = bucket;
                strokeWidths[key] = item.StrokeWidthMm;
            }

            bucket.Add(item.Points.ToList());
        }

        foreach (((NestingSemanticLayer layer, string? rawLayer, string? compoundGroupId, bool isFilled) key, List<List<(double X, double Y)>> chains) in openBuckets)
        {
            double strokeWidth = strokeWidths[key];
            for (int i = 0; i < chains.Count; i++)
            {
                List<(double X, double Y)> cur = chains[i];
                if (cur.Count == 0)
                {
                    continue;
                }

                bool merged;
                do
                {
                    merged = false;
                    if (cur.Count >= 3 && Dist(cur[0], cur[^1]) <= toleranceMm)
                    {
                        break;
                    }

                    for (int j = i + 1; j < chains.Count; j++)
                    {
                        List<(double X, double Y)> other = chains[j];
                        if (other.Count < 2)
                        {
                            continue;
                        }

                        if (Dist(cur[^1], other[0]) <= toleranceMm)
                        {
                            cur.AddRange(other.Skip(1));
                        }
                        else if (Dist(cur[^1], other[^1]) <= toleranceMm)
                        {
                            other.Reverse();
                            cur.AddRange(other.Skip(1));
                        }
                        else if (Dist(cur[0], other[^1]) <= toleranceMm)
                        {
                            cur.InsertRange(0, other.Take(other.Count - 1));
                        }
                        else if (Dist(cur[0], other[0]) <= toleranceMm)
                        {
                            other.Reverse();
                            cur.InsertRange(0, other.Take(other.Count - 1));
                        }
                        else
                        {
                            continue;
                        }

                        other.Clear();
                        merged = true;
                        break;
                    }
                }
                while (merged);

                bool isClosed = cur.Count >= 3 && Dist(cur[0], cur[^1]) <= toleranceMm;
                if (isClosed)
                {
                    cur.RemoveAt(cur.Count - 1);
                }

                if (cur.Count >= (isClosed ? 3 : 2))
                {
                    result.Add(new RawImportedPolyline(
                        cur,
                        isClosed,
                        key.layer,
                        strokeWidth,
                        key.rawLayer,
                        UsedDefaultZeroFallback: false,
                        CompoundGroupId: isClosed ? key.compoundGroupId : null,
                        IsFilled: key.isFilled && isClosed));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Agrupa contornos fechados de corte por hierarquia de contenção Ponto-em-Polígono,
    /// criando uma lista limpa de <see cref="NestingPart"/> com furos e gravações acoplados à peça-pai.
    /// </summary>
    public static IReadOnlyList<NestingPart> BuildHierarchicalParts(
        IReadOnlyList<RawImportedPolyline> items,
        IReadOnlyList<NestingRasterOverlay>? rasters,
        string sourceTag,
        string namePrefix,
        bool keepAsSinglePart = false,
        NestingRotationPolicy rotationPolicy = NestingRotationPolicy.Step90)
    {
        if (items.Count == 0)
        {
            return Array.Empty<NestingPart>();
        }

        // Separa candidatos de corte fechado
        var cutClosed = new List<(int Index, RawImportedPolyline Poly, double Area, (double X, double Y) TestPt)>();
        for (int i = 0; i < items.Count; i++)
        {
            RawImportedPolyline p = items[i];
            if (p.IsClosed && p.Points.Count >= 3 && p.Layer is NestingSemanticLayer.CutExternal or NestingSemanticLayer.CutInternal)
            {
                double area = PolygonNestingGeometry.AbsArea(p.Points);
                if (area > 1e-4)
                {
                    cutClosed.Add((i, p, area, PolygonNestingGeometry.GetInteriorTestPoint(p.Points)));
                }
            }
        }

        // Caso não existam loops classificados como corte, promove loops fechados restantes (se houver)
        if (cutClosed.Count == 0)
        {
            for (int i = 0; i < items.Count; i++)
            {
                RawImportedPolyline p = items[i];
                if (p.IsClosed && p.Points.Count >= 3)
                {
                    double area = PolygonNestingGeometry.AbsArea(p.Points);
                    if (area > 1e-4)
                    {
                        cutClosed.Add((i, p with { Layer = NestingSemanticLayer.CutExternal }, area, PolygonNestingGeometry.GetInteriorTestPoint(p.Points)));
                    }
                }
            }
        }

        if (cutClosed.Count == 0)
        {
            // Último recurso: cria envelope mínimo das polilinhas abertas em bloco único
            var allPts = items.SelectMany(x => x.Points).ToList();
            (double minX, double minY, double maxX, double maxY) = PolygonNestingGeometry.ComputeBounds(allPts);
            if (maxX - minX <= 1e-3 || maxY - minY <= 1e-3)
            {
                return Array.Empty<NestingPart>();
            }

            (double X, double Y)[] bbox = [(minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY)];
            List<NestingPolyline2D> feats = items
                .Select(x => new NestingPolyline2D(x.Points, x.IsClosed, x.Layer, x.StrokeWidthMm, x.SourceLayerName, x.CompoundGroupId, x.IsFilled))
                .ToList();
            MergeConcentricFilledEngraveGroups(feats);

            return
            [
                new NestingPart(
                    $"{sourceTag.ToLowerInvariant()}_part_1",
                    $"{namePrefix}",
                    bbox,
                    feats,
                    rasters,
                    sourceTag: sourceTag,
                    rotationPolicy: rotationPolicy),
            ];
        }

        if (keepAsSinglePart)
        {
            int outerEntryIdx = cutClosed.OrderByDescending(c => c.Area).First().Index;
            IReadOnlyList<(double X, double Y)> outerPts = items[outerEntryIdx].Points;
            var feats = new List<NestingPolyline2D>();
            for (int i = 0; i < items.Count; i++)
            {
                if (i == outerEntryIdx)
                {
                    continue;
                }

                RawImportedPolyline p = items[i];
                NestingSemanticLayer layer = p.Layer == NestingSemanticLayer.CutExternal ? NestingSemanticLayer.CutInternal : p.Layer;
                feats.Add(new NestingPolyline2D(p.Points, p.IsClosed, layer, p.StrokeWidthMm, p.SourceLayerName, p.CompoundGroupId, p.IsFilled));
            }

            MergeConcentricFilledEngraveGroups(feats);

            return
            [
                new NestingPart(
                    $"{sourceTag.ToLowerInvariant()}_part_1",
                    $"{namePrefix}",
                    outerPts,
                    feats,
                    rasters,
                    sourceTag: sourceTag,
                    rotationPolicy: rotationPolicy),
            ];
        }

        // Calcula árvore de contenção entre loops fechados de corte
        int n = cutClosed.Count;
        var parentIdx = new int[n];
        var depth = new int[n];
        Array.Fill(parentIdx, -1);

        for (int i = 0; i < n; i++)
        {
            double bestParentArea = double.PositiveInfinity;
            int bestParent = -1;
            int countAncestors = 0;

            for (int j = 0; j < n; j++)
            {
                if (i == j || cutClosed[j].Area <= cutClosed[i].Area + 1e-4)
                {
                    continue;
                }

                if (PolygonNestingGeometry.IsPointInPolygon(cutClosed[i].TestPt, cutClosed[j].Poly.Points))
                {
                    countAncestors++;
                    if (cutClosed[j].Area < bestParentArea)
                    {
                        bestParentArea = cutClosed[j].Area;
                        bestParent = j;
                    }
                }
            }

            depth[i] = countAncestors;
            parentIdx[i] = bestParent;
        }

        // Profundidades pares (0, 2, ...) são peças independentes; ímpares (1, 3, ...) são furos internos do pai imediato
        var partIndices = new List<int>();
        var partByCutIdx = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            if (depth[i] % 2 == 0)
            {
                partByCutIdx[i] = partIndices.Count;
                partIndices.Add(i);
            }
        }

        var featuresByPart = Enumerable.Range(0, partIndices.Count)
            .Select(_ => new List<NestingPolyline2D>())
            .ToArray();
        var rastersByPart = Enumerable.Range(0, partIndices.Count)
            .Select(_ => new List<NestingRasterOverlay>())
            .ToArray();

        var consumedItemIndices = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            consumedItemIndices.Add(cutClosed[i].Index);
            if (depth[i] % 2 == 1 && parentIdx[i] >= 0 && partByCutIdx.TryGetValue(parentIdx[i], out int ownerPart))
            {
                RawImportedPolyline hole = cutClosed[i].Poly;
                featuresByPart[ownerPart].Add(new NestingPolyline2D(
                    hole.Points,
                    IsClosed: true,
                    NestingSemanticLayer.CutInternal,
                    hole.StrokeWidthMm,
                    hole.SourceLayerName));
            }
        }

        // Vincula gravações, marcações e recortes abertos à peça-pai que os contém
        int FindSmallestContainingPart((double X, double Y) testPt)
        {
            int chosenPart = -1;
            double minArea = double.PositiveInfinity;
            for (int p = 0; p < partIndices.Count; p++)
            {
                var owner = cutClosed[partIndices[p]];
                if (owner.Area < minArea && PolygonNestingGeometry.IsPointInPolygon(testPt, owner.Poly.Points))
                {
                    minArea = owner.Area;
                    chosenPart = p;
                }
            }

            if (chosenPart >= 0)
            {
                return chosenPart;
            }

            // Fallback: peça mais próxima pelo centro da AABB (se houver apenas 1 ou proximidade imediata)
            if (partIndices.Count == 1)
            {
                return 0;
            }

            double bestDistSq = double.PositiveInfinity;
            for (int p = 0; p < partIndices.Count; p++)
            {
                (double minX, double minY, double maxX, double maxY) = PolygonNestingGeometry.ComputeBounds(cutClosed[partIndices[p]].Poly.Points);
                double cx = (minX + maxX) * 0.5;
                double cy = (minY + maxY) * 0.5;
                double dSq = ((testPt.X - cx) * (testPt.X - cx)) + ((testPt.Y - cy) * (testPt.Y - cy));
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    chosenPart = p;
                }
            }

            return chosenPart;
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (consumedItemIndices.Contains(i))
            {
                continue;
            }

            RawImportedPolyline feat = items[i];
            (double X, double Y) probe = feat.IsClosed && feat.Points.Count >= 3
                ? PolygonNestingGeometry.GetInteriorTestPoint(feat.Points)
                : feat.Points[feat.Points.Count / 2];

            int owner = FindSmallestContainingPart(probe);
            if (owner >= 0)
            {
                NestingSemanticLayer layer = feat.Layer == NestingSemanticLayer.CutExternal
                    ? NestingSemanticLayer.CutInternal
                    : feat.Layer;
                featuresByPart[owner].Add(new NestingPolyline2D(
                    feat.Points,
                    feat.IsClosed,
                    layer,
                    feat.StrokeWidthMm,
                    feat.SourceLayerName,
                    feat.CompoundGroupId,
                    feat.IsFilled));
            }
        }

        if (rasters is not null)
        {
            foreach (NestingRasterOverlay r in rasters)
            {
                (double X, double Y) center = (r.X + (r.WidthMm * 0.5), r.Y + (r.HeightMm * 0.5));
                int owner = FindSmallestContainingPart(center);
                if (owner >= 0)
                {
                    rastersByPart[owner].Add(r);
                }
            }
        }

        var parts = new List<NestingPart>(partIndices.Count);
        for (int p = 0; p < partIndices.Count; p++)
        {
            MergeConcentricFilledEngraveGroups(featuresByPart[p]);
            var outer = cutClosed[partIndices[p]];
            string slug = string.Format(CultureInfo.InvariantCulture, "{0}_part_{1}", sourceTag.ToLowerInvariant(), p + 1);
            string displayName = partIndices.Count == 1
                ? namePrefix
                : string.Format(CultureInfo.InvariantCulture, "{0} #{1}", namePrefix, p + 1);

            parts.Add(new NestingPart(
                id: slug,
                name: displayName,
                outerContour: outer.Poly.Points,
                internalFeatures: featuresByPart[p],
                rasterOverlays: rastersByPart[p],
                sourceTag: sourceTag,
                rotationPolicy: rotationPolicy));
        }

        return parts;
    }

    /// <summary>
    /// Unifica o <see cref="NestingPolyline2D.CompoundGroupId"/> de polígonos fechados preenchidos na camada
    /// <see cref="NestingSemanticLayer.EngraveFill"/> quando um polígono menor está estritamente contido no interior
    /// de um contorno externo preenchido do mesmo elemento/peça, preservando recortes internos via <c>evenodd</c>.
    /// </summary>
    public static void MergeConcentricFilledEngraveGroups(List<NestingPolyline2D> features, int startIndex = 0)
    {
        if (features.Count - startIndex < 2)
        {
            return;
        }

        var candidates = new List<(int FeatIdx, double Area)>();
        for (int i = startIndex; i < features.Count; i++)
        {
            NestingPolyline2D f = features[i];
            if (f.Layer == NestingSemanticLayer.EngraveFill && f.IsClosed && f.IsFilled && f.Points.Count >= 3)
            {
                double area = PolygonNestingGeometry.AbsArea(f.Points);
                if (area > 1e-6)
                {
                    candidates.Add((i, area));
                }
            }
        }

        int m = candidates.Count;
        if (m < 2)
        {
            return;
        }

        // Union-Find sobre os índices dos candidatos (preservando vínculos já existentes via CompoundGroupId)
        var parent = new int[m];
        for (int i = 0; i < m; i++)
        {
            parent[i] = i;
        }

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(int a, int b)
        {
            int ra = Find(a);
            int rb = Find(b);
            if (ra != rb)
            {
                parent[rb] = ra;
            }
        }

        var firstByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < m; i++)
        {
            string? gid = features[candidates[i].FeatIdx].CompoundGroupId;
            if (!string.IsNullOrWhiteSpace(gid))
            {
                if (firstByGroup.TryGetValue(gid!, out int prev))
                {
                    Union(prev, i);
                }
                else
                {
                    firstByGroup[gid!] = i;
                }
            }
        }

        for (int i = 0; i < m; i++)
        {
            double bestOuterArea = double.PositiveInfinity;
            int bestOuter = -1;

            for (int j = 0; j < m; j++)
            {
                if (i == j || candidates[j].Area <= candidates[i].Area + 1e-4)
                {
                    continue;
                }

                if (Find(i) == Find(j))
                {
                    continue;
                }

                if (candidates[j].Area < bestOuterArea &&
                    PolygonNestingGeometry.IsPolygonStrictlyContainedIn(
                        features[candidates[i].FeatIdx].Points,
                        features[candidates[j].FeatIdx].Points))
                {
                    bestOuterArea = candidates[j].Area;
                    bestOuter = j;
                }
            }

            if (bestOuter >= 0)
            {
                Union(bestOuter, i);
            }
        }

        var rootGroupId = new Dictionary<int, string>();
        for (int i = 0; i < m; i++)
        {
            int root = Find(i);
            string? existing = features[candidates[i].FeatIdx].CompoundGroupId;
            if (!rootGroupId.ContainsKey(root) && !string.IsNullOrWhiteSpace(existing))
            {
                rootGroupId[root] = existing!;
            }
        }

        for (int i = 0; i < m; i++)
        {
            int root = Find(i);
            if (!rootGroupId.TryGetValue(root, out string? resolvedId))
            {
                resolvedId = string.Format(CultureInfo.InvariantCulture, "compound_fill_{0}", candidates[root].FeatIdx);
                rootGroupId[root] = resolvedId;
            }

            int featIdx = candidates[i].FeatIdx;
            if (!string.Equals(features[featIdx].CompoundGroupId, resolvedId, StringComparison.Ordinal))
            {
                features[featIdx] = features[featIdx] with { CompoundGroupId = resolvedId };
            }
        }
    }

    public static NestingSemanticLayer ResolveCncOperationLayer(CncOperationType op) => op switch
    {
        CncOperationType.CutExternal => NestingSemanticLayer.CutExternal,
        CncOperationType.CutInternal => NestingSemanticLayer.CutInternal,
        CncOperationType.EngraveFill => NestingSemanticLayer.EngraveFill,
        CncOperationType.Mark => NestingSemanticLayer.Mark,
        CncOperationType.EngraveRaster => NestingSemanticLayer.EngraveRaster,
        _ => NestingSemanticLayer.EngraveVector,
    };

    private static double Dist((double X, double Y) a, (double X, double Y) b)
        => Math.Sqrt(DistSq(a, b));

    private static double DistSq((double X, double Y) a, (double X, double Y) b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static Point2D Mid(Point2D a, Point2D b) => new((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

    private static double PointLineDistance(Point2D pt, Point2D a, Point2D b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = (dx * dx) + (dy * dy);
        if (lenSq <= 1e-18)
        {
            return Math.Sqrt(((pt.X - a.X) * (pt.X - a.X)) + ((pt.Y - a.Y) * (pt.Y - a.Y)));
        }

        return Math.Abs((dy * pt.X) - (dx * pt.Y) + (b.X * a.Y) - (b.Y * a.X)) / Math.Sqrt(lenSq);
    }
}
