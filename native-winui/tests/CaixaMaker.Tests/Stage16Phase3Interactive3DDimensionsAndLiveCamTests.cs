using System.Xml.Linq;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage16Phase3Interactive3DDimensionsAndLiveCamTests
{
    internal static string ResolveRepoRoot()
    {
        string? versionFile = AppVersion.ResolveVersionFile(AppContext.BaseDirectory);
        versionFile.Should().NotBeNull("o arquivo VERSION na raiz do repositório deve estar acessível");
        return Path.GetDirectoryName(versionFile!)!;
    }

    internal static MainWindowViewModel CreateIsolatedMainVm(AppPaths paths)
        => new(paths, new NullNativeFileDialogService(), BoxModelRegistry.Default, ResolvedAppTheme.Dark);

    internal static AppPaths CreateTempPaths(out string sandboxRoot)
    {
        sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_Stage16P3_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(sandboxRoot);

        AppPaths paths = AppPaths.Resolve(new AppPathsOptions
        {
            DesktopMode = false,
            ProjectsDir = Path.Combine(sandboxRoot, "projects"),
            OutputsDir = Path.Combine(sandboxRoot, "outputs"),
            ConfigDir = Path.Combine(sandboxRoot, "config"),
            LogsDir = Path.Combine(sandboxRoot, "logs"),
            TempDir = Path.Combine(sandboxRoot, "temp"),
        });
        paths.EnsureDirs();
        return paths;
    }

    internal static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Melhor esforço na limpeza de sandbox temporário
        }
    }

    internal static byte[] CreateHighContrastPngBytes(int width = 32, int height = 24)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var midGrayPaint = new SKPaint { Color = new SKColor(140, 140, 140), Style = SKPaintStyle.Fill };
            canvas.DrawRect(2, 2, width - 4, height - 4, midGrayPaint);
            using var darkPaint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            canvas.DrawRect(4, 4, width - 8, height - 8, darkPaint);
            using var innerWhite = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            canvas.DrawRect(10, 8, width - 20, height - 16, innerWhite);
        }

        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

