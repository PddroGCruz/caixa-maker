using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CaixaMaker.Cam.Svg;
using CaixaMaker.Core.Geometry;
using SkiaSharp;

namespace CaixaMaker.Cam.Exporters;

/// <summary>Formatos vetoriais e finais suportados pelo conversor 1:1.</summary>
public enum VectorExportFormat
{
    Svg,
    Dxf,
    Pdf,
}

/// <summary>
/// Conversor vetorial 1:1 em milímetros de SVG para DXF R2000 (<c>AC1015</c>) e PDF 1.4 (<c>%PDF-1.4</c>),
/// preservando camadas CNC, <c>CIRCLE</c>, arcos circulares via <c>bulge</c>, inversão Y CAD/PDF e imagens com alfa.
/// </summary>
public static class VectorConverter
{
    public const double MmToPt = 72.0 / 25.4;
    private const double ClosureEpsMm = 1e-4;

    private static readonly (string Name, int AciColor)[] DxfLayers =
    [
        ("CUT_EXTERNAL", 7),
        ("CUT_INTERNAL", 1),
        ("ENGRAVE_VECTOR", 5),
        ("ENGRAVE_FILL", 6),
        ("MARK", 3),
    ];

    private static readonly Regex RgbFuncRegex = new(
        @"rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Converte um SVG para os bytes do formato solicitado (<c>svg</c>, <c>dxf</c>, <c>pdf</c>).</summary>
    public static byte[] SvgToExportBytes(string svgContent, VectorExportFormat format)
        => format switch
        {
            VectorExportFormat.Svg => Encoding.UTF8.GetBytes(svgContent),
            VectorExportFormat.Dxf => Encoding.ASCII.GetBytes(SvgToDxf(svgContent)),
            VectorExportFormat.Pdf => SvgToPdf(svgContent),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato de exportação não suportado."),
        };

    /// <summary>Converte um documento SVG em arquivo DXF R2000 (<c>AC1015</c>) 1:1 em milímetros.</summary>
    public static string SvgToDxf(string svgContent)
    {
        if (string.IsNullOrWhiteSpace(svgContent))
        {
            throw new ArgumentException("Conteúdo SVG vazio.", nameof(svgContent));
        }

        ParsedSvgDocument parsed = SvgDocumentParser.Parse(svgContent);
        (double widthMm, double heightMm) = ResolveDocumentBoundsMm(parsed);

        var writer = new DxfR2000Writer(widthMm, heightMm);

        foreach (SvgShapeElement shape in parsed.Shapes)
        {
            IReadOnlyList<SvgSubpath> subpaths = SvgDocumentParser.SplitSubpaths(shape.Segments);
            string layerName = ClassifyDxfLayer(shape, subpaths.Count);

            foreach (SvgSubpath sub in subpaths)
            {
                if (string.Equals(shape.TagName, "circle", StringComparison.OrdinalIgnoreCase) &&
                    sub.IsClosed &&
                    TryDetectPureCircle(sub, out double cx, out double cy, out double r))
                {
                    writer.AddCircle(cx, cy, r, layerName);
                    continue;
                }

                List<DxfPolyVertex> vertices = FlattenSubpathToDxfPolyline(sub);
                if (vertices.Count >= 2)
                {
                    writer.AddLwPolyline(vertices, sub.IsClosed, layerName);
                }
            }
        }

        return writer.Build();
    }

    /// <summary>Converte um documento SVG em arquivo PDF 1.4 (<c>%PDF-1.4</c>) em escala real (mm -> pt).</summary>
    public static byte[] SvgToPdf(string svgContent)
    {
        if (string.IsNullOrWhiteSpace(svgContent))
        {
            throw new ArgumentException("Conteúdo SVG vazio.", nameof(svgContent));
        }

        ParsedSvgDocument parsed = SvgDocumentParser.Parse(svgContent);
        (double widthMm, double heightMm) = ResolveDocumentBoundsMm(parsed);

        double widthPt = widthMm * MmToPt;
        double heightPt = heightMm * MmToPt;

        var pdfBuilder = new PdfDocumentBuilder(widthPt, heightPt);
        var streamSb = new StringBuilder(4096);

        // 1. Imagens raster (camada de fundo/gravação raster)
        for (int i = 0; i < parsed.Images.Count; i++)
        {
            SvgRasterImageElement img = parsed.Images[i];
            if (!TryCreatePdfImageXObjects(img.ImageBytes, out PdfEncodedImage encoded))
            {
                continue;
            }

            string xobjName = pdfBuilder.AddImageXObject(encoded);

            // Mapeia o quadrado unitário [0,1]x[0,1] do XObject para os cantos reais transformados em pontos PDF (Y para cima)
            double originX = img.BottomLeftMm.X * MmToPt;
            double originY = (heightMm - img.BottomLeftMm.Y) * MmToPt;
            double ux = (img.BottomRightMm.X * MmToPt) - originX;
            double uy = ((heightMm - img.BottomRightMm.Y) * MmToPt) - originY;
            double vx = (img.TopLeftMm.X * MmToPt) - originX;
            double vy = ((heightMm - img.TopLeftMm.Y) * MmToPt) - originY;

            streamSb.Append(CultureInfo.InvariantCulture,
                $"q {PdfFmt(ux)} {PdfFmt(uy)} {PdfFmt(vx)} {PdfFmt(vy)} {PdfFmt(originX)} {PdfFmt(originY)} cm /{xobjName} Do Q\n");
        }

        // 2. Caminhos vetoriais
        foreach (SvgShapeElement shape in parsed.Shapes)
        {
            EmitShapeToPdfStream(shape, heightMm, streamSb);
        }

        return pdfBuilder.Build(Encoding.ASCII.GetBytes(streamSb.ToString()));
    }

    private static (double WidthMm, double HeightMm) ResolveDocumentBoundsMm(ParsedSvgDocument parsed)
    {
        if (parsed.WidthMm > 0.0 && parsed.HeightMm > 0.0)
        {
            return (parsed.WidthMm, parsed.HeightMm);
        }

        double maxX = 100.0;
        double maxY = 100.0;
        foreach (SvgShapeElement shape in parsed.Shapes)
        {
            foreach (SvgPathSegment seg in shape.Segments)
            {
                maxX = Math.Max(maxX, Math.Max(seg.Start.X, seg.End.X));
                maxY = Math.Max(maxY, Math.Max(seg.Start.Y, seg.End.Y));
            }
        }

        return (parsed.WidthMm > 0.0 ? parsed.WidthMm : maxX, parsed.HeightMm > 0.0 ? parsed.HeightMm : maxY);
    }

    private static string ClassifyDxfLayer(SvgShapeElement shape, int subpathCount)
    {
        if (TryParseColorRgb(shape.StrokeColor, out int sR, out int sG, out int sB))
        {
            string strokeHex = $"#{sR:X2}{sG:X2}{sB:X2}";
            if (strokeHex == "#B71C1C")
            {
                return "CUT_INTERNAL";
            }

            if (strokeHex == "#0D47A1")
            {
                return "ENGRAVE_VECTOR";
            }

            if (strokeHex == "#6A1B9A")
            {
                return "ENGRAVE_FILL";
            }

            if (strokeHex == "#1B5E20")
            {
                return "MARK";
            }
        }

        if (TryParseColorRgb(shape.FillColor, out int fR, out int fG, out int fB))
        {
            if ($"#{fR:X2}{fG:X2}{fB:X2}" == "#6A1B9A")
            {
                return "ENGRAVE_FILL";
            }
        }

        string token = $"{shape.DataLayer} {shape.ElementId} {shape.AncestorLayerId}".ToLowerInvariant();
        if (token.Contains("cut_internal", StringComparison.Ordinal))
        {
            return "CUT_INTERNAL";
        }

        if (token.Contains("cut_external", StringComparison.Ordinal))
        {
            return "CUT_EXTERNAL";
        }

        if (token.Contains("engrave_fill", StringComparison.Ordinal))
        {
            return "ENGRAVE_FILL";
        }

        if (token.Contains("mark", StringComparison.Ordinal))
        {
            return "MARK";
        }

        if (token.Contains("engrave_vector", StringComparison.Ordinal))
        {
            return "ENGRAVE_VECTOR";
        }

        if (shape.HasFill && !shape.HasStroke)
        {
            if (TryParseColorRgb(shape.FillColor, out int fr, out int fg, out int fb))
            {
                string byFill = ClassifyRgbToLayer(fr, fg, fb, fallback: "ENGRAVE_FILL");
                return byFill == "CUT_EXTERNAL" ? "ENGRAVE_FILL" : byFill;
            }

            return "ENGRAVE_FILL";
        }

        if (TryParseColorRgb(shape.StrokeColor, out int sr, out int sg, out int sb))
        {
            return ClassifyRgbToLayer(sr, sg, sb, fallback: "CUT_EXTERNAL");
        }

        if (string.Equals(shape.TagName, "circle", StringComparison.OrdinalIgnoreCase) || subpathCount > 1)
        {
            return "CUT_INTERNAL";
        }

        return "CUT_EXTERNAL";
    }

    private static string ClassifyRgbToLayer(int r, int g, int b, string fallback)
    {
        // Preto ou cinza neutro escuro -> corte externo
        if (Math.Max(r, Math.Max(g, b)) < 70 && Math.Abs(r - g) < 25 && Math.Abs(g - b) < 25)
        {
            return "CUT_EXTERNAL";
        }

        // Vermelho dominante -> corte interno
        if (r > g + 50 && r > b + 50)
        {
            return "CUT_INTERNAL";
        }

        // Magenta / Roxo ANTES de azul puro (ex.: #6A1B9A tem B > R + 40, mas R e B estão muito acima de G)
        if (r > g + 35 && b > g + 35)
        {
            return "ENGRAVE_FILL";
        }

        // Azul dominante -> gravação vetorial
        if (b > r + 40 && b > g + 30)
        {
            return "ENGRAVE_VECTOR";
        }

        // Verde dominante -> marcação
        if (g > r + 35 && g > b + 35)
        {
            return "MARK";
        }

        return fallback;
    }

    private static bool TryDetectPureCircle(SvgSubpath subpath, out double cx, out double cy, out double r)
    {
        cx = cy = r = 0.0;
        List<SvgPathSegment> arcs = subpath.Segments
            .Where(s => s.Kind != SvgSegmentKind.Move && s.Kind != SvgSegmentKind.Close)
            .ToList();

        if (arcs.Count == 0 || arcs.Any(a => a.Kind != SvgSegmentKind.Arc))
        {
            return false;
        }

        SvgPathSegment first = arcs[0];
        if (Math.Abs(first.Rx - first.Ry) > 1e-4 * Math.Max(1.0, first.Rx) || first.Rx <= 1e-6)
        {
            return false;
        }

        double refCx = first.ArcCenter.X;
        double refCy = first.ArcCenter.Y;
        double refR = first.Rx;
        double totalSweep = 0.0;

        foreach (SvgPathSegment arc in arcs)
        {
            if (Math.Abs(arc.Rx - refR) > 1e-3 || Math.Abs(arc.Ry - refR) > 1e-3)
            {
                return false;
            }

            if (Hypot(arc.ArcCenter.X - refCx, arc.ArcCenter.Y - refCy) > 1e-3)
            {
                return false;
            }

            totalSweep += arc.ArcDeltaRad;
        }

        if (Math.Abs(Math.Abs(totalSweep) - (2.0 * Math.PI)) > 1e-3)
        {
            return false;
        }

        cx = refCx;
        cy = refCy;
        r = refR;
        return true;
    }

    private static List<DxfPolyVertex> FlattenSubpathToDxfPolyline(SvgSubpath sub)
    {
        var pts = new List<DxfPolyVertex>();

        void AddPoint(Point2D p, double bulgeOnPrev)
        {
            if (pts.Count > 0 && Math.Abs(bulgeOnPrev) > 1e-9)
            {
                DxfPolyVertex last = pts[^1];
                pts[^1] = last with { Bulge = bulgeOnPrev };
            }

            if (pts.Count == 0 || Hypot(p.X - pts[^1].X, p.Y - pts[^1].Y) > ClosureEpsMm)
            {
                pts.Add(new DxfPolyVertex(p.X, p.Y, 0.0));
            }
        }

        foreach (SvgPathSegment seg in sub.Segments)
        {
            switch (seg.Kind)
            {
                case SvgSegmentKind.Move:
                    if (pts.Count == 0)
                    {
                        pts.Add(new DxfPolyVertex(seg.End.X, seg.End.Y, 0.0));
                    }
                    break;

                case SvgSegmentKind.Line:
                    if (pts.Count == 0)
                    {
                        pts.Add(new DxfPolyVertex(seg.Start.X, seg.Start.Y, 0.0));
                    }

                    AddPoint(seg.End, 0.0);
                    break;

                case SvgSegmentKind.Quad:
                    EmitAdaptiveCurvePoints(seg.Start, seg.End, seg.Control1, default, isCubic: false, AddPoint);
                    break;

                case SvgSegmentKind.Cubic:
                    EmitAdaptiveCurvePoints(seg.Start, seg.End, seg.Control1, seg.Control2, isCubic: true, AddPoint);
                    break;

                case SvgSegmentKind.Arc:
                {
                    bool isCircular = Math.Abs(seg.Rx - seg.Ry) <= 1e-5 * Math.Max(1.0, seg.Rx);
                    if (isCircular && seg.Rx > 1e-6)
                    {
                        if (pts.Count == 0)
                        {
                            pts.Add(new DxfPolyVertex(seg.Start.X, seg.Start.Y, 0.0));
                        }

                        // Divide arcos >= pi em chunks <= pi/2 para manter precisão do bulge CAD
                        int chunks = Math.Max(1, (int)Math.Ceiling(Math.Abs(seg.ArcDeltaRad) / (Math.PI * 0.5)));
                        double chunkDelta = seg.ArcDeltaRad / chunks;
                        double bulge = Math.Tan(-chunkDelta / 4.0); // negativo por inversão do eixo Y no CAD

                        for (int c = 1; c <= chunks; c++)
                        {
                            Point2D nextPt;
                            if (c == chunks)
                            {
                                nextPt = seg.End;
                            }
                            else
                            {
                                double theta = seg.ArcStartRad + (c * chunkDelta);
                                nextPt = new Point2D(
                                    seg.ArcCenter.X + (seg.Rx * Math.Cos(theta)),
                                    seg.ArcCenter.Y + (seg.Ry * Math.Sin(theta)));
                            }

                            AddPoint(nextPt, bulge);
                        }
                    }
                    else
                    {
                        foreach (SvgPathSegment cubic in SvgDocumentParser.ArcToCubicBeziers(seg))
                        {
                            EmitAdaptiveCurvePoints(cubic.Start, cubic.End, cubic.Control1, cubic.Control2, isCubic: true, AddPoint);
                        }
                    }
                    break;
                }
            }
        }

        if (sub.IsClosed && pts.Count >= 2)
        {
            if (Hypot(pts[^1].X - pts[0].X, pts[^1].Y - pts[0].Y) <= ClosureEpsMm)
            {
                // Se o último vértice fechou exatamente sobre pts[0] sem bulge nele próprio, removemos duplicado preservando flag closed=1
                if (Math.Abs(pts[^1].Bulge) <= 1e-9)
                {
                    pts.RemoveAt(pts.Count - 1);
                }
            }
        }

        return pts;
    }

    private static void EmitAdaptiveCurvePoints(
        Point2D p0,
        Point2D p3,
        Point2D c1,
        Point2D c2,
        bool isCubic,
        Action<Point2D, double> addPoint)
    {
        double lenEst = isCubic ? EstimateCubicLength(p0, c1, c2, p3) : EstimateQuadLength(p0, c1, p3);
        int steps = Math.Clamp((int)Math.Ceiling(lenEst / 0.25), 4, 48);

        addPoint(p0, 0.0);
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            Point2D pt = isCubic
                ? SvgDocumentParser.EvaluateCubic(p0, c1, c2, p3, t)
                : SvgDocumentParser.EvaluateQuad(p0, c1, p3, t);
            addPoint(pt, 0.0);
        }
    }

