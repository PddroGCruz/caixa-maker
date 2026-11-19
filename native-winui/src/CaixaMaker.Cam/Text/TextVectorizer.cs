using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using CaixaMaker.Core.Personalization;
using SkiaSharp;

namespace CaixaMaker.Cam.Text;

/// <summary>Resultado da vetorização tipográfica de um bloco de texto em milímetros.</summary>
public sealed record TextVectorizationResult(
    IReadOnlyList<VectorPathItem> Paths,
    double WidthMm,
    double HeightMm,
    string ResolvedFontName,
    bool FontFallbackUsed);

/// <summary>
/// Converte textos em caminhos vetoriais SVG (<see cref="VectorPathItem"/>) em milímetros,
/// com suporte a múltiplas linhas, kerning OpenType, alinhamento horizontal e hachura CNC.
/// </summary>
public sealed class TextVectorizer
{
    private const uint TagKern = 0x6B65726E; // 'kern'
    private const uint TagHhea = 0x68686561; // 'hhea'
    private const uint TagHmtx = 0x686D7478; // 'hmtx'

    private readonly FontResolver _resolver;

    public TextVectorizer(FontResolver? fontResolver = null)
    {
        _resolver = fontResolver ?? FontResolver.Default;
    }

    public TextVectorizationResult Vectorize(
        string content,
        string fontFamily = "Arial",
        string? fontPath = null,
        double fontSizeMm = 12.0,
        bool bold = false,
        bool italic = false,
        HorizontalAlign horizontalAlign = HorizontalAlign.Left,
        double letterSpacingMm = 0.0,
        double lineSpacing = 1.2,
        TextRenderMode renderMode = TextRenderMode.Outline,
        double hatchSpacingMm = 0.25)
    {
        (FontDescriptor descriptor, bool fallbackUsed) = _resolver.Resolve(fontFamily, bold, italic, fontPath);
        string text = (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = text.Normalize(NormalizationForm.FormC);

        if (string.IsNullOrEmpty(text))
        {
            return new TextVectorizationResult([], 0.01, 0.01, descriptor.FullName, fallbackUsed);
        }

        SKTypeface typeface = _resolver.LoadTypeface(descriptor);
        int unitsPerEm = Math.Max(typeface.UnitsPerEm, 1);
        double scale = fontSizeMm / unitsPerEm;

        using SKFont designFont = new(typeface, unitsPerEm)
        {
            Subpixel = true,
            Hinting = SKFontHinting.None,
            Edging = SKFontEdging.Alias,
        };

        (int ascent, int descent, int lineGap, int numberOfHMetrics) = ReadHorizontalHeader(typeface, designFont);
        double rawLineHeight = Math.Max((ascent - descent + lineGap) * scale, fontSizeMm);
        double lineAdvanceMm = rawLineHeight * Math.Max(0.5, lineSpacing);

        ushort[]? hmtxAdvances = ReadHmtxAdvances(typeface, numberOfHMetrics);
        Dictionary<(ushort Left, ushort Right), int> kernMap = BuildKernMap(typeface);
        string[] lines = text.Split('\n');

        var lineRecords = new List<(List<GlyphPlacement> Glyphs, double LineWidthMm)>(lines.Length);
        double maxLineWidth = 0.0;

        foreach (string rawLine in lines)
        {
            string expandedLine = rawLine.Replace("\t", "    ", StringComparison.Ordinal);
            var glyphs = new List<GlyphPlacement>();
            double penX = 0.0;
            ushort? prevGlyphId = null;

            foreach (Rune rune in expandedLine.EnumerateRunes())
            {
                ushort glyphId = LookupGlyphId(typeface, rune);
                if (prevGlyphId.HasValue && kernMap.TryGetValue((prevGlyphId.Value, glyphId), out int kernValue))
                {
                    penX += kernValue * scale;
                }

                double advanceWidth = MeasureGlyphAdvanceUnits(designFont, hmtxAdvances, glyphId) * scale;
                SKPath? rawGlyphPath = designFont.GetGlyphPath(glyphId);
                glyphs.Add(new GlyphPlacement(rawGlyphPath, penX));

                penX += advanceWidth + letterSpacingMm;
                prevGlyphId = glyphId;
            }

            if (glyphs.Count > 0)
            {
                penX -= letterSpacingMm;
            }

            double lineWidth = Math.Max(0.0, penX);
            maxLineWidth = Math.Max(maxLineWidth, lineWidth);
            lineRecords.Add((glyphs, lineWidth));
        }

        using SKPath combinedPath = new() { FillType = SKPathFillType.EvenOdd };
        var glyphPathStrings = new List<string>();
        var glyphWorldPaths = new List<SKPath>();

        try
        {
            for (int lineIdx = 0; lineIdx < lineRecords.Count; lineIdx++)
            {
                (List<GlyphPlacement> glyphs, double lineWidth) = lineRecords[lineIdx];
                double xOffset = horizontalAlign switch
                {
                    HorizontalAlign.Center => (maxLineWidth - lineWidth) / 2.0,
                    HorizontalAlign.Right => maxLineWidth - lineWidth,
                    _ => 0.0,
                };
                double yBaseline = ascent * scale + (lineIdx * lineAdvanceMm);

                foreach (GlyphPlacement glyph in glyphs)
                {
                    using SKPath? rawPath = glyph.RawPath;
                    if (rawPath is null || rawPath.IsEmpty)
                    {
                        continue;
                    }

                    SKMatrix matrix = SKMatrix.CreateScale((float)scale, (float)scale);
                    matrix = SKMatrix.Concat(SKMatrix.CreateTranslation((float)(xOffset + glyph.PenXMm), (float)yBaseline), matrix);

                    var transformed = new SKPath();
                    rawPath.Transform(matrix, transformed);
                    if (transformed.IsEmpty)
                    {
                        transformed.Dispose();
                        continue;
                    }

                    combinedPath.AddPath(transformed);
                    glyphWorldPaths.Add(transformed);
                }
            }

            if (combinedPath.IsEmpty)
            {
                double fallbackWidth = Math.Max(maxLineWidth, 0.01);
                double fallbackHeight = Math.Max(fontSizeMm * Math.Max(lines.Length, 1), 0.01);
                return new TextVectorizationResult([], Round4(fallbackWidth), Round4(fallbackHeight), descriptor.FullName, fallbackUsed);
            }

            SKRect bounds = combinedPath.ComputeTightBounds();
            if (bounds.Width <= 0f && bounds.Height <= 0f)
            {
                bounds = combinedPath.Bounds;
            }

            float shiftX = -bounds.Left;
            float shiftY = -bounds.Top;
            double widthMm = Math.Max((double)bounds.Width, 0.01);
            double heightMm = Math.Max((double)bounds.Height, 0.01);

            SKMatrix normalizeTranslation = SKMatrix.CreateTranslation(shiftX, shiftY);
            combinedPath.Transform(normalizeTranslation);

            foreach (SKPath gp in glyphWorldPaths)
            {
                gp.Transform(normalizeTranslation);
                string d = PathToSvgData(gp);
                if (!string.IsNullOrEmpty(d))
                {
                    glyphPathStrings.Add(d);
                }
            }

            var outputPaths = new List<VectorPathItem>();

            if (renderMode is TextRenderMode.Fill or TextRenderMode.Both)
            {
                foreach (string d in glyphPathStrings)
                {
                    outputPaths.Add(new VectorPathItem
                    {
                        D = d,
                        Operation = CncOperationType.EngraveFill,
                        IsClosed = true,
                        HasFill = true,
                        HasStroke = false,
                        LayerName = "text_fill",
                    });
                }
            }

            if (renderMode is TextRenderMode.Outline or TextRenderMode.Both)
            {
                foreach (string d in glyphPathStrings)
                {
                    outputPaths.Add(new VectorPathItem
                    {
                        D = d,
                        Operation = CncOperationType.EngraveVector,
                        IsClosed = true,
                        HasFill = false,
                        HasStroke = true,
                        LayerName = "text_outline",
                    });
                }
            }

            if (renderMode == TextRenderMode.Hatch)
            {
                List<string> hatchSegments = GenerateHatchPaths(combinedPath, widthMm, heightMm, hatchSpacingMm);
                if (hatchSegments.Count > 0)
                {
                    foreach (string d in hatchSegments)
                    {
                        outputPaths.Add(new VectorPathItem
                        {
                            D = d,
                            Operation = CncOperationType.EngraveVector,
                            IsClosed = false,
                            HasFill = false,
                            HasStroke = true,
                            LayerName = "text_hatch",
                        });
                    }
                }
                else
                {
                    foreach (string d in glyphPathStrings)
                    {
                        outputPaths.Add(new VectorPathItem
                        {
                            D = d,
                            Operation = CncOperationType.EngraveVector,
                            IsClosed = true,
                            HasFill = false,
                            HasStroke = true,
                            LayerName = "text_outline_fallback",
                        });
                    }
                }
            }

            return new TextVectorizationResult(
                Paths: outputPaths,
                WidthMm: Round4(widthMm),
                HeightMm: Round4(heightMm),
                ResolvedFontName: descriptor.FullName,
                FontFallbackUsed: fallbackUsed);
        }
        finally
        {
            foreach (SKPath gp in glyphWorldPaths)
            {
                gp.Dispose();
            }
        }
    }

    /// <summary>
    /// Vetoriza um <see cref="TextPersonalizationElement"/> in-place, preenchendo seus caminhos,
    /// dimensões base, fonte resolvida e operação padrão compatível com o modo de renderização.
    /// </summary>
    public TextVectorizationResult VectorizeElement(TextPersonalizationElement element, bool updateDimensions = true)
    {
        ArgumentNullException.ThrowIfNull(element);

        TextVectorizationResult result = Vectorize(
            content: element.Content,
            fontFamily: element.FontFamily,
            fontPath: element.FontPath,
            fontSizeMm: element.FontSizeMm,
            bold: element.Bold,
            italic: element.Italic,
            horizontalAlign: element.HorizontalAlign,
            letterSpacingMm: element.LetterSpacingMm,
            lineSpacing: element.LineSpacing,
            renderMode: element.RenderMode,
            hatchSpacingMm: element.HatchSpacingMm);

        element.VectorPaths = result.Paths.Select(p => p.DeepClone()).ToList();
        element.Vectorized = element.ConvertToPaths;
        element.BaseWidthMm = result.WidthMm;
        element.BaseHeightMm = result.HeightMm;
        element.ResolvedFontName = result.ResolvedFontName;
        element.FontFallbackUsed = result.FontFallbackUsed;
        element.Operation = TextPersonalizationElement.DefaultOperationForRenderMode(element.RenderMode);

        if (updateDimensions)
        {
            element.WidthMm = result.WidthMm;
            element.HeightMm = result.HeightMm;
        }

        return result;
    }

    private static List<string> GenerateHatchPaths(
        SKPath normalizedPath,
        double widthMm,
        double heightMm,
        double hatchSpacingMm)
    {
        double spacing = Math.Max(0.05, hatchSpacingMm);
        double pxPerMm = Math.Clamp(Math.Max(12.0, 6.0 / spacing), 12.0, 40.0);
        int imgW = Math.Max(8, (int)Math.Ceiling(widthMm * pxPerMm) + 2);
        int imgH = Math.Max(8, (int)Math.Ceiling(heightMm * pxPerMm) + 2);

        using SKBitmap maskBitmap = new(imgW, imgH, SKColorType.Alpha8, SKAlphaType.Premul);
        using SKCanvas canvas = new(maskBitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)pxPerMm, (float)pxPerMm);

        using SKPaint fillPaint = new()
        {
            IsAntialias = false,
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
        };

        normalizedPath.FillType = SKPathFillType.EvenOdd;
        canvas.DrawPath(normalizedPath, fillPaint);
        canvas.Flush();

        IntPtr pixelsPtr = maskBitmap.GetPixels();
        int rowBytes = maskBitmap.RowBytes;
        byte[] buffer = new byte[imgH * rowBytes];
        System.Runtime.InteropServices.Marshal.Copy(pixelsPtr, buffer, 0, buffer.Length);

        var hatchPaths = new List<string>();
        double yMm = spacing * 0.5;
        while (yMm < heightMm)
        {
            int py = (int)Math.Floor(yMm * pxPerMm);
            if (py >= imgH)
            {
                break;
            }

            int rowOffset = py * rowBytes;
            bool inside = false;
            int runStart = 0;

            for (int px = 0; px < imgW; px++)
            {
                bool fg = buffer[rowOffset + px] >= 128;
                if (fg && !inside)
                {
                    inside = true;
                    runStart = px;
                }
                else if (!fg && inside)
                {
                    inside = false;
                    EmitHatchSegment(hatchPaths, runStart, px - 1, yMm, widthMm, pxPerMm);
                }
            }

            if (inside)
            {
                EmitHatchSegment(hatchPaths, runStart, imgW - 1, yMm, widthMm, pxPerMm);
            }

            yMm += spacing;
        }

        return hatchPaths;
    }

