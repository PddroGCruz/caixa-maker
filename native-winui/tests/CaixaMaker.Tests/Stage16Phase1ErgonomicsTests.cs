using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CaixaMaker.App.Models;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Cam.Calibration;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Cam.Svg;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class CadMathExpressionEvaluatorTests
{
    [Theory]
    [InlineData("200 + 6", 206.0)]
    [InlineData("150 - 2 * 3", 144.0)]
    [InlineData("(100 + 50) / 2", 75.0)]
    [InlineData("12,5 * 4", 50.0)]
    [InlineData("-0.15 + 0.05", -0.10)]
    [InlineData("(120 + 30) * 1.5", 225.0)]
    [InlineData("300 / 2,5", 120.0)]
    [InlineData("-(10 + 5) * -2", 30.0)]
    [InlineData("0.5", 0.5)]
    [InlineData(",25 + .75", 1.0)]
    public void TryEvaluate_ValidExpressions_RespectsPrecedenceParenthesesAndCommaDecimals(
        string expression,
        double expected)
    {
        bool ok = CadMathExpressionEvaluator.TryEvaluate(expression, out double result);

        ok.Should().BeTrue($"'{expression}' deve ser avaliada com sucesso");
        result.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10 / 0")]
    [InlineData("10 / (5 - 5)")]
    [InlineData("((1 + 2)")]
    [InlineData("(1 + 2))")]
    [InlineData("()")]
    [InlineData("10 + * 2")]
    [InlineData("10 + ")]
    [InlineData("1.2.3")]
    [InlineData("1,2,3")]
    [InlineData("abc")]
    [InlineData("200mm")]
    public void TryEvaluate_InvalidOrDivisionByZero_ReturnsFalseWithoutThrowing(string? expression)
    {
        bool ok = CadMathExpressionEvaluator.TryEvaluate(expression, out double result);

        ok.Should().BeFalse();
        result.Should().Be(0.0);
    }
}

public sealed class DimensionReferenceModeTests
{
    [Theory]
    [InlineData(BoxWithLidAndHandlesModel.Id)]
    [InlineData(OpenBoxModel.Id)]
    public void InternalUsableMode_LocksInnerClearanceAcrossThicknessChanges_AndUpdatesExternalAndSurfaces(
        string modelId)
    {
        var vm = new BoxWorkspaceViewModel();
        vm.LoadModel(modelId, applyDefaults: true, markAsEdit: false);
        vm.DimensionMode.Should().Be(DimensionReferenceMode.External);

        // Alterna para referência Interna Útil e define vão interno alvo 194 × 144 × 97 mm com MDF 3.0 mm
        vm.SetDimensionReferenceMode(DimensionReferenceMode.InternalUsable);
        vm.IsInternalDimensionMode.Should().BeTrue();
        vm.SetInputDimensions(194.0, 144.0, 97.0);

        vm.BoxWidth.Should().BeApproximately(200.0, 1e-4);
        vm.BoxDepth.Should().BeApproximately(150.0, 1e-4);
        vm.BoxHeight.Should().BeApproximately(100.0, 1e-4);
        vm.InnerWidthMm.Should().BeApproximately(194.0, 1e-4);
        vm.InnerDepthMm.Should().BeApproximately(144.0, 1e-4);
        vm.InnerHeightMm.Should().BeApproximately(97.0, 1e-4);

        // Ao alterar a espessura para 6.0 mm em InternalUsable, o vão interno permanece travado em 194 × 144 × 97 mm
        // e a caixa externa se expande automaticamente para 206 × 156 × 103 mm.
        int rebuildCount = 0;
        vm.GeometryRebuilt += (_, _) => rebuildCount++;

        vm.BoxThickness = 6.0;

        rebuildCount.Should().Be(1);
        vm.BoxWidth.Should().BeApproximately(206.0, 1e-4);
        vm.BoxDepth.Should().BeApproximately(156.0, 1e-4);
        vm.BoxHeight.Should().BeApproximately(103.0, 1e-4);
        vm.InnerWidthMm.Should().BeApproximately(194.0, 1e-4);
        vm.InnerDepthMm.Should().BeApproximately(144.0, 1e-4);
        vm.InnerHeightMm.Should().BeApproximately(97.0, 1e-4);
        vm.InputWidth.Should().BeApproximately(194.0, 1e-4);
        vm.InputDepth.Should().BeApproximately(144.0, 1e-4);
        vm.InputHeight.Should().BeApproximately(97.0, 1e-4);
        vm.Summary!.ComputedDimensions.InnerWidth.Should().BeApproximately(194.0, 1e-4);
        vm.Summary.ComputedDimensions.InnerDepth.Should().BeApproximately(144.0, 1e-4);
        vm.Summary.ComputedDimensions.InnerHeight.Should().BeApproximately(97.0, 1e-4);

        // No modo Externa Total, trocar espessura preserva as externas (200×150×100) e reduz a interna útil.
        vm.SetDimensionReferenceMode(DimensionReferenceMode.External);
        vm.ApplyParameters(new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 200.0,
            BoxDepth = 150.0,
            BoxHeight = 100.0,
            Folga = 0.0,
        });
        vm.BoxThickness = 6.0;

        vm.BoxWidth.Should().BeApproximately(200.0, 1e-4);
        vm.BoxDepth.Should().BeApproximately(150.0, 1e-4);
        vm.BoxHeight.Should().BeApproximately(100.0, 1e-4);
        vm.InnerWidthMm.Should().BeApproximately(188.0, 1e-4);
        vm.InnerDepthMm.Should().BeApproximately(138.0, 1e-4);
        vm.InnerHeightMm.Should().BeApproximately(94.0, 1e-4);
    }

    [Fact]
    public void MainWindowViewModel_PropagatesInternalLockResizeToSurfaceManagerAndManufacturingThickness()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "CaixaMaker_Stage16_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);
        try
        {
            var paths = AppPaths.Resolve(new AppPathsOptions
            {
                DesktopMode = false,
                ProjectsDir = Path.Combine(sandbox, "projects"),
                OutputsDir = Path.Combine(sandbox, "outputs"),
                ConfigDir = Path.Combine(sandbox, "config"),
                LogsDir = Path.Combine(sandbox, "logs"),
                TempDir = Path.Combine(sandbox, "temp"),
            });
            paths.EnsureDirs();

            var mainVm = new MainWindowViewModel(paths, new NullNativeFileDialogService());
            mainVm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
            mainVm.Box.SetDimensionReferenceMode(DimensionReferenceMode.InternalUsable);
            mainVm.Box.SetInputDimensions(194.0, 144.0, 97.0);
            mainVm.Box.SetQuickThickness(6.0);

            mainVm.Box.BoxWidth.Should().BeApproximately(206.0, 1e-4);
            mainVm.Personalization.Manager.Calc.Width.Should().BeApproximately(206.0, 1e-4);
            mainVm.Personalization.Manager.Calc.Thickness.Should().BeApproximately(6.0, 1e-4);
            mainVm.Manufacturing.CurrentThicknessMm.Should().BeApproximately(6.0, 1e-4);

            // Ao aplicar preset rápido, InputWidth/InputDepth/InputHeight acompanham imediatamente o preset.
            mainVm.Box.ApplyCustomPresetDimensions(120.0, 90.0, 60.0);
            mainVm.Box.BoxWidth.Should().BeApproximately(120.0, 1e-4);
            mainVm.Box.InputWidth.Should().BeApproximately(108.0, 1e-4);
            mainVm.Box.InputDepth.Should().BeApproximately(78.0, 1e-4);
            mainVm.Box.InputHeight.Should().BeApproximately(54.0, 1e-4);
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }
}