    private static double EstimateCubicLength(Point2D p0, Point2D p1, Point2D p2, Point2D p3)
    {
        double chord = Hypot(p3.X - p0.X, p3.Y - p0.Y);
        double net = Hypot(p1.X - p0.X, p1.Y - p0.Y)
            + Hypot(p2.X - p1.X, p2.Y - p1.Y)
            + Hypot(p3.X - p2.X, p3.Y - p2.Y);
        return 0.5 * (chord + net);
    }

    private static double EstimateQuadLength(Point2D p0, Point2D p1, Point2D p2)
    {
        double chord = Hypot(p2.X - p0.X, p2.Y - p0.Y);
        double net = Hypot(p1.X - p0.X, p1.Y - p0.Y) + Hypot(p2.X - p1.X, p2.Y - p1.Y);
        return 0.5 * (chord + net);
    }

    private static double Hypot(double dx, double dy) => Math.Sqrt((dx * dx) + (dy * dy));

    private static void EmitShapeToPdfStream(SvgShapeElement shape, double pageHeightMm, StringBuilder sb)
    {
        bool doStroke = shape.HasStroke;
        bool doFill = shape.HasFill;
        if (!doStroke && !doFill)
        {
            return;
        }

        if (doStroke)
        {
            (double r, double g, double b) = ParseColorUnitRgb(shape.StrokeColor, (0.0, 0.0, 0.0));
            double lwPt = Math.Max((shape.StrokeWidthMm ?? 0.1) * MmToPt, 0.1);
            sb.Append(CultureInfo.InvariantCulture, $"{PdfFmt(r)} {PdfFmt(g)} {PdfFmt(b)} RG {PdfFmt(lwPt)} w\n");
        }

        if (doFill)
        {
            (double r, double g, double b) = ParseColorUnitRgb(shape.FillColor, (0.0, 0.0, 0.0));
            sb.Append(CultureInfo.InvariantCulture, $"{PdfFmt(r)} {PdfFmt(g)} {PdfFmt(b)} rg\n");
        }

        IReadOnlyList<SvgSubpath> subpaths = SvgDocumentParser.SplitSubpaths(shape.Segments);
        int emittedSegments = 0;

        foreach (SvgSubpath sub in subpaths)
        {
            foreach (SvgPathSegment seg in sub.Segments)
            {
                switch (seg.Kind)
                {
                    case SvgSegmentKind.Move:
                        AppendPdfMove(sb, seg.End, pageHeightMm);
                        break;
                    case SvgSegmentKind.Line:
                        AppendPdfLine(sb, seg.End, pageHeightMm);
                        emittedSegments++;
                        break;
                    case SvgSegmentKind.Quad:
                    {
                        // Elevação exata de grau quadrática -> cúbica
                        Point2D c1 = new(
                            seg.Start.X + ((2.0 / 3.0) * (seg.Control1.X - seg.Start.X)),
                            seg.Start.Y + ((2.0 / 3.0) * (seg.Control1.Y - seg.Start.Y)));
                        Point2D c2 = new(
                            seg.End.X + ((2.0 / 3.0) * (seg.Control1.X - seg.End.X)),
                            seg.End.Y + ((2.0 / 3.0) * (seg.Control1.Y - seg.End.Y)));
                        AppendPdfCubic(sb, c1, c2, seg.End, pageHeightMm);
                        emittedSegments++;
                        break;
                    }
                    case SvgSegmentKind.Cubic:
                        AppendPdfCubic(sb, seg.Control1, seg.Control2, seg.End, pageHeightMm);
                        emittedSegments++;
                        break;
                    case SvgSegmentKind.Arc:
                        foreach (SvgPathSegment cubic in SvgDocumentParser.ArcToCubicBeziers(seg))
                        {
                            AppendPdfCubic(sb, cubic.Control1, cubic.Control2, cubic.End, pageHeightMm);
                            emittedSegments++;
                        }
                        break;
                }
            }

            if (sub.IsClosed)
            {
                sb.Append("h\n");
            }
        }

        if (emittedSegments == 0)
        {
            sb.Append("n\n");
            return;
        }

        string op = (doFill, doStroke, shape.EvenOddFillRule) switch
        {
            (true, true, true) => "B*",
            (true, true, false) => "B",
            (true, false, true) => "f*",
            (true, false, false) => "f",
            _ => "S",
        };
        sb.Append(op).Append('\n');
    }