    private static void EmitHatchSegment(
        List<string> hatchPaths,
        int startPx,
        int endPx,
        double yMm,
        double widthMm,
        double pxPerMm)
    {
        double x0 = Math.Max(0.0, startPx / pxPerMm);
        double x1 = Math.Min(widthMm, (endPx + 1) / pxPerMm);
        if (x1 - x0 <= 0.02)
        {
            return;
        }

        hatchPaths.Add($"M {FormatNum(x0)} {FormatNum(yMm)} L {FormatNum(x1)} {FormatNum(yMm)}");
    }

    private static string PathToSvgData(SKPath path)
    {
        using SKPath? noConics = ConvertConicsIfNeeded(path);
        SKPath source = noConics ?? path;

        var commands = new List<string>();
        using SKPath.RawIterator iter = source.CreateRawIterator();
        Span<SKPoint> pts = stackalloc SKPoint[4];
        SKPathVerb verb;
        while ((verb = iter.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    commands.Add($"M {FormatNum(pts[0].X)} {FormatNum(pts[0].Y)}");
                    break;
                case SKPathVerb.Line:
                    commands.Add($"L {FormatNum(pts[1].X)} {FormatNum(pts[1].Y)}");
                    break;
                case SKPathVerb.Quad:
                    commands.Add(
                        $"Q {FormatNum(pts[1].X)} {FormatNum(pts[1].Y)} " +
                        $"{FormatNum(pts[2].X)} {FormatNum(pts[2].Y)}");
                    break;
                case SKPathVerb.Cubic:
                    commands.Add(
                        $"C {FormatNum(pts[1].X)} {FormatNum(pts[1].Y)} " +
                        $"{FormatNum(pts[2].X)} {FormatNum(pts[2].Y)} " +
                        $"{FormatNum(pts[3].X)} {FormatNum(pts[3].Y)}");
                    break;
                case SKPathVerb.Close:
                    commands.Add("Z");
                    break;
            }
        }

        return string.Join(' ', commands);
    }

