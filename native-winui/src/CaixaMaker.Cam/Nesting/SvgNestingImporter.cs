using System.Globalization;
using System.Text.RegularExpressions;
using CaixaMaker.Cam.Svg;
using CaixaMaker.Core.Nesting;

namespace CaixaMaker.Cam.Nesting;

/// <summary>Resultado da importação de um SVG externo para peças de nesting 1:1 em milímetros.</summary>
public sealed record SvgNestingImportResult(
    IReadOnlyList<NestingPart> Parts,
    IReadOnlyList<string> Warnings,
    double DocumentWidthMm,
    double DocumentHeightMm,
    bool HadTextElements);

/// <summary>
/// Importador de arquivos SVG externos em escala 1:1 (<c>mm</c>, <c>cm</c>, <c>in</c>, <c>pt</c>, <c>px</c> 96 DPI)
/// para a fila do motor de nesting 2D, reaproveitando o sanitizador e parser vetorial em <see cref="SvgDocumentParser"/>
/// e aplicando agrupamento hierárquico automático de peças, furos internos e gravações.
/// </summary>
public sealed class SvgNestingImporter
{
    private static readonly Regex RgbRegex = new(
        @"rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public SvgNestingImportResult ImportFromFile(
        string filePath,
        bool keepAsSinglePart = false,
        NestingRotationPolicy rotationPolicy = NestingRotationPolicy.Step90)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Caminho do arquivo SVG não informado.", nameof(filePath));
        }

        string rawSvg = File.ReadAllText(filePath);
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        return ImportFromString(rawSvg, baseName, keepAsSinglePart, rotationPolicy);
    }

    public SvgNestingImportResult ImportFromString(
        string rawSvg,
        string partBaseName = "SVG",
        bool keepAsSinglePart = false,
        NestingRotationPolicy rotationPolicy = NestingRotationPolicy.Step90)
    {
        var sanitizer = new SvgImporter();
        (string sanitizedXml, IReadOnlyList<string> sanitizeWarnings) = sanitizer.SanitizeSvgXml(rawSvg);
        var warnings = new List<string>(sanitizeWarnings);

        ParsedSvgDocument parsed = SvgDocumentParser.Parse(sanitizedXml);
        if (parsed.HadTextElements)
        {
            warnings.Add("Elementos <text> no SVG externo podem exigir conversão prévia em curvas (Path) no software de origem.");
        }

        var rawPolylines = new List<NestingImportHelper.RawImportedPolyline>();
        int shapeIdx = 0;
        foreach (SvgShapeElement shape in parsed.Shapes)
        {
            shapeIdx++;
            IReadOnlyList<SvgSubpath> subpaths = SvgDocumentParser.SplitSubpaths(shape.Segments);
            if (subpaths.Count == 0)
            {
                continue;
            }

            NestingSemanticLayer layer = ClassifySvgShapeLayer(shape, subpaths.Count);
            double strokeWidthMm = shape.StrokeWidthMm ?? (layer is NestingSemanticLayer.CutExternal or NestingSemanticLayer.CutInternal ? 0.03 : 0.1);
            string? sourceLayerName = !string.IsNullOrWhiteSpace(shape.AncestorLayerId) ? shape.AncestorLayerId : shape.DataLayer;
            bool isFilled = layer == NestingSemanticLayer.EngraveFill && shape.HasFill;
            string? compoundGroupId = isFilled
                ? string.Format(CultureInfo.InvariantCulture, "svg_shape_{0}", shapeIdx)
                : null;

            foreach (SvgSubpath sub in subpaths)
            {
                List<(double X, double Y)> pts = NestingImportHelper.FlattenSvgSubpath(sub);
                if (pts.Count < 2)
                {
                    continue;
                }

                bool isClosed = sub.IsClosed && pts.Count >= 3;
                rawPolylines.Add(new NestingImportHelper.RawImportedPolyline(
                    pts,
                    isClosed,
                    layer,
                    strokeWidthMm,
                    sourceLayerName,
                    UsedDefaultZeroFallback: false,
                    CompoundGroupId: isClosed ? compoundGroupId : null,
                    IsFilled: isFilled && isClosed));
            }
        }

        var rasters = new List<NestingRasterOverlay>(parsed.Images.Count);
        foreach (SvgRasterImageElement img in parsed.Images)
        {
            double cx = (img.TopLeftMm.X + img.BottomRightMm.X) * 0.5;
            double cy = (img.TopLeftMm.Y + img.BottomRightMm.Y) * 0.5;
            double rotDeg = Math.Atan2(img.BottomRightMm.Y - img.BottomLeftMm.Y, img.BottomRightMm.X - img.BottomLeftMm.X) * (180.0 / Math.PI);
            rasters.Add(new NestingRasterOverlay(
                cx - (img.WidthMm * 0.5),
                cy - (img.HeightMm * 0.5),
                img.WidthMm,
                img.HeightMm,
                rotDeg,
                img.ImageBytes,
                img.ElementId));
        }

        List<NestingImportHelper.RawImportedPolyline> stitched = NestingImportHelper.StitchOpenPolylines(rawPolylines);
        IReadOnlyList<NestingPart> parts = NestingImportHelper.BuildHierarchicalParts(
            stitched,
            rasters,
            sourceTag: "SVG",
            namePrefix: string.IsNullOrWhiteSpace(partBaseName) ? "SVG" : partBaseName,
            keepAsSinglePart: keepAsSinglePart,
            rotationPolicy: rotationPolicy);

        return new SvgNestingImportResult(
            parts,
            warnings,
            parsed.WidthMm,
            parsed.HeightMm,
            parsed.HadTextElements);
    }

    private static NestingSemanticLayer ClassifySvgShapeLayer(SvgShapeElement shape, int subpathCount)
    {
        if (TryParseColorRgb(shape.StrokeColor, out int sr, out int sg, out int sb))
        {
            string hex = $"#{sr:X2}{sg:X2}{sb:X2}";
            if (hex == "#B71C1C") return NestingSemanticLayer.CutInternal;
            if (hex == "#0D47A1") return NestingSemanticLayer.EngraveVector;
            if (hex == "#6A1B9A") return NestingSemanticLayer.EngraveFill;
            if (hex == "#1B5E20") return NestingSemanticLayer.Mark;
        }

        if (TryParseColorRgb(shape.FillColor, out int fr, out int fg, out int fb) &&
            $"#{fr:X2}{fg:X2}{fb:X2}" == "#6A1B9A")
        {
            return NestingSemanticLayer.EngraveFill;
        }

        string token = $"{shape.DataLayer} {shape.ElementId} {shape.AncestorLayerId}".ToLowerInvariant();
        if (token.Contains("cut_internal", StringComparison.Ordinal)) return NestingSemanticLayer.CutInternal;
        if (token.Contains("cut_external", StringComparison.Ordinal)) return NestingSemanticLayer.CutExternal;
        if (token.Contains("engrave_fill", StringComparison.Ordinal)) return NestingSemanticLayer.EngraveFill;
        if (token.Contains("mark", StringComparison.Ordinal)) return NestingSemanticLayer.Mark;
        if (token.Contains("engrave", StringComparison.Ordinal) || token.Contains("grav", StringComparison.Ordinal)) return NestingSemanticLayer.EngraveVector;

        if (shape.HasFill && !shape.HasStroke)
        {
            if (TryParseColorRgb(shape.FillColor, out int r, out int g, out int b))
            {
                NestingSemanticLayer byFill = ClassifyRgb(r, g, b, NestingSemanticLayer.EngraveFill);
                return byFill == NestingSemanticLayer.CutExternal ? NestingSemanticLayer.EngraveFill : byFill;
            }

            return NestingSemanticLayer.EngraveFill;
        }

        if (TryParseColorRgb(shape.StrokeColor, out int strR, out int strG, out int strB))
        {
            return ClassifyRgb(strR, strG, strB, NestingSemanticLayer.CutExternal);
        }

        if (string.Equals(shape.TagName, "circle", StringComparison.OrdinalIgnoreCase) || subpathCount > 1)
        {
            return NestingSemanticLayer.CutInternal;
        }

        return NestingSemanticLayer.CutExternal;
    }

    private static NestingSemanticLayer ClassifyRgb(int r, int g, int b, NestingSemanticLayer fallback)
    {
        if (Math.Max(r, Math.Max(g, b)) < 70 && Math.Abs(r - g) < 25 && Math.Abs(g - b) < 25)
        {
            return NestingSemanticLayer.CutExternal;
        }

        if (r > g + 50 && r > b + 50)
        {
            return NestingSemanticLayer.CutInternal;
        }

        if (r > g + 35 && b > g + 35)
        {
            return NestingSemanticLayer.EngraveFill;
        }

        if (b > r + 40 && b > g + 30)
        {
            return NestingSemanticLayer.EngraveVector;
        }

        if (g > r + 35 && g > b + 35)
        {
            return NestingSemanticLayer.Mark;
        }

        return fallback;
    }

    private static bool TryParseColorRgb(string? raw, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string t = raw.Trim().ToLowerInvariant();
        switch (t)
        {
            case "black": r = 0; g = 0; b = 0; return true;
            case "white": r = 255; g = 255; b = 255; return true;
            case "red": r = 255; g = 0; b = 0; return true;
            case "blue": r = 0; g = 0; b = 255; return true;
            case "green": r = 0; g = 128; b = 0; return true;
            case "purple": r = 128; g = 0; b = 128; return true;
            case "magenta": r = 255; g = 0; b = 255; return true;
        }

        if (t.StartsWith('#'))
        {
            string hex = t[1..];
            if (hex.Length == 3 &&
                int.TryParse(new string(hex[0], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                int.TryParse(new string(hex[1], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                int.TryParse(new string(hex[2], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return true;
            }

            if (hex.Length is 6 or 8 &&
                int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                int.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                int.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return true;
            }
        }

        Match m = RgbRegex.Match(t);
        if (m.Success)
        {
            r = Math.Clamp(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0, 255);
            g = Math.Clamp(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), 0, 255);
            b = Math.Clamp(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), 0, 255);
            return true;
        }

        return false;
    }
}
