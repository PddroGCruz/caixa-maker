using System.Globalization;
using System.Text.RegularExpressions;
using CaixaMaker.Cam.Text;
using CaixaMaker.Core.Personalization;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Cam;

public sealed class TextVectorizerTests
{
    private readonly FontResolver _resolver = FontResolver.Default;
    private readonly TextVectorizer _vectorizer = new();

    [Fact]
    public void FontResolver_ListsSystemFonts_AndResolvesArialWithoutFallback()
    {
        IReadOnlyList<string> families = _resolver.ListFamilies();
        families.Should().NotBeEmpty();

        (FontDescriptor desc, bool fallback) = _resolver.Resolve("Arial", bold: false, italic: false);
        desc.Should().NotBeNull();
        File.Exists(desc.Path).Should().BeTrue();
        desc.Family.Should().ContainEquivalentOf("Arial");
        fallback.Should().BeFalse();
    }

    [Fact]
    public void FontResolver_UsesFallbackFlag_WhenFontFamilyDoesNotExist()
    {
        (FontDescriptor desc, bool fallback) = _resolver.Resolve("NonExistentFontXYZ_99999");
        desc.Should().NotBeNull();
        File.Exists(desc.Path).Should().BeTrue();
        fallback.Should().BeTrue();
    }

    [Fact]
    public void Vectorize_OutlineFillBothModes_ProduceExpectedOperationsAndTightBounds()
    {
        TextVectorizationResult outline = _vectorizer.Vectorize(
            "Caixa",
            fontFamily: "Arial",
            fontSizeMm: 14.0,
            renderMode: TextRenderMode.Outline);

        outline.Paths.Should().NotBeEmpty();
        outline.Paths.Should().OnlyContain(p => p.Operation == CncOperationType.EngraveVector && !p.HasFill && p.HasStroke);
        outline.WidthMm.Should().BeGreaterThan(10.0);
        outline.HeightMm.Should().BeInRange(8.0, 18.0);

        // Valida tight bounds normalizados próximos de (0, 0)
        (double minX, double minY, double maxX, double maxY) = MeasureApproximateBounds(outline.Paths);
        minX.Should().BeInRange(0.0, 0.5);
        minY.Should().BeInRange(0.0, 0.5);
        maxX.Should().BeApproximately(outline.WidthMm, 0.8);
        maxY.Should().BeApproximately(outline.HeightMm, 0.8);

        TextVectorizationResult fill = _vectorizer.Vectorize(
            "Caixa",
            fontFamily: "Arial",
            fontSizeMm: 14.0,
            renderMode: TextRenderMode.Fill);

        fill.Paths.Should().NotBeEmpty();
        fill.Paths.Should().OnlyContain(p => p.Operation == CncOperationType.EngraveFill && p.HasFill && !p.HasStroke);

        TextVectorizationResult both = _vectorizer.Vectorize(
            "Caixa",
            fontFamily: "Arial",
            fontSizeMm: 14.0,
            renderMode: TextRenderMode.Both);

        both.Paths.Should().Contain(p => p.Operation == CncOperationType.EngraveFill);
        both.Paths.Should().Contain(p => p.Operation == CncOperationType.EngraveVector);
    }

    [Fact]
    public void Vectorize_HatchMode_GeneratesScanlineSegmentsWithParityHoleInsideLetterO()
    {
        TextVectorizationResult hatch = _vectorizer.Vectorize(
            "O",
            fontFamily: "Arial",
            fontSizeMm: 20.0,
            renderMode: TextRenderMode.Hatch,
            hatchSpacingMm: 0.4);

        hatch.Paths.Should().NotBeEmpty();
        hatch.Paths.Should().OnlyContain(p => !p.IsClosed && p.Operation == CncOperationType.EngraveVector && p.HasStroke);

        // Na linha horizontal mediana da letra 'O', a paridade EvenOdd exige 2 segmentos separados (esquerda e direita do miolo)
        double midY = hatch.HeightMm * 0.5;
        var midLineSegments = new List<(double X0, double X1, double Y)>();

        foreach (VectorPathItem item in hatch.Paths)
        {
            Match m = Regex.Match(item.D, @"^M\s+([^\s]+)\s+([^\s]+)\s+L\s+([^\s]+)\s+([^\s]+)$");
            m.Success.Should().BeTrue($"esperado segmento de hachura 'M x0 y L x1 y', obtido '{item.D}'");
            double x0 = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            double y0 = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            double x1 = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            double y1 = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            y0.Should().BeApproximately(y1, 1e-4);
            x1.Should().BeGreaterThan(x0);

            if (Math.Abs(y0 - midY) <= 0.6)
            {
                midLineSegments.Add((x0, x1, y0));
            }
        }

        // Pelo menos alguma scanline próxima ao centro Y tem 2 segmentos (perna esquerda e perna direita do 'O')
        midLineSegments
            .GroupBy(s => Math.Round(s.Y, 3))
            .Any(g => g.Count() == 2)
            .Should().BeTrue();
    }

