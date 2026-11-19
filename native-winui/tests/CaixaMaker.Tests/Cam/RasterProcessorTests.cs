using System.Buffers.Binary;
using CaixaMaker.Cam.Raster;
using CaixaMaker.Core.Personalization;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests.Cam;

public sealed class RasterProcessorTests
{
    private readonly RasterProcessor _processor = new();

    [Fact]
    public void ProcessFromBytes_RejectsDecompressionBombBeforeAllocatingPixels()
    {
        // Header BMP v3 sintético de 54 bytes declarando 8000x8000 (64 MP > limite de 40 MP)
        byte[] bmpHeader = new byte[54 + 4];
        bmpHeader[0] = (byte)'B';
        bmpHeader[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmpHeader.AsSpan(2, 4), bmpHeader.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmpHeader.AsSpan(10, 4), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmpHeader.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmpHeader.AsSpan(18, 4), 8000);
        BinaryPrimitives.WriteInt32LittleEndian(bmpHeader.AsSpan(22, 4), 8000);
        BinaryPrimitives.WriteInt16LittleEndian(bmpHeader.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmpHeader.AsSpan(28, 2), 24);

        Action act = () => _processor.ProcessFromBytes(bmpHeader, "bomb.bmp");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*40.000.000*");
    }

    [Fact]
    public void ProcessFromBytes_FlattensAlphaOverWhite_AndAppliesFloydSteinbergDithering()
    {
        // Cria PNG 40x20 RGBA com fundo totalmente transparente (alpha=0) e gradiente cinza central
        using var bmp = new SKBitmap(40, 20, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                if (x < 10)
                {
                    bmp.SetPixel(x, y, new SKColor(0, 0, 0, 0)); // transparente -> deve virar branco 255
                }
                else
                {
                    byte v = (byte)(x * 6);
                    bmp.SetPixel(x, y, new SKColor(v, v, v, 255));
                }
            }
        }

        byte[] pngBytes = EncodePng(bmp);
        var opts = new RasterProcessingOptions
        {
            Grayscale = true,
            Dithering = true,
            Threshold = 128,
        };

        RasterImagePersonalizationElement el = _processor.ProcessFromBytes(
            pngBytes,
            "gradient.png",
            options: opts,
            targetWidthMm: 40.0);

        el.SourceFormat.Should().Be("PNG");
        el.OriginalPixelSize.Should().Equal(40, 20);
        el.BaseWidthMm.Should().BeApproximately(40.0, 1e-3);
        el.BaseHeightMm.Should().BeApproximately(20.0, 1e-3);

        byte[] outPng = Convert.FromBase64String(el.ProcessedImageB64);
        using SKBitmap decoded = SKBitmap.Decode(outPng);

        // Área transparente virou branco puro opaco
        SKColor corner = decoded.GetPixel(2, 2);
        corner.Red.Should().Be(255);
        corner.Green.Should().Be(255);
        corner.Blue.Should().Be(255);
        corner.Alpha.Should().Be(255);

        // Todos os pixels após dithering são binários {0, 255}
        for (int y = 0; y < decoded.Height; y++)
        {
            for (int x = 0; x < decoded.Width; x++)
            {
                SKColor c = decoded.GetPixel(x, y);
                (c.Red == 0 || c.Red == 255).Should().BeTrue();
                c.Red.Should().Be(c.Green);
                c.Green.Should().Be(c.Blue);
            }
        }
    }

    [Fact]
    public void Vectorize_DonutImage_PreservesOuterContourAndInternalHole()
    {
        // Anel preto 80x80: quadrado preto [10..70] com quadrado branco interno [28..52]
        using var bmp = new SKBitmap(80, 80, SKColorType.Bgra8888, SKAlphaType.Opaque);
        for (int y = 0; y < 80; y++)
        {
            for (int x = 0; x < 80; x++)
            {
                bool inOuter = x >= 10 && x <= 70 && y >= 10 && y <= 70;
                bool inHole = x >= 28 && x <= 52 && y >= 28 && y <= 52;
                bmp.SetPixel(x, y, (inOuter && !inHole) ? SKColors.Black : SKColors.White);
            }
        }

        byte[] pngBytes = EncodePng(bmp);

        // Modo Vetor (contornos individuais): deve gerar 2 contornos fechados (externo + furo)
        RasterImagePersonalizationElement vecEl = _processor.ProcessFromBytes(
            pngBytes,
            "donut.png",
            targetWidthMm: 80.0,
            vectorize: true,
            vectorizeEpsilonPx: 1.0,
            operation: CncOperationType.EngraveVector);

        vecEl.VectorPaths.Should().HaveCount(2);
        vecEl.VectorPaths.Should().OnlyContain(p => p.Operation == CncOperationType.EngraveVector && p.IsClosed);

        // Modo Preenchimento (EngraveFill): deve agrupar externo + furo num único caminho composto "M...Z M...Z"
        RasterImagePersonalizationElement fillEl = _processor.ProcessFromBytes(
            pngBytes,
            "donut.png",
            targetWidthMm: 80.0,
            vectorize: true,
            vectorizeEpsilonPx: 1.0,
            operation: CncOperationType.EngraveFill);

        fillEl.VectorPaths.Should().ContainSingle();
        string d = fillEl.VectorPaths[0].D;
        fillEl.VectorPaths[0].Operation.Should().Be(CncOperationType.EngraveFill);
        fillEl.VectorPaths[0].HasFill.Should().BeTrue();
        d.Count(ch => ch == 'M').Should().Be(2);
        d.Count(ch => ch == 'Z').Should().Be(2);
    }

    [Fact]
    public void Reprocess_WithCropAndResize_UpdatesProcessedImageAndDimensions()
    {
        using var bmp = new SKBitmap(100, 100, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var black = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            canvas.DrawRect(20, 20, 60, 60, black);
        }

        RasterImagePersonalizationElement el = _processor.ProcessFromBytes(
            EncodePng(bmp),
            "square.png",
            targetWidthMm: 50.0);

        el.OriginalPixelSize.Should().Equal(100, 100);
        el.ProcessedPixelSize.Should().Equal(100, 100);

        _processor.Reprocess(
            el,
            newOptions: new RasterProcessingOptions
            {
                CropBox = [0.0, 0.0, 0.5, 1.0],
                ResizeMaxPx = 40,
                Monochrome = true,
            },
            vectorize: true);

        el.ProcessedPixelSize[0].Should().Be(20);
        el.ProcessedPixelSize[1].Should().Be(40);
        el.Vectorize.Should().BeTrue();
        el.VectorPaths.Should().NotBeEmpty();
    }

    private static byte[] EncodePng(SKBitmap bmp)
    {
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