    private static SKPath? ConvertConicsIfNeeded(SKPath path)
    {
        bool hasConic = false;
        using (SKPath.RawIterator probe = path.CreateRawIterator())
        {
            Span<SKPoint> pts = stackalloc SKPoint[4];
            SKPathVerb v;
            while ((v = probe.Next(pts)) != SKPathVerb.Done)
            {
                if (v == SKPathVerb.Conic)
                {
                    hasConic = true;
                    break;
                }
            }
        }

        if (!hasConic)
        {
            return null;
        }

        var converted = new SKPath { FillType = path.FillType };
        using SKPath.RawIterator iter = path.CreateRawIterator();
        Span<SKPoint> p = stackalloc SKPoint[4];
        SKPathVerb verb;
        while ((verb = iter.Next(p)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    converted.MoveTo(p[0]);
                    break;
                case SKPathVerb.Line:
                    converted.LineTo(p[1]);
                    break;
                case SKPathVerb.Quad:
                    converted.QuadTo(p[1], p[2]);
                    break;
                case SKPathVerb.Cubic:
                    converted.CubicTo(p[1], p[2], p[3]);
                    break;
                case SKPathVerb.Conic:
                    SKPoint[] quadPts = SKPath.ConvertConicToQuads(p[0], p[1], p[2], iter.ConicWeight(), 2);
                    for (int i = 1; i + 1 < quadPts.Length; i += 2)
                    {
                        converted.QuadTo(quadPts[i], quadPts[i + 1]);
                    }
                    break;
                case SKPathVerb.Close:
                    converted.Close();
                    break;
            }
        }

        return converted;
    }

