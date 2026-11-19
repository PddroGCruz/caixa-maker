using System.Globalization;
using CaixaMaker.Cam.Nesting;
using CaixaMaker.Core.Nesting;

namespace CaixaMaker.Cam.Dxf;

/// <summary>Resultado da importação de um arquivo ASCII DXF para peças de nesting 1:1 em mm.</summary>
public sealed record DxfImportResult(
    IReadOnlyList<NestingPart> Parts,
    IReadOnlyList<string> Warnings,
    double UnitScaleToMm,
    int RawEntityCount);

/// <summary>
/// Importador nativo de arquivos ASCII DXF (Autodesk Fusion, AutoCAD, LibreCAD, Inkscape, LightBurn e Caixa Maker)
/// com conversão de <c>$INSUNITS</c> para milímetros 1:1, costura automática de contornos abertos,
/// mapeamento semântico de layers/ACI e agrupamento hierárquico de furos e gravações.
/// </summary>
public sealed class DxfImporter
{
    private readonly record struct DxfVertex(double X, double Y, double Bulge);

    public DxfImportResult ImportFromFile(
        string filePath,
        bool keepAsSinglePart = false,
        NestingRotationPolicy rotationPolicy = NestingRotationPolicy.Step90)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Caminho do arquivo DXF não informado.", nameof(filePath));
        }

        string dxfText = File.ReadAllText(filePath);
        string name = Path.GetFileNameWithoutExtension(filePath);
        return ImportFromString(dxfText, name, keepAsSinglePart, rotationPolicy);
    }

    public DxfImportResult ImportFromString(
        string dxfContent,
        string partBaseName = "DXF",
        bool keepAsSinglePart = false,
        NestingRotationPolicy rotationPolicy = NestingRotationPolicy.Step90)
    {
        if (string.IsNullOrWhiteSpace(dxfContent))
        {
            throw new ArgumentException("Conteúdo DXF está vazio.", nameof(dxfContent));
        }

        List<(int Code, string Value)> pairs = ParseGroupPairs(dxfContent);
        var warnings = new List<string>();
        double unitScale = 1.0; // Default: mm
        var layerColors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rawPolylines = new List<NestingImportHelper.RawImportedPolyline>();
        int entityCount = 0;

        string currentSection = string.Empty;
        for (int i = 0; i < pairs.Count; i++)
        {
            (int code, string val) = pairs[i];
            if (code == 0 && string.Equals(val, "SECTION", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < pairs.Count && pairs[i + 1].Code == 2)
                {
                    currentSection = pairs[i + 1].Value.Trim().ToUpperInvariant();
                    i++;
                }

                continue;
            }

            if (code == 0 && string.Equals(val, "ENDSEC", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = string.Empty;
                continue;
            }

            if (currentSection == "HEADER")
            {
                if (code == 9 && string.Equals(val, "$INSUNITS", StringComparison.OrdinalIgnoreCase))
                {
                    for (int k = i + 1; k < Math.Min(i + 5, pairs.Count); k++)
                    {
                        if (pairs[k].Code == 70 && int.TryParse(pairs[k].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int units))
                        {
                            unitScale = ResolveInsUnitsScale(units, warnings);
                            i = k;
                            break;
                        }
                    }
                }
            }
            else if (currentSection == "TABLES")
            {
                if (code == 0 && string.Equals(val, "LAYER", StringComparison.OrdinalIgnoreCase))
                {
                    string layerName = string.Empty;
                    int aci = 7;
                    int next = i + 1;
                    while (next < pairs.Count && pairs[next].Code != 0)
                    {
                        if (pairs[next].Code == 2) layerName = pairs[next].Value.Trim();
                        else if (pairs[next].Code == 62 && int.TryParse(pairs[next].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c))
                        {
                            aci = Math.Abs(c);
                        }

                        next++;
                    }

                    if (!string.IsNullOrEmpty(layerName))
                    {
                        layerColors[layerName] = aci;
                    }

                    i = next - 1;
                }
            }
            else if (currentSection == "ENTITIES")
            {
                if (code != 0)
                {
                    continue;
                }

                string entityType = val.Trim().ToUpperInvariant();
                if (entityType is "SEQEND" or "ENDSEC")
                {
                    continue;
                }

                int nextIdx = i + 1;
                var entityTags = new List<(int Code, string Value)>();
                while (nextIdx < pairs.Count && pairs[nextIdx].Code != 0)
                {
                    entityTags.Add(pairs[nextIdx]);
                    nextIdx++;
                }

                if (entityType == "POLYLINE")
                {
                    int polyFlags = GetInt(entityTags, 70, 0);
                    string polyLayer = GetString(entityTags, 8, "0");
                    int? polyColor = TryGetInt(entityTags, 62);
                    var verts = new List<DxfVertex>();

                    int scan = nextIdx;
                    while (scan < pairs.Count)
                    {
                        if (pairs[scan].Code == 0)
                        {
                            string subType = pairs[scan].Value.Trim().ToUpperInvariant();
                            if (subType == "SEQEND")
                            {
                                scan++;
                                while (scan < pairs.Count && pairs[scan].Code != 0) scan++;
                                break;
                            }

                            if (subType == "VERTEX")
                            {
                                scan++;
                                double vx = 0, vy = 0, vbulge = 0;
                                while (scan < pairs.Count && pairs[scan].Code != 0)
                                {
                                    if (pairs[scan].Code == 10) vx = ParseDouble(pairs[scan].Value);
                                    else if (pairs[scan].Code == 20) vy = ParseDouble(pairs[scan].Value);
                                    else if (pairs[scan].Code == 42) vbulge = ParseDouble(pairs[scan].Value);
                                    scan++;
                                }

                                verts.Add(new DxfVertex(vx * unitScale, -vy * unitScale, vbulge));
                                continue;
                            }

                            break;
                        }

                        scan++;
                    }

                    entityCount++;
                    EmitPolylineWithBulge(verts, (polyFlags & 1) != 0, polyLayer, polyColor, layerColors, rawPolylines);
                    i = scan - 1;
                    continue;
                }

                entityCount++;
                ProcessSingleEntity(entityType, entityTags, unitScale, layerColors, rawPolylines, warnings);
                i = nextIdx - 1;
            }
        }

        // Caso não haja nenhuma entidade explicitamente de corte, promove loops fechados na layer padrão '0' para CutExternal
        bool hasAnyCutLayer = rawPolylines.Any(p => p.Layer is NestingSemanticLayer.CutExternal or NestingSemanticLayer.CutInternal);
        if (!hasAnyCutLayer)
        {
            for (int k = 0; k < rawPolylines.Count; k++)
            {
                if (rawPolylines[k].UsedDefaultZeroFallback)
                {
                    rawPolylines[k] = rawPolylines[k] with { Layer = NestingSemanticLayer.CutExternal };
                }
            }
        }

        List<NestingImportHelper.RawImportedPolyline> stitched = NestingImportHelper.StitchOpenPolylines(rawPolylines);
        IReadOnlyList<NestingPart> parts = NestingImportHelper.BuildHierarchicalParts(
            stitched,
            rasters: null,
            sourceTag: "DXF",
            namePrefix: string.IsNullOrWhiteSpace(partBaseName) ? "DXF" : partBaseName,
            keepAsSinglePart: keepAsSinglePart,
            rotationPolicy: rotationPolicy);

        return new DxfImportResult(parts, warnings, unitScale, entityCount);
    }

    private static void ProcessSingleEntity(
        string entityType,
        List<(int Code, string Value)> tags,
        double unitScale,
        Dictionary<string, int> layerColors,
        List<NestingImportHelper.RawImportedPolyline> output,
        List<string> warnings)
    {
        string layerName = GetString(tags, 8, "0");
        int? explicitColor = TryGetInt(tags, 62);
        (NestingSemanticLayer layer, bool zeroFallback) = ClassifyDxfEntityLayer(layerName, explicitColor, layerColors);

        switch (entityType)
        {
            case "LINE":
            {
                double x1 = GetDouble(tags, 10) * unitScale;
                double y1 = -GetDouble(tags, 20) * unitScale;
                double x2 = GetDouble(tags, 11) * unitScale;
                double y2 = -GetDouble(tags, 21) * unitScale;
                output.Add(new NestingImportHelper.RawImportedPolyline(
                    [(x1, y1), (x2, y2)],
                    IsClosed: false,
                    layer,
                    0.1,
                    layerName,
                    zeroFallback));
                break;
            }

            case "CIRCLE":
            {
                double cx = GetDouble(tags, 10) * unitScale;
                double cy = -GetDouble(tags, 20) * unitScale;
                double r = Math.Abs(GetDouble(tags, 40) * unitScale);
                if (r > 1e-4)
                {
                    IReadOnlyList<(double X, double Y)> pts = NestingImportHelper.DiscretizeCircle(cx, cy, r);
                    output.Add(new NestingImportHelper.RawImportedPolyline(
                        pts,
                        IsClosed: true,
                        layer,
                        0.03,
                        layerName,
                        zeroFallback));
                }
                break;
            }

            case "ARC":
            {
                double cx = GetDouble(tags, 10) * unitScale;
                double cy = -GetDouble(tags, 20) * unitScale;
                double r = Math.Abs(GetDouble(tags, 40) * unitScale);
                double startDeg = GetDouble(tags, 50);
                double endDeg = GetDouble(tags, 51);
                if (r > 1e-4)
                {
                    // Em DXF (+Y para cima), os ângulos crescem no sentido anti-horário; com Y invertido tornam-se negativos
                    double sweepDeg = ((endDeg - startDeg) % 360.0 + 360.0) % 360.0;
                    if (sweepDeg < 1e-6) sweepDeg = 360.0;
                    double startRad = -startDeg * (Math.PI / 180.0);
                    double deltaRad = -sweepDeg * (Math.PI / 180.0);

                    var pts = new List<(double X, double Y)>
                    {
                        (cx + (r * Math.Cos(startRad)), cy + (r * Math.Sin(startRad))),
                    };
                    NestingImportHelper.AppendArcPoints(pts, cx, cy, r, startRad, deltaRad);
                    output.Add(new NestingImportHelper.RawImportedPolyline(
                        pts,
                        IsClosed: sweepDeg >= 359.99,
                        layer,
                        0.1,
                        layerName,
                        zeroFallback));
                }
                break;
            }

            case "LWPOLYLINE":
            {
                int flags = GetInt(tags, 70, 0);
                bool isClosed = (flags & 1) != 0;
                var verts = new List<DxfVertex>();
                double curX = 0, curY = 0, curBulge = 0;
                bool hasX = false;

                foreach ((int c, string v) in tags)
                {
                    if (c == 10)
                    {
                        if (hasX)
                        {
                            verts.Add(new DxfVertex(curX, curY, curBulge));
                            curY = 0;
                            curBulge = 0;
                        }

                        curX = ParseDouble(v) * unitScale;
                        hasX = true;
                    }
                    else if (c == 20 && hasX)
                    {
                        curY = -ParseDouble(v) * unitScale;
                    }
                    else if (c == 42 && hasX)
                    {
                        curBulge = ParseDouble(v);
                    }
                }

                if (hasX)
                {
                    verts.Add(new DxfVertex(curX, curY, curBulge));
                }

                EmitPolylineWithBulge(verts, isClosed, layerName, explicitColor, layerColors, output);
                break;
            }

            case "ELLIPSE":
            {
                double cx = GetDouble(tags, 10) * unitScale;
                double cy = -GetDouble(tags, 20) * unitScale;
                double mx = GetDouble(tags, 11) * unitScale;
                double my = -GetDouble(tags, 21) * unitScale;
                double ratio = Math.Clamp(GetDouble(tags, 40, 1.0), 1e-4, 1.0);
                double pStart = GetDouble(tags, 41, 0.0);
                double pEnd = GetDouble(tags, 42, 2.0 * Math.PI);

                double a = Math.Sqrt((mx * mx) + (my * my));
                if (a > 1e-4)
                {
                    double b = a * ratio;
                    double axisAngle = Math.Atan2(my, mx);
                    double cosA = Math.Cos(axisAngle);
                    double sinA = Math.Sin(axisAngle);

                    double span = pEnd - pStart;
                    if (span <= 0) span += 2.0 * Math.PI;
                    bool closed = Math.Abs(span - (2.0 * Math.PI)) < 1e-3;

                    double maxR = Math.Max(a, b);
                    int segs = Math.Clamp((int)Math.Ceiling((span * maxR) / Math.Sqrt(maxR * 0.04)), 24, 128);
                    var pts = new List<(double X, double Y)>(segs + 1);
                    for (int s = 0; s <= (closed ? segs - 1 : segs); s++)
                    {
                        double t = pStart + ((span * s) / segs);
                        double lx = a * Math.Cos(t);
                        double ly = b * Math.Sin(t);
                        pts.Add((cx + (lx * cosA) - (ly * sinA), cy + (lx * sinA) + (ly * cosA)));
                    }

                    output.Add(new NestingImportHelper.RawImportedPolyline(
                        pts,
                        closed,
                        layer,
                        0.1,
                        layerName,
                        zeroFallback));
                }
                break;
            }

            case "SPLINE":
            {
                int flags = GetInt(tags, 70, 0);
                bool closed = (flags & 1) != 0;
                int degree = Math.Clamp(GetInt(tags, 71, 3), 1, 7);
                var knots = new List<double>();
                var controlPts = new List<(double X, double Y)>();
                var fitPts = new List<(double X, double Y)>();

                for (int t = 0; t < tags.Count; t++)
                {
                    int tc = tags[t].Code;
                    if (tc == 40)
                    {
                        knots.Add(ParseDouble(tags[t].Value));
                    }
                    else if (tc == 10)
                    {
                        double sx = ParseDouble(tags[t].Value) * unitScale;
                        double sy = (t + 1 < tags.Count && tags[t + 1].Code == 20) ? -ParseDouble(tags[++t].Value) * unitScale : 0.0;
                        controlPts.Add((sx, sy));
                    }
                    else if (tc == 11)
                    {
                        double fx = ParseDouble(tags[t].Value) * unitScale;
                        double fy = (t + 1 < tags.Count && tags[t + 1].Code == 20) ? -ParseDouble(tags[++t].Value) * unitScale : 0.0;
                        fitPts.Add((fx, fy));
                    }
                }

                List<(double X, double Y)>? splinePts = controlPts.Count >= 2
                    ? SampleBSpline(controlPts, knots, degree)
                    : (fitPts.Count >= 2 ? fitPts : null);

                if (splinePts is { Count: >= 2 })
                {
                    output.Add(new NestingImportHelper.RawImportedPolyline(
                        splinePts,
                        closed,
                        layer,
                        0.1,
                        layerName,
                        zeroFallback));
                }
                else
                {
                    warnings.Add($"SPLINE na camada '{layerName}' sem pontos de controle/ajuste suficientes.");
                }
                break;
            }
        }
    }

    private static void EmitPolylineWithBulge(
        List<DxfVertex> verts,
        bool isClosed,
        string layerName,
        int? explicitColor,
        Dictionary<string, int> layerColors,
        List<NestingImportHelper.RawImportedPolyline> output)
    {
        if (verts.Count < 2)
        {
            return;
        }

        (NestingSemanticLayer layer, bool zeroFallback) = ClassifyDxfEntityLayer(layerName, explicitColor, layerColors);
        var pts = new List<(double X, double Y)> { (verts[0].X, verts[0].Y) };

        int segCount = isClosed ? verts.Count : verts.Count - 1;
        for (int i = 0; i < segCount; i++)
        {
            DxfVertex a = verts[i];
            DxfVertex b = verts[(i + 1) % verts.Count];

            if (Math.Abs(a.Bulge) > 1e-6)
            {
                // Bulge = tan(theta / 4) em coordenadas pré-flip; como invertemos Y, invertemos o sinal do bulge para manter sentido geométrico
                AppendBulgeArc(pts, a.X, a.Y, b.X, b.Y, -a.Bulge);
            }
            else if (i < segCount - 1 || !isClosed)
            {
                pts.Add((b.X, b.Y));
            }
        }

        output.Add(new NestingImportHelper.RawImportedPolyline(
            pts,
            isClosed && pts.Count >= 3,
            layer,
            layer is NestingSemanticLayer.CutExternal or NestingSemanticLayer.CutInternal ? 0.03 : 0.1,
            layerName,
            zeroFallback));
    }

    private static void AppendBulgeArc(
        List<(double X, double Y)> target,
        double x1,
        double y1,
        double x2,
        double y2,
        double bulge)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double chord = Math.Sqrt((dx * dx) + (dy * dy));
        if (chord < 1e-6)
        {
            return;
        }

        double theta = 4.0 * Math.Atan(bulge);
        double sinHalf = Math.Sin(Math.Abs(theta) * 0.5);
        if (sinHalf < 1e-8)
        {
            target.Add((x2, y2));
            return;
        }

        double r = (chord * 0.5) / sinHalf;
        double sagittaOffset = (chord * 0.5) / Math.Tan(theta * 0.5);
        double mx = (x1 + x2) * 0.5;
        double my = (y1 + y2) * 0.5;
        double nx = -dy / chord;
        double ny = dx / chord;
        double cx = mx + (nx * sagittaOffset);
        double cy = my + (ny * sagittaOffset);

        double startRad = Math.Atan2(y1 - cy, x1 - cx);
        NestingImportHelper.AppendArcPoints(target, cx, cy, r, startRad, theta);
        if (target.Count > 0)
        {
            target[^1] = (x2, y2);
        }
    }

    private static List<(double X, double Y)> SampleBSpline(
        List<(double X, double Y)> ctrl,
        List<double> knots,
        int degree)
    {
        int n = ctrl.Count - 1;
        int p = Math.Min(degree, n);

        double[] knotVec;
        if (knots.Count == n + p + 2)
        {
            knotVec = knots.ToArray();
        }
        else
        {
            // Vetor clamped uniforme caso DXF omita knots explícitos
            int m = n + p + 1;
            knotVec = new double[m + 1];
            for (int i = 0; i <= m; i++)
            {
                if (i <= p) knotVec[i] = 0.0;
                else if (i >= m - p) knotVec[i] = 1.0;
                else knotVec[i] = (double)(i - p) / (m - (2 * p));
            }
        }

        double tMin = knotVec[p];
        double tMax = knotVec[n + 1];
        if (tMax <= tMin)
        {
            return ctrl;
        }

        double estLen = 0.0;
        for (int i = 1; i < ctrl.Count; i++)
        {
            double dx = ctrl[i].X - ctrl[i - 1].X;
            double dy = ctrl[i].Y - ctrl[i - 1].Y;
            estLen += Math.Sqrt((dx * dx) + (dy * dy));
        }

        int samples = Math.Clamp((int)Math.Ceiling(estLen / 0.25), 16, 256);
        var result = new List<(double X, double Y)>(samples + 1);
        for (int s = 0; s <= samples; s++)
        {
            double t = s == samples ? tMax - 1e-9 : tMin + (((tMax - tMin) * s) / samples);
            if (s == 0) t = tMin;
            result.Add(EvaluateDeBoor(ctrl, knotVec, p, t, s == samples));
        }

        return result;
    }

    private static (double X, double Y) EvaluateDeBoor(
        List<(double X, double Y)> ctrl,
        double[] knots,
        int p,
        double t,
        bool isLastSample)
    {
        if (isLastSample)
        {
            return ctrl[^1];
        }

        int n = ctrl.Count - 1;
        int k = p;
        for (int i = p; i <= n; i++)
        {
            if (t >= knots[i] && t < knots[i + 1])
            {
                k = i;
                break;
            }
        }

        var d = new (double X, double Y)[p + 1];
        for (int j = 0; j <= p; j++)
        {
            int idx = Math.Clamp(k - p + j, 0, n);
            d[j] = ctrl[idx];
        }

        for (int r = 1; r <= p; r++)
        {
            for (int j = p; j >= r; j--)
            {
                int iLeft = k - p + j;
                double denom = knots[iLeft + 1 + p - r] - knots[iLeft];
                double alpha = Math.Abs(denom) <= 1e-12 ? 0.0 : (t - knots[iLeft]) / denom;
                d[j] = (
                    ((1.0 - alpha) * d[j - 1].X) + (alpha * d[j].X),
                    ((1.0 - alpha) * d[j - 1].Y) + (alpha * d[j].Y));
            }
        }

        return d[p];
    }

    private static (NestingSemanticLayer Layer, bool UsedDefaultZeroFallback) ClassifyDxfEntityLayer(
        string layerName,
        int? explicitColor,
        Dictionary<string, int> layerColors)
    {
        string token = layerName.Trim().ToUpperInvariant();

        if (token == "CUT_EXTERNAL" || token.Contains("CUT_EXTERNAL", StringComparison.Ordinal))
        {
            return (NestingSemanticLayer.CutExternal, false);
        }

        if (token == "CUT_INTERNAL" || token.Contains("CUT_INTERNAL", StringComparison.Ordinal) ||
            token.Contains("HOLE", StringComparison.Ordinal) || token.Contains("FURO", StringComparison.Ordinal))
        {
            return (NestingSemanticLayer.CutInternal, false);
        }

        if (token.Contains("ENGRAVE_FILL", StringComparison.Ordinal) || token.Contains("HATCH", StringComparison.Ordinal))
        {
            return (NestingSemanticLayer.EngraveFill, false);
        }

        if (token.Contains("ENGRAVE", StringComparison.Ordinal) || token.Contains("GRAV", StringComparison.Ordinal) ||
            token.Contains("ETCH", StringComparison.Ordinal) || token.Contains("TEXT", StringComparison.Ordinal))
        {
            return (NestingSemanticLayer.EngraveVector, false);
        }

        if (token.Contains("MARK", StringComparison.Ordinal) || token.Contains("SCORE", StringComparison.Ordinal))
        {
            return (NestingSemanticLayer.Mark, false);
        }

        if (token.Contains("CUT", StringComparison.Ordinal) || token.Contains("CORTE", StringComparison.Ordinal) ||
            token.Contains("OUTER", StringComparison.Ordinal))
        {
            return (NestingSemanticLayer.CutExternal, false);
        }

        // Semântica por cor ACI quando não há nome explícito
        int aci = explicitColor ?? (layerColors.TryGetValue(layerName, out int lc) ? lc : 7);
        return aci switch
        {
            1 or 2 => (NestingSemanticLayer.CutExternal, false),
            5 or 4 => (NestingSemanticLayer.EngraveVector, false),
            3 => (NestingSemanticLayer.Mark, false),
            6 => (NestingSemanticLayer.EngraveFill, false),
            7 => (NestingSemanticLayer.Mark, explicitColor is null),
            _ => (NestingSemanticLayer.CutExternal, true),
        };
    }

    private static double ResolveInsUnitsScale(int insUnits, List<string> warnings) => insUnits switch
    {
        0 => 1.0,
        1 => 25.4,
        2 => 304.8,
        3 => 1_609_344.0,
        4 => 1.0,
        5 => 10.0,
        6 => 1000.0,
        _ => AddUnknownUnitWarning(insUnits, warnings),
    };

    private static double AddUnknownUnitWarning(int insUnits, List<string> warnings)
    {
        warnings.Add($"Código $INSUNITS={insUnits} não mapeado; assumindo escala 1:1 em milímetros.");
        return 1.0;
    }

    private static List<(int Code, string Value)> ParseGroupPairs(string dxfContent)
    {
        var pairs = new List<(int Code, string Value)>();
        using var reader = new StringReader(dxfContent);
        while (true)
        {
            string? codeLine = reader.ReadLine();
            string? valLine = reader.ReadLine();
            if (codeLine is null || valLine is null)
            {
                break;
            }

            if (int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                pairs.Add((code, valLine.Trim()));
            }
        }

        return pairs;
    }

    private static string GetString(List<(int Code, string Value)> tags, int code, string fallback)
    {
        for (int i = 0; i < tags.Count; i++)
        {
            if (tags[i].Code == code)
            {
                return tags[i].Value;
            }
        }

        return fallback;
    }

    private static int GetInt(List<(int Code, string Value)> tags, int code, int fallback = 0)
        => TryGetInt(tags, code) ?? fallback;

    private static int? TryGetInt(List<(int Code, string Value)> tags, int code)
    {
        for (int i = 0; i < tags.Count; i++)
        {
            if (tags[i].Code == code && int.TryParse(tags[i].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            {
                return v;
            }
        }

        return null;
    }

    private static double GetDouble(List<(int Code, string Value)> tags, int code, double fallback = 0.0)
    {
        for (int i = 0; i < tags.Count; i++)
        {
            if (tags[i].Code == code)
            {
                return ParseDouble(tags[i].Value, fallback);
            }
        }

        return fallback;
    }

    private static double ParseDouble(string raw, double fallback = 0.0)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
}