public sealed class CuttingPerimeterCalculationTests
{
    [Fact]
    public void TotalCutPerimeter_IsDeterministicScalesWithSizeAndIncludesHingeHoles()
    {
        IBoxModelDefinition lidModel = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        IBoxModelDefinition openModel = BoxModelRegistry.Default.Get(OpenBoxModel.Id);

        BoxParameters small = new() { BoxThickness = 3.0, BoxWidth = 120.0, BoxDepth = 90.0, BoxHeight = 60.0 };
        BoxParameters medium = BoxParameters.Default; // 200 × 150 × 100
        BoxParameters large = new() { BoxThickness = 3.0, BoxWidth = 300.0, BoxDepth = 220.0, BoxHeight = 140.0 };

        BoxCalculationSummary sSmall = lidModel.BuildSummary(small);
        BoxCalculationSummary sMedA = lidModel.BuildSummary(medium);
        BoxCalculationSummary sMedB = lidModel.BuildSummary(medium);
        BoxCalculationSummary sLarge = lidModel.BuildSummary(large);
        BoxCalculationSummary sOpenMed = openModel.BuildSummary(medium);

        sMedA.TotalCutPerimeterMm.Should().Be(sMedB.TotalCutPerimeterMm);
        sMedA.TotalCutPerimeterMm.Should().BeGreaterThan(0.0);
        sMedA.OuterCutPerimeterMm.Should().BeGreaterThan(0.0);
        sMedA.InternalCutPerimeterMm.Should().BeGreaterThan(0.0);
        sMedA.TotalCutPerimeterMm.Should().BeApproximately(
            sMedA.OuterCutPerimeterMm + sMedA.InternalCutPerimeterMm,
            1e-2);
        sMedA.TotalCutPerimeterMeters.Should().BeApproximately(
            Math.Round(sMedA.TotalCutPerimeterMm / 1000.0, 3),
            1e-6);

        sSmall.TotalCutPerimeterMm.Should().BeLessThan(sMedA.TotalCutPerimeterMm);
        sMedA.TotalCutPerimeterMm.Should().BeLessThan(sLarge.TotalCutPerimeterMm);

        // Caixa com tampa e alças possui 6 chapas + 2 furos circulares extras das alças, superando a caixa aberta.
        sMedA.TotalCutPerimeterMm.Should().BeGreaterThan(sOpenMed.TotalCutPerimeterMm);

        // Verificar soma exata das chapas na geometria 2D (incluindo circunferências 2*pi*r dos furos das laterais).
        BoxSheetGeometry2D sheet = lidModel.BuildSheetGeometry(medium);
        PanelGeometry2D rightSide = sheet.PanelsById["right"];
        double expectedCirclePerimeter = rightSide.CircularHoles.Sum(c => 2.0 * Math.PI * c.Radius);
        double expectedInternalHoles = rightSide.InternalHoles.Sum(h => h.PerimeterMm);
        rightSide.InternalCutPerimeterMm.Should().BeApproximately(expectedCirclePerimeter + expectedInternalHoles, 1e-6);
        rightSide.TotalCutPerimeterMm.Should().BeApproximately(
            rightSide.OuterCutPerimeterMm + rightSide.InternalCutPerimeterMm,
            1e-6);
        sheet.TotalCutPerimeterMm.Should().BeApproximately(sMedA.TotalCutPerimeterMm, 1e-2);
    }
}