    [Fact]
    public void Vectorize_MultiLineAlignmentAndAccents_Succeeds()
    {
        TextVectorizationResult left = _vectorizer.Vectorize("A\nAção Ção", fontFamily: "Arial", fontSizeMm: 10.0, horizontalAlign: HorizontalAlign.Left);
        TextVectorizationResult center = _vectorizer.Vectorize("A\nAção Ção", fontFamily: "Arial", fontSizeMm: 10.0, horizontalAlign: HorizontalAlign.Center);
        TextVectorizationResult right = _vectorizer.Vectorize("A\nAção Ção", fontFamily: "Arial", fontSizeMm: 10.0, horizontalAlign: HorizontalAlign.Right);

        left.Paths.Should().HaveCountGreaterThan(5);
        center.Paths.Should().HaveCount(left.Paths.Count);
        right.Paths.Should().HaveCount(left.Paths.Count);

        // Largura da tinta varia apenas pelos side-bearings dos glifos das extremidades
        left.WidthMm.Should().BeApproximately(right.WidthMm, 1.0);
        left.HeightMm.Should().BeGreaterThan(14.0);

        // O primeiro glifo ("A" na linha 1) desloca sua coordenada mínima X de esquerda (~0) -> centro -> direita
        (double leftMinX, _, _, _) = MeasureApproximateBounds([left.Paths[0]]);
        (double centerMinX, _, _, _) = MeasureApproximateBounds([center.Paths[0]]);
        (double _, _, double rightMaxX, _) = MeasureApproximateBounds([right.Paths[0]]);

        leftMinX.Should().BeLessThan(1.0);
        centerMinX.Should().BeInRange(center.WidthMm * 0.25, center.WidthMm * 0.65);
        rightMaxX.Should().BeGreaterThan(right.WidthMm * 0.85);
    }

    [Fact]
    public void VectorizeElement_PopulatesModelPropertiesAndOperation()
    {
        var element = new TextPersonalizationElement
        {
            Content = "IFMaker",
            FontFamily = "Arial",
            FontSizeMm = 12.0,
            RenderMode = TextRenderMode.Fill,
            Bold = true,
        };

        TextVectorizationResult result = _vectorizer.VectorizeElement(element);

        element.Vectorized.Should().BeTrue();
        element.VectorPaths.Should().HaveCount(result.Paths.Count);
        element.BaseWidthMm.Should().Be(result.WidthMm);
        element.BaseHeightMm.Should().Be(result.HeightMm);
        element.WidthMm.Should().Be(result.WidthMm);
        element.HeightMm.Should().Be(result.HeightMm);
        element.Operation.Should().Be(CncOperationType.EngraveFill);
        element.FontFallbackUsed.Should().BeFalse();
        element.ResolvedFontName.Should().NotBeNullOrWhiteSpace();
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) MeasureApproximateBounds(
        IEnumerable<VectorPathItem> items)
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        foreach (VectorPathItem item in items)
        {
            MatchCollection nums = Regex.Matches(item.D, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");
            for (int i = 0; i + 1 < nums.Count; i += 2)
            {
                double x = double.Parse(nums[i].Value, CultureInfo.InvariantCulture);
                double y = double.Parse(nums[i + 1].Value, CultureInfo.InvariantCulture);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return (minX, minY, maxX, maxY);
    }
}