    private static ushort LookupGlyphId(SKTypeface typeface, Rune rune)
    {
        ushort glyphId = typeface.GetGlyph(rune.Value);
        if (glyphId != 0)
        {
            return glyphId;
        }

        string decomposed = rune.ToString().Normalize(NormalizationForm.FormKD);
        foreach (Rune fallbackRune in decomposed.EnumerateRunes())
        {
            UnicodeCategory cat = Rune.GetUnicodeCategory(fallbackRune);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            glyphId = typeface.GetGlyph(fallbackRune.Value);
            if (glyphId != 0)
            {
                return glyphId;
            }
        }

        if (rune.Value != '?')
        {
            ushort questionGlyph = typeface.GetGlyph('?');
            if (questionGlyph != 0)
            {
                return questionGlyph;
            }
        }

        return 0;
    }

    private static double MeasureGlyphAdvanceUnits(SKFont designFont, ushort[]? hmtxAdvances, ushort glyphId)
    {
        if (hmtxAdvances is { Length: > 0 })
        {
            return glyphId < hmtxAdvances.Length ? hmtxAdvances[glyphId] : hmtxAdvances[^1];
        }

        Span<ushort> ids = stackalloc ushort[1] { glyphId };
        Span<float> widths = stackalloc float[1];
        designFont.GetGlyphWidths(ids, widths, Span<SKRect>.Empty);
        return widths[0];
    }