public sealed class KerfCalibrationCombGeneratorTests : IDisposable
{
    private readonly string _sandboxRoot;

    public KerfCalibrationCombGeneratorTests()
    {
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_KerfTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_sandboxRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandboxRoot))
            {
                Directory.Delete(_sandboxRoot, recursive: true);
            }
        }
        catch
        {
            // Melhor esforço
        }
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(6.0)]
    public void Generate_ForMdf3And6Mm_ProducesExactSevenSlotWidthsAndValidSvgDxfPdf(double thicknessMm)
    {
        KerfCalibrationCombResult result = KerfCalibrationCombGenerator.Default.Generate(thicknessMm);

        result.ThicknessMm.Should().BeApproximately(thicknessMm, 1e-4);
        result.TenonWidthMm.Should().BeApproximately(thicknessMm, 1e-4);
        result.TenonLengthMm.Should().BeApproximately(10.0, 1e-4);
        result.Slots.Should().HaveCount(7);

        double[] expectedDeltas = [-0.15, -0.10, -0.05, 0.00, 0.05, 0.10, 0.15];
        for (int i = 0; i < expectedDeltas.Length; i++)
        {
            double delta = expectedDeltas[i];
            double expectedWidth = Math.Max(0.10, thicknessMm + delta);
            result.Slots[i].FolgaMm.Should().BeApproximately(delta, 1e-4);
            result.Slots[i].EffectiveWidthMm.Should().BeApproximately(expectedWidth, 1e-4);
            (result.Slots[i].XEndMm - result.Slots[i].XStartMm).Should().BeApproximately(expectedWidth, 1e-4);
        }

        // Validar estrutura XML SVG e vértices reais do contorno CUT_EXTERNAL
        XDocument doc = XDocument.Parse(result.SvgContent);
        XNamespace svgNs = "http://www.w3.org/2000/svg";
        doc.Root!.Name.Should().Be(svgNs + "svg");

        XElement? extLayer = doc.Root.Elements(svgNs + "g")
            .FirstOrDefault(g => g.Attribute("id")?.Value == "layer_cut_external");
        extLayer.Should().NotBeNull();
        string outlineD = extLayer!.Element(svgNs + "path")!.Attribute("d")!.Value;

        List<(double X, double Y)> vertices = ParseOutlinePoints(outlineD);
        for (int i = 0; i < result.Slots.Count; i++)
        {
            KerfCalibrationSlotSpec slot = result.Slots[i];
            bool hasTopHorizontalSegment = false;
            for (int v = 0; v < vertices.Count - 1; v++)
            {
                if (Math.Abs(vertices[v].Y - vertices[v + 1].Y) < 1e-4 &&
                    Math.Abs(vertices[v].X - slot.XEndMm) < 1e-4 &&
                    Math.Abs(vertices[v + 1].X - slot.XStartMm) < 1e-4)
                {
                    hasTopHorizontalSegment = true;
                    Math.Abs(vertices[v].X - vertices[v + 1].X)
                        .Should().BeApproximately(slot.EffectiveWidthMm, 1e-4);
                    break;
                }
            }

            hasTopHorizontalSegment.Should().BeTrue($"o rasgo {i} ({slot.EffectiveWidthMm:F2} mm) deve existir exatamente no path CUT_EXTERNAL");
        }

        // Validar camada de gravação vetorial (#0D47A1) e furo interno (#B71C1C)
        doc.Root.Elements(svgNs + "g")
            .FirstOrDefault(g => g.Attribute("id")?.Value == "layer_cut_internal")
            .Should().NotBeNull();
        XElement? engraveLayer = doc.Root.Elements(svgNs + "g")
            .FirstOrDefault(g => g.Attribute("id")?.Value == "layer_engrave_vector");
        engraveLayer.Should().NotBeNull();
        engraveLayer!.Descendants(svgNs + "path").Should().NotBeEmpty();

        // Validar SVG importável pelo SvgImporter sem warnings
        SvgImportResult reimported = new SvgImporter().ImportFromString(
            svgXml: result.SvgContent,
            sourceFilename: "comb.svg");
        reimported.Warnings.Should().BeEmpty();
        reimported.Element.Paths.Should().NotBeEmpty();

        // Validar DXF R2000 (AC1015)
        string dxfText = Encoding.ASCII.GetString(result.ToExportBytes(VectorExportFormat.Dxf));
        dxfText.Should().Contain("AC1015");
        dxfText.Should().Contain("CUT_EXTERNAL");
        dxfText.Should().Contain("CUT_INTERNAL");
        dxfText.Should().Contain("ENGRAVE_VECTOR");
        dxfText.Should().Contain("LWPOLYLINE");
        dxfText.Should().Contain("CIRCLE");
        dxfText.Should().Contain("EOF");

        // Validar PDF 1.4
        byte[] pdfBytes = result.ToExportBytes(VectorExportFormat.Pdf);
        string pdfHeader = Encoding.ASCII.GetString(pdfBytes, 0, Math.Min(16, pdfBytes.Length));
        string pdfTail = Encoding.ASCII.GetString(pdfBytes, Math.Max(0, pdfBytes.Length - 16), Math.Min(16, pdfBytes.Length));
        pdfHeader.Should().StartWith("%PDF-1.4");
        pdfTail.Should().Contain("%%EOF");
    }

    [Fact]
    public async Task ManufacturingWorkspaceViewModel_ExportsKerfCombAndRegistersInRecentOutputs()
    {
        var paths = AppPaths.Resolve(new AppPathsOptions
        {
            DesktopMode = false,
            ProjectsDir = Path.Combine(_sandboxRoot, "projects"),
            OutputsDir = Path.Combine(_sandboxRoot, "outputs"),
            ConfigDir = Path.Combine(_sandboxRoot, "config"),
            LogsDir = Path.Combine(_sandboxRoot, "logs"),
            TempDir = Path.Combine(_sandboxRoot, "temp"),
        });
        paths.EnsureDirs();

        var mfg = new ManufacturingWorkspaceViewModel(paths)
        {
            CurrentThicknessMm = 6.0,
        };

        string? svgPath = await mfg.ExportKerfCalibrationCombAsync("svg");
        string? dxfPath = await mfg.ExportKerfCalibrationCombAsync("dxf");
        string? pdfPath = await mfg.ExportKerfCalibrationCombAsync("pdf");

        svgPath.Should().NotBeNull();
        dxfPath.Should().NotBeNull();
        pdfPath.Should().NotBeNull();

        File.Exists(svgPath!).Should().BeTrue();
        File.Exists(dxfPath!).Should().BeTrue();
        File.Exists(pdfPath!).Should().BeTrue();

        Path.GetFileName(svgPath!).Should().Be("pente_kerf_6.0mm.svg");
        Path.GetFileName(dxfPath!).Should().Be("pente_kerf_6.0mm.dxf");
        Path.GetFileName(pdfPath!).Should().Be("pente_kerf_6.0mm.pdf");

        mfg.RecentOutputs.Should().HaveCount(3);
        mfg.RecentOutputs.Select(r => r.FormatLabel).Should().ContainInOrder("KERF PDF", "KERF DXF", "KERF SVG");
    }

    private static List<(double X, double Y)> ParseOutlinePoints(string pathData)
    {
        var matches = Regex.Matches(pathData, @"([ML])\s*([-+]?\d+(?:\.\d+)?),([-+]?\d+(?:\.\d+)?)");
        var pts = new List<(double X, double Y)>(matches.Count);
        foreach (Match m in matches)
        {
            double x = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            double y = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            pts.Add((x, y));
        }

        return pts;
    }
}