    private static void AppendPdfMove(StringBuilder sb, Point2D pt, double pageHeightMm)
        => sb.Append(CultureInfo.InvariantCulture, $"{PdfFmt(pt.X * MmToPt)} {PdfFmt((pageHeightMm - pt.Y) * MmToPt)} m\n");

    private static void AppendPdfLine(StringBuilder sb, Point2D pt, double pageHeightMm)
        => sb.Append(CultureInfo.InvariantCulture, $"{PdfFmt(pt.X * MmToPt)} {PdfFmt((pageHeightMm - pt.Y) * MmToPt)} l\n");

    private static void AppendPdfCubic(StringBuilder sb, Point2D c1, Point2D c2, Point2D end, double pageHeightMm)
        => sb.Append(CultureInfo.InvariantCulture,
            $"{PdfFmt(c1.X * MmToPt)} {PdfFmt((pageHeightMm - c1.Y) * MmToPt)} " +
            $"{PdfFmt(c2.X * MmToPt)} {PdfFmt((pageHeightMm - c2.Y) * MmToPt)} " +
            $"{PdfFmt(end.X * MmToPt)} {PdfFmt((pageHeightMm - end.Y) * MmToPt)} c\n");

    private static (double R, double G, double B) ParseColorUnitRgb(string? color, (double R, double G, double B) fallback)
    {
        if (TryParseColorRgb(color, out int r, out int g, out int b))
        {
            return (r / 255.0, g / 255.0, b / 255.0);
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
            case "black":
                r = g = b = 0;
                return true;
            case "white":
                r = g = b = 255;
                return true;
            case "red":
                r = 255;
                return true;
            case "green":
                g = 128;
                return true;
            case "lime":
                g = 255;
                return true;
            case "blue":
                b = 255;
                return true;
            case "purple":
                r = 128;
                b = 128;
                return true;
            case "magenta":
                r = 255;
                b = 255;
                return true;
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
                int.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                int.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                int.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            {
                return true;
            }
        }

        Match m = RgbFuncRegex.Match(t);
        if (m.Success)
        {
            r = Math.Clamp(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 0, 255);
            g = Math.Clamp(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), 0, 255);
            b = Math.Clamp(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), 0, 255);
            return true;
        }

        return false;
    }

    private static bool TryCreatePdfImageXObjects(byte[] rawBytes, out PdfEncodedImage encoded)
    {
        encoded = default!;
        using SKBitmap? bmp = SKBitmap.Decode(rawBytes);
        if (bmp is null || bmp.Width <= 0 || bmp.Height <= 0)
        {
            return false;
        }

        int w = bmp.Width;
        int h = bmp.Height;
        int pxCount = w * h;
        byte[] rgbRaw = new byte[pxCount * 3];
        byte[] alphaRaw = new byte[pxCount];
        bool hasAlpha = false;

        int iRgb = 0;
        int iAlpha = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                rgbRaw[iRgb++] = c.Red;
                rgbRaw[iRgb++] = c.Green;
                rgbRaw[iRgb++] = c.Blue;
                alphaRaw[iAlpha++] = c.Alpha;
                if (c.Alpha != 255)
                {
                    hasAlpha = true;
                }
            }
        }

        encoded = new PdfEncodedImage(
            Width: w,
            Height: h,
            CompressedRgb: ZlibCompress(rgbRaw),
            CompressedAlpha: hasAlpha ? ZlibCompress(alphaRaw) : null);
        return true;
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return ms.ToArray();
    }

    private static string PdfFmt(double val)
    {
        if (Math.Abs(val) < 5e-5)
        {
            return "0";
        }

        string s = val.ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private readonly record struct DxfPolyVertex(double X, double Y, double Bulge);

    private sealed record PdfEncodedImage(int Width, int Height, byte[] CompressedRgb, byte[]? CompressedAlpha);

    /// <summary>Escritor DXF R2000 (<c>AC1015</c>) compatível com <c>ezdxf</c> e softwares CAM/CAD.</summary>
    private sealed class DxfR2000Writer
    {
        private readonly double _widthMm;
        private readonly double _heightMm;
        private readonly StringBuilder _entities = new(4096);
        private int _nextHandle = 0x100;
        private double _minX = double.PositiveInfinity;
        private double _minY = double.PositiveInfinity;
        private double _maxX = double.NegativeInfinity;
        private double _maxY = double.NegativeInfinity;

        public DxfR2000Writer(double widthMm, double heightMm)
        {
            _widthMm = widthMm;
            _heightMm = heightMm;
        }

        public void AddCircle(double cxSvg, double cySvg, double r, string layer)
        {
            double cx = cxSvg;
            double cy = _heightMm - cySvg;
            IncludeBounds(cx - r, cy - r);
            IncludeBounds(cx + r, cy + r);

            string handle = AllocHandle();
            AppendPair(0, "CIRCLE");
            AppendPair(5, handle);
            AppendPair(330, "1F");
            AppendPair(100, "AcDbEntity");
            AppendPair(8, layer);
            AppendPair(100, "AcDbCircle");
            AppendFloat(10, cx);
            AppendFloat(20, cy);
            AppendFloat(30, 0.0);
            AppendFloat(40, r);
        }

        public void AddLwPolyline(IReadOnlyList<DxfPolyVertex> vertices, bool isClosed, string layer)
        {
            string handle = AllocHandle();
            AppendPair(0, "LWPOLYLINE");
            AppendPair(5, handle);
            AppendPair(330, "1F");
            AppendPair(100, "AcDbEntity");
            AppendPair(8, layer);
            AppendPair(100, "AcDbPolyline");
            AppendPair(90, vertices.Count.ToString(CultureInfo.InvariantCulture));
            AppendPair(70, isClosed ? "1" : "0");
            AppendFloat(43, 0.0);

            foreach (DxfPolyVertex v in vertices)
            {
                double x = v.X;
                double y = _heightMm - v.Y;
                IncludeBounds(x, y);
                AppendFloat(10, x);
                AppendFloat(20, y);
                if (Math.Abs(v.Bulge) > 1e-8)
                {
                    AppendFloat(42, v.Bulge);
                }
            }
        }

        public string Build()
        {
            double extMinX = double.IsFinite(_minX) ? _minX : 0.0;
            double extMinY = double.IsFinite(_minY) ? _minY : 0.0;
            double extMaxX = double.IsFinite(_maxX) ? _maxX : _widthMm;
            double extMaxY = double.IsFinite(_maxY) ? _maxY : _heightMm;
            string handseed = _nextHandle.ToString("X", CultureInfo.InvariantCulture);

            var sb = new StringBuilder(8192 + _entities.Length);
            WriteHeader(sb, handseed, extMinX, extMinY, extMaxX, extMaxY);
            WriteClasses(sb);
            WriteTables(sb);
            WriteBlocks(sb);
            sb.Append("  0\nSECTION\n  2\nENTITIES\n");
            sb.Append(_entities);
            sb.Append("  0\nENDSEC\n");
            WriteObjects(sb);
            sb.Append("  0\nEOF\n");
            return sb.ToString();
        }

        private string AllocHandle()
        {
            string h = _nextHandle.ToString("X", CultureInfo.InvariantCulture);
            _nextHandle++;
            return h;
        }

        private void IncludeBounds(double x, double y)
        {
            _minX = Math.Min(_minX, x);
            _minY = Math.Min(_minY, y);
            _maxX = Math.Max(_maxX, x);
            _maxY = Math.Max(_maxY, y);
        }

        private void AppendPair(int code, string value)
        {
            _entities.Append(code.ToString(CultureInfo.InvariantCulture).PadLeft(3))
                .Append('\n')
                .Append(value)
                .Append('\n');
        }

        private void AppendFloat(int code, double val)
            => AppendPair(code, val.ToString("0.######", CultureInfo.InvariantCulture));

        private void WriteHeader(
            StringBuilder sb,
            string handseed,
            double extMinX,
            double extMinY,
            double extMaxX,
            double extMaxY)
        {
            sb.Append("  0\nSECTION\n  2\nHEADER\n");
            G(sb, 9, "$ACADVER"); G(sb, 1, "AC1015");
            G(sb, 9, "$DWGCODEPAGE"); G(sb, 3, "ANSI_1252");
            G(sb, 9, "$INSBASE"); GF(sb, 10, 0.0); GF(sb, 20, 0.0); GF(sb, 30, 0.0);
            G(sb, 9, "$EXTMIN"); GF(sb, 10, extMinX); GF(sb, 20, extMinY); GF(sb, 30, 0.0);
            G(sb, 9, "$EXTMAX"); GF(sb, 10, extMaxX); GF(sb, 20, extMaxY); GF(sb, 30, 0.0);
            G(sb, 9, "$LIMMIN"); GF(sb, 10, 0.0); GF(sb, 20, 0.0);
            G(sb, 9, "$LIMMAX"); GF(sb, 10, _widthMm); GF(sb, 20, _heightMm);
            G(sb, 9, "$LUNITS"); G(sb, 70, "2");
            G(sb, 9, "$LUPREC"); G(sb, 70, "4");
            G(sb, 9, "$MEASUREMENT"); G(sb, 70, "1");
            G(sb, 9, "$INSUNITS"); G(sb, 70, "4");
            G(sb, 9, "$HANDSEED"); G(sb, 5, handseed);
            sb.Append("  0\nENDSEC\n");
        }

        private static void WriteClasses(StringBuilder sb)
            => sb.Append("  0\nSECTION\n  2\nCLASSES\n  0\nENDSEC\n");

        private void WriteTables(StringBuilder sb)
        {
            sb.Append("  0\nSECTION\n  2\nTABLES\n");

            // VPORT
            G(sb, 0, "TABLE"); G(sb, 2, "VPORT"); G(sb, 5, "8"); G(sb, 330, "0"); G(sb, 100, "AcDbSymbolTable"); G(sb, 70, "1");
            G(sb, 0, "VPORT"); G(sb, 5, "21"); G(sb, 330, "8"); G(sb, 100, "AcDbSymbolTableRecord"); G(sb, 100, "AcDbViewportTableRecord");
            G(sb, 2, "*ACTIVE"); G(sb, 70, "0");
            GF(sb, 10, 0.0); GF(sb, 20, 0.0); GF(sb, 11, 1.0); GF(sb, 21, 1.0);
            GF(sb, 12, _widthMm * 0.5); GF(sb, 22, _heightMm * 0.5);
            GF(sb, 40, Math.Max(_heightMm, 100.0)); GF(sb, 41, 1.5);
            G(sb, 0, "ENDTAB");

            // LTYPE
            G(sb, 0, "TABLE"); G(sb, 2, "LTYPE"); G(sb, 5, "5"); G(sb, 330, "0"); G(sb, 100, "AcDbSymbolTable"); G(sb, 70, "3");
            WriteLType(sb, "14", "BYBLOCK");
            WriteLType(sb, "15", "BYLAYER");
            WriteLType(sb, "16", "CONTINUOUS");
            G(sb, 0, "ENDTAB");

            // LAYER (0 + 5 camadas CNC)
            G(sb, 0, "TABLE"); G(sb, 2, "LAYER"); G(sb, 5, "2"); G(sb, 330, "0"); G(sb, 100, "AcDbSymbolTable");
            G(sb, 70, (1 + DxfLayers.Length).ToString(CultureInfo.InvariantCulture));
            WriteLayerRecord(sb, "10", "0", 7);
            for (int i = 0; i < DxfLayers.Length; i++)
            {
                string h = (0x30 + i).ToString("X", CultureInfo.InvariantCulture);
                WriteLayerRecord(sb, h, DxfLayers[i].Name, DxfLayers[i].AciColor);
            }
            G(sb, 0, "ENDTAB");

            // STYLE
            G(sb, 0, "TABLE"); G(sb, 2, "STYLE"); G(sb, 5, "3"); G(sb, 330, "0"); G(sb, 100, "AcDbSymbolTable"); G(sb, 70, "1");
            G(sb, 0, "STYLE"); G(sb, 5, "11"); G(sb, 330, "3"); G(sb, 100, "AcDbSymbolTableRecord"); G(sb, 100, "AcDbTextStyleTableRecord");
            G(sb, 2, "STANDARD"); G(sb, 70, "0"); GF(sb, 40, 0.0); GF(sb, 41, 1.0); G(sb, 3, "txt");
            G(sb, 0, "ENDTAB");

            // VIEW
            G(sb, 0, "TABLE"); G(sb, 2, "VIEW"); G(sb, 5, "6"); G(sb, 330, "0"); G(sb, 100, "AcDbSymbolTable"); G(sb, 70, "0"); G(sb, 0, "ENDTAB");
            // UCS
            G(sb, 0, "TABLE"); G(sb, 2, "UCS"); G(sb, 5, "7"); G(sb, 330, "0"); G(sb, 100, "AcDbSymbolTable"); G(sb, 70, "0"); G(sb, 0, "ENDTAB");
            // APPID
            G(sb, 0, "TABLE"); G(sb, 2, "APPID"); G(sb, 5, "9"); G(sb, 330, "0"); G(sb, 100, "AcDbSymbolTable"); G(sb, 70, "1");
            G(sb, 0, "APPID"); G(sb, 5, "12"); G(sb, 330, "9"); G(sb, 100, "AcDbSymbolTableRecord"); G(sb, 100, "AcDbRegAppTableRecord"); G(sb, 2, "ACAD"); G(sb, 70, "0");
            G(sb, 0, "ENDTAB");
            // DIMSTYLE
            G(sb, 0, "TABLE"); G(sb, 2, "DIMSTYLE"); G(sb, 5, "A"); G(sb, 330, "0"); G(sb, 100, "AcDbSymbolTable"); G(sb, 100, "AcDbDimStyleTable"); G(sb, 70, "1");
            G(sb, 0, "DIMSTYLE"); G(sb, 105, "27"); G(sb, 330, "A"); G(sb, 100, "AcDbSymbolTableRecord"); G(sb, 100, "AcDbDimStyleTableRecord"); G(sb, 2, "STANDARD"); G(sb, 70, "0");
            G(sb, 0, "ENDTAB");
            // BLOCK_RECORD
            G(sb, 0, "TABLE"); G(sb, 2, "BLOCK_RECORD"); G(sb, 5, "1"); G(sb, 330, "0"); G(sb, 100, "AcDbSymbolTable"); G(sb, 70, "2");
            WriteBlockRecord(sb, "1F", "*Model_Space");
            WriteBlockRecord(sb, "1E", "*Paper_Space");
            G(sb, 0, "ENDTAB");

            sb.Append("  0\nENDSEC\n");
        }

        private static void WriteLType(StringBuilder sb, string handle, string name)
        {
            G(sb, 0, "LTYPE"); G(sb, 5, handle); G(sb, 330, "5"); G(sb, 100, "AcDbSymbolTableRecord"); G(sb, 100, "AcDbLinetypeTableRecord");
            G(sb, 2, name); G(sb, 70, "0"); G(sb, 3, "Solid line"); G(sb, 72, "65"); G(sb, 73, "0"); GF(sb, 40, 0.0);
        }

        private static void WriteLayerRecord(StringBuilder sb, string handle, string name, int aciColor)
        {
            G(sb, 0, "LAYER"); G(sb, 5, handle); G(sb, 330, "2"); G(sb, 100, "AcDbSymbolTableRecord"); G(sb, 100, "AcDbLayerTableRecord");
            G(sb, 2, name); G(sb, 70, "0"); G(sb, 62, aciColor.ToString(CultureInfo.InvariantCulture)); G(sb, 6, "CONTINUOUS");
        }

        private static void WriteBlockRecord(StringBuilder sb, string handle, string name)
        {
            G(sb, 0, "BLOCK_RECORD"); G(sb, 5, handle); G(sb, 330, "1"); G(sb, 100, "AcDbSymbolTableRecord"); G(sb, 100, "AcDbBlockTableRecord");
            G(sb, 2, name);
        }

        private static void WriteBlocks(StringBuilder sb)
        {
            sb.Append("  0\nSECTION\n  2\nBLOCKS\n");
            WriteBlockDef(sb, "20", "1F", "21B", "*Model_Space");
            WriteBlockDef(sb, "1C", "1E", "1D", "*Paper_Space");
            sb.Append("  0\nENDSEC\n");
        }

        private static void WriteBlockDef(StringBuilder sb, string hBlock, string ownerHandle, string hEndBlk, string name)
        {
            G(sb, 0, "BLOCK"); G(sb, 5, hBlock); G(sb, 330, ownerHandle); G(sb, 100, "AcDbEntity"); G(sb, 8, "0"); G(sb, 100, "AcDbBlockBegin");
            G(sb, 2, name); G(sb, 70, "0"); GF(sb, 10, 0.0); GF(sb, 20, 0.0); GF(sb, 30, 0.0); G(sb, 3, name); G(sb, 1, "");
            G(sb, 0, "ENDBLK"); G(sb, 5, hEndBlk); G(sb, 330, ownerHandle); G(sb, 100, "AcDbEntity"); G(sb, 8, "0"); G(sb, 100, "AcDbBlockEnd");
        }

        private static void WriteObjects(StringBuilder sb)
        {
            sb.Append("  0\nSECTION\n  2\nOBJECTS\n");
            G(sb, 0, "DICTIONARY"); G(sb, 5, "C"); G(sb, 330, "0"); G(sb, 100, "AcDbDictionary"); G(sb, 281, "1");
            G(sb, 3, "ACAD_GROUP"); G(sb, 350, "D");
            G(sb, 0, "DICTIONARY"); G(sb, 5, "D"); G(sb, 330, "C"); G(sb, 100, "AcDbDictionary"); G(sb, 281, "1");
            sb.Append("  0\nENDSEC\n");
        }

        private static void G(StringBuilder sb, int code, string value)
        {
            sb.Append(code.ToString(CultureInfo.InvariantCulture).PadLeft(3))
                .Append('\n')
                .Append(value)
                .Append('\n');
        }

        private static void GF(StringBuilder sb, int code, double val)
            => G(sb, code, val.ToString("0.######", CultureInfo.InvariantCulture));
    }

    /// <summary>Construtor binário de arquivo PDF 1.4 (<c>%PDF-1.4</c>) com FlateDecode e tabela xref precisa.</summary>
    private sealed class PdfDocumentBuilder
    {
        private readonly double _widthPt;
        private readonly double _heightPt;
        private readonly List<(string Name, int ObjId)> _imageResources = new();
        private readonly List<byte[]> _extraObjects = new();
        private int _nextObjId = 5; // 1=Catalog, 2=Pages, 3=Page, 4=Content

        public PdfDocumentBuilder(double widthPt, double heightPt)
        {
            _widthPt = widthPt;
            _heightPt = heightPt;
        }

        public string AddImageXObject(PdfEncodedImage img)
        {
            string resName = $"Im{_imageResources.Count + 1}";
            int imgObjId = _nextObjId++;
            int? smaskObjId = null;

            if (img.CompressedAlpha is not null)
            {
                smaskObjId = _nextObjId++;
            }

            string smaskRef = smaskObjId.HasValue ? $" /SMask {smaskObjId.Value} 0 R" : string.Empty;
            byte[] imgDictHeader = Encoding.ASCII.GetBytes(string.Format(
                CultureInfo.InvariantCulture,
                "<< /Type /XObject /Subtype /Image /Width {0} /Height {1} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {2}{3} >>\nstream\n",
                img.Width,
                img.Height,
                img.CompressedRgb.Length,
                smaskRef));
            byte[] endStream = Encoding.ASCII.GetBytes("\nendstream");

            byte[] imgBody = Combine(imgDictHeader, img.CompressedRgb, endStream);
            _extraObjects.Add(WrapPdfObject(imgObjId, imgBody));

            if (smaskObjId.HasValue && img.CompressedAlpha is not null)
            {
                byte[] alphaHeader = Encoding.ASCII.GetBytes(string.Format(
                    CultureInfo.InvariantCulture,
                    "<< /Type /XObject /Subtype /Image /Width {0} /Height {1} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length {2} >>\nstream\n",
                    img.Width,
                    img.Height,
                    img.CompressedAlpha.Length));
                byte[] alphaBody = Combine(alphaHeader, img.CompressedAlpha, endStream);
                _extraObjects.Add(WrapPdfObject(smaskObjId.Value, alphaBody));
            }

            _imageResources.Add((resName, imgObjId));
            return resName;
        }

        public byte[] Build(byte[] contentRawBytes)
        {
            byte[] compressedContent = ZlibCompress(contentRawBytes);
            string xobjEntry = _imageResources.Count > 0
                ? "/XObject << " + string.Join(' ', _imageResources.Select(r => $"/{r.Name} {r.ObjId} 0 R")) + " >>"
                : string.Empty;

            var objects = new List<(int Id, byte[] Body)>
            {
                (1, WrapPdfObject(1, Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"))),
                (2, WrapPdfObject(2, Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"))),
                (3, WrapPdfObject(3, Encoding.ASCII.GetBytes(string.Format(
                    CultureInfo.InvariantCulture,
                    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0} {1}] /Resources << {2} >> /Contents 4 0 R >>",
                    PdfFmt(_widthPt),
                    PdfFmt(_heightPt),
                    xobjEntry)))),
                (4, WrapPdfObject(4, Combine(
                    Encoding.ASCII.GetBytes(string.Format(
                        CultureInfo.InvariantCulture,
                        "<< /Filter /FlateDecode /Length {0} >>\nstream\n",
                        compressedContent.Length)),
                    compressedContent,
                    Encoding.ASCII.GetBytes("\nendstream")))),
            };

            for (int i = 0; i < _extraObjects.Count; i++)
            {
                objects.Add((5 + i, _extraObjects[i]));
            }

            using var ms = new MemoryStream();
            byte[] header = [.. "%PDF-1.4\n%"u8, 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n'];
            ms.Write(header);

            var offsets = new long[objects.Count + 1];
            foreach ((int id, byte[] objBytes) in objects)
            {
                offsets[id] = ms.Position;
                ms.Write(objBytes);
            }

            long xrefStart = ms.Position;
            int totalEntries = objects.Count + 1;
            var xrefSb = new StringBuilder();
            xrefSb.Append(CultureInfo.InvariantCulture, $"xref\n0 {totalEntries}\n");
            xrefSb.Append("0000000000 65535 f \n");
            for (int i = 1; i < totalEntries; i++)
            {
                xrefSb.Append(CultureInfo.InvariantCulture, $"{offsets[i]:0000000000} 00000 n \n");
            }

            xrefSb.Append(CultureInfo.InvariantCulture,
                $"trailer\n<< /Size {totalEntries} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF\n");

            ms.Write(Encoding.ASCII.GetBytes(xrefSb.ToString()));
            return ms.ToArray();
        }

        private static byte[] WrapPdfObject(int objId, byte[] inner)
            => Combine(
                Encoding.ASCII.GetBytes($"{objId} 0 obj\n"),
                inner,
                Encoding.ASCII.GetBytes("\nendobj\n"));

        private static byte[] Combine(params byte[][] arrays)
        {
            int len = arrays.Sum(a => a.Length);
            byte[] res = new byte[len];
            int pos = 0;
            foreach (byte[] arr in arrays)
            {
                Buffer.BlockCopy(arr, 0, res, pos, arr.Length);
                pos += arr.Length;
            }

            return res;
        }
    }
}
