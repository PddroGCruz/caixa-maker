using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;

namespace CaixaMaker.Cam.Svg;

/// <summary>Resultado da importação e normalização de um arquivo SVG para milímetros.</summary>
public sealed record SvgImportResult(
    VectorSvgPersonalizationElement Element,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Importador e sanitizador seguro de SVGs externos, prevenindo XXE/XSS,
/// aplicando escala CSS 96 PPI para milímetros (25.4/96.0) e achatando transformações.
/// </summary>
public sealed class SvgImporter
{
    public const int MaxSvgBytes = 10 * 1024 * 1024;
    public const double MmPerPx = SvgDocumentParser.MmPerPx;

    private static readonly HashSet<string> DangerousTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script",
        "foreignobject",
        "iframe",
        "object",
        "embed",
        "audio",
        "video",
        "animate",
        "animatemotion",
        "animatetransform",
        "set",
        "mpath",
    };

    private static readonly Regex EntityDeclarationRegex = new(
        @"<!ENTITY\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DoctypeRegex = new(
        @"<!DOCTYPE\b[^>]*?(?:\[[\s\S]*?\]\s*)?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExternalUriRegex = new(
        @"^\s*(?:https?:|file:|ftp:|javascript:|vbscript:|//)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CssUrlRegex = new(
        @"url\s*\([^)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CssImportRegex = new(
        @"@import\b[^;]*;?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public (string SanitizedXml, IReadOnlyList<string> Warnings) SanitizeSvgXml(string rawSvg)
    {
        if (string.IsNullOrWhiteSpace(rawSvg))
        {
            throw new ArgumentException("O conteúdo SVG está vazio.", nameof(rawSvg));
        }

        int byteCount = Encoding.UTF8.GetByteCount(rawSvg);
        if (byteCount > MaxSvgBytes)
        {
            throw new ArgumentException(
                $"O arquivo SVG excede o limite máximo permitido de {MaxSvgBytes / (1024 * 1024)} MB.",
                nameof(rawSvg));
        }

        if (EntityDeclarationRegex.IsMatch(rawSvg))
        {
            throw new InvalidDataException(
                "Declarações <!ENTITY> não são permitidas em arquivos SVG por motivo de segurança (XXE).");
        }

        var warnings = new List<string>();
        string withoutDoctype = DoctypeRegex.Replace(rawSvg, match =>
        {
            warnings.Add("Declaração DOCTYPE removida por segurança.");
            return string.Empty;
        });

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = false,
            MaxCharactersInDocument = 25_000_000,
            MaxCharactersFromEntities = 0,
        };

        XDocument doc;
        try
        {
            using var sr = new StringReader(withoutDoctype);
            using var xr = XmlReader.Create(sr, settings);
            doc = XDocument.Load(xr, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"XML inválido ao analisar SVG: {ex.Message}", ex);
        }

        XElement root = doc.Root ?? throw new InvalidDataException("SVG sem elemento raiz.");
        if (!string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Elemento raiz inválido '{root.Name.LocalName}'. Esperado <svg>.");
        }

        // Remove tags perigosas
        List<XElement> toRemove = root.Descendants()
            .Where(el => DangerousTags.Contains(el.Name.LocalName))
            .ToList();
        foreach (XElement badEl in toRemove)
        {
            string tag = badEl.Name.LocalName.ToLowerInvariant();
            badEl.Remove();
            warnings.Add($"Tag potencialmente insegura <{tag}> removida durante sanitização.");
        }

        // Remove eventos XSS, URIs externas e @import em atributos e <style>
        foreach (XElement el in root.DescendantsAndSelf())
        {
            if (string.Equals(el.Name.LocalName, "style", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(el.Value))
            {
                string cleanedStyleBlock = CleanCssContent(el.Value, warnings);
                if (cleanedStyleBlock != el.Value)
                {
                    el.Value = cleanedStyleBlock;
                }
            }

            List<XAttribute> attrs = el.Attributes().ToList();
            foreach (XAttribute attr in attrs)
            {
                string localName = attr.Name.LocalName.ToLowerInvariant();
                if (localName.StartsWith("on", StringComparison.Ordinal))
                {
                    attr.Remove();
                    warnings.Add($"Atributo de evento '{attr.Name.LocalName}' removido por segurança.");
                    continue;
                }

                if (localName is "href" or "src" or "xlink:href")
                {
                    if (ExternalUriRegex.IsMatch(attr.Value))
                    {
                        attr.Remove();
                        warnings.Add($"Referência externa em '{attr.Name.LocalName}' removida por segurança.");
                        continue;
                    }
                }

                if (localName == "style")
                {
                    string cleanedInline = CleanCssContent(attr.Value, warnings);
                    if (cleanedInline != attr.Value)
                    {
                        attr.Value = cleanedInline;
                    }
                }
            }
        }

        return (doc.ToString(SaveOptions.DisableFormatting), warnings);
    }

    public SvgImportResult ImportFromFile(
        string filePath,
        SurfaceId panelId = SurfaceId.Front,
        double? targetWidthMm = null,
        double? targetHeightMm = null,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo SVG não encontrado.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > MaxSvgBytes)
        {
            throw new ArgumentException(
                $"O arquivo SVG '{Path.GetFileName(filePath)}' excede o limite de {MaxSvgBytes / (1024 * 1024)} MB.",
                nameof(filePath));
        }

        string xml = File.ReadAllText(filePath, Encoding.UTF8);
        return ImportFromString(
            svgXml: xml,
            sourceFilename: Path.GetFileName(filePath),
            panelId: panelId,
            targetWidthMm: targetWidthMm,
            targetHeightMm: targetHeightMm,
            name: name);
    }

    public SvgImportResult ImportFromString(
        string svgXml,
        string? sourceFilename = null,
        SurfaceId panelId = SurfaceId.Front,
        double? targetWidthMm = null,
        double? targetHeightMm = null,
        string? name = null)
    {
        (string sanitizedXml, IReadOnlyList<string> securityWarnings) = SanitizeSvgXml(svgXml);
        var warnings = new List<string>(securityWarnings);

        ParsedSvgDocument parsed = SvgDocumentParser.Parse(sanitizedXml);
        if (parsed.HadTextElements)
        {
            warnings.Add("Elementos <text> nativos encontrados no SVG foram ignorados; converta texto em curvas para gravar/cortar.");
        }

        if (parsed.Shapes.Count == 0)
        {
            throw new InvalidDataException("O arquivo SVG não contém elementos vetoriais visíveis.");
        }

        // Calcular bounding box real dos segmentos normalizados
        BoundingBox2D bbox = ComputeSegmentsBounds(parsed.Shapes.SelectMany(s => s.Segments));
        double rawW = Math.Max(bbox.Width, 0.01);
        double rawH = Math.Max(bbox.Height, 0.01);

        double origW = parsed.WidthMm > 0.0 ? parsed.WidthMm : rawW;
        double origH = parsed.HeightMm > 0.0 ? parsed.HeightMm : rawH;

        // Normaliza translação para (0,0) e escala se dimensões-alvo forem informadas mantendo razão de aspecto
        (double outW, double outH, double sx, double sy) = ComputeTargetScale(rawW, rawH, targetWidthMm, targetHeightMm);
        AffineMatrix2D normalize = AffineMatrix2D.CreateScale(sx, sy)
            .Multiply(AffineMatrix2D.CreateTranslation(-bbox.MinX, -bbox.MinY));

        var importedPaths = new List<VectorPathItem>(parsed.Shapes.Count);
        foreach (SvgShapeElement shape in parsed.Shapes)
        {
            List<SvgPathSegment> shifted = TransformForNormalize(shape.Segments, normalize);
            string d = SvgDocumentParser.SegmentsToSvgPathData(shifted);
            if (string.IsNullOrEmpty(d))
            {
                continue;
            }

            CncOperationType op = ClassifyShapeOperation(shape);
            bool isClosed = shape.Segments.Any(s => s.Kind == SvgSegmentKind.Close);
            importedPaths.Add(new VectorPathItem
            {
                D = d,
                Operation = op,
                IsClosed = isClosed,
                HasFill = shape.HasFill,
                HasStroke = shape.HasStroke,
                LayerName = shape.DataLayer ?? (!string.IsNullOrWhiteSpace(shape.ElementId) ? shape.ElementId : null),
            });
        }

        if (importedPaths.Count == 0)
        {
            throw new InvalidDataException("Nenhum caminho vetorial válido pôde ser extraído do SVG.");
        }

        CncOperationType dominantOp = DetermineDominantOperation(importedPaths);
        string resolvedName = !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : !string.IsNullOrWhiteSpace(sourceFilename)
                ? Path.GetFileNameWithoutExtension(sourceFilename)
                : "SVG Importado";

        string retainedSvgText = securityWarnings.Count > 0 ? sanitizedXml : svgXml;
        var element = new VectorSvgPersonalizationElement
        {
            Name = resolvedName,
            PanelId = panelId,
            SourceFilename = sourceFilename,
            OriginalFileName = sourceFilename,
            RawSvgText = retainedSvgText,
            SourceUnit = parsed.SourceUnit,
            OriginalWidthMm = Round4(origW),
            OriginalHeightMm = Round4(origH),
            BaseWidthMm = Round4(outW),
            BaseHeightMm = Round4(outH),
            WidthMm = Round4(outW),
            HeightMm = Round4(outH),
            Operation = dominantOp,
            Paths = importedPaths,
            Warnings = warnings,
        };

        return new SvgImportResult(element, warnings);
    }

    private static string CleanCssContent(string css, List<string> warnings)
    {
        string result = CssImportRegex.Replace(css, _ =>
        {
            warnings.Add("Regra CSS @import removida por segurança.");
            return string.Empty;
        });

        result = CssUrlRegex.Replace(result, match =>
        {
            if (match.Value.Contains('#', StringComparison.Ordinal))
            {
                return match.Value;
            }

            warnings.Add("Referência CSS url(...) externa removida por segurança.");
            return "none";
        });

        return result;
    }

    private static (double WidthMm, double HeightMm, double ScaleX, double ScaleY) ComputeTargetScale(
        double rawW,
        double rawH,
        double? targetW,
        double? targetH)
    {
        if (!targetW.HasValue && !targetH.HasValue)
        {
            return (rawW, rawH, 1.0, 1.0);
        }

        double aspect = rawW / Math.Max(rawH, 1e-9);
        if (targetW.HasValue && !targetH.HasValue)
        {
            double w = Math.Max(0.01, targetW.Value);
            double s = w / rawW;
            return (w, rawH * s, s, s);
        }

        if (!targetW.HasValue && targetH.HasValue)
        {
            double h = Math.Max(0.01, targetH.Value);
            double s = h / rawH;
            return (rawW * s, h, s, s);
        }

        double tw = Math.Max(0.01, targetW!.Value);
        double th = Math.Max(0.01, targetH!.Value);
        double scale = Math.Min(tw / rawW, th / rawH);
        double fittedW = rawW * scale;
        double fittedH = rawH * scale;
        _ = aspect;
        return (fittedW, fittedH, scale, scale);
    }

    private static CncOperationType ClassifyShapeOperation(SvgShapeElement shape)
    {
        if (TryMapLayerToken(shape.DataLayer, out CncOperationType op) ||
            TryMapLayerToken(shape.ElementId, out op))
        {
            return op;
        }

        if (shape.HasFill && !shape.HasStroke)
        {
            return CncOperationType.EngraveFill;
        }

        return CncOperationType.EngraveVector;
    }

    private static bool TryMapLayerToken(string? token, out CncOperationType op)
    {
        op = CncOperationType.EngraveVector;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string lower = token.Trim().ToLowerInvariant();
        if (lower.Contains("cut_internal", StringComparison.Ordinal))
        {
            op = CncOperationType.CutInternal;
            return true;
        }

        if (lower.Contains("cut_external", StringComparison.Ordinal))
        {
            op = CncOperationType.CutExternal;
            return true;
        }

        if (lower.Contains("engrave_fill", StringComparison.Ordinal))
        {
            op = CncOperationType.EngraveFill;
            return true;
        }

        if (lower.Contains("mark", StringComparison.Ordinal))
        {
            op = CncOperationType.Mark;
            return true;
        }

        if (lower.Contains("engrave_vector", StringComparison.Ordinal))
        {
            op = CncOperationType.EngraveVector;
            return true;
        }

        return false;
    }

    private static CncOperationType DetermineDominantOperation(IReadOnlyList<VectorPathItem> paths)
    {
        if (paths.All(p => p.Operation == CncOperationType.EngraveFill))
        {
            return CncOperationType.EngraveFill;
        }

        return paths[0].Operation;
    }

    private static List<SvgPathSegment> TransformForNormalize(IReadOnlyList<SvgPathSegment> segs, AffineMatrix2D matrix)
    {
        if (matrix.IsIdentity)
        {
            return segs.ToList();
        }

        var result = new List<SvgPathSegment>(segs.Count);
        foreach (SvgPathSegment seg in segs)
        {
            if (seg.Kind == SvgSegmentKind.Arc)
            {
                if (matrix.TryGetUniformScale(out double scale))
                {
                    result.Add(seg with
                    {
                        Start = matrix.Transform(seg.Start),
                        End = matrix.Transform(seg.End),
                        Rx = seg.Rx * scale,
                        Ry = seg.Ry * scale,
                        ArcCenter = matrix.Transform(seg.ArcCenter),
                    });
                }
                else
                {
                    foreach (SvgPathSegment cubic in SvgDocumentParser.ArcToCubicBeziers(seg))
                    {
                        result.Add(cubic with
                        {
                            Start = matrix.Transform(cubic.Start),
                            End = matrix.Transform(cubic.End),
                            Control1 = matrix.Transform(cubic.Control1),
                            Control2 = matrix.Transform(cubic.Control2),
                        });
                    }
                }
            }
            else
            {
                result.Add(seg with
                {
                    Start = matrix.Transform(seg.Start),
                    End = matrix.Transform(seg.End),
                    Control1 = matrix.Transform(seg.Control1),
                    Control2 = matrix.Transform(seg.Control2),
                });
            }
        }

        return result;
    }

    private static BoundingBox2D ComputeSegmentsBounds(IEnumerable<SvgPathSegment> segments)
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        void IncludePoint(Point2D p)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        foreach (SvgPathSegment seg in segments)
        {
            switch (seg.Kind)
            {
                case SvgSegmentKind.Move:
                case SvgSegmentKind.Line:
                case SvgSegmentKind.Close:
                    IncludePoint(seg.Start);
                    IncludePoint(seg.End);
                    break;
                case SvgSegmentKind.Quad:
                    for (int i = 0; i <= 16; i++)
                    {
                        IncludePoint(SvgDocumentParser.EvaluateQuad(seg.Start, seg.Control1, seg.End, i / 16.0));
                    }
                    break;
                case SvgSegmentKind.Cubic:
                    for (int i = 0; i <= 16; i++)
                    {
                        IncludePoint(SvgDocumentParser.EvaluateCubic(seg.Start, seg.Control1, seg.Control2, seg.End, i / 16.0));
                    }
                    break;
                case SvgSegmentKind.Arc:
                    foreach (SvgPathSegment cubic in SvgDocumentParser.ArcToCubicBeziers(seg))
                    {
                        for (int i = 0; i <= 16; i++)
                        {
                            IncludePoint(SvgDocumentParser.EvaluateCubic(cubic.Start, cubic.Control1, cubic.Control2, cubic.End, i / 16.0));
                        }
                    }
                    break;
            }
        }

        if (!double.IsFinite(minX))
        {
            return new BoundingBox2D(0.0, 0.0, 0.01, 0.01);
        }

        return new BoundingBox2D(minX, minY, maxX, maxY);
    }

    private static double Round4(double value) => Math.Round(value, 4, MidpointRounding.ToEven);
}