    private static ushort[]? ReadHmtxAdvances(SKTypeface typeface, int numberOfHMetrics)
    {
        if (numberOfHMetrics <= 0 || !typeface.TryGetTableData(TagHmtx, out byte[]? data) || data is null)
        {
            return null;
        }

        int count = Math.Min(numberOfHMetrics, data.Length / 4);
        if (count <= 0)
        {
            return null;
        }

        var advances = new ushort[count];
        ReadOnlySpan<byte> span = data;
        for (int i = 0; i < count; i++)
        {
            advances[i] = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(i * 4, 2));
        }

        return advances;
    }

    private static (int Ascent, int Descent, int LineGap, int NumberOfHMetrics) ReadHorizontalHeader(SKTypeface typeface, SKFont designFont)
    {
        if (typeface.TryGetTableData(TagHhea, out byte[]? data) && data is not null && data.Length >= 36)
        {
            short ascent = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(4, 2));
            short descent = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(6, 2));
            short lineGap = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(8, 2));
            ushort numHMetrics = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(34, 2));
            if (ascent != 0 || descent != 0)
            {
                return (ascent, descent, lineGap, numHMetrics);
            }
        }

        SKFontMetrics metrics = designFont.Metrics;
        int fallbackAscent = (int)Math.Round(-metrics.Ascent);
        int fallbackDescent = (int)Math.Round(-metrics.Descent);
        int fallbackLeading = (int)Math.Round(metrics.Leading);
        return (fallbackAscent, fallbackDescent, fallbackLeading, 0);
    }

    private static Dictionary<(ushort Left, ushort Right), int> BuildKernMap(SKTypeface typeface)
    {
        var map = new Dictionary<(ushort Left, ushort Right), int>();
        if (!typeface.TryGetTableData(TagKern, out byte[]? data) || data is null || data.Length < 4)
        {
            return map;
        }

        ReadOnlySpan<byte> span = data;
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(0, 2));
        if (version != 0)
        {
            return map;
        }

        ushort nTables = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));
        int offset = 4;

        for (int t = 0; t < nTables; t++)
        {
            if (offset + 6 > span.Length)
            {
                break;
            }

            ushort length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 2, 2));
            ushort coverage = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 4, 2));
            byte format = (byte)(coverage >> 8);
            bool horizontal = (coverage & 0x0001) != 0;

            if (length < 6 || offset + length > span.Length)
            {
                break;
            }

            if (horizontal && format == 0 && offset + 14 <= span.Length)
            {
                ushort nPairs = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 6, 2));
                int pairOffset = offset + 14;
                int maxOffset = offset + length;
                for (int p = 0; p < nPairs && pairOffset + 6 <= maxOffset; p++)
                {
                    ushort left = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pairOffset, 2));
                    ushort right = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pairOffset + 2, 2));
                    short value = BinaryPrimitives.ReadInt16BigEndian(span.Slice(pairOffset + 4, 2));
                    if (value != 0)
                    {
                        map[(left, right)] = value;
                    }

                    pairOffset += 6;
                }
            }

            offset += length;
        }

        return map;
    }

    private static string FormatNum(double value)
    {
        if (Math.Abs(value) < 5e-5)
        {
            return "0";
        }

        string formatted = value.ToString("0.####", CultureInfo.InvariantCulture);
        return formatted == "-0" ? "0" : formatted;
    }

    private static double Round4(double value) => Math.Round(value, 4, MidpointRounding.ToEven);

    private readonly record struct GlyphPlacement(SKPath? RawPath, double PenXMm);
}
