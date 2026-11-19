using System.Buffers.Binary;
using System.Globalization;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using SkiaSharp;

namespace CaixaMaker.Cam.Raster;

/// <summary>Resultado do processamento e opcional vetorização de uma imagem rasterizada.</summary>
public sealed record RasterProcessingResult(
    RasterImagePersonalizationElement Element);

/// <summary>
/// Processador seguro de imagens raster (PNG, JPEG, BMP) em código gerenciado + SkiaSharp,
/// com suporte a EXIF, composição alfa sobre branco, filtros, dithering Floyd-Steinberg
/// e vetorização hierárquica de contornos com preservação de furos/ilhas internas.
/// </summary>
public sealed class RasterProcessor
{
    public const int MaxRasterBytes = 20 * 1024 * 1024;
    public const long MaxRasterPixels = 40_000_000L;
    public const double DefaultMmPerPx = 25.4 / 96.0;
    public const double MinContourAreaPx2 = 2.0;

    private static readonly HashSet<SKEncodedImageFormat> AllowedSkiaFormats =
    [
        SKEncodedImageFormat.Png,
        SKEncodedImageFormat.Jpeg,
        SKEncodedImageFormat.Bmp,
    ];

    public RasterImagePersonalizationElement ProcessFromFile(
        string filePath,
        SurfaceId panelId = SurfaceId.Front,
        RasterProcessingOptions? options = null,
        double? targetWidthMm = null,
        double? targetHeightMm = null,
        bool vectorize = false,
        double vectorizeEpsilonPx = 1.5,
        CncOperationType operation = CncOperationType.EngraveRaster,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("Arquivo de imagem não encontrado.", filePath);
        }

        var info = new FileInfo(filePath);
        if (info.Length > MaxRasterBytes)
        {
            throw new ArgumentException(
                $"A imagem excede o limite máximo permitido de {MaxRasterBytes / (1024 * 1024)} MB.",
                nameof(filePath));
        }

