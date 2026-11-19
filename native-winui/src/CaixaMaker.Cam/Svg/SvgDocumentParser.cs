using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CaixaMaker.Core.Geometry;

namespace CaixaMaker.Cam.Svg;

/// <summary>Matriz afim 2D (linha-major simplificada compatível com SVG matrix(a b c d e f)).</summary>
public readonly record struct AffineMatrix2D(double A, double B, double C, double D, double E, double F)
{
    public static AffineMatrix2D Identity => new(1.0, 0.0, 0.0, 1.0, 0.0, 0.0);

    public bool IsIdentity =>
        Math.Abs(A - 1.0) < 1e-12 &&
        Math.Abs(B) < 1e-12 &&
        Math.Abs(C) < 1e-12 &&
        Math.Abs(D - 1.0) < 1e-12 &&
        Math.Abs(E) < 1e-12 &&
        Math.Abs(F) < 1e-12;

    public double Determinant => (A * D) - (B * C);

    public Point2D Transform(Point2D p) => new((A * p.X) + (C * p.Y) + E, (B * p.X) + (D * p.Y) + F);

    public Point2D Transform(double x, double y) => new((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    /// <summary>Multiplica this * other (aplica other primeiro, depois this).</summary>
    public AffineMatrix2D Multiply(AffineMatrix2D other) => new(
        A: (A * other.A) + (C * other.B),
        B: (B * other.A) + (D * other.B),
        C: (A * other.C) + (C * other.D),
        D: (B * other.C) + (D * other.D),
        E: (A * other.E) + (C * other.F) + E,
        F: (B * other.E) + (D * other.F) + F);

    public static AffineMatrix2D CreateTranslation(double tx, double ty) => new(1.0, 0.0, 0.0, 1.0, tx, ty);

    public static AffineMatrix2D CreateScale(double sx, double sy) => new(sx, 0.0, 0.0, sy, 0.0, 0.0);

    public static AffineMatrix2D CreateRotation(double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        return new AffineMatrix2D(cos, sin, -sin, cos, 0.0, 0.0);
    }

    public static AffineMatrix2D CreateRotation(double angleDeg, double cx, double cy)
        => CreateTranslation(cx, cy).Multiply(CreateRotation(angleDeg)).Multiply(CreateTranslation(-cx, -cy));

    public static AffineMatrix2D CreateSkewX(double angleDeg)
        => new(1.0, 0.0, Math.Tan(angleDeg * Math.PI / 180.0), 1.0, 0.0, 0.0);

    public static AffineMatrix2D CreateSkewY(double angleDeg)
        => new(1.0, Math.Tan(angleDeg * Math.PI / 180.0), 0.0, 1.0, 0.0, 0.0);

    /// <summary>
    /// Verifica se a parte linear é transformação de semelhança ortogonal (escala uniforme + rotação 90°/reflexão),
    /// permitindo preservar círculos e arcos circulares puros.
    /// </summary>
    public bool TryGetUniformScale(out double scale)
    {
        double normX2 = (A * A) + (B * B);
        double normY2 = (C * C) + (D * D);
        double dot = (A * C) + (B * D);
        double tol = 1e-5 * Math.Max(1.0, Math.Max(normX2, normY2));
        if (Math.Abs(normX2 - normY2) <= tol && Math.Abs(dot) <= tol && normX2 > 1e-18)
        {
            scale = Math.Sqrt(normX2);
            return true;
        }

        scale = 0.0;
        return false;
    }

    /// <summary>
    /// Retorna rotação em graus caso a matriz seja semelhança ortogonal sem reflexão ou escalas separadas.
    /// </summary>
    public double TransformXAxisAngleDeg() => Math.Atan2(B, A) * 180.0 / Math.PI;
}

public enum SvgSegmentKind
{
    Move,
    Line,
    Quad,
    Cubic,
    Arc,
    Close,
}

public sealed record SvgPathSegment(
    SvgSegmentKind Kind,
    Point2D Start,
    Point2D End,
    Point2D Control1 = default,
    Point2D Control2 = default,
    double Rx = 0.0,
    double Ry = 0.0,
    double XAxisRotationDeg = 0.0,
    bool LargeArc = false,
    bool Sweep = false,
    Point2D ArcCenter = default,
    double ArcStartRad = 0.0,
    double ArcDeltaRad = 0.0);

public sealed record SvgSubpath(
    IReadOnlyList<SvgPathSegment> Segments,
    bool IsClosed);

public sealed record SvgShapeElement(
    string TagName,
    string ElementId,
    string? DataLayer,
    string? StrokeColor,
    string? FillColor,
    double? StrokeWidthMm,
    bool HasStroke,
    bool HasFill,
    bool EvenOddFillRule,
    IReadOnlyList<SvgPathSegment> Segments,
    string NormalizedPathData,
    string? AncestorLayerId = null);

public sealed record SvgRasterImageElement(
    string ElementId,
    Point2D TopLeftMm,
    Point2D BottomLeftMm,
    Point2D BottomRightMm,
    double WidthMm,
    double HeightMm,
    byte[] ImageBytes);

public sealed record ParsedSvgDocument(
    double WidthMm,
    double HeightMm,
    string SourceUnit,
    IReadOnlyList<SvgShapeElement> Shapes,
    IReadOnlyList<SvgRasterImageElement> Images,
    bool HadTextElements);

/// <summary>
/// Parser e achatador geométrico compartilhado para documentos SVG em precisão milimétrica (96 PPI),
/// usado tanto por <see cref="SvgImporter"/> quanto por <c>VectorConverter</c>.
/// </summary>
internal static class SvgDocumentParser
{
    public const double MmPerPx = 25.4 / 96.0;
    private const double Pi2 = 2.0 * Math.PI;

    private static readonly HashSet<string> ContainerIgnoreTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "defs", "clippath", "mask", "marker", "symbol", "pattern", "title", "desc", "metadata", "style",
    };

    private static readonly Regex TransformCallRegex = new(
        @"([a-zA-Z]+)\s*\(([^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex CssRuleRegex = new(
        @"([^{}]+)\{([^}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex NumberListRegex = new(
        @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?",
        RegexOptions.Compiled);

    public static ParsedSvgDocument Parse(string svgXml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = 25_000_000,
            MaxCharactersFromEntities = 0,
        };

        using var stringReader = new StringReader(svgXml);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        XDocument doc = XDocument.Load(xmlReader, LoadOptions.None);

        XElement root = doc.Root ?? throw new InvalidDataException("SVG sem elemento raiz.");
        if (!string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Elemento raiz inválido '{root.Name.LocalName}'. Esperado <svg>.");
        }

        // Indexar IDs para resolução de <use> e coletar regras <style>.
        var elementsById = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var cssRules = new List<CssRule>();
        foreach (XElement el in root.DescendantsAndSelf())
        {
            string? id = el.Attribute("id")?.Value;
            if (!string.IsNullOrWhiteSpace(id) && !elementsById.ContainsKey(id))
            {
                elementsById[id] = el;
            }

            if (string.Equals(el.Name.LocalName, "style", StringComparison.OrdinalIgnoreCase))
            {
                ParseCssRules(el.Value, cssRules);
            }
        }

        (double widthMm, double heightMm, string sourceUnit, AffineMatrix2D rootMatrix, double userUnitPerMmFactor) =
            ComputeRootViewportAndMatrix(root);

        var shapes = new List<SvgShapeElement>();
        var images = new List<SvgRasterImageElement>();
        bool hadText = false;

        var initialStyle = new ComputedSvgStyle(
            Stroke: "none",
            Fill: "black",
            StrokeWidthRaw: "1",
            Opacity: 1.0,
            FillOpacity: 1.0,
            StrokeOpacity: 1.0,
            DisplayVisible: true,
            Visibility: "visible",
            EvenOddFillRule: false);

        TraverseElement(
            root,
            rootMatrix,
            initialStyle,
            cssRules,
            elementsById,
            new HashSet<string>(StringComparer.Ordinal),
            userUnitPerMmFactor,
            inheritedLayerId: null,
            shapes,
            images,
            ref hadText,
            isRoot: true);

        return new ParsedSvgDocument(widthMm, heightMm, sourceUnit, shapes, images, hadText);
    }

    private static (double WidthMm, double HeightMm, string SourceUnit, AffineMatrix2D RootMatrix, double MmToUserUnits)
        ComputeRootViewportAndMatrix(XElement root)
    {
        string? rawW = root.Attribute("width")?.Value;
        string? rawH = root.Attribute("height")?.Value;
        string? rawViewBox = root.Attribute("viewBox")?.Value ?? root.Attribute("viewbox")?.Value;

        bool hasW = TryParseLengthMm(rawW, out double parsedWMm, out string unitW);
        bool hasH = TryParseLengthMm(rawH, out double parsedHMm, out string unitH);
        bool hasVb = TryParseViewBox(rawViewBox, out double vbX, out double vbY, out double vbW, out double vbH);

        string sourceUnit = !string.IsNullOrEmpty(unitW) ? unitW
            : !string.IsNullOrEmpty(unitH) ? unitH
            : "px";

        double docWidthMm;
        double docHeightMm;

        if (hasVb && vbW > 0 && vbH > 0)
        {
            if (hasW && hasH)
            {
                docWidthMm = parsedWMm;
                docHeightMm = parsedHMm;
            }
            else if (hasW)
            {
                docWidthMm = parsedWMm;
                docHeightMm = parsedWMm * (vbH / vbW);
            }
            else if (hasH)
            {
                docHeightMm = parsedHMm;
                docWidthMm = parsedHMm * (vbW / vbH);
            }
            else
            {
                docWidthMm = vbW * MmPerPx;
                docHeightMm = vbH * MmPerPx;
            }

            AffineMatrix2D vbMatrix = BuildViewBoxMatrix(
                vbX, vbY, vbW, vbH,
                docWidthMm, docHeightMm,
                root.Attribute("preserveAspectRatio")?.Value);

            double avgScale = Math.Max((docWidthMm / vbW + docHeightMm / vbH) * 0.5, 1e-6);
            return (docWidthMm, docHeightMm, sourceUnit, vbMatrix, 1.0 / avgScale);
        }

        docWidthMm = hasW ? parsedWMm : 0.0;
        docHeightMm = hasH ? parsedHMm : 0.0;

        // Sem viewBox: 1 user unit = 1 CSS px (25.4/96 mm)
        AffineMatrix2D rootScale = AffineMatrix2D.CreateScale(MmPerPx, MmPerPx);
        return (docWidthMm, docHeightMm, sourceUnit, rootScale, 1.0 / MmPerPx);
    }

    private static AffineMatrix2D BuildViewBoxMatrix(
        double vbX,
        double vbY,
        double vbW,
        double vbH,
        double portW,
        double portH,
        string? preserveAspectRatio)
    {
        string par = (preserveAspectRatio ?? "xMidYMid meet").Trim();
        double sx = portW / vbW;
        double sy = portH / vbH;

        if (par.StartsWith("none", StringComparison.OrdinalIgnoreCase))
        {
            return AffineMatrix2D.CreateScale(sx, sy).Multiply(AffineMatrix2D.CreateTranslation(-vbX, -vbY));
        }

        bool slice = par.Contains("slice", StringComparison.OrdinalIgnoreCase);
        double scale = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);

        double extraW = portW - (vbW * scale);
        double extraH = portH - (vbH * scale);

        double tx = 0.0;
        if (par.Contains("xMid", StringComparison.OrdinalIgnoreCase))
        {
            tx = extraW * 0.5;
        }
        else if (par.Contains("xMax", StringComparison.OrdinalIgnoreCase))
        {
            tx = extraW;
        }

        double ty = 0.0;
        if (par.Contains("YMid", StringComparison.OrdinalIgnoreCase))
        {
            ty = extraH * 0.5;
        }
        else if (par.Contains("YMax", StringComparison.OrdinalIgnoreCase))
        {
            ty = extraH;
        }

        return AffineMatrix2D.CreateTranslation(tx, ty)
            .Multiply(AffineMatrix2D.CreateScale(scale, scale))
            .Multiply(AffineMatrix2D.CreateTranslation(-vbX, -vbY));
    }

    private static string? ResolveElementLayerToken(XElement element, string? inheritedLayerId)
    {
        string? dataLayer = element.Attribute("data-layer")?.Value;
        string? id = element.Attribute("id")?.Value;
        foreach (string? candidate in new[] { dataLayer, id })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string trimmed = candidate.Trim().ToLowerInvariant();
            if (trimmed is "layer_cut_internal" or "cut_internal"
                or "layer_cut_external" or "cut_external"
                or "layer_engrave_vector" or "engrave_vector"
                or "layer_engrave_fill" or "engrave_fill"
                or "layer_mark" or "mark")
            {
                return trimmed;
            }
        }

        return inheritedLayerId;
    }

    private static void TraverseElement(
        XElement element,
        AffineMatrix2D currentTransform,
        ComputedSvgStyle parentStyle,
        IReadOnlyList<CssRule> cssRules,
        IReadOnlyDictionary<string, XElement> elementsById,
        HashSet<string> activeUseStack,
        double userUnitScaleToPx,
        string? inheritedLayerId,
        List<SvgShapeElement> shapes,
        List<SvgRasterImageElement> images,
        ref bool hadText,
        bool isRoot = false)
    {
        string tag = element.Name.LocalName.ToLowerInvariant();
        if (!isRoot && ContainerIgnoreTags.Contains(tag))
        {
            return;
        }

        ComputedSvgStyle style = ComputeStyle(element, parentStyle, cssRules);
        if (!style.DisplayVisible || style.Opacity <= 0.0)
        {
            return;
        }

        string? currentLayerId = ResolveElementLayerToken(element, inheritedLayerId);
        AffineMatrix2D localTransform = isRoot
            ? currentTransform
            : currentTransform.Multiply(ParseTransformList(element.Attribute("transform")?.Value));

        if (tag == "use")
        {
            string? href = element.Attribute("href")?.Value
                ?? element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
            if (!string.IsNullOrWhiteSpace(href) && href.StartsWith('#'))
            {
                string targetId = href[1..];
                if (elementsById.TryGetValue(targetId, out XElement? target) && activeUseStack.Add(targetId))
                {
                    try
                    {
                        double ux = ParseLengthUserUnits(element.Attribute("x")?.Value, 0.0);
                        double uy = ParseLengthUserUnits(element.Attribute("y")?.Value, 0.0);
                        AffineMatrix2D useTransform = localTransform.Multiply(AffineMatrix2D.CreateTranslation(ux, uy));

                        if (string.Equals(target.Name.LocalName, "symbol", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (XElement child in target.Elements())
                            {
                                TraverseElement(
                                    child,
                                    useTransform,
                                    style,
                                    cssRules,
                                    elementsById,
                                    activeUseStack,
                                    userUnitScaleToPx,
                                    currentLayerId,
                                    shapes,
                                    images,
                                    ref hadText);
                            }
                        }
                        else
                        {
                            TraverseElement(
                                target,
                                useTransform,
                                style,
                                cssRules,
                                elementsById,
                                activeUseStack,
                                userUnitScaleToPx,
                                currentLayerId,
                                shapes,
                                images,
                                ref hadText);
                        }
                    }
                    finally
                    {
                        activeUseStack.Remove(targetId);
                    }
                }
            }

            return;
        }

        if (tag == "text")
        {
            if (style.Visibility == "visible" && !string.IsNullOrWhiteSpace(element.Value))
            {
                hadText = true;
            }

            return;
        }

        if (style.Visibility == "visible")
        {
            if (tag == "image")
            {
                TryEmitImage(element, localTransform, images);
            }
            else if (IsShapeTag(tag))
            {
                TryEmitShape(tag, element, localTransform, style, userUnitScaleToPx, inheritedLayerId, shapes);
            }
        }

        foreach (XElement child in element.Elements())
        {
            TraverseElement(
                child,
                localTransform,
                style,
                cssRules,
                elementsById,
                activeUseStack,
                userUnitScaleToPx,
                currentLayerId,
                shapes,
                images,
                ref hadText);
        }
    }

    private static bool IsShapeTag(string tag)
        => tag is "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon";

    private static void TryEmitImage(XElement element, AffineMatrix2D transform, List<SvgRasterImageElement> images)
    {
        string? href = element.Attribute("href")?.Value
            ?? element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
        if (string.IsNullOrWhiteSpace(href) || !href.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int commaIdx = href.IndexOf(',');
        if (commaIdx < 0)
        {
            return;
        }

        string header = href[..commaIdx];
        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        byte[] rawBytes;
        try
        {
            rawBytes = Convert.FromBase64String(href[(commaIdx + 1)..]);
        }
        catch
        {
            return;
        }

        double x = ParseLengthUserUnits(element.Attribute("x")?.Value, 0.0);
        double y = ParseLengthUserUnits(element.Attribute("y")?.Value, 0.0);
        double w = ParseLengthUserUnits(element.Attribute("width")?.Value, 0.0);
        double h = ParseLengthUserUnits(element.Attribute("height")?.Value, 0.0);
        if (w <= 0.0 || h <= 0.0)
        {
            return;
        }

        Point2D topLeft = transform.Transform(x, y);
        Point2D topRight = transform.Transform(x + w, y);
        Point2D bottomLeft = transform.Transform(x, y + h);
        Point2D bottomRight = transform.Transform(x + w, y + h);
        double outW = Hypot(topRight.X - topLeft.X, topRight.Y - topLeft.Y);
        double outH = Hypot(bottomLeft.X - topLeft.X, bottomLeft.Y - topLeft.Y);

        images.Add(new SvgRasterImageElement(
            ElementId: element.Attribute("id")?.Value ?? string.Empty,
            TopLeftMm: topLeft,
            BottomLeftMm: bottomLeft,
            BottomRightMm: bottomRight,
            WidthMm: outW,
            HeightMm: outH,
            ImageBytes: rawBytes));
    }

    private static void TryEmitShape(
        string tag,
        XElement element,
        AffineMatrix2D transform,
        ComputedSvgStyle style,
        double userUnitScaleToPx,
        string? ancestorLayerId,
        List<SvgShapeElement> shapes)
    {
        List<SvgPathSegment>? rawSegments = tag switch
        {
            "path" => ParsePathData(element.Attribute("d")?.Value),
            "rect" => BuildRectSegments(element),
            "circle" => BuildCircleSegments(element),
            "ellipse" => BuildEllipseSegments(element),
            "line" => BuildLineSegments(element),
            "polyline" => BuildPolySegments(element, close: false),
            "polygon" => BuildPolySegments(element, close: true),
            _ => null,
        };

        if (rawSegments is null || rawSegments.Count == 0)
        {
            return;
        }

        List<SvgPathSegment> worldSegments = TransformSegments(rawSegments, transform);
        if (worldSegments.Count == 0)
        {
            return;
        }

        bool hasStroke = style.HasStroke;
        bool hasFill = style.HasFill;
        if (!hasStroke && !hasFill)
        {
            return;
        }

        double? strokeWidthMm = null;
        if (hasStroke && TryParseStrokeWidthPx(style.StrokeWidthRaw, out double swVal))
        {
            double avgScale = 0.5 * (Hypot(transform.A, transform.B) + Hypot(transform.C, transform.D));
            strokeWidthMm = swVal * Math.Max(avgScale, 1e-6);
            if (userUnitScaleToPx <= 0.0)
            {
                strokeWidthMm = swVal * MmPerPx;
            }
        }

        string d = SegmentsToSvgPathData(worldSegments);
        if (string.IsNullOrEmpty(d))
        {
            return;
        }

        shapes.Add(new SvgShapeElement(
            TagName: tag,
            ElementId: element.Attribute("id")?.Value ?? string.Empty,
            DataLayer: element.Attribute("data-layer")?.Value,
            StrokeColor: hasStroke ? style.Stroke : null,
            FillColor: hasFill ? style.Fill : null,
            StrokeWidthMm: strokeWidthMm,
            HasStroke: hasStroke,
            HasFill: hasFill,
            EvenOddFillRule: style.EvenOddFillRule,
            Segments: worldSegments,
            NormalizedPathData: d,
            AncestorLayerId: ancestorLayerId));
    }

    private static List<SvgPathSegment> BuildRectSegments(XElement el)
    {
        double x = ParseLengthUserUnits(el.Attribute("x")?.Value, 0.0);
        double y = ParseLengthUserUnits(el.Attribute("y")?.Value, 0.0);
        double w = ParseLengthUserUnits(el.Attribute("width")?.Value, 0.0);
        double h = ParseLengthUserUnits(el.Attribute("height")?.Value, 0.0);
        if (w <= 0.0 || h <= 0.0)
        {
            return [];
        }

        double? rawRx = TryParseLengthUserUnits(el.Attribute("rx")?.Value);
        double? rawRy = TryParseLengthUserUnits(el.Attribute("ry")?.Value);
        double rx = rawRx ?? rawRy ?? 0.0;
        double ry = rawRy ?? rawRx ?? 0.0;
        rx = Math.Clamp(rx, 0.0, w * 0.5);
        ry = Math.Clamp(ry, 0.0, h * 0.5);

        if (rx <= 1e-9 || ry <= 1e-9)
        {
            Point2D p0 = new(x, y);
            Point2D p1 = new(x + w, y);
            Point2D p2 = new(x + w, y + h);
            Point2D p3 = new(x, y + h);
            return
            [
                new(SvgSegmentKind.Move, p0, p0),
                new(SvgSegmentKind.Line, p0, p1),
                new(SvgSegmentKind.Line, p1, p2),
                new(SvgSegmentKind.Line, p2, p3),
                new(SvgSegmentKind.Close, p3, p0),
            ];
        }

        var segs = new List<SvgPathSegment>();
        Point2D cur = new(x + rx, y);
        segs.Add(new(SvgSegmentKind.Move, cur, cur));
        Point2D next = new(x + w - rx, y);
        if (!PointsClose(cur, next))
        {
            segs.Add(new(SvgSegmentKind.Line, cur, next));
        }

        cur = next;
        next = new(x + w, y + ry);
        AddArcFromSvgParams(segs, cur, rx, ry, 0.0, false, true, next);
        cur = next;

        next = new(x + w, y + h - ry);
        if (!PointsClose(cur, next))
        {
            segs.Add(new(SvgSegmentKind.Line, cur, next));
        }

        cur = next;
        next = new(x + w - rx, y + h);
        AddArcFromSvgParams(segs, cur, rx, ry, 0.0, false, true, next);
        cur = next;

        next = new(x + rx, y + h);
        if (!PointsClose(cur, next))
        {
            segs.Add(new(SvgSegmentKind.Line, cur, next));
        }

        cur = next;
        next = new(x, y + h - ry);
        AddArcFromSvgParams(segs, cur, rx, ry, 0.0, false, true, next);
        cur = next;

        next = new(x, y + ry);
        if (!PointsClose(cur, next))
        {
            segs.Add(new(SvgSegmentKind.Line, cur, next));
        }

        cur = next;
        next = new(x + rx, y);
        AddArcFromSvgParams(segs, cur, rx, ry, 0.0, false, true, next);
        segs.Add(new(SvgSegmentKind.Close, next, new Point2D(x + rx, y)));
        return segs;
    }

    private static List<SvgPathSegment> BuildCircleSegments(XElement el)
    {
        double cx = ParseLengthUserUnits(el.Attribute("cx")?.Value, 0.0);
        double cy = ParseLengthUserUnits(el.Attribute("cy")?.Value, 0.0);
        double r = ParseLengthUserUnits(el.Attribute("r")?.Value, 0.0);
        if (r <= 0.0)
        {
            return [];
        }

        Point2D pRight = new(cx + r, cy);
        Point2D pLeft = new(cx - r, cy);
        var segs = new List<SvgPathSegment>
        {
            new(SvgSegmentKind.Move, pRight, pRight),
        };
        AddArcFromSvgParams(segs, pRight, r, r, 0.0, false, true, pLeft);
        AddArcFromSvgParams(segs, pLeft, r, r, 0.0, false, true, pRight);
        segs.Add(new(SvgSegmentKind.Close, pRight, pRight));
        return segs;
    }

    private static List<SvgPathSegment> BuildEllipseSegments(XElement el)
    {
        double cx = ParseLengthUserUnits(el.Attribute("cx")?.Value, 0.0);
        double cy = ParseLengthUserUnits(el.Attribute("cy")?.Value, 0.0);
        double rx = ParseLengthUserUnits(el.Attribute("rx")?.Value, 0.0);
        double ry = ParseLengthUserUnits(el.Attribute("ry")?.Value, 0.0);
        if (rx <= 0.0 || ry <= 0.0)
        {
            return [];
        }

        Point2D pRight = new(cx + rx, cy);
        Point2D pLeft = new(cx - rx, cy);
        var segs = new List<SvgPathSegment>
        {
            new(SvgSegmentKind.Move, pRight, pRight),
        };
        AddArcFromSvgParams(segs, pRight, rx, ry, 0.0, false, true, pLeft);
        AddArcFromSvgParams(segs, pLeft, rx, ry, 0.0, false, true, pRight);
        segs.Add(new(SvgSegmentKind.Close, pRight, pRight));
        return segs;
    }

    private static List<SvgPathSegment> BuildLineSegments(XElement el)
    {
        double x1 = ParseLengthUserUnits(el.Attribute("x1")?.Value, 0.0);
        double y1 = ParseLengthUserUnits(el.Attribute("y1")?.Value, 0.0);
        double x2 = ParseLengthUserUnits(el.Attribute("x2")?.Value, 0.0);
        double y2 = ParseLengthUserUnits(el.Attribute("y2")?.Value, 0.0);
        Point2D p1 = new(x1, y1);
        Point2D p2 = new(x2, y2);
        return
        [
            new(SvgSegmentKind.Move, p1, p1),
            new(SvgSegmentKind.Line, p1, p2),
        ];
    }

    private static List<SvgPathSegment> BuildPolySegments(XElement el, bool close)
    {
        string? raw = el.Attribute("points")?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        MatchCollection matches = NumberListRegex.Matches(raw);
        if (matches.Count < 4)
        {
            return [];
        }

        int count = matches.Count / 2;
        var pts = new List<Point2D>(count);
        for (int i = 0; i + 1 < count * 2; i += 2)
        {
            double x = double.Parse(matches[i].Value, CultureInfo.InvariantCulture);
            double y = double.Parse(matches[i + 1].Value, CultureInfo.InvariantCulture);
            pts.Add(new Point2D(x, y));
        }

        var segs = new List<SvgPathSegment>(pts.Count + 1)
        {
            new(SvgSegmentKind.Move, pts[0], pts[0]),
        };
        for (int i = 1; i < pts.Count; i++)
        {
            segs.Add(new(SvgSegmentKind.Line, pts[i - 1], pts[i]));
        }

        if (close)
        {
            segs.Add(new(SvgSegmentKind.Close, pts[^1], pts[0]));
        }

        return segs;
    }

    internal static List<SvgPathSegment> ParsePathData(string? d)
    {
        var segs = new List<SvgPathSegment>();
        if (string.IsNullOrWhiteSpace(d))
        {
            return segs;
        }

        var scanner = new PathDataScanner(d);
        Point2D current = default;
        Point2D subpathStart = default;
        Point2D lastControl = default;
        char lastCmd = '\0';

        while (scanner.TryReadCommand(out char cmd))
        {
            bool rel = char.IsLower(cmd);
            char upper = char.ToUpperInvariant(cmd);

            switch (upper)
            {
                case 'M':
                {
                    bool first = true;
                    while (scanner.HasNextNumber())
                    {
                        double x = scanner.ReadNumber();
                        double y = scanner.ReadNumber();
                        Point2D target = rel ? new(current.X + x, current.Y + y) : new(x, y);
                        if (first)
                        {
                            segs.Add(new(SvgSegmentKind.Move, target, target));
                            subpathStart = target;
                            first = false;
                        }
                        else
                        {
                            segs.Add(new(SvgSegmentKind.Line, current, target));
                        }

                        current = target;
                        lastControl = current;
                    }

                    lastCmd = upper;
                    break;
                }
                case 'L':
                    while (scanner.HasNextNumber())
                    {
                        double x = scanner.ReadNumber();
                        double y = scanner.ReadNumber();
                        Point2D target = rel ? new(current.X + x, current.Y + y) : new(x, y);
                        segs.Add(new(SvgSegmentKind.Line, current, target));
                        current = target;
                        lastControl = current;
                    }

                    lastCmd = upper;
                    break;
                case 'H':
                    while (scanner.HasNextNumber())
                    {
                        double x = scanner.ReadNumber();
                        Point2D target = rel ? new(current.X + x, current.Y) : new(x, current.Y);
                        segs.Add(new(SvgSegmentKind.Line, current, target));
                        current = target;
                        lastControl = current;
                    }

                    lastCmd = upper;
                    break;
                case 'V':
                    while (scanner.HasNextNumber())
                    {
                        double y = scanner.ReadNumber();
                        Point2D target = rel ? new(current.X, current.Y + y) : new(current.X, y);
                        segs.Add(new(SvgSegmentKind.Line, current, target));
                        current = target;
                        lastControl = current;
                    }

                    lastCmd = upper;
                    break;
                case 'C':
                    while (scanner.HasNextNumber())
                    {
                        double x1 = scanner.ReadNumber();
                        double y1 = scanner.ReadNumber();
                        double x2 = scanner.ReadNumber();
                        double y2 = scanner.ReadNumber();
                        double x = scanner.ReadNumber();
                        double y = scanner.ReadNumber();
                        Point2D c1 = rel ? new(current.X + x1, current.Y + y1) : new(x1, y1);
                        Point2D c2 = rel ? new(current.X + x2, current.Y + y2) : new(x2, y2);
                        Point2D target = rel ? new(current.X + x, current.Y + y) : new(x, y);
                        segs.Add(new(SvgSegmentKind.Cubic, current, target, c1, c2));
                        lastControl = c2;
                        current = target;
                    }

                    lastCmd = upper;
                    break;
                case 'S':
                    while (scanner.HasNextNumber())
                    {
                        Point2D c1 = (lastCmd is 'C' or 'S')
                            ? new((2.0 * current.X) - lastControl.X, (2.0 * current.Y) - lastControl.Y)
                            : current;
                        double x2 = scanner.ReadNumber();
                        double y2 = scanner.ReadNumber();
                        double x = scanner.ReadNumber();
                        double y = scanner.ReadNumber();
                        Point2D c2 = rel ? new(current.X + x2, current.Y + y2) : new(x2, y2);
                        Point2D target = rel ? new(current.X + x, current.Y + y) : new(x, y);
                        segs.Add(new(SvgSegmentKind.Cubic, current, target, c1, c2));
                        lastControl = c2;
                        current = target;
                        lastCmd = upper;
                    }

                    break;
                case 'Q':
                    while (scanner.HasNextNumber())
                    {
                        double x1 = scanner.ReadNumber();
                        double y1 = scanner.ReadNumber();
                        double x = scanner.ReadNumber();
                        double y = scanner.ReadNumber();
                        Point2D c1 = rel ? new(current.X + x1, current.Y + y1) : new(x1, y1);
                        Point2D target = rel ? new(current.X + x, current.Y + y) : new(x, y);
                        segs.Add(new(SvgSegmentKind.Quad, current, target, c1));
                        lastControl = c1;
                        current = target;
                    }

                    lastCmd = upper;
                    break;
                case 'T':
                    while (scanner.HasNextNumber())
                    {
                        Point2D c1 = (lastCmd is 'Q' or 'T')
                            ? new((2.0 * current.X) - lastControl.X, (2.0 * current.Y) - lastControl.Y)
                            : current;
                        double x = scanner.ReadNumber();
                        double y = scanner.ReadNumber();
                        Point2D target = rel ? new(current.X + x, current.Y + y) : new(x, y);
                        segs.Add(new(SvgSegmentKind.Quad, current, target, c1));
                        lastControl = c1;
                        current = target;
                        lastCmd = upper;
                    }

                    break;
                case 'A':
                    while (scanner.HasNextNumber())
                    {
                        double rx = scanner.ReadNumber();
                        double ry = scanner.ReadNumber();
                        double rot = scanner.ReadNumber();
                        bool largeArc = scanner.ReadFlag();
                        bool sweep = scanner.ReadFlag();
                        double x = scanner.ReadNumber();
                        double y = scanner.ReadNumber();
                        Point2D target = rel ? new(current.X + x, current.Y + y) : new(x, y);
                        AddArcFromSvgParams(segs, current, rx, ry, rot, largeArc, sweep, target);
                        current = target;
                        lastControl = current;
                    }

                    lastCmd = upper;
                    break;
                case 'Z':
                    segs.Add(new(SvgSegmentKind.Close, current, subpathStart));
                    current = subpathStart;
                    lastControl = current;
                    lastCmd = upper;
                    break;
            }
        }

        return segs;
    }

    private static void AddArcFromSvgParams(
        List<SvgPathSegment> segs,
        Point2D p1,
        double rxIn,
        double ryIn,
        double phiDeg,
        bool largeArc,
        bool sweep,
        Point2D p2)
    {
        if (PointsClose(p1, p2, 1e-12))
        {
            return;
        }

        double rx = Math.Abs(rxIn);
        double ry = Math.Abs(ryIn);
        if (rx < 1e-12 || ry < 1e-12)
        {
            segs.Add(new(SvgSegmentKind.Line, p1, p2));
            return;
        }

        double phi = (phiDeg % 360.0) * Math.PI / 180.0;
        double cosPhi = Math.Cos(phi);
        double sinPhi = Math.Sin(phi);

        double dx2 = (p1.X - p2.X) * 0.5;
        double dy2 = (p1.Y - p2.Y) * 0.5;
        double x1p = (cosPhi * dx2) + (sinPhi * dy2);
        double y1p = (-sinPhi * dx2) + (cosPhi * dy2);

        double lambda = ((x1p * x1p) / (rx * rx)) + ((y1p * y1p) / (ry * ry));
        if (lambda > 1.0)
        {
            double s = Math.Sqrt(lambda);
            rx *= s;
            ry *= s;
        }

        double rx2 = rx * rx;
        double ry2 = ry * ry;
        double x1p2 = x1p * x1p;
        double y1p2 = y1p * y1p;

        double denom = (rx2 * y1p2) + (ry2 * x1p2);
        double num = Math.Max(0.0, (rx2 * ry2) - denom);
        double factor = denom > 1e-18 ? Math.Sqrt(num / denom) : 0.0;
        if (largeArc == sweep)
        {
            factor = -factor;
        }

        double cxp = factor * ((rx * y1p) / ry);
        double cyp = factor * (-(ry * x1p) / rx);

        double cx = (cosPhi * cxp) - (sinPhi * cyp) + ((p1.X + p2.X) * 0.5);
        double cy = (sinPhi * cxp) + (cosPhi * cyp) + ((p1.Y + p2.Y) * 0.5);

        double ux = (x1p - cxp) / rx;
        double uy = (y1p - cyp) / ry;
        double vx = (-x1p - cxp) / rx;
        double vy = (-y1p - cyp) / ry;

        double theta1 = VectorAngle(1.0, 0.0, ux, uy);
        double dTheta = VectorAngle(ux, uy, vx, vy);
        if (!sweep && dTheta > 0.0)
        {
            dTheta -= Pi2;
        }
        else if (sweep && dTheta < 0.0)
        {
            dTheta += Pi2;
        }

        segs.Add(new SvgPathSegment(
            Kind: SvgSegmentKind.Arc,
            Start: p1,
            End: p2,
            Rx: rx,
            Ry: ry,
            XAxisRotationDeg: phiDeg,
            LargeArc: largeArc,
            Sweep: sweep,
            ArcCenter: new Point2D(cx, cy),
            ArcStartRad: theta1,
            ArcDeltaRad: dTheta));
    }

    private static List<SvgPathSegment> TransformSegments(IReadOnlyList<SvgPathSegment> source, AffineMatrix2D matrix)
    {
        var result = new List<SvgPathSegment>(source.Count);
        foreach (SvgPathSegment seg in source)
        {
            switch (seg.Kind)
            {
                case SvgSegmentKind.Move:
                {
                    Point2D p = matrix.Transform(seg.End);
                    result.Add(seg with { Start = p, End = p });
                    break;
                }
                case SvgSegmentKind.Line:
                    result.Add(seg with
                    {
                        Start = matrix.Transform(seg.Start),
                        End = matrix.Transform(seg.End),
                    });
                    break;
                case SvgSegmentKind.Quad:
                    result.Add(seg with
                    {
                        Start = matrix.Transform(seg.Start),
                        End = matrix.Transform(seg.End),
                        Control1 = matrix.Transform(seg.Control1),
                    });
                    break;
                case SvgSegmentKind.Cubic:
                    result.Add(seg with
                    {
                        Start = matrix.Transform(seg.Start),
                        End = matrix.Transform(seg.End),
                        Control1 = matrix.Transform(seg.Control1),
                        Control2 = matrix.Transform(seg.Control2),
                    });
                    break;
                case SvgSegmentKind.Arc:
                    TransformArcSegment(seg, matrix, result);
                    break;
                case SvgSegmentKind.Close:
                    result.Add(seg with
                    {
                        Start = matrix.Transform(seg.Start),
                        End = matrix.Transform(seg.End),
                    });
                    break;
            }
        }

        return result;
    }

    private static void TransformArcSegment(SvgPathSegment arc, AffineMatrix2D matrix, List<SvgPathSegment> output)
    {
        bool isCircular = Math.Abs(arc.Rx - arc.Ry) <= 1e-5 * Math.Max(1.0, Math.Max(arc.Rx, arc.Ry));
        if (isCircular && matrix.TryGetUniformScale(out double scale))
        {
            Point2D p1 = matrix.Transform(arc.Start);
            Point2D p2 = matrix.Transform(arc.End);
            double r = arc.Rx * scale;
            bool sweep = matrix.Determinant < 0.0 ? !arc.Sweep : arc.Sweep;
            AddArcFromSvgParams(output, p1, r, r, 0.0, arc.LargeArc, sweep, p2);
            return;
        }

        // Caso geral (escala não-uniforme, cisalhamento ou elipse): converte em segmentos Bézier cúbicos exatos
        // antes de aplicar a transformação afim (como transformações afins preservam Béziers cúbicas exatamente).
        foreach (SvgPathSegment cubic in ArcToCubicBeziers(arc))
        {
            output.Add(cubic with
            {
                Start = matrix.Transform(cubic.Start),
                End = matrix.Transform(cubic.End),
                Control1 = matrix.Transform(cubic.Control1),
                Control2 = matrix.Transform(cubic.Control2),
            });
        }
    }

    /// <summary>Converte um arco elíptico SVG em até 4 curvas Bézier cúbicas (<= 90 graus cada).</summary>
    public static IReadOnlyList<SvgPathSegment> ArcToCubicBeziers(SvgPathSegment arc)
    {
        int nSegs = Math.Max(1, (int)Math.Ceiling(Math.Abs(arc.ArcDeltaRad) / (Math.PI * 0.5)));
        double delta = arc.ArcDeltaRad / nSegs;
        double phi = (arc.XAxisRotationDeg % 360.0) * Math.PI / 180.0;
        double cosPhi = Math.Cos(phi);
        double sinPhi = Math.Sin(phi);

        double t = (8.0 / 3.0) * Math.Pow(Math.Sin(delta * 0.25), 2) / Math.Sin(delta * 0.5);
        if (!double.IsFinite(t))
        {
            return [new SvgPathSegment(SvgSegmentKind.Line, arc.Start, arc.End)];
        }

        var cubics = new List<SvgPathSegment>(nSegs);
        Point2D curPt = arc.Start;

        for (int i = 0; i < nSegs; i++)
        {
            double theta1 = arc.ArcStartRad + (i * delta);
            double theta2 = arc.ArcStartRad + ((i + 1) * delta);

            double cos1 = Math.Cos(theta1);
            double sin1 = Math.Sin(theta1);
            double cos2 = Math.Cos(theta2);
            double sin2 = Math.Sin(theta2);

            Point2D c1Local = new(arc.Rx * (cos1 - (t * sin1)), arc.Ry * (sin1 + (t * cos1)));
            Point2D c2Local = new(arc.Rx * (cos2 + (t * sin2)), arc.Ry * (sin2 - (t * cos2)));
            Point2D endLocal = new(arc.Rx * cos2, arc.Ry * sin2);

            Point2D c1 = MapEllipsePoint(arc.ArcCenter, cosPhi, sinPhi, c1Local);
            Point2D c2 = MapEllipsePoint(arc.ArcCenter, cosPhi, sinPhi, c2Local);
            Point2D endPt = (i == nSegs - 1)
                ? arc.End
                : MapEllipsePoint(arc.ArcCenter, cosPhi, sinPhi, endLocal);

            cubics.Add(new SvgPathSegment(SvgSegmentKind.Cubic, curPt, endPt, c1, c2));
            curPt = endPt;
        }

        return cubics;
    }

    private static Point2D MapEllipsePoint(Point2D center, double cosPhi, double sinPhi, Point2D local)
        => new(
            center.X + (cosPhi * local.X) - (sinPhi * local.Y),
            center.Y + (sinPhi * local.X) + (cosPhi * local.Y));

    public static IReadOnlyList<SvgSubpath> SplitSubpaths(IReadOnlyList<SvgPathSegment> segments)
    {
        var result = new List<SvgSubpath>();
        var current = new List<SvgPathSegment>();
        bool closed = false;

        foreach (SvgPathSegment seg in segments)
        {
            if (seg.Kind == SvgSegmentKind.Move)
            {
                if (current.Count > 0)
                {
                    result.Add(new SvgSubpath(current, closed));
                    current = new List<SvgPathSegment>();
                    closed = false;
                }

                current.Add(seg);
            }
            else if (seg.Kind == SvgSegmentKind.Close)
            {
                if (current.Count > 0)
                {
                    if (!PointsClose(seg.Start, seg.End, 1e-6))
                    {
                        current.Add(new SvgPathSegment(SvgSegmentKind.Line, seg.Start, seg.End));
                    }

                    closed = true;
                    result.Add(new SvgSubpath(current, true));
                    current = new List<SvgPathSegment>();
                    closed = false;
                }
            }
            else
            {
                if (current.Count == 0)
                {
                    current.Add(new SvgPathSegment(SvgSegmentKind.Move, seg.Start, seg.Start));
                }

                current.Add(seg);
            }
        }

        if (current.Count > 0)
        {
            result.Add(new SvgSubpath(current, closed));
        }

        return result;
    }

    public static string SegmentsToSvgPathData(IEnumerable<SvgPathSegment> segments)
    {
        var parts = new List<string>();
        foreach (SvgPathSegment seg in segments)
        {
            switch (seg.Kind)
            {
                case SvgSegmentKind.Move:
                    parts.Add($"M {Fmt(seg.End.X)} {Fmt(seg.End.Y)}");
                    break;
                case SvgSegmentKind.Line:
                    parts.Add($"L {Fmt(seg.End.X)} {Fmt(seg.End.Y)}");
                    break;
                case SvgSegmentKind.Quad:
                    parts.Add($"Q {Fmt(seg.Control1.X)} {Fmt(seg.Control1.Y)} {Fmt(seg.End.X)} {Fmt(seg.End.Y)}");
                    break;
                case SvgSegmentKind.Cubic:
                    parts.Add(
                        $"C {Fmt(seg.Control1.X)} {Fmt(seg.Control1.Y)} " +
                        $"{Fmt(seg.Control2.X)} {Fmt(seg.Control2.Y)} " +
                        $"{Fmt(seg.End.X)} {Fmt(seg.End.Y)}");
                    break;
                case SvgSegmentKind.Arc:
                    parts.Add(
                        $"A {Fmt(seg.Rx)} {Fmt(seg.Ry)} {Fmt(seg.XAxisRotationDeg)} " +
                        $"{(seg.LargeArc ? 1 : 0)} {(seg.Sweep ? 1 : 0)} " +
                        $"{Fmt(seg.End.X)} {Fmt(seg.End.Y)}");
                    break;
                case SvgSegmentKind.Close:
                    parts.Add("Z");
                    break;
            }
        }

        return string.Join(' ', parts);
    }

    public static string Fmt(double value)
    {
        if (Math.Abs(value) < 5e-6)
        {
            return "0";
        }

        string text = value.ToString("0.#####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    public static Point2D EvaluateCubic(Point2D p0, Point2D p1, Point2D p2, Point2D p3, double t)
    {
        double u = 1.0 - t;
        double tt = t * t;
        double uu = u * u;
        double uuu = uu * u;
        double ttt = tt * t;
        return new Point2D(
            (uuu * p0.X) + (3.0 * uu * t * p1.X) + (3.0 * u * tt * p2.X) + (ttt * p3.X),
            (uuu * p0.Y) + (3.0 * uu * t * p1.Y) + (3.0 * u * tt * p2.Y) + (ttt * p3.Y));
    }

    public static Point2D EvaluateQuad(Point2D p0, Point2D p1, Point2D p2, double t)
    {
        double u = 1.0 - t;
        return new Point2D(
            (u * u * p0.X) + (2.0 * u * t * p1.X) + (t * t * p2.X),
            (u * u * p0.Y) + (2.0 * u * t * p1.Y) + (t * t * p2.Y));
    }

    private static AffineMatrix2D ParseTransformList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AffineMatrix2D.Identity;
        }

        AffineMatrix2D result = AffineMatrix2D.Identity;
        foreach (Match match in TransformCallRegex.Matches(raw))
        {
            string fn = match.Groups[1].Value.Trim().ToLowerInvariant();
            double[] args = ParseNumberList(match.Groups[2].Value);

            AffineMatrix2D step = fn switch
            {
                "matrix" when args.Length >= 6 => new AffineMatrix2D(args[0], args[1], args[2], args[3], args[4], args[5]),
                "translate" when args.Length >= 1 => AffineMatrix2D.CreateTranslation(args[0], args.Length > 1 ? args[1] : 0.0),
                "scale" when args.Length >= 1 => AffineMatrix2D.CreateScale(args[0], args.Length > 1 ? args[1] : args[0]),
                "rotate" when args.Length == 1 => AffineMatrix2D.CreateRotation(args[0]),
                "rotate" when args.Length >= 3 => AffineMatrix2D.CreateRotation(args[0], args[1], args[2]),
                "skewx" when args.Length >= 1 => AffineMatrix2D.CreateSkewX(args[0]),
                "skewy" when args.Length >= 1 => AffineMatrix2D.CreateSkewY(args[0]),
                _ => AffineMatrix2D.Identity,
            };

            result = result.Multiply(step);
        }

        return result;
    }

    private static double[] ParseNumberList(string raw)
    {
        MatchCollection matches = NumberListRegex.Matches(raw);
        var arr = new double[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            arr[i] = double.Parse(matches[i].Value, CultureInfo.InvariantCulture);
        }

        return arr;
    }

    private static bool TryParseViewBox(string? raw, out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0.0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        double[] nums = ParseNumberList(raw);
        if (nums.Length < 4)
        {
            return false;
        }

        x = nums[0];
        y = nums[1];
        w = nums[2];
        h = nums[3];
        return true;
    }

    private static bool TryParseLengthMm(string? raw, out double mm, out string unit)
    {
        mm = 0.0;
        unit = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.EndsWith('%'))
        {
            return false;
        }

        Match m = Regex.Match(trimmed, @"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([a-zA-Z]*)$");
        if (!m.Success)
        {
            return false;
        }

        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            return false;
        }

        unit = m.Groups[2].Value.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(unit))
        {
            unit = "px";
        }

        mm = unit switch
        {
            "mm" => val,
            "cm" => val * 10.0,
            "in" => val * 25.4,
            "pt" => val * (25.4 / 72.0),
            "pc" => val * (25.4 / 6.0),
            _ => val * MmPerPx,
        };
        return true;
    }

    private static double ParseLengthUserUnits(string? raw, double fallback)
        => TryParseLengthUserUnits(raw) ?? fallback;

    private static double? TryParseLengthUserUnits(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim();
        Match m = Regex.Match(trimmed, @"^([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*([a-zA-Z]*)$");
        if (!m.Success)
        {
            return null;
        }

        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            return null;
        }

        string unit = m.Groups[2].Value.Trim().ToLowerInvariant();
        return unit switch
        {
            "" or "px" => val,
            "mm" => val / MmPerPx,
            "cm" => (val * 10.0) / MmPerPx,
            "in" => val * 96.0,
            "pt" => val * (96.0 / 72.0),
            "pc" => val * 16.0,
            _ => val,
        };
    }

    private static bool TryParseStrokeWidthPx(string raw, out double value)
    {
        double? parsed = TryParseLengthUserUnits(raw);
        if (parsed.HasValue)
        {
            value = parsed.Value;
            return true;
        }

        value = 0.0;
        return false;
    }

    private static void ParseCssRules(string css, List<CssRule> output)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return;
        }

        string cleaned = Regex.Replace(css, @"/\*[\s\S]*?\*/", string.Empty);
        foreach (Match m in CssRuleRegex.Matches(cleaned))
        {
            string selectorsRaw = m.Groups[1].Value;
            Dictionary<string, string> decls = ParseStyleDeclarations(m.Groups[2].Value);
            if (decls.Count == 0)
            {
                continue;
            }

            foreach (string sel in selectorsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = sel.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    output.Add(new CssRule(trimmed, decls));
                }
            }
        }
    }

    private static Dictionary<string, string> ParseStyleDeclarations(string? styleStr)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(styleStr))
        {
            return dict;
        }

        foreach (string part in styleStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string key = part[..colon].Trim().ToLowerInvariant();
            string val = part[(colon + 1)..].Trim();
            if (!string.IsNullOrEmpty(key))
            {
                dict[key] = val;
            }
        }

        return dict;
    }

    private static ComputedSvgStyle ComputeStyle(
        XElement element,
        ComputedSvgStyle parent,
        IReadOnlyList<CssRule> cssRules)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string tag = element.Name.LocalName.ToLowerInvariant();
        string? id = element.Attribute("id")?.Value;
        string[] classes = (element.Attribute("class")?.Value ?? string.Empty)
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // 1. Atributos de apresentação XML (menor precedência entre estilos próprios)
        foreach (XAttribute attr in element.Attributes())
        {
            string name = attr.Name.LocalName.ToLowerInvariant();
            if (name is "fill" or "stroke" or "stroke-width" or "opacity" or "fill-opacity"
                or "stroke-opacity" or "display" or "visibility" or "fill-rule")
            {
                props[name] = attr.Value.Trim();
            }
        }

        // 2. Regras <style> (tag, .class, #id)
        foreach (CssRule rule in cssRules)
        {
            bool matches = rule.Selector.Equals(tag, StringComparison.OrdinalIgnoreCase)
                || (id is not null && rule.Selector.Equals("#" + id, StringComparison.Ordinal))
                || (rule.Selector.StartsWith('.') && classes.Contains(rule.Selector[1..], StringComparer.Ordinal));
            if (matches)
            {
                foreach ((string k, string v) in rule.Declarations)
                {
                    props[k] = v;
                }
            }
        }

        // 3. Inline style="..." (maior precedência)
        Dictionary<string, string> inlineProps = ParseStyleDeclarations(element.Attribute("style")?.Value);
        foreach ((string k, string v) in inlineProps)
        {
            props[k] = v;
        }

        string stroke = props.TryGetValue("stroke", out string? sVal) && !string.Equals(sVal, "inherit", StringComparison.OrdinalIgnoreCase)
            ? sVal
            : parent.Stroke;
        string fill = props.TryGetValue("fill", out string? fVal) && !string.Equals(fVal, "inherit", StringComparison.OrdinalIgnoreCase)
            ? fVal
            : parent.Fill;
        string strokeWidthRaw = props.TryGetValue("stroke-width", out string? swVal) && !string.Equals(swVal, "inherit", StringComparison.OrdinalIgnoreCase)
            ? swVal
            : parent.StrokeWidthRaw;

        double opacity = parent.Opacity;
        if (props.TryGetValue("opacity", out string? opVal) && TryParseDouble(opVal, out double parsedOp))
        {
            opacity = parent.Opacity * Math.Clamp(parsedOp, 0.0, 1.0);
        }

        double fillOpacity = parent.FillOpacity;
        if (props.TryGetValue("fill-opacity", out string? fopVal) && TryParseDouble(fopVal, out double parsedFop))
        {
            fillOpacity = Math.Clamp(parsedFop, 0.0, 1.0);
        }

        double strokeOpacity = parent.StrokeOpacity;
        if (props.TryGetValue("stroke-opacity", out string? sopVal) && TryParseDouble(sopVal, out double parsedSop))
        {
            strokeOpacity = Math.Clamp(parsedSop, 0.0, 1.0);
        }

        bool displayVisible = parent.DisplayVisible;
        if (props.TryGetValue("display", out string? dispVal) &&
            string.Equals(dispVal.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            displayVisible = false;
        }

        string visibility = parent.Visibility;
        if (props.TryGetValue("visibility", out string? visVal))
        {
            string vLower = visVal.Trim().ToLowerInvariant();
            if (vLower is "visible" or "hidden" or "collapse")
            {
                visibility = vLower;
            }
        }

        bool evenOdd = parent.EvenOddFillRule;
        if (props.TryGetValue("fill-rule", out string? frVal))
        {
            evenOdd = string.Equals(frVal.Trim(), "evenodd", StringComparison.OrdinalIgnoreCase);
        }

        return new ComputedSvgStyle(
            Stroke: stroke,
            Fill: fill,
            StrokeWidthRaw: strokeWidthRaw,
            Opacity: opacity,
            FillOpacity: fillOpacity,
            StrokeOpacity: strokeOpacity,
            DisplayVisible: displayVisible,
            Visibility: visibility,
            EvenOddFillRule: evenOdd);
    }

    private static bool TryParseDouble(string raw, out double val)
        => double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out val);

    private static double VectorAngle(double ux, double uy, double vx, double vy)
    {
        double dot = (ux * vx) + (uy * vy);
        double len = Math.Sqrt(((ux * ux) + (uy * uy)) * ((vx * vx) + (vy * vy)));
        if (len < 1e-18)
        {
            return 0.0;
        }

        double ratio = Math.Clamp(dot / len, -1.0, 1.0);
        double ang = Math.Acos(ratio);
        return ((ux * vy) - (uy * vx)) < 0.0 ? -ang : ang;
    }

    private static bool PointsClose(Point2D a, Point2D b, double eps = 1e-6)
        => Math.Abs(a.X - b.X) <= eps && Math.Abs(a.Y - b.Y) <= eps;

    private static double Hypot(double dx, double dy) => Math.Sqrt((dx * dx) + (dy * dy));

    private sealed record CssRule(string Selector, Dictionary<string, string> Declarations);

    private sealed record ComputedSvgStyle(
        string Stroke,
        string Fill,
        string StrokeWidthRaw,
        double Opacity,
        double FillOpacity,
        double StrokeOpacity,
        bool DisplayVisible,
        string Visibility,
        bool EvenOddFillRule)
    {
        public bool HasStroke =>
            !IsNonePaint(Stroke) && StrokeOpacity > 0.0;

        public bool HasFill =>
            !IsNonePaint(Fill) && FillOpacity > 0.0;

        private static bool IsNonePaint(string? paint)
        {
            if (string.IsNullOrWhiteSpace(paint))
            {
                return true;
            }

            string t = paint.Trim().ToLowerInvariant();
            return t is "none" or "transparent";
        }
    }

    private sealed class PathDataScanner
    {
        private readonly string _data;
        private int _pos;

        public PathDataScanner(string data)
        {
            _data = data;
        }

        public bool TryReadCommand(out char cmd)
        {
            SkipSeparators();
            if (_pos >= _data.Length)
            {
                cmd = '\0';
                return false;
            }

            if (char.IsLetter(_data[_pos]))
            {
                cmd = _data[_pos++];
                return true;
            }

            cmd = '\0';
            return false;
        }

        public bool HasNextNumber()
        {
            SkipSeparators();
            if (_pos >= _data.Length)
            {
                return false;
            }

            char ch = _data[_pos];
            return ch is >= '0' and <= '9' or '+' or '-' or '.';
        }

        public bool ReadFlag()
        {
            SkipSeparators();
            if (_pos >= _data.Length)
            {
                throw new FormatException("Flag de arco inesperada no fim de path SVG.");
            }

            char ch = _data[_pos++];
            return ch != '0';
        }

        public double ReadNumber()
        {
            SkipSeparators();
            int start = _pos;
            if (_pos < _data.Length && _data[_pos] is '+' or '-')
            {
                _pos++;
            }

            bool seenDot = false;
            while (_pos < _data.Length)
            {
                char ch = _data[_pos];
                if (ch is >= '0' and <= '9')
                {
                    _pos++;
                }
                else if (ch == '.' && !seenDot)
                {
                    seenDot = true;
                    _pos++;
                }
                else
                {
                    break;
                }
            }

            if (_pos < _data.Length && _data[_pos] is 'e' or 'E')
            {
                int expPos = _pos + 1;
                if (expPos < _data.Length && _data[expPos] is '+' or '-')
                {
                    expPos++;
                }

                if (expPos < _data.Length && _data[expPos] is >= '0' and <= '9')
                {
                    _pos = expPos;
                    while (_pos < _data.Length && _data[_pos] is >= '0' and <= '9')
                    {
                        _pos++;
                    }
                }
            }

            string token = _data[start.._pos];
            if (string.IsNullOrEmpty(token) || !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                throw new FormatException($"Número inválido no path SVG perto do índice {start}.");
            }

            return val;
        }

        private void SkipSeparators()
        {
            while (_pos < _data.Length)
            {
                char ch = _data[_pos];
                if (char.IsWhiteSpace(ch) || ch == ',')
                {
                    _pos++;
                }
                else
                {
                    break;
                }
            }
        }
    }
}