public sealed class DimensionCallout3DHitTestingAndInlineEditTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    public DimensionCallout3DHitTestingAndInlineEditTests()
    {
        _paths = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateTempPaths(out _sandboxRoot);
    }

    public void Dispose()
        => Stage16Phase3Interactive3DDimensionsAndLiveCamTests.SafeDeleteDirectory(_sandboxRoot);

    [Fact]
    public void ProjectDimensionCalloutBounds_And_HitTestDimensionCallout_ShouldLocateAllThreeAxesAndRespectVisibility()
    {
        MainWindowViewModel vm = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(_paths);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        BoxScene3D scene = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            vm.Box.LidAngle,
            vm.Box.ExplodedView);

        scene.Dimensions.Select(d => d.Axis).Should().BeEquivalentTo(
            [BoxDimensionAxis.WidthX, BoxDimensionAxis.DepthZ, BoxDimensionAxis.HeightY]);

        var camera = new Camera3DController();
        camera.SyncFromViewModel(vm.Box);

        IReadOnlyList<DimensionCalloutHitResult> bounds = OffscreenViewportRenderer.ProjectDimensionCalloutBounds(
            scene,
            camera,
            viewportWidth: 960,
            viewportHeight: 680,
            showDimensions: true);

        bounds.Should().HaveCount(3);
        foreach (DimensionCalloutHitResult item in bounds)
        {
            item.BadgeBoundsScreen.Width.Should().BeGreaterThan(20f);
            item.BadgeBoundsScreen.Height.Should().BeGreaterThan(12f);
            item.BadgeBoundsScreen.Left.Should().BeGreaterThanOrEqualTo(10f);
            item.BadgeBoundsScreen.Right.Should().BeLessThanOrEqualTo(960f - 10f);
            item.BadgeBoundsScreen.Top.Should().BeGreaterThanOrEqualTo(42f);
            item.BadgeBoundsScreen.Bottom.Should().BeLessThanOrEqualTo(680f - 44f);

            DimensionCalloutHitResult? hit = OffscreenViewportRenderer.HitTestDimensionCallout(
                scene,
                camera,
                960,
                680,
                item.CenterScreen.X,
                item.CenterScreen.Y,
                showDimensions: true);

            hit.Should().NotBeNull();
            hit!.Value.Axis.Should().Be(item.Axis);
        }

        DimensionCalloutHitResult? miss = OffscreenViewportRenderer.HitTestDimensionCallout(
            scene,
            camera,
            960,
            680,
            screenX: 4f,
            screenY: 4f,
            showDimensions: true);
        miss.Should().BeNull();

        OffscreenViewportRenderer.ProjectDimensionCalloutBounds(scene, camera, 960, 680, showDimensions: false)
            .Should().BeEmpty();

        OffscreenViewportRenderer.HitTestDimensionCallout(
                scene,
                camera,
                960,
                680,
                bounds[0].CenterScreen.X,
                bounds[0].CenterScreen.Y,
                showDimensions: false)
            .Should().BeNull();
    }

    [Fact]
    public void RenderScene3DBitmap_WithHighlightedDimensionAxis_ShouldHighlightBadgePixels()
    {
        MainWindowViewModel vm = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(_paths);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        BoxScene3D scene = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            vm.Box.LidAngle,
            vm.Box.ExplodedView);

        var camera = new Camera3DController();
        camera.SyncFromViewModel(vm.Box);

        using SKBitmap normal = OffscreenViewportRenderer.RenderScene3DBitmap(
            scene,
            camera,
            vm.Personalization.Manager,
            new RenderScene3DOptions
            {
                WidthPx = 800,
                HeightPx = 560,
                DarkTheme = true,
                ShowDimensions = true,
                HighlightedDimensionAxis = null,
            });

        using SKBitmap highlighted = OffscreenViewportRenderer.RenderScene3DBitmap(
            scene,
            camera,
            vm.Personalization.Manager,
            new RenderScene3DOptions
            {
                WidthPx = 800,
                HeightPx = 560,
                DarkTheme = true,
                ShowDimensions = true,
                HighlightedDimensionAxis = BoxDimensionAxis.WidthX,
            });

        ReadOnlySpan<byte> pxA = normal.GetPixelSpan();
        ReadOnlySpan<byte> pxB = highlighted.GetPixelSpan();
        int changedPixels = 0;
        for (int i = 0; i < pxA.Length; i += 4)
        {
            if (pxA[i] != pxB[i] || pxA[i + 1] != pxB[i + 1] || pxA[i + 2] != pxB[i + 2])
            {
                changedPixels++;
            }
        }

        changedPixels.Should().BeGreaterThan(25, "realçar a cota WidthX deve destacar pixels do badge 3D");
    }

    [Fact]
    public void TryApplyDimensionAxisExpression_ShouldEvaluateMathRespectingDimensionModeAndRejectInvalidInputs()
    {
        MainWindowViewModel vm = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(_paths);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.Box.BoxThickness = 3.0;
        vm.Box.SetDimensionReferenceMode(DimensionReferenceMode.External);

        vm.Box.GetDimensionAxisEditHeader(BoxDimensionAxis.WidthX)
            .Should().Be("LARGURA (X) • EXTERNA (MM)");

        vm.Box.TryApplyDimensionAxisExpression(BoxDimensionAxis.WidthX, "200 + 25").Should().BeTrue();
        vm.Box.BoxWidth.Should().Be(225.0);
        vm.Box.InnerWidthMm.Should().Be(219.0);

        vm.Box.TryApplyDimensionAxisExpression(BoxDimensionAxis.DepthZ, "300 / 2 + 10").Should().BeTrue();
        vm.Box.BoxDepth.Should().Be(160.0);

        vm.Box.TryApplyDimensionAxisExpression(BoxDimensionAxis.HeightY, "80 * 1.5").Should().BeTrue();
        vm.Box.BoxHeight.Should().Be(120.0);

        // Alterna para Interna Útil: valor pré-preenchido e aplicação devem descontar/somar espessura
        vm.Box.SetDimensionReferenceMode(DimensionReferenceMode.InternalUsable);
        vm.Box.GetDimensionAxisEditHeader(BoxDimensionAxis.WidthX)
            .Should().Be("LARGURA (X) • INTERNA ÚTIL (MM)");
        vm.Box.FormatDimensionAxisEditValue(BoxDimensionAxis.WidthX).Should().Be("219");

        vm.Box.TryApplyDimensionAxisExpression(BoxDimensionAxis.WidthX, "190 + 10").Should().BeTrue();
        vm.Box.InputWidth.Should().Be(200.0);
        vm.Box.BoxWidth.Should().Be(206.0);
        vm.Box.InnerWidthMm.Should().Be(200.0);

        vm.Box.TryApplyDimensionAxisExpression(BoxDimensionAxis.HeightY, "90 + 5").Should().BeTrue();
        vm.Box.InputHeight.Should().Be(95.0);
        vm.Box.BoxHeight.Should().Be(98.0);

        // Entradas inválidas não devem alterar as dimensões atuais
        double wBefore = vm.Box.BoxWidth;
        double dBefore = vm.Box.BoxDepth;
        double hBefore = vm.Box.BoxHeight;

        vm.Box.TryApplyDimensionAxisExpression(BoxDimensionAxis.WidthX, "").Should().BeFalse();
        vm.Box.TryApplyDimensionAxisExpression(BoxDimensionAxis.WidthX, "abc").Should().BeFalse();
        vm.Box.TryApplyDimensionAxisExpression(BoxDimensionAxis.DepthZ, "10 / 0").Should().BeFalse();
        vm.Box.TryApplyDimensionAxisExpression(BoxDimensionAxis.HeightY, "10 - 20").Should().BeFalse();

        vm.Box.BoxWidth.Should().Be(wBefore);
        vm.Box.BoxDepth.Should().Be(dBefore);
        vm.Box.BoxHeight.Should().Be(hBefore);
    }

    [Fact]
    public void GetNextDimensionAxis_ShouldCycleForwardAndReverse()
    {
        BoxWorkspaceViewModel.GetNextDimensionAxis(BoxDimensionAxis.WidthX).Should().Be(BoxDimensionAxis.DepthZ);
        BoxWorkspaceViewModel.GetNextDimensionAxis(BoxDimensionAxis.DepthZ).Should().Be(BoxDimensionAxis.HeightY);
        BoxWorkspaceViewModel.GetNextDimensionAxis(BoxDimensionAxis.HeightY).Should().Be(BoxDimensionAxis.WidthX);

        BoxWorkspaceViewModel.GetNextDimensionAxis(BoxDimensionAxis.WidthX, reverse: true).Should().Be(BoxDimensionAxis.HeightY);
        BoxWorkspaceViewModel.GetNextDimensionAxis(BoxDimensionAxis.HeightY, reverse: true).Should().Be(BoxDimensionAxis.DepthZ);
        BoxWorkspaceViewModel.GetNextDimensionAxis(BoxDimensionAxis.DepthZ, reverse: true).Should().Be(BoxDimensionAxis.WidthX);
    }

    [Fact]
    public void BoxViewer3DControlXaml_ShouldExposeInlineDimensionOverlayControls()
    {
        string xamlPath = Path.Combine(
            Stage16Phase3Interactive3DDimensionsAndLiveCamTests.ResolveRepoRoot(),
            "native-winui", "src", "CaixaMaker.App", "Controls", "BoxViewer3DControl.xaml");

        XDocument doc = XDocument.Load(xamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> names = doc.Descendants()
            .Select(e => e.Attribute(x + "Name")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        names.Should().Contain("InlineDimensionEditorOverlay");
        names.Should().Contain("InlineDimHeaderText");
        names.Should().Contain("InlineDimTextBox");
        names.Should().Contain("InlineDimErrorText");
    }
}

public sealed class LiveCamReactivityAndHistoryCoalescingTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    public LiveCamReactivityAndHistoryCoalescingTests()
    {
        _paths = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateTempPaths(out _sandboxRoot);
    }

    public void Dispose()
        => Stage16Phase3Interactive3DDimensionsAndLiveCamTests.SafeDeleteDirectory(_sandboxRoot);

    [Fact]
    public void LiveInspectorUpdates_TextProperties_ShouldRevectorizeImmediatelyWithoutManualApply()
    {
        MainWindowViewModel vm = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(_paths);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);

        TextPersonalizationElement el = vm.Personalization.AddTextElement("TESTE");
        vm.Personalization.SelectElement(el.Id);

        int visualNotifications = 0;
        vm.Personalization.VisualStateChanged += (_, _) => visualNotifications++;

        string initialPathsSignature = string.Join("|", el.VectorPaths.Select(p => p.D));
        double initialWidth = el.WidthMm;

        // 1. Alteração live do conteúdo textual
        vm.Personalization.SelectedTextContent = "CAIXA MAKER CNC";
        el.Content.Should().Be("CAIXA MAKER CNC");
        el.VectorPaths.Should().NotBeEmpty();
        string updatedPathsSignature = string.Join("|", el.VectorPaths.Select(p => p.D));
        updatedPathsSignature.Should().NotBe(initialPathsSignature);
        el.WidthMm.Should().BeGreaterThan(initialWidth);
        visualNotifications.Should().BeGreaterThan(0);

        // 2. Alteração live de tamanho de fonte
        double widthBeforeFontSize = el.WidthMm;
        vm.Personalization.SelectedTextFontSizeMm = 28.0;
        el.FontSizeMm.Should().Be(28.0);
        el.WidthMm.Should().BeGreaterThan(widthBeforeFontSize);

        // 3. Alteração live de Negrito + Itálico
        string pathsBeforeBold = string.Join("|", el.VectorPaths.Select(p => p.D));
        vm.Personalization.SelectedTextBold = true;
        vm.Personalization.SelectedTextItalic = true;
        el.Bold.Should().BeTrue();
        el.Italic.Should().BeTrue();
        string pathsAfterBold = string.Join("|", el.VectorPaths.Select(p => p.D));
        pathsAfterBold.Should().NotBe(pathsBeforeBold);

        // 4. Alteração live de TextRenderMode para Hatch
        int countOutlineOnly = el.VectorPaths.Count;
        vm.Personalization.SelectedTextRenderMode = TextRenderMode.Hatch;
        el.RenderMode.Should().Be(TextRenderMode.Hatch);
        el.VectorPaths.Count.Should().BeGreaterThan(countOutlineOnly);
    }

    [Fact]
    public void LiveInspectorUpdates_RasterControls_ShouldReprocessImmediatelyWithoutManualApply()
    {
        MainWindowViewModel vm = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(_paths);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);

        byte[] png = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateHighContrastPngBytes();
        RasterImagePersonalizationElement raster = vm.Personalization.ImportRasterFromBytes(png, "logo.png", vectorize: false);
        vm.Personalization.SelectElement(raster.Id);

        string initialProcessed = raster.ProcessedImageB64;

        vm.Personalization.SelectedRasterInvert = true;
        raster.ProcessingOptions.Invert.Should().BeTrue();
        raster.ProcessedImageB64.Should().NotBe(initialProcessed);

        string afterInvert = raster.ProcessedImageB64;
        vm.Personalization.SelectedRasterThreshold = 90;
        raster.ProcessingOptions.Threshold.Should().Be(90);

        vm.Personalization.SelectedRasterMonochrome = true;
        raster.ProcessingOptions.Monochrome.Should().BeTrue();
        raster.ProcessedImageB64.Should().NotBe(afterInvert);

        raster.VectorPaths.Should().BeEmpty();
        vm.Personalization.SelectedRasterVectorize = true;
        raster.Vectorize.Should().BeTrue();
        raster.VectorPaths.Should().NotBeEmpty("habilitar vetorização CNC deve gerar trajetórias SVG imediatamente");
    }

    [Fact]
    public void RapidTextAndSliderEdits_ShouldCoalesceInPersonalizationHistory_AndUndoAtomically()
    {
        MainWindowViewModel vm = Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(_paths);
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);

        TextPersonalizationElement el = vm.Personalization.AddTextElement("ORIG");
        vm.Personalization.SelectElement(el.Id);

        int undoBeforeTyping = vm.Personalization.UndoCount;

        // Digitação rápida letra por letra deve gerar apenas 1 passo consolidado de Undo
        vm.Personalization.SelectedTextContent = "C";
        vm.Personalization.SelectedTextContent = "CA";
        vm.Personalization.SelectedTextContent = "CAI";
        vm.Personalization.SelectedTextContent = "CAIX";
        vm.Personalization.SelectedTextContent = "CAIXA";

        el.Content.Should().Be("CAIXA");
        vm.Personalization.UndoCount.Should().Be(undoBeforeTyping + 1);

        // Undo único restaura "ORIG" integralmente (inclusive seus vetores)
        vm.Personalization.Undo();
        TextPersonalizationElement restoredOrig = (TextPersonalizationElement)vm.Personalization.SelectedElement!;
        restoredOrig.Content.Should().Be("ORIG");
        restoredOrig.VectorPaths.Should().NotBeEmpty();
        vm.Personalization.UndoCount.Should().Be(undoBeforeTyping);

        // Redo único restaura "CAIXA"
        vm.Personalization.Redo();
        ((TextPersonalizationElement)vm.Personalization.SelectedElement!).Content.Should().Be("CAIXA");

        // Encerrando a sessão de edição ou trocando para outro campo cria novo checkpoint
        vm.Personalization.FlushHistoryCoalescing();
        int undoBeforeNextSession = vm.Personalization.UndoCount;
        vm.Personalization.SelectedTextContent = "CAIXA 2";
        vm.Personalization.UndoCount.Should().Be(undoBeforeNextSession + 1);

        // Scrubbing contínuo de fonte também coalescente em 1 passo (elemento criado inicia em 14 mm)
        double initialFontSize = ((TextPersonalizationElement)vm.Personalization.SelectedElement!).FontSizeMm;
        initialFontSize.Should().Be(14.0);
        int undoBeforeFontScrub = vm.Personalization.UndoCount;
        vm.Personalization.SelectedTextFontSizeMm = 16.0;
        vm.Personalization.SelectedTextFontSizeMm = 18.0;
        vm.Personalization.SelectedTextFontSizeMm = 20.0;
        vm.Personalization.SelectedTextFontSizeMm = 24.0;
        vm.Personalization.UndoCount.Should().Be(undoBeforeFontScrub + 1);

        vm.Personalization.Undo();
        ((TextPersonalizationElement)vm.Personalization.SelectedElement!).FontSizeMm.Should().Be(initialFontSize);
    }

    [Fact]
    public void PersonalizationAreaRightPanelXaml_ShouldUseAutoCamStripInsteadOfManualApplyButton()
    {
        string xamlPath = Path.Combine(
            Stage16Phase3Interactive3DDimensionsAndLiveCamTests.ResolveRepoRoot(),
            "native-winui", "src", "CaixaMaker.App", "Views", "Areas", "PersonalizationAreaRightPanel.xaml");

        XDocument doc = XDocument.Load(xamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> names = doc.Descendants()
            .Select(e => e.Attribute(x + "Name")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        names.Should().NotContain("ApplyReprocessBtn");
        names.Should().Contain("AutoCamStatusStrip");
        names.Should().Contain("ForceReprocessBtn");
    }
}