        byte[] rawBytes = File.ReadAllBytes(filePath);
        return ProcessFromBytes(
            rawBytes: rawBytes,
            sourceFilename: Path.GetFileName(filePath),
            panelId: panelId,
            options: options,
            targetWidthMm: targetWidthMm,
            targetHeightMm: targetHeightMm,
            vectorize: vectorize,
            vectorizeEpsilonPx: vectorizeEpsilonPx,
            operation: operation,
            name: name);
    }

    public RasterImagePersonalizationElement ProcessFromBytes(
        byte[] rawBytes,
        string? sourceFilename = null,
        SurfaceId panelId = SurfaceId.Front,
        RasterProcessingOptions? options = null,
        double? targetWidthMm = null,
        double? targetHeightMm = null,
        bool vectorize = false,
        double vectorizeEpsilonPx = 1.5,
        CncOperationType operation = CncOperationType.EngraveRaster,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        if (rawBytes.Length == 0)
        {
            throw new ArgumentException("Os bytes da imagem estão vazios.", nameof(rawBytes));
        }

        if (rawBytes.Length > MaxRasterBytes)
        {
            throw new ArgumentException(
                $"A imagem excede o limite máximo permitido de {MaxRasterBytes / (1024 * 1024)} MB.",
                nameof(rawBytes));
        }

        RasterProcessingOptions opts = options?.Clone() ?? new RasterProcessingOptions();
        (SKBitmap sourceOpaque, string sourceFormat, double dpiX, double dpiY) = DecodeValidateAndFlatten(rawBytes);
        using (sourceOpaque)
        {
            int origW = sourceOpaque.Width;
            int origH = sourceOpaque.Height;

            using SKBitmap processed = ApplyProcessing(sourceOpaque, opts);
            int procW = processed.Width;
            int procH = processed.Height;

            (double physWMm, double physHMm) = ComputePhysicalDimensionsMm(procW, procH, dpiX, dpiY);
            (double outW, double outH) = FitDimensions(physWMm, physHMm, targetWidthMm, targetHeightMm);

            byte[] rawPngBytes = EncodePng(sourceOpaque);
            byte[] processedPngBytes = EncodePng(processed);

            CncOperationType effectiveOp = vectorize && operation == CncOperationType.EngraveRaster
                ? CncOperationType.EngraveVector
                : operation;

            List<VectorPathItem> vectorPaths = vectorize
                ? VectorizeProcessedBitmap(processed, outW, opts.Threshold, vectorizeEpsilonPx, effectiveOp)
                : [];

            string resolvedName = !string.IsNullOrWhiteSpace(name)
                ? name.Trim()
                : !string.IsNullOrWhiteSpace(sourceFilename)
                    ? Path.GetFileNameWithoutExtension(sourceFilename)
                    : "Imagem Raster";

            byte[] originalCopy = rawBytes.ToArray();
            return new RasterImagePersonalizationElement
            {
                Name = resolvedName,
                PanelId = panelId,
                SourceFilename = sourceFilename,
                OriginalFileName = sourceFilename,
                RawImageBytes = originalCopy,
                SourceFormat = sourceFormat,
                OriginalPixelSize = [origW, origH],
                ProcessedPixelSize = [procW, procH],
                ProcessingOptions = opts,
                RawImageB64 = Convert.ToBase64String(rawPngBytes),
                ProcessedImageB64 = Convert.ToBase64String(processedPngBytes),
                Vectorize = vectorize,
                VectorizeEpsilonPx = vectorizeEpsilonPx,
                BaseWidthMm = Round4(outW),
                BaseHeightMm = Round4(outH),
                WidthMm = Round4(outW),
                HeightMm = Round4(outH),
                Operation = effectiveOp,
                VectorPaths = vectorPaths,
            };
        }
    }

    public RasterImagePersonalizationElement Reprocess(
        RasterImagePersonalizationElement element,
        RasterProcessingOptions? newOptions = null,
        bool? vectorize = null,
        double? vectorizeEpsilonPx = null,
        CncOperationType? operation = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        byte[]? sourceBytes = element.RawImageBytes is { Length: > 0 }
            ? element.RawImageBytes
            : !string.IsNullOrWhiteSpace(element.RawImageB64)
                ? Convert.FromBase64String(element.RawImageB64)
                : null;

        if (sourceBytes is null || sourceBytes.Length == 0)
        {
            throw new InvalidOperationException("O elemento raster não possui dados originais (RawImageBytes/RawImageB64) para reprocessamento.");
        }

        string preservedSourceFormat = element.SourceFormat;
        string? preservedOriginalName = element.OriginalFileName ?? element.SourceFilename;
        string? preservedAssetEntry = element.AssetEntryPath;

        RasterProcessingOptions opts = newOptions?.Clone() ?? element.ProcessingOptions.Clone();
        bool useVectorize = vectorize ?? element.Vectorize;
        double eps = vectorizeEpsilonPx ?? element.VectorizeEpsilonPx;
        CncOperationType op = operation ?? element.Operation;

        RasterImagePersonalizationElement regenerated = ProcessFromBytes(
            rawBytes: sourceBytes,
            sourceFilename: element.SourceFilename ?? preservedOriginalName,
            panelId: element.PanelId,
            options: opts,
            targetWidthMm: element.WidthMm,
            targetHeightMm: element.HeightMm,
            vectorize: useVectorize,
            vectorizeEpsilonPx: eps,
            operation: op,
            name: element.Name);

        element.ProcessingOptions = regenerated.ProcessingOptions;
        element.ProcessedPixelSize = regenerated.ProcessedPixelSize;
        element.ProcessedImageB64 = regenerated.ProcessedImageB64;
        if (string.IsNullOrWhiteSpace(element.RawImageB64))
        {
            element.RawImageB64 = regenerated.RawImageB64;
        }

        if (element.RawImageBytes is not { Length: > 0 })
        {
            element.RawImageBytes = regenerated.RawImageBytes;
        }

        element.SourceFormat = preservedSourceFormat;
        element.OriginalFileName = preservedOriginalName;
        element.AssetEntryPath = preservedAssetEntry;
        element.Vectorize = regenerated.Vectorize;
        element.VectorizeEpsilonPx = regenerated.VectorizeEpsilonPx;
        element.VectorPaths = regenerated.VectorPaths;
        element.Operation = regenerated.Operation;
        element.BaseWidthMm = regenerated.BaseWidthMm;
        element.BaseHeightMm = regenerated.BaseHeightMm;
        return element;
    }

    /// <summary>
    /// Executa o pipeline determinístico de pré-processamento sobre um bitmap já composto em fundo branco opaco.
    /// Ordem: Crop -> Resize -> Brilho -> Contraste -> Escala de cinza -> Inverter -> Floyd-Steinberg / Monochrome.
    /// </summary>
    public SKBitmap ApplyProcessing(SKBitmap source, RasterProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        SKBitmap working = ApplyCrop(source, options.CropBox);
        try
        {
            if (options.ResizeMaxPx is > 0 && Math.Max(working.Width, working.Height) > options.ResizeMaxPx.Value)
            {
                SKBitmap resized = ResizeToMaxDimension(working, options.ResizeMaxPx.Value);
                working.Dispose();
                working = resized;
            }

            int w = working.Width;
            int h = working.Height;
            int count = w * h;

            byte[] r = new byte[count];
            byte[] g = new byte[count];
            byte[] b = new byte[count];
            ExtractRgbBytes(working, r, g, b);

            if (Math.Abs(options.Brightness - 1.0) > 1e-6)
            {
                double factor = Math.Max(0.0, options.Brightness);
                for (int i = 0; i < count; i++)
                {
                    r[i] = ClampByte(Math.Round(r[i] * factor));
                    g[i] = ClampByte(Math.Round(g[i] * factor));
                    b[i] = ClampByte(Math.Round(b[i] * factor));
                }
            }

            if (Math.Abs(options.Contrast - 1.0) > 1e-6)
            {
                double meanSum = 0.0;
                for (int i = 0; i < count; i++)
                {
                    meanSum += (0.299 * r[i]) + (0.587 * g[i]) + (0.114 * b[i]);
                }

                double mean = count > 0 ? meanSum / count : 128.0;
                double factor = Math.Max(0.0, options.Contrast);
                for (int i = 0; i < count; i++)
                {
                    r[i] = ClampByte(Math.Round(mean + ((r[i] - mean) * factor)));
                    g[i] = ClampByte(Math.Round(mean + ((g[i] - mean) * factor)));
                    b[i] = ClampByte(Math.Round(mean + ((b[i] - mean) * factor)));
                }
            }

            if (options.Grayscale || options.Dithering || options.Monochrome)
            {
                for (int i = 0; i < count; i++)
                {
                    byte lum = ClampByte(Math.Round((0.299 * r[i]) + (0.587 * g[i]) + (0.114 * b[i])));
                    r[i] = lum;
                    g[i] = lum;
                    b[i] = lum;
                }
            }

            if (options.Invert)
            {
                for (int i = 0; i < count; i++)
                {
                    r[i] = (byte)(255 - r[i]);
                    g[i] = (byte)(255 - g[i]);
                    b[i] = (byte)(255 - b[i]);
                }
            }

            if (options.Dithering)
            {
                ApplyFloydSteinbergInPlace(r, w, h, options.Threshold);
                Array.Copy(r, g, count);
                Array.Copy(r, b, count);
            }
            else if (options.Monochrome)
            {
                int thr = Math.Clamp(options.Threshold, 0, 255);
                for (int i = 0; i < count; i++)
                {
                    byte bw = r[i] < thr ? (byte)0 : (byte)255;
                    r[i] = bw;
                    g[i] = bw;
                    b[i] = bw;
                }
            }

            return BuildBitmapFromRgb(w, h, r, g, b);
        }
        finally
        {
            working.Dispose();
        }
    }

    /// <summary>
    /// Extrai caminhos SVG fechados a partir dos pixels escuros (<c>lum &lt; threshold</c>)
    /// preservando furos/ilhas internas via <see cref="BinaryContourTracer"/>.
    /// </summary>
    public List<VectorPathItem> VectorizeProcessedBitmap(
        SKBitmap processedBitmap,
        double targetWidthMm,
        int threshold = 128,
        double epsilonPx = 1.5,
        CncOperationType operation = CncOperationType.EngraveVector)
    {
        ArgumentNullException.ThrowIfNull(processedBitmap);

        int w = processedBitmap.Width;
        int h = processedBitmap.Height;
        if (w <= 0 || h <= 0)
        {
            return [];
        }

        double mmPerPx = Math.Max(targetWidthMm, 0.01) / w;
        int thr = Math.Clamp(threshold, 0, 255);
        double eps = Math.Max(0.2, epsilonPx);

        bool[,] fgMask = new bool[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                SKColor c = processedBitmap.GetPixel(x, y);
                int lum = (int)Math.Round((0.299 * c.Red) + (0.587 * c.Green) + (0.114 * c.Blue));
                fgMask[y, x] = lum < thr;
            }
        }

        List<TracedContour> rawContours = BinaryContourTracer.FindContoursTwoLevel(fgMask);
        if (rawContours.Count == 0)
        {
            return [];
        }

        var simplifiedByIndex = new Dictionary<int, (List<Point2D> Points, bool IsHole, int ParentIdx)>();
        for (int i = 0; i < rawContours.Count; i++)
        {
            TracedContour c = rawContours[i];
            if (c.AreaPx2 < MinContourAreaPx2 || c.Points.Count < 3)
            {
                continue;
            }

            List<Point2D> poly = BinaryContourTracer.SimplifyClosedPolygon(c.Points, eps);
            if (poly.Count < 3)
            {
                continue;
            }

            simplifiedByIndex[i] = (poly, c.IsHole, c.ParentIndex);
        }

        if (simplifiedByIndex.Count == 0)
        {
            return [];
        }

        var result = new List<VectorPathItem>();

        if (operation == CncOperationType.EngraveFill)
        {
            // Agrupa contornos externos com todos os seus furos internos em caminhos compostos "M...Z M...Z"
            var usedChildIndices = new HashSet<int>();
            foreach ((int idx, var info) in simplifiedByIndex.OrderBy(kv => kv.Key))
            {
                if (info.IsHole)
                {
                    continue;
                }

                var compoundParts = new List<string>
                {
                    ContourToSvgSubpath(EnsureSignedOrientation(info.Points, positive: true), mmPerPx),
                };

                foreach ((int childIdx, var childInfo) in simplifiedByIndex.OrderBy(kv => kv.Key))
                {
                    if (childInfo.IsHole && childInfo.ParentIdx == idx)
                    {
                        compoundParts.Add(ContourToSvgSubpath(EnsureSignedOrientation(childInfo.Points, positive: false), mmPerPx));
                        usedChildIndices.Add(childIdx);
                    }
                }

                result.Add(new VectorPathItem
                {
                    D = string.Join(' ', compoundParts),
                    Operation = CncOperationType.EngraveFill,
                    IsClosed = true,
                    HasFill = true,
                    HasStroke = false,
                    LayerName = "raster_fill",
                });
            }

            // Eventuais furos órfãos (caso o contorno externo tenha sido filtrado por área)
            foreach ((int orphanIdx, var orphanInfo) in simplifiedByIndex.OrderBy(kv => kv.Key))
            {
                if (orphanInfo.IsHole && !usedChildIndices.Contains(orphanIdx))
                {
                    result.Add(new VectorPathItem
                    {
                        D = ContourToSvgSubpath(orphanInfo.Points, mmPerPx),
                        Operation = CncOperationType.EngraveFill,
                        IsClosed = true,
                        HasFill = true,
                        HasStroke = false,
                        LayerName = "raster_fill",
                    });
                }
            }

            return result;
        }

        foreach ((_, var info) in simplifiedByIndex.OrderBy(kv => kv.Key))
        {
            result.Add(new VectorPathItem
            {
                D = ContourToSvgSubpath(info.Points, mmPerPx),
                Operation = CncOperationType.EngraveVector,
                IsClosed = true,
                HasFill = false,
                HasStroke = true,
                LayerName = info.IsHole ? "raster_hole" : "raster_outline",
            });
        }

        return result;
    }

    /// <summary>Decodifica bytes originais (PNG/JPEG/BMP), achata alfa sobre branco e codifica PNG canônico para reprocessamento.</summary>
    public static byte[] EncodeCanonicalPngBytes(byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        (SKBitmap sourceOpaque, _, _, _) = DecodeValidateAndFlatten(rawBytes);
        using (sourceOpaque)
        {
            return EncodePng(sourceOpaque);
        }
    }

    private static (SKBitmap Bitmap, string FormatName, double DpiX, double DpiY) DecodeValidateAndFlatten(byte[] rawBytes)
    {
        using var stream = new SKMemoryStream(rawBytes);
        using SKCodec? codec = SKCodec.Create(stream);
        if (codec is null)
        {
            throw new InvalidDataException("Não foi possível decodificar o stream como imagem válida.");
        }

        if (!AllowedSkiaFormats.Contains(codec.EncodedFormat))
        {
            throw new ArgumentException(
                $"Formato de imagem não suportado: {codec.EncodedFormat}. Permitidos: PNG, JPEG, BMP.",
                nameof(rawBytes));
        }

        SKImageInfo info = codec.Info;
        long totalPixels = (long)info.Width * info.Height;
        if (info.Width <= 0 || info.Height <= 0 || totalPixels > MaxRasterPixels)
        {
            throw new ArgumentException(
                $"As dimensões da imagem ({info.Width}x{info.Height}) são inválidas ou excedem o limite de {MaxRasterPixels:N0} pixels.",
                nameof(rawBytes));
        }

        using var decoded = new SKBitmap(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        SKCodecResult codecResult = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (codecResult != SKCodecResult.Success && codecResult != SKCodecResult.IncompleteInput)
        {
            throw new InvalidDataException($"Falha ao extrair pixels da imagem: {codecResult}.");
        }

        using SKBitmap oriented = ApplyEncodedOrigin(decoded, codec.EncodedOrigin);
        SKBitmap flattened = FlattenOverWhite(oriented);

        string fmtName = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "PNG",
            SKEncodedImageFormat.Jpeg => "JPEG",
            SKEncodedImageFormat.Bmp => "BMP",
            _ => codec.EncodedFormat.ToString().ToUpperInvariant(),
        };

        (double dpiX, double dpiY) = ReadDpiMetadata(rawBytes, codec.EncodedFormat);
        return (flattened, fmtName, dpiX, dpiY);
    }

    private static SKBitmap ApplyEncodedOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return src.Copy();
        }

        bool swapAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int outW = swapAxes ? src.Height : src.Width;
        int outH = swapAxes ? src.Width : src.Height;

        var dst = new SKBitmap(outW, outH, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(outW, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(outW, outH);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, outH);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(outW, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(outW, outH);
                canvas.RotateDegrees(270);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, outH);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(src, 0, 0);
        canvas.Flush();
        return dst;
    }

    private static SKBitmap FlattenOverWhite(SKBitmap src)
    {
        var opaque = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(opaque);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(src, 0, 0);
        canvas.Flush();
        return opaque;
    }

    private static SKBitmap ApplyCrop(SKBitmap src, double[]? cropBox)
    {
        if (cropBox is null || cropBox.Length < 4)
        {
            return src.Copy();
        }

        double x0Frac = Math.Clamp(cropBox[0], 0.0, 1.0);
        double y0Frac = Math.Clamp(cropBox[1], 0.0, 1.0);
        double x1Frac = Math.Clamp(cropBox[2], 0.0, 1.0);
        double y1Frac = Math.Clamp(cropBox[3], 0.0, 1.0);

        int x0 = (int)Math.Floor(x0Frac * src.Width);
        int y0 = (int)Math.Floor(y0Frac * src.Height);
        int x1 = (int)Math.Ceiling(x1Frac * src.Width);
        int y1 = (int)Math.Ceiling(y1Frac * src.Height);

        x0 = Math.Clamp(x0, 0, src.Width - 1);
        y0 = Math.Clamp(y0, 0, src.Height - 1);
        x1 = Math.Clamp(x1, x0 + 1, src.Width);
        y1 = Math.Clamp(y1, y0 + 1, src.Height);

        int cropW = x1 - x0;
        int cropH = y1 - y0;
        var cropped = new SKBitmap(cropW, cropH, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(cropped);
        canvas.DrawBitmap(src, new SKRectI(x0, y0, x1, y1), new SKRect(0, 0, cropW, cropH));
        canvas.Flush();
        return cropped;
    }

    private static SKBitmap ResizeToMaxDimension(SKBitmap src, int maxDimPx)
    {
        int w = src.Width;
        int h = src.Height;
        double scale = (double)maxDimPx / Math.Max(w, h);
        int newW = Math.Max(1, (int)Math.Round(w * scale));
        int newH = Math.Max(1, (int)Math.Round(h * scale));

        var info = new SKImageInfo(newW, newH, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        return src.Resize(info, sampling) ?? src.Copy();
    }

    private static void ExtractRgbBytes(SKBitmap bitmap, byte[] r, byte[] g, byte[] b)
    {
        int w = bitmap.Width;
        int h = bitmap.Height;
        int idx = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                r[idx] = c.Red;
                g[idx] = c.Green;
                b[idx] = c.Blue;
                idx++;
            }
        }
    }

    private static SKBitmap BuildBitmapFromRgb(int w, int h, byte[] r, byte[] g, byte[] b)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        IntPtr ptr = bmp.GetPixels();
        byte[] bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            bgra[o] = b[i];
            bgra[o + 1] = g[i];
            bgra[o + 2] = r[i];
            bgra[o + 3] = 255;
        }

        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, ptr, bgra.Length);
        return bmp;
    }

    private static void ApplyFloydSteinbergInPlace(byte[] lum, int w, int h, int threshold)
    {
        double thr = Math.Clamp(threshold, 0, 255);
        double[] work = new double[lum.Length];
        for (int i = 0; i < lum.Length; i++)
        {
            work[i] = lum[i];
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w) + x;
                double oldVal = work[idx];
                double newVal = oldVal < thr ? 0.0 : 255.0;
                double err = oldVal - newVal;
                lum[idx] = (byte)newVal;

                if (x + 1 < w)
                {
                    work[idx + 1] += err * (7.0 / 16.0);
                }

                if (y + 1 < h)
                {
                    int nextRow = (y + 1) * w;
                    if (x - 1 >= 0)
                    {
                        work[nextRow + x - 1] += err * (3.0 / 16.0);
                    }

                    work[nextRow + x] += err * (5.0 / 16.0);

                    if (x + 1 < w)
                    {
                        work[nextRow + x + 1] += err * (1.0 / 16.0);
                    }
                }
            }
        }
    }

    private static (double DpiX, double DpiY) ReadDpiMetadata(byte[] rawBytes, SKEncodedImageFormat fmt)
    {
        if (fmt == SKEncodedImageFormat.Png && rawBytes.Length > 33)
        {
            int pos = 8;
            while (pos + 12 <= rawBytes.Length)
            {
                uint chunkLen = BinaryPrimitives.ReadUInt32BigEndian(rawBytes.AsSpan(pos, 4));
                if (rawBytes[pos + 4] == (byte)'p' && rawBytes[pos + 5] == (byte)'H' &&
                    rawBytes[pos + 6] == (byte)'Y' && rawBytes[pos + 7] == (byte)'s' && chunkLen == 9 &&
                    pos + 17 <= rawBytes.Length)
                {
                    uint ppmX = BinaryPrimitives.ReadUInt32BigEndian(rawBytes.AsSpan(pos + 8, 4));
                    uint ppmY = BinaryPrimitives.ReadUInt32BigEndian(rawBytes.AsSpan(pos + 12, 4));
                    byte unit = rawBytes[pos + 16];
                    if (unit == 1 && ppmX > 0 && ppmY > 0)
                    {
                        return (ppmX * 0.0254, ppmY * 0.0254);
                    }

                    break;
                }

                if (chunkLen > int.MaxValue - 12 || pos + 12L + chunkLen > rawBytes.Length)
                {
                    break;
                }

                pos += 12 + (int)chunkLen;
            }
        }
        else if (fmt == SKEncodedImageFormat.Jpeg && rawBytes.Length > 20)
        {
            if (rawBytes[0] == 0xFF && rawBytes[1] == 0xD8 && rawBytes[2] == 0xFF && rawBytes[3] == 0xE0 &&
                rawBytes[6] == (byte)'J' && rawBytes[7] == (byte)'F' && rawBytes[8] == (byte)'I' &&
                rawBytes[9] == (byte)'F' && rawBytes[10] == 0)
            {
                byte units = rawBytes[13];
                ushort densX = BinaryPrimitives.ReadUInt16BigEndian(rawBytes.AsSpan(14, 2));
                ushort densY = BinaryPrimitives.ReadUInt16BigEndian(rawBytes.AsSpan(16, 2));
                if (units == 1 && densX > 0 && densY > 0)
                {
                    return (densX, densY);
                }

                if (units == 2 && densX > 0 && densY > 0)
                {
                    return (densX * 2.54, densY * 2.54);
                }
            }
        }
        else if (fmt == SKEncodedImageFormat.Bmp && rawBytes.Length >= 46)
        {
            int ppmX = BinaryPrimitives.ReadInt32LittleEndian(rawBytes.AsSpan(38, 4));
            int ppmY = BinaryPrimitives.ReadInt32LittleEndian(rawBytes.AsSpan(42, 4));
            if (ppmX > 0 && ppmY > 0)
            {
                return (ppmX * 0.0254, ppmY * 0.0254);
            }
        }

        return (96.0, 96.0);
    }

    private static (double WidthMm, double HeightMm) ComputePhysicalDimensionsMm(int pxW, int pxH, double dpiX, double dpiY)
    {
        double wMm = pxW * 25.4 / (dpiX > 1.0 ? dpiX : 96.0);
        double hMm = pxH * 25.4 / (dpiY > 1.0 ? dpiY : 96.0);
        return (Math.Max(wMm, 0.01), Math.Max(hMm, 0.01));
    }

    private static (double WidthMm, double HeightMm) FitDimensions(
        double physWMm,
        double physHMm,
        double? targetW,
        double? targetH)
    {
        if (!targetW.HasValue && !targetH.HasValue)
        {
            return (physWMm, physHMm);
        }

        if (targetW.HasValue && !targetH.HasValue)
        {
            double w = Math.Max(0.01, targetW.Value);
            return (w, w * (physHMm / Math.Max(physWMm, 1e-6)));
        }

        if (!targetW.HasValue && targetH.HasValue)
        {
            double h = Math.Max(0.01, targetH.Value);
            return (h * (physWMm / Math.Max(physHMm, 1e-6)), h);
        }

        double tw = Math.Max(0.01, targetW!.Value);
        double th = Math.Max(0.01, targetH!.Value);
        double scale = Math.Min(tw / physWMm, th / physHMm);
        return (physWMm * scale, physHMm * scale);
    }

    private static byte[] EncodePng(SKBitmap bmp)
    {
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static List<Point2D> EnsureSignedOrientation(List<Point2D> pts, bool positive)
    {
        double area = BinaryContourTracer.SignedArea(pts);
        bool currentlyPositive = area >= 0.0;
        if (currentlyPositive == positive)
        {
            return pts;
        }

        var reversed = new List<Point2D>(pts);
        reversed.Reverse();
        return reversed;
    }

    private static string ContourToSvgSubpath(IReadOnlyList<Point2D> pts, double mmPerPx)
    {
        if (pts.Count == 0)
        {
            return string.Empty;
        }

        var cmds = new List<string>(pts.Count + 1);
        for (int i = 0; i < pts.Count; i++)
        {
            string prefix = i == 0 ? "M" : "L";
            double x = pts[i].X * mmPerPx;
            double y = pts[i].Y * mmPerPx;
            cmds.Add($"{prefix} {Fmt(x)} {Fmt(y)}");
        }

        cmds.Add("Z");
        return string.Join(' ', cmds);
    }

    private static byte ClampByte(double val) => (byte)Math.Clamp((int)val, 0, 255);

    private static string Fmt(double value)
    {
        if (Math.Abs(value) < 5e-5)
        {
            return "0";
        }

        string s = value.ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static double Round4(double value) => Math.Round(value, 4, MidpointRounding.ToEven);
}
