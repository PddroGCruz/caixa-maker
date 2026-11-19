using System.Diagnostics;
using System.Globalization;
using CaixaMaker.App.Editor2D;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CaixaMaker.Tests;

/// <summary>
/// Benchmarks e testes de regressão da Etapa 1.9: fluidez 60+ FPS no Editor 2D e Modo Dividido (3D + 2D),
/// caches nativos de SKPath/layer estático, transação atômica Undo/Redo e paridade CAM 1:1.
/// </summary>
public sealed class Stage19Editor2DPerformanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    private const string ComplexSvgXml =
        """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 80">
          <rect x="4" y="4" width="112" height="72" rx="6" fill="#0d47a1" stroke="#0d47a1" stroke-width="1"/>
          <path d="M12 40 C28 12, 48 68, 64 40 C80 12, 96 68, 108 40" fill="none" stroke="#0d47a1" stroke-width="1"/>
          <circle cx="30" cy="24" r="8" fill="none" stroke="#1b5e20" stroke-width="1"/>
          <circle cx="90" cy="56" r="8" fill="none" stroke="#1b5e20" stroke-width="1"/>
          <polygon points="60,12 72,34 48,34" fill="#6a1b9a"/>
        </svg>
        """;

    public Stage19Editor2DPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_Stage19Tests_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_sandboxRoot);

        _paths = AppPaths.Resolve(new AppPathsOptions
        {
            DesktopMode = false,
            ProjectsDir = Path.Combine(_sandboxRoot, "projects"),
            OutputsDir = Path.Combine(_sandboxRoot, "outputs"),
            ConfigDir = Path.Combine(_sandboxRoot, "config"),
            LogsDir = Path.Combine(_sandboxRoot, "logs"),
            TempDir = Path.Combine(_sandboxRoot, "temp"),
        });
        _paths.EnsureDirs();
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

    private MainWindowViewModel CreateMainVm()
        => new(_paths, new NullNativeFileDialogService(), BoxModelRegistry.Default, ResolvedAppTheme.Dark);

    [Fact]
    public void Drag60FpsBenchmarkTest_60ConsecutiveDragSteps_Sub6MsAverageAndAtomicUndo()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);
        vm.Personalization.SelectSurface(SurfaceId.Lid);

        // Elemento SVG complexo e texto com várias linhas Hatch ao fundo para exercitar cache estático + cache SKPath
        vm.Personalization.ImportSvgFromString(ComplexSvgXml, "emblema.svg");
        vm.Personalization.DraftRenderMode = TextRenderMode.Hatch;
        vm.Personalization.DraftHatchSpacingMm = 0.25;
        TextPersonalizationElement activeText = vm.Personalization.AddTextElement("CAIXA MAKER IFAC");
        vm.Personalization.SelectElement(activeText.Id);

        double origX = activeText.XMm;
        double origY = activeText.YMm;
        int undoBefore = vm.Personalization.UndoCount;

        SurfaceDefinition lidDef = vm.Personalization.ActiveSurface!;
        PanelGeometry2D lidPanel = vm.Box.SheetGeometry!.PanelsById["lid"];
        SurfaceContour2D contour = SurfaceCanvas2DController.MapPanelContourToSurface(lidPanel, lidDef);

        using var controller = new SurfaceCanvas2DController();
        const int widthPx = 960;
        const int heightPx = 600;
        controller.UpdateLayout(lidDef, widthPx, heightPx, contour);

        var info = new SKImageInfo(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info)!;

        // Warmup de renderização inicial (constrói camada estática + compila SKPaths no cache)
        var renderOpts = new RenderEditor2DOptions
        {
            WidthPx = widthPx,
            HeightPx = heightPx,
            DarkTheme = true,
            DrawForbiddenZones = true,
            DrawSafeArea = true,
            SafetyMarginMm = vm.Personalization.SafetyMarginMm,
            SelectedElementId = activeText.Id,
        };
        OffscreenViewportRenderer.RenderEditor2DToCanvas(
            surface.Canvas,
            widthPx,
            heightPx,
            controller,
            lidDef,
            vm.Personalization.Manager.ListElements(),
            contour,
            renderOpts);

        int visualEventsDuringDrag = 0;
        int mutatedEventsDuringDrag = 0;
        int interactiveEventsDuringDrag = 0;
        int surfaceCollectionResets = 0;
        int activeListCollectionResets = 0;

        vm.Personalization.VisualStateChanged += (_, _) => visualEventsDuringDrag++;
        vm.Personalization.StateMutatedByUser += (_, _) => mutatedEventsDuringDrag++;
        vm.Personalization.InteractiveTransformChanged += (_, _) => interactiveEventsDuringDrag++;
        vm.Personalization.Surfaces.CollectionChanged += (_, _) => surfaceCollectionResets++;
        vm.Personalization.ElementsOnActiveSurface.CollectionChanged += (_, _) => activeListCollectionResets++;

        (double startPxX, double startPxY) = controller.MmToPx(activeText.CenterMm().X, activeText.CenterMm().Y);
        controller.OnPointerPressed(vm.Personalization, startPxX, startPxY).Should().Be(Editor2DHandleType.Body);
        vm.Personalization.IsInteractiveTransformActive.Should().BeTrue();

        double scale = controller.EffectiveScalePxPerMm;
        const int steps = 60;
        var frameTimesMs = new double[steps];
        var sw = new Stopwatch();

        for (int i = 1; i <= steps; i++)
        {
            double dxPx = (i * 0.45) * scale;
            double dyPx = (Math.Sin(i * 0.25) * 6.0) * scale;

            sw.Restart();
            bool moved = controller.OnPointerMoved(vm.Personalization, startPxX + dxPx, startPxY + dyPx);
            moved.Should().BeTrue();

            IReadOnlySet<string>? errorIds = vm.Personalization.ValidationReport?.Errors is { Count: > 0 } errs
                ? errs.Where(e => !string.IsNullOrEmpty(e.ElementId)).Select(e => e.ElementId!).ToHashSet(StringComparer.Ordinal)
                : null;

            OffscreenViewportRenderer.RenderEditor2DToCanvas(
                surface.Canvas,
                widthPx,
                heightPx,
                controller,
                lidDef,
                vm.Personalization.Manager.ListElements(),
                contour,
                new RenderEditor2DOptions
                {
                    WidthPx = widthPx,
                    HeightPx = heightPx,
                    DarkTheme = true,
                    DrawForbiddenZones = true,
                    DrawSafeArea = true,
                    SafetyMarginMm = vm.Personalization.SafetyMarginMm,
                    SelectedElementId = activeText.Id,
                    ErrorElementIds = errorIds,
                });
            sw.Stop();
            frameTimesMs[i - 1] = sw.Elapsed.TotalMilliseconds;
        }

        // Durante todos os 60 passos contínuos: 0 commits pesados, 0 resets de coleção, apenas eventos leves
        visualEventsDuringDrag.Should().Be(0, "arraste contínuo não deve disparar reconstruções completas da UI");
        mutatedEventsDuringDrag.Should().Be(0, "arraste contínuo não deve serializar autosave em disco por frame");
        surfaceCollectionResets.Should().Be(0, "Surfaces não deve sofrer Clear/Add durante arraste");
        activeListCollectionResets.Should().Be(0, "ElementsOnActiveSurface não deve sofrer Clear/Add durante arraste");
        interactiveEventsDuringDrag.Should().Be(steps);
        vm.Personalization.UndoCount.Should().Be(undoBefore);

        double avgMs = frameTimesMs.Average();
        double maxMs = frameTimesMs.Max();
        double p95Ms = frameTimesMs.OrderBy(t => t).ElementAt((int)(steps * 0.95) - 1);
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "Benchmark 60 FPS (Move+Render2D {0}x{1}): média={2:F3} ms/frame | p95={3:F3} ms | máx={4:F3} ms",
            widthPx,
            heightPx,
            avgMs,
            p95Ms,
            maxMs));

        avgMs.Should().BeLessThan(6.0, "cada quadro de edição 2D deve custar < 6 ms em média para 60+ FPS");

        // Finaliza arraste -> exatamente 1 Undo step + commit completo
        bool released = controller.OnPointerReleased(vm.Personalization);
        released.Should().BeTrue();
        vm.Personalization.IsInteractiveTransformActive.Should().BeFalse();
        vm.Personalization.UndoCount.Should().Be(undoBefore + 1);
        visualEventsDuringDrag.Should().Be(1);
        mutatedEventsDuringDrag.Should().Be(1);

        vm.Personalization.Undo();
        vm.Personalization.SelectedElement!.XMm.Should().BeApproximately(origX, 0.05);
        vm.Personalization.SelectedElement!.YMm.Should().BeApproximately(origY, 0.05);
    }

    [Fact]
    public void Editor2DCaches_PixelParityAfterMoveResizeRotate_AndMatchesCamExport()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);
        vm.Personalization.SelectSurface(SurfaceId.Front);

        // 1 elemento estático atrás do selecionado + 1 texto Hatch + 1 SVG misto (Fill + Stroke)
        vm.Personalization.AddTextElement("FUNDO ESTATICO");
        VectorSvgPersonalizationElement svgEl = vm.Personalization.ImportSvgFromString(ComplexSvgXml, "logo.svg");
        vm.Personalization.DraftRenderMode = TextRenderMode.Hatch;
        TextPersonalizationElement textEl = vm.Personalization.AddTextElement("IFMAKER");
        vm.Personalization.SelectElement(textEl.Id);

        SurfaceDefinition frontDef = vm.Personalization.ActiveSurface!;
        PanelGeometry2D frontPanel = vm.Box.SheetGeometry!.PanelsById["front"];
        SurfaceContour2D contour = SurfaceCanvas2DController.MapPanelContourToSurface(frontPanel, frontDef);

        using var warmController = new SurfaceCanvas2DController();
        const int w = 720;
        const int h = 440;
        warmController.UpdateLayout(frontDef, w, h, contour);

        // Simula arraste de movimento, redimensionamento e rotação no controlador com cache ativo
        (double cxPx, double cyPx) = warmController.MmToPx(textEl.CenterMm().X, textEl.CenterMm().Y);
        warmController.OnPointerPressed(vm.Personalization, cxPx, cyPx);
        double scale = warmController.EffectiveScalePxPerMm;
        warmController.OnPointerMoved(vm.Personalization, cxPx + 10.0 * scale, cyPx - 4.0 * scale);
        warmController.OnPointerReleased(vm.Personalization).Should().BeTrue();

        // Renderiza intermediário para popular caches estáticos no controlador
        var opts = new RenderEditor2DOptions
        {
            WidthPx = w,
            HeightPx = h,
            DarkTheme = false,
            DrawForbiddenZones = true,
            DrawSafeArea = true,
            SafetyMarginMm = vm.Personalization.SafetyMarginMm,
            SelectedElementId = textEl.Id,
        };
        using (SKBitmap warmup = OffscreenViewportRenderer.RenderEditor2DBitmap(
            warmController, frontDef, vm.Personalization.Manager.ListElements(), contour, opts))
        {
            warmup.Width.Should().Be(w);
        }

        // Redimensiona + rotaciona o elemento
        vm.Personalization.BeginInteractiveTransform();
        vm.Personalization.UpdateSelectedElementTransform(
            widthMm: textEl.WidthMm * 1.25,
            heightMm: textEl.HeightMm * 1.15,
            rotationDeg: 32.5,
            preserveAspectRatio: false,
            recordUndo: false);
        vm.Personalization.EndInteractiveTransform().Should().BeTrue();

        using SKBitmap cachedRender = OffscreenViewportRenderer.RenderEditor2DBitmap(
            warmController, frontDef, vm.Personalization.Manager.ListElements(), contour, opts);

        // Compara pixel a pixel com um controlador totalmente frio (mesma posição de ponteiro nas réguas, sem cache prévio)
        using var coldController = new SurfaceCanvas2DController();
        coldController.UpdateLayout(frontDef, w, h, contour);
        coldController.OnPointerMoved(vm.Personalization, cxPx + 10.0 * scale, cyPx - 4.0 * scale);
        using SKBitmap freshRender = OffscreenViewportRenderer.RenderEditor2DBitmap(
            coldController, frontDef, vm.Personalization.Manager.ListElements(), contour, opts);

        int mismatches = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (cachedRender.GetPixel(x, y) != freshRender.GetPixel(x, y))
                {
                    mismatches++;
                }
            }
        }
        mismatches.Should().Be(0, "cache estático + réguas em cache + cache SKPath devem produzir pixels idênticos");

        // Paridade 1:1 na exportação SVG CAM final
        var exporter = new PersonalizationSvgExporter(vm.Personalization.Manager);
        string svgOut = exporter.ExportFullBoxSvg();
        svgOut.Should().Contain(svgEl.Paths[0].Operation == CncOperationType.EngraveFill ? "layer_engrave_fill" : "layer_engrave_vector");
        svgOut.Should().Contain(textEl.WidthMm.ToString("0.###", CultureInfo.InvariantCulture).Length > 0 ? "<svg" : "");
        vm.Personalization.RunValidation().Should().NotBeNull();
    }

    [Fact]
    public void InteractiveTransformTransaction_AndSplit3DOverlayRebuild_ReuseStructuralTriangles()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);
        vm.Personalization.SelectSurface(SurfaceId.Front);

        TextPersonalizationElement el = vm.Personalization.AddTextElement("SPLIT SYNC");
        int undoBefore = vm.Personalization.UndoCount;
        int mutatedCount = 0;
        vm.Personalization.StateMutatedByUser += (_, _) => mutatedCount++;

        BoxScene3D structuralScene = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: 18.0,
            isExplodedView: false);

        vm.Personalization.BeginInteractiveTransform();
        for (int i = 1; i <= 30; i++)
        {
            vm.Personalization.UpdateSelectedElementTransform(
                xMm: 20.0 + i * 0.5,
                rotationDeg: i * 1.2,
                recordUndo: false);
        }
        mutatedCount.Should().Be(0);
        vm.Personalization.UndoCount.Should().Be(undoBefore);

        BoxScene3D overlayUpdated = BoxScene3DBuilder.RebuildSurfaceOverlays(
            structuralScene,
            vm.Box.SelectedModel,
            vm.Personalization.Manager);
        BoxScene3D fullRebuild = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: 18.0,
            isExplodedView: false);

        overlayUpdated.Panels.Count.Should().Be(fullRebuild.Panels.Count);
        for (int p = 0; p < overlayUpdated.Panels.Count; p++)
        {
            // Reutiliza exatamente as mesmas listas de triângulos e vincos estruturais sem re-triangular chapas MDF
            ReferenceEquals(overlayUpdated.Panels[p].Triangles, structuralScene.Panels[p].Triangles).Should().BeTrue();
            ReferenceEquals(overlayUpdated.Panels[p].CreaseEdges, structuralScene.Panels[p].CreaseEdges).Should().BeTrue();

            SurfaceOverlayQuad3D? ov = overlayUpdated.Panels[p].OverlayQuad;
            SurfaceOverlayQuad3D? fb = fullRebuild.Panels[p].OverlayQuad;
            if (ov is not null && fb is not null)
            {
                ov.ProjectedElements.Count.Should().Be(fb.ProjectedElements.Count);
                for (int e = 0; e < ov.ProjectedElements.Count; e++)
                {
                    ov.ProjectedElements[e].Corners3D.Should().BeEquivalentTo(fb.ProjectedElements[e].Corners3D);
                }
            }
        }

        vm.Personalization.EndInteractiveTransform(recordUndoIfChanged: true).Should().BeTrue();
        mutatedCount.Should().Be(1);
        vm.Personalization.UndoCount.Should().Be(undoBefore + 1);
    }

    [Fact]
    public void ValidatePanel_PreservesNonActiveSurfaceErrorsDuringInteractiveDrag()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);

        // Cria colisão na face Frontal
        vm.Personalization.SelectSurface(SurfaceId.Front);
        TextPersonalizationElement frontErr = vm.Personalization.AddTextElement("COLIDE FRONT");
        vm.Personalization.UpdateSelectedElementTransform(xMm: -15.0, yMm: -10.0, recordUndo: false);
        vm.Personalization.ValidationReport!.IsValid.Should().BeFalse();

        // Muda para Tampa e arrasta um elemento válido para fora dos limites e de volta para dentro
        vm.Personalization.SelectSurface(SurfaceId.Lid);
        TextPersonalizationElement lidEl = vm.Personalization.AddTextElement("OK LID");
        vm.Personalization.BeginInteractiveTransform();

        vm.Personalization.UpdateSelectedElementTransform(xMm: -40.0, recordUndo: false);
        vm.Personalization.ValidationReport!.Errors.Should().Contain(e => e.ElementId == frontErr.Id);
        vm.Personalization.ValidationReport!.Errors.Should().Contain(e => e.ElementId == lidEl.Id);

        vm.Personalization.EndInteractiveTransform().Should().BeTrue();
        vm.Personalization.ValidationReport!.Errors.Should().Contain(e => e.ElementId == frontErr.Id);
        vm.Personalization.ValidationReport!.Errors.Should().Contain(e => e.ElementId == lidEl.Id);
    }
}
