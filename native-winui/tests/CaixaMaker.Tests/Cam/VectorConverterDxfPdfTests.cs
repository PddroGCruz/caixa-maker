using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Cam.Raster;
using CaixaMaker.Cam.Text;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests.Cam;

public sealed class VectorConverterDxfPdfTests
{
    [Fact]
    public void SvgToDxf_EmitsR2000MillimeterHeaders_Layers_Circles_AndArcBulges()
    {
        // Cria uma caixa completa (possui furos circulares de pino de dobradiça) + texto + curva arredondada
        var manager = new SurfaceManager(new BoxCalculator(3.0, 200.0, 150.0, 100.0));
        var vectorizer = new TextVectorizer();
        var text = new TextPersonalizationElement
        {
            PanelId = SurfaceId.Front,
            Content = "CNC",
            FontSizeMm = 12.0,
            RenderMode = TextRenderMode.Outline,
        };
        vectorizer.VectorizeElement(text);
        manager.AddElement(text);

        var exporter = new PersonalizationSvgExporter(manager);
        string boxSvg = exporter.ExportFullBoxSvg();

        string dxf = VectorConverter.SvgToDxf(boxSvg);

        dxf.Should().Contain("$ACADVER\n  1\nAC1015");
        dxf.Should().Contain("$INSUNITS\n 70\n4");
        dxf.Should().Contain("$MEASUREMENT\n 70\n1");
        dxf.Should().Contain("CUT_EXTERNAL");
        dxf.Should().Contain("CUT_INTERNAL");
        dxf.Should().Contain("ENGRAVE_VECTOR");
        dxf.Should().Contain("ENGRAVE_FILL");
        dxf.Should().Contain("MARK");

        // Furos das dobradiças e contornos curvos da caixa devem emitir CIRCLE e LWPOLYLINE
        dxf.Should().Contain("\nCIRCLE\n");
        dxf.Should().Contain("\nLWPOLYLINE\n");
        dxf.TrimEnd().Should().EndWith("EOF");
    }

    [Fact]
    public void SvgToDxf_PreservesPureCircles_RoundedRectBulge_AndVisibilityRules()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm" viewBox="0 0 100 80">
              <defs>
                <circle id="hidden_def" cx="50" cy="40" r="15" stroke="#000000" fill="none"/>
              </defs>
              <circle cx="20" cy="20" r="5" fill="none" stroke="#B71C1C"/>
              <rect x="40" y="10" width="30" height="20" rx="4" ry="4" fill="none" stroke="#0D47A1"/>
              <g display="none">
                <circle cx="80" cy="60" r="9" fill="none" stroke="#000000"/>
              </g>
              <g visibility="hidden">
                <rect x="5" y="5" width="10" height="10" fill="none" stroke="#1B5E20" visibility="visible"/>
                <rect x="25" y="5" width="10" height="10" fill="none" stroke="#000000"/>
              </g>
            </svg>
            """;

        string dxf = VectorConverter.SvgToDxf(svg);

        // Apenas 1 círculo visível em (20, 80 - 20 = 60) com raio 5 no layer CUT_INTERNAL; r=15 em defs e r=9 em display="none" ignorados
        Regex.Matches(dxf, @"(?m)^\s*CIRCLE\s*$").Count.Should().Be(1);
        dxf.Should().Contain("  8\nCUT_INTERNAL\n");

        // Rect com rx=4 ry=4 emite LWPOLYLINE no layer ENGRAVE_VECTOR com group code 42 (bulge != 0)
        dxf.Should().Contain("  8\nENGRAVE_VECTOR\n");
        dxf.Should().Contain("\n 42\n");

        // Rect interno visibility="visible" dentro de grupo hidden aparece em MARK; o segundo rect hidden não aparece
        dxf.Should().Contain("  8\nMARK\n");
        Regex.Matches(dxf, @"(?m)^\s*LWPOLYLINE\s*$").Count.Should().Be(2);
    }

    [Fact]
    public void SvgToDxf_FlattensNonUniformlyScaledCircleIntoSmoothPolylineInsteadOfDistortedCircle()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="100mm" viewBox="0 0 100 100">
              <g transform="scale(2, 1)">
                <circle cx="20" cy="30" r="10" fill="none" stroke="#000000"/>
              </g>
            </svg>
            """;

        string dxf = VectorConverter.SvgToDxf(svg);

