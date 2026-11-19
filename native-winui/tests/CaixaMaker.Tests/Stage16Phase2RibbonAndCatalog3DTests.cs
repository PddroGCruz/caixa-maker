using System.Xml.Linq;
using CaixaMaker.App.Input;
using CaixaMaker.App.Rendering;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Projects;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage16Phase2RibbonAndCatalog3DTests
{
    private static string ResolveRepoRoot()
    {
        string? versionFile = AppVersion.ResolveVersionFile(AppContext.BaseDirectory);
        versionFile.Should().NotBeNull("o arquivo VERSION na raiz do repositório deve estar acessível");
        return Path.GetDirectoryName(versionFile!)!;
    }

    private static int CountDifferentPixels(SKBitmap a, SKBitmap b)
    {
        a.Width.Should().Be(b.Width);
        a.Height.Should().Be(b.Height);

        ReadOnlySpan<byte> pxA = a.GetPixelSpan();
        ReadOnlySpan<byte> pxB = b.GetPixelSpan();
        int diffCount = 0;
        for (int i = 0; i < pxA.Length; i += 4)
        {
            if (pxA[i] != pxB[i] ||
                pxA[i + 1] != pxB[i + 1] ||
                pxA[i + 2] != pxB[i + 2] ||
                pxA[i + 3] != pxB[i + 3])
            {
                diffCount++;
            }
        }

        return diffCount;
    }

    private static int CountNonBackgroundPixels(SKBitmap bmp)
    {
        ReadOnlySpan<byte> px = bmp.GetPixelSpan();
        byte bgB = px[0];
        byte bgG = px[1];
        byte bgR = px[2];
        int nonBg = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (Math.Abs(px[i] - bgB) > 4 ||
                Math.Abs(px[i + 1] - bgG) > 4 ||
                Math.Abs(px[i + 2] - bgR) > 4)
            {
                nonBg++;
            }
        }

        return nonBg;
    }

    [Theory]
    [InlineData(BoxWithLidAndHandlesModel.Id)]
    [InlineData(OpenBoxModel.Id)]
    public void CatalogPreview3DService_RendersDistinctMountedExplodedAndThemePreviews(string modelId)
    {
        CatalogPreview3DService.ClearCache();

        using SKBitmap mountedDark = CatalogPreview3DService.RenderModelPreviewBitmap(
            modelId, exploded: false, darkTheme: true, widthPx: 320, heightPx: 224);
        using SKBitmap explodedDark = CatalogPreview3DService.RenderModelPreviewBitmap(
            modelId, exploded: true, darkTheme: true, widthPx: 320, heightPx: 224);
        using SKBitmap mountedLight = CatalogPreview3DService.RenderModelPreviewBitmap(
            modelId, exploded: false, darkTheme: false, widthPx: 320, heightPx: 224);

        mountedDark.Width.Should().Be(320);
        mountedDark.Height.Should().Be(224);
        mountedDark.ColorType.Should().Be(SKColorType.Bgra8888);

        CountNonBackgroundPixels(mountedDark).Should().BeGreaterThan(2000);
        CountNonBackgroundPixels(explodedDark).Should().BeGreaterThan(2000);
        CountNonBackgroundPixels(mountedLight).Should().BeGreaterThan(2000);

        CountDifferentPixels(mountedDark, explodedDark).Should().BeGreaterThan(1500);
        CountDifferentPixels(mountedDark, mountedLight).Should().BeGreaterThan(10000);

        byte[] pngBytes = CatalogPreview3DService.RenderModelPreviewPng(
            modelId, exploded: false, darkTheme: true, widthPx: 200, heightPx: 140);
        pngBytes.Length.Should().BeGreaterThan(1024);
        pngBytes[0].Should().Be(0x89);
        pngBytes[1].Should().Be(0x50);
        pngBytes[2].Should().Be(0x4E);
        pngBytes[3].Should().Be(0x47);
    }

    [Fact]
    public void CatalogPreview3DService_RendersCompactProjectThumbnailsForDistinctDimensions()
    {
        var compactProject = new ProjectSummary
        {
            Id = "proj-small",
            Name = "Estojo Pequeno",
            CreatedAt = "2026-11-14T12:00:00Z",
            UpdatedAt = "2026-11-14T12:05:00Z",
            ModelId = BoxWithLidAndHandlesModel.Id,
            ModelName = "Caixa com Tampa e Alças",
            BoxParameters = new ProjectBoxParameters
            {
                BoxWidth = 110,
                BoxDepth = 90,
                BoxHeight = 60,
                BoxThickness = 3.0,
                Folga = 0.05,
            },
            ViewerState = new ProjectViewerState { Exploded = false, LidAngle = 0 },
        };

        var tallProject = new ProjectSummary
        {
            Id = "proj-tall",
            Name = "Caixa Aberta Alta",
            CreatedAt = "2026-11-14T12:00:00Z",
            UpdatedAt = "2026-11-14T12:10:00Z",
            ModelId = OpenBoxModel.Id,
            ModelName = "Caixa Aberta",
            BoxParameters = new ProjectBoxParameters
            {
                BoxWidth = 340,
                BoxDepth = 180,
                BoxHeight = 160,
                BoxThickness = 6.0,
                Folga = 0.05,
            },
            ViewerState = new ProjectViewerState { Exploded = true, LidAngle = 0 },
        };

        using SKBitmap thumbSmall48 = CatalogPreview3DService.RenderProjectThumbnailBitmap(
            compactProject, darkTheme: true, widthPx: 48, heightPx: 34);
        using SKBitmap thumbTall48 = CatalogPreview3DService.RenderProjectThumbnailBitmap(
            tallProject, darkTheme: true, widthPx: 48, heightPx: 34);

        thumbSmall48.Width.Should().Be(48);
        thumbSmall48.Height.Should().Be(34);
        CountNonBackgroundPixels(thumbSmall48).Should().BeGreaterThan(60);
        CountNonBackgroundPixels(thumbTall48).Should().BeGreaterThan(60);
        CountDifferentPixels(thumbSmall48, thumbTall48).Should().BeGreaterThan(80);
    }

    [Fact]
    public void NumericScrubSession_SimulatesSliderRatioAndScrubModifiers()
    {
        var session = new NumericScrubSession
        {
            Minimum = 30.0,
            Maximum = 600.0,
            Step = 5.0,
            DecimalPlaces = 1,
            Value = 200.0,
        };

        session.SimulateSliderRatio(0.0).Should().Be(30.0);
        session.SimulateSliderRatio(1.0).Should().Be(600.0);
        session.SimulateSliderRatio(-0.5).Should().Be(30.0);
        session.SimulateSliderRatio(1.5).Should().Be(600.0);
        session.SimulateSliderRatio(0.5).Should().Be(315.0);

        // Shift = ajuste fino (0.1 × Step = 0.5 mm)
        session.Value = 200.0;
        session.SimulateScrubDelta(3, fine: true, fast: false).Should().Be(201.5);

        // Ctrl = ajuste rápido (5 × Step = 25 mm)
        session.Value = 200.0;
        session.SimulateScrubDelta(-2, fine: false, fast: true).Should().Be(150.0);

        // Normal (1 × Step) e clamp no limite máximo
        session.Value = 590.0;
        session.SimulateScrubDelta(4, fine: false, fast: false).Should().Be(600.0);

        // Arraste contínuo em pixels (4 px por passo)
        session.BeginDrag(startX: 100.0, initialValue: 150.0);
        session.IsDragging.Should().BeTrue();
        session.UpdateDragPixels(currentX: 112.0, fine: false, fast: false).Should().Be(165.0);
        session.EndDrag();
        session.IsDragging.Should().BeFalse();
    }

    [Fact]
    public void RibbonAndInteractiveInputs_DeclareFusion360GroupsAndScrubPrimitivesInXaml()
    {
        string repoRoot = ResolveRepoRoot();
        string appRoot = Path.Combine(repoRoot, "native-winui", "src", "CaixaMaker.App");

        string shellXaml = File.ReadAllText(Path.Combine(appRoot, "Views", "WorkspaceSplitShell.xaml"));
        string toolbarGroupXaml = File.ReadAllText(Path.Combine(appRoot, "Controls", "CadPrimitives", "CadToolbarGroup.xaml"));
        string toolbarButtonXaml = File.ReadAllText(Path.Combine(appRoot, "Controls", "CadPrimitives", "CadToolbarButton.xaml"));
        string numericInputXaml = File.ReadAllText(Path.Combine(appRoot, "Controls", "CadPrimitives", "CadNumericInput.xaml"));
        string propertyRowXaml = File.ReadAllText(Path.Combine(appRoot, "Controls", "CadPrimitives", "CadPropertyRow.xaml"));

        shellXaml.Should().Contain("Height=\"48\"");
        shellXaml.Should().Contain("ModelingRibbonGroups");
        shellXaml.Should().Contain("PersonalizationRibbonGroups");
        shellXaml.Should().Contain("ManufacturingRibbonGroups");

        string[] expectedGroups =
        [
            "ARQUIVO",
            "HISTÓRICO",
            "REFERÊNCIA DIM.",
            "MDF RÁPIDO",
            "INSPEÇÃO 3D",
            "CALIBRAÇÃO",
            "INSERIR CAM",
            "POSICIONAR NA FACE",
            "EDIÇÃO CAM",
            "ESCOPO DE SAÍDA",
            "CALIBRAÇÃO MDF",
            "EXPORTAÇÃO 1:1",
            "AUDITORIA",
            "LAYOUT &amp; VISTAS",
        ];

        foreach (string label in expectedGroups)
        {
            shellXaml.Should().Contain($"GroupLabel=\"{label}\"");
        }

        XDocument groupDoc = XDocument.Parse(toolbarGroupXaml);
        groupDoc.Root.Should().NotBeNull();
        toolbarGroupXaml.Should().Contain("Height=\"38\"");
        toolbarGroupXaml.Should().Contain("Height=\"28\"");
        toolbarGroupXaml.Should().Contain("Height=\"14\"");
        toolbarGroupXaml.Should().Contain("FontSize=\"9.5\"");
        toolbarGroupXaml.Should().Contain("CmTextMutedBrush");

        toolbarButtonXaml.Should().Contain("PrimarySurface");
        toolbarButtonXaml.Should().Contain("Height=\"26\"");

        numericInputXaml.Should().Contain("ProgressTrackGrid");
        numericInputXaml.Should().Contain("SliderThumb");
        numericInputXaml.Should().Contain("ScrubHandleBorder");

        propertyRowXaml.Should().Contain("LabelTouchArea");
        propertyRowXaml.Should().Contain("OnLabelPointerPressed");
    }
}