        // Escala não-uniforme transforma círculo em elipse -> deve virar polilinha fechada suave, NÃO CIRCLE
        Regex.Matches(dxf, @"(?m)^\s*CIRCLE\s*$").Count.Should().Be(0);
        Regex.Matches(dxf, @"(?m)^\s*LWPOLYLINE\s*$").Count.Should().Be(1);
    }

    [Fact]
    public void SvgToPdf_ProducesValidPdf14_WithExactMediaBox_FlateDecode_AndAlphaSMaskAndAccurateXref()
    {
        var manager = new SurfaceManager(new BoxCalculator(3.0, 120.0, 80.0, 60.0));
        var rasterProc = new RasterProcessor();

        using var bmp = new SKBitmap(24, 24, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 24; x++)
            {
                bmp.SetPixel(x, y, x < 12 ? new SKColor(10, 20, 30, 128) : new SKColor(200, 100, 50, 255));
            }
        }

        using SKImage skImg = SKImage.FromBitmap(bmp);
        using SKData pngData = skImg.Encode(SKEncodedImageFormat.Png, 100);
        RasterImagePersonalizationElement raster = rasterProc.ProcessFromBytes(pngData.ToArray(), "icon.png", panelId: SurfaceId.Front, targetWidthMm: 20.0);

        // Injeta PNG com canal alfa na folha exportada para testar /SMask e /DeviceGray também
        string svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="50mm" viewBox="0 0 100 50">
              <image href="data:image/png;base64,{Convert.ToBase64String(pngData.ToArray())}" x="10" y="10" width="20" height="20"/>
              <path d="M 5 5 L 95 5 L 95 45 L 5 45 Z" fill="none" stroke="#000000" stroke-width="0.03mm"/>
              <path d="M 40 15 L 60 15 L 60 35 L 40 35 Z" fill="#6A1B9A" stroke="none"/>
            </svg>
            """;

        byte[] pdfBytes = VectorConverter.SvgToPdf(svg);
        string pdfLatin1 = Encoding.Latin1.GetString(pdfBytes);

        pdfLatin1.Should().StartWith("%PDF-1.4");
        pdfLatin1.TrimEnd().Should().EndWith("%%EOF");

        // 100 mm x 50 mm em pontos PDF = 283.4646 x 141.7323
        double expectedW = 100.0 * VectorConverter.MmToPt;
        double expectedH = 50.0 * VectorConverter.MmToPt;
        Match mbMatch = Regex.Match(pdfLatin1, @"/MediaBox\s*\[\s*0\s+0\s+([0-9.]+)\s+([0-9.]+)\s*\]");
        mbMatch.Success.Should().BeTrue();
        double.Parse(mbMatch.Groups[1].Value, CultureInfo.InvariantCulture).Should().BeApproximately(expectedW, 0.01);
        double.Parse(mbMatch.Groups[2].Value, CultureInfo.InvariantCulture).Should().BeApproximately(expectedH, 0.01);

        pdfLatin1.Should().Contain("/Filter /FlateDecode");
        pdfLatin1.Should().Contain("/Subtype /Image");
        pdfLatin1.Should().Contain("/ColorSpace /DeviceRGB");
        pdfLatin1.Should().Contain("/SMask ");
        pdfLatin1.Should().Contain("/ColorSpace /DeviceGray");

        // Valida que todos os offsets da tabela xref apontam exatamente para os objetos "N 0 obj"
        Match xrefMatch = Regex.Match(pdfLatin1, @"xref\s+0\s+(\d+)\s+((?:\d{10}\s+\d{5}\s+[fn]\s+)+)trailer");
        xrefMatch.Success.Should().BeTrue();
        int count = int.Parse(xrefMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        MatchCollection entries = Regex.Matches(xrefMatch.Groups[2].Value, @"(\d{10})\s+(\d{5})\s+([fn])");
        entries.Count.Should().Be(count);

        for (int objId = 1; objId < count; objId++)
        {
            long offset = long.Parse(entries[objId].Groups[1].Value, CultureInfo.InvariantCulture);
            entries[objId].Groups[3].Value.Should().Be("n");
            string slice = Encoding.ASCII.GetString(pdfBytes, (int)offset, Math.Min(16, pdfBytes.Length - (int)offset));
            slice.Should().StartWith($"{objId} 0 obj");
        }
    }

    [Fact]
    public void SvgToDxf_MapsPurpleFillToEngraveFill_AndOnlyCircleElementsToCircleEntities()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="60mm" height="40mm" viewBox="0 0 60 40">
              <circle cx="15" cy="15" r="5" fill="none" stroke="#B71C1C"/>
              <path d="M 35 15 A 5 5 0 1 1 25 15 A 5 5 0 1 1 35 15 Z" fill="#6A1B9A" stroke="none"/>
            </svg>
            """;

        string dxf = VectorConverter.SvgToDxf(svg);

        Regex.Matches(dxf, @"(?m)^\s*CIRCLE\s*$").Count.Should().Be(1);
        Regex.Matches(dxf, @"(?m)^\s*LWPOLYLINE\s*$").Count.Should().Be(1);
        dxf.Should().Contain("  8\nCUT_INTERNAL\n");
        dxf.Should().Contain("  8\nENGRAVE_FILL\n");
        dxf.Should().NotContain("  8\nENGRAVE_VECTOR\n");
    }
}
