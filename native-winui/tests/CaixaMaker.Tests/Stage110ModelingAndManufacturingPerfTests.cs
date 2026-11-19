using System.Diagnostics;
using System.Runtime.InteropServices;
using CaixaMaker.App.Editor2D;
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
using Xunit.Abstractions;

namespace CaixaMaker.Tests;

public sealed class Stage110ModelingAndManufacturingPerfTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempRoot;
    private readonly AppPaths _paths;

    public Stage110ModelingAndManufacturingPerfTests(ITestOutputHelper output)
    {
        _output = output;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"cm_stage110_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);

        _paths = new AppPaths(new AppPathsOptions
        {
            DesktopMode = false,
            ProjectsDir = Path.Combine(_tempRoot, "projects"),
            OutputsDir = Path.Combine(_tempRoot, "outputs"),
            ConfigDir = Path.Combine(_tempRoot, "config"),
            LogsDir = Path.Combine(_tempRoot, "logs"),
            TempDir = Path.Combine(_tempRoot, "temp"),
        });
        _paths.EnsureDirs();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignora falha transitória de limpeza em diretório temporário de teste.
        }
    }

    private MainWindowViewModel CreateMainVm()
    {
        return new MainWindowViewModel(
            _paths,
            new NullNativeFileDialogService(),
            BoxModelRegistry.Default,
            ResolvedAppTheme.Dark);
    }

    private static string BuildComplexSvgSample()
    {
        return """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 80">
              <rect x="4" y="4" width="112" height="72" rx="10" fill="none" stroke="#000000" stroke-width="1.5"/>
              <path d="M 14 40 C 26 12, 54 12, 60 40 C 66 68, 94 68, 106 40 Z" fill="#000000"/>
              <circle cx="34" cy="40" r="10" fill="#000000"/>
              <circle cx="86" cy="40" r="10" fill="#000000"/>
              <path d="M 20 18 L 100 18 M 20 62 L 100 62" stroke="#000000" stroke-width="1.2"/>
            </svg>
            """;
    }

    private static (double AvgMs, double P95Ms) MeasurePercentiles(double[] samples)
    {
        Array.Sort(samples);
        double avg = samples.Average();
        int p95Idx = Math.Clamp((int)Math.Ceiling(samples.Length * 0.95) - 1, 0, samples.Length - 1);
        return (avg, samples[p95Idx]);
    }

    private static RenderManufacturingSheet2DOptions CreateSheetOptions(
        int widthPx,
        int heightPx,
        double userZoom = 1.0,
        double panPxX = 0.0,
        double panPxY = 0.0,
        double cursorMmX = 50.0,
        double cursorMmY = 40.0)
    {
        return new RenderManufacturingSheet2DOptions
        {
            WidthPx = widthPx,
            HeightPx = heightPx,
            DarkTheme = true,
            SelectedPartId = "front",
            LaserBedWidthMm = 600,
            LaserBedHeightMm = 400,
            LaserBedLabel = "600x400 mm",
            ShowLaserBedOverlay = true,
            ShowSheetDimensions = true,
            UserZoom = userZoom,
            PanPxX = panPxX,
            PanPxY = panPxY,
            CursorMmX = cursorMmX,
            CursorMmY = cursorMmY,
        };
    }

    [Fact]
    public void ZoomIn3DPerformanceBenchmarkTest()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);

        vm.Personalization.SelectSurface(SurfaceId.Front);
        vm.Personalization.AddTextElement("IFAC LASER 3D");
        vm.Personalization.ImportSvgFromString(
            BuildComplexSvgSample(),
            "badge-front.svg",
            CncOperationType.EngraveFill);

        vm.Personalization.SelectSurface(SurfaceId.Lid);
        vm.Personalization.AddTextElement("CAIXAMAKER PRO");
        vm.Personalization.ImportSvgFromString(
            BuildComplexSvgSample(),
            "crest-lid.svg",
            CncOperationType.EngraveVector);

        BoxScene3D scene = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: 18.0,
            isExplodedView: false);

        var camera = new Camera3DController();
        camera.ApplyPreset(CameraPreset.Isometric);
        Camera3DController.MaxZoom.Should().Be(6.0);

        // Valida que ZoomBy respeita o teto expandido 6.0x.
        camera.ZoomBy(4.0 / camera.Zoom);
        camera.Zoom.Should().BeApproximately(4.0, 0.001);
        camera.ZoomBy(2.0);
        camera.Zoom.Should().Be(Camera3DController.MaxZoom);

        const int widthPx = 960;
        const int heightPx = 600;
        var info = new SKImageInfo(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var canvas = new SKCanvas(bitmap);

        var options = new RenderScene3DOptions
        {
            WidthPx = widthPx,
            HeightPx = heightPx,
            DarkTheme = true,
            ShowDimensions = false,
            ShowSurfaceGuides = false,
            ActiveSurfaceId = SurfaceId.Lid,
            SupersampleScale = 1.0f,
        };

        // Warmup JIT + buffers por thread
        for (int w = 0; w < 3; w++)
        {
            camera.ApplyPreset(CameraPreset.Isometric);
            camera.Orbit(2.0, 1.0);
            OffscreenViewportRenderer.RenderScene3DToCanvas(
                canvas, widthPx, heightPx, scene, camera, vm.Personalization.Manager, options);
        }

        double[] zoomLevels = [1.0, 3.5, 5.0];
        const int framesPerLevel = 30;

        foreach (double zoom in zoomLevels)
        {
            camera.ApplyPreset(CameraPreset.Isometric);
            camera.ZoomBy(zoom / camera.Zoom);
            camera.Zoom.Should().BeApproximately(zoom, 0.001);

            // Warmup no nível atual
            OffscreenViewportRenderer.RenderScene3DToCanvas(
                canvas, widthPx, heightPx, scene, camera, vm.Personalization.Manager, options);

            var samples = new double[framesPerLevel];
            var sw = new Stopwatch();
            for (int f = 0; f < framesPerLevel; f++)
            {
                camera.Orbit(4.5, f % 2 == 0 ? 1.5 : -1.2);
                camera.Pan(f % 3 - 1, -0.5, 200.0);

                sw.Restart();
                OffscreenViewportRenderer.RenderScene3DToCanvas(
                    canvas, widthPx, heightPx, scene, camera, vm.Personalization.Manager, options);
                sw.Stop();
                samples[f] = sw.Elapsed.TotalMilliseconds;
            }

            (double avgMs, double p95Ms) = MeasurePercentiles(samples);
            _output.WriteLine($"[Stage1.10 3D Bench] Zoom={zoom:0.0}x ({widthPx}x{heightPx} 1x): avg={avgMs:0.00} ms/frame ({1000.0 / Math.Max(0.01, avgMs):0.0} FPS), p95={p95Ms:0.00} ms");

            // Em 5.0x os cantos do painel saem da tela; valida que a homografia analítica e o Z-Buffer continuam
            // preenchendo o modelo e suas gravações dentro do viewport.
            if (zoom >= 4.9)
            {
                ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan());
                uint bgPixel = pixels[0];
                int nonBgCount = 0;
                int darkEngraveCount = 0;
                foreach (uint px in pixels)
                {
                    if (px != bgPixel)
                    {
                        nonBgCount++;
                        byte r = (byte)((px >> 16) & 0xFF);
                        byte g = (byte)((px >> 8) & 0xFF);
                        byte b = (byte)(px & 0xFF);
                        if (r < 110 && g < 100 && b < 90 && (r + g + b) > 50)
                        {
                            darkEngraveCount++;
                        }
                    }
                }

                nonBgCount.Should().BeGreaterThan((widthPx * heightPx) / 3,
                    "a caixa em zoom 5.0x deve preencher grande parte da viewport sem desaparecer ao sair da tela");
                darkEngraveCount.Should().BeGreaterThan(200,
                    "o overlay rasterizado via homografia analítica 3x3 deve permanecer visível em zoom 5.0x");
            }

            avgMs.Should().BeLessThan(16.6,
                $"Zoom={zoom:0.0}x deve sustentar >= 60 FPS (< 16.6 ms/frame) no pipeline otimizado");
        }
    }

    [Fact]
    public void ModelingLidArticulation_ReusesTriangulation_AndDefersDiskWrites()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Modeling);

        // Warmup do cache geométrico local
        BoxScene3D scene0 = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: 0.0,
            isExplodedView: false);

        const int steps = 45;
        var rebuildTimesMs = new double[steps];
        var sw = new Stopwatch();
        BoxScene3D? lastScene = null;

        for (int i = 1; i <= steps; i++)
        {
            sw.Restart();
            lastScene = BoxScene3DBuilder.BuildScene(
                vm.Box.CurrentCalculator!,
                vm.Box.SelectedModel,
                vm.Box.SheetGeometry!,
                vm.Personalization.Manager,
                lidAngleDeg: i * 2.0,
                isExplodedView: false);
            sw.Stop();
            rebuildTimesMs[i - 1] = sw.Elapsed.TotalMilliseconds;
        }

        lastScene.Should().NotBeNull();
        lastScene!.Panels.Count.Should().Be(scene0.Panels.Count);

        int staticPanelsVerified = 0;
        for (int p = 0; p < scene0.Panels.Count; p++)
        {
            if (!scene0.Panels[p].FollowsLidHinge)
            {
                staticPanelsVerified++;
                ReferenceEquals(lastScene.Panels[p].Triangles, scene0.Panels[p].Triangles)
                    .Should().BeTrue("painéis estáticos devem reutilizar zero-copy os arrays de triângulos cacheados");
                ReferenceEquals(lastScene.Panels[p].CreaseEdges, scene0.Panels[p].CreaseEdges)
                    .Should().BeTrue("painéis estáticos devem reutilizar zero-copy os vincos cacheados");
            }
            else
            {
                lastScene.Panels[p].Triangles.Count.Should().Be(scene0.Panels[p].Triangles.Count);
            }
        }
        staticPanelsVerified.Should().BeGreaterThan(0);

        (double avgRebuildMs, double p95RebuildMs) = MeasurePercentiles(rebuildTimesMs);
        _output.WriteLine($"[Stage1.10 Modeling Bench] BuildScene across {steps} lid steps: avg={avgRebuildMs:0.000} ms, p95={p95RebuildMs:0.000} ms");
        avgRebuildMs.Should().BeLessThan(2.5);

        // Verifica adiamento de escrita em disco durante arraste contínuo de sliders (Box + Manufacturing)
        var draftService = new AutosaveDraftService(_paths);
        var settingsService = new UiSettingsService(_paths);

        vm.Box.BoxWidth = 205.0;
        vm.Manufacturing.CustomBedWidthMm = 620.0;

        File.Exists(draftService.DraftFilePath).Should().BeTrue();
        File.Exists(settingsService.SettingsFilePath).Should().BeTrue();
        string draftBeforeDrag = File.ReadAllText(draftService.DraftFilePath);
        string settingsBeforeDrag = File.ReadAllText(settingsService.SettingsFilePath);

        vm.Box.BeginInteractiveUpdate();
        vm.Manufacturing.BeginInteractiveUpdate();
        vm.Box.IsInteractiveUpdate.Should().BeTrue();
        vm.Manufacturing.IsInteractiveUpdate.Should().BeTrue();

        for (int i = 1; i <= 15; i++)
        {
            vm.Box.BoxWidth = 205.0 + i * 2.0;
            vm.Box.LidAngle = i * 3.0;
            vm.Box.CameraZoom = 1.0 + i * 0.1;
            vm.Manufacturing.CustomBedWidthMm = 620.0 + i * 10.0;
        }

        File.ReadAllText(draftService.DraftFilePath).Should().Be(
            draftBeforeDrag,
            "arraste contínuo não pode serializar autosave_draft.json por frame");
        File.ReadAllText(settingsService.SettingsFilePath).Should().Be(
            settingsBeforeDrag,
            "arraste contínuo não pode serializar ui_settings.json por frame");

        vm.Box.EndInteractiveUpdate();
        vm.Manufacturing.EndInteractiveUpdate();
        vm.Box.IsInteractiveUpdate.Should().BeFalse();
        vm.Manufacturing.IsInteractiveUpdate.Should().BeFalse();

        AutosaveDraftRecord? savedDraft = draftService.TryLoadDraft();
        savedDraft.Should().NotBeNull();
        savedDraft!.BoxParameters.BoxWidth.Should().BeApproximately(235.0, 0.01);

        UiSettingsData loadedSettings = settingsService.Load();
        loadedSettings.CustomBedWidthMm.Should().BeApproximately(770.0, 0.01);
    }

    [Fact]
    public void ManufacturingSheet60FpsBenchmarkTest()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);

        vm.Personalization.SelectSurface(SurfaceId.Front);
        vm.Personalization.AddTextElement("PLANO DE CORTE 1:1");
        vm.Personalization.ImportSvgFromString(
            BuildComplexSvgSample(),
            "sheet-front.svg",
            CncOperationType.EngraveFill);

        vm.Personalization.SelectSurface(SurfaceId.Lid);
        vm.Personalization.ImportSvgFromString(
            BuildComplexSvgSample(),
            "sheet-lid.svg",
            CncOperationType.EngraveVector);

        vm.SetWorkspaceArea(WorkspaceArea.Fabrication);

        BoxSheetGeometry2D sheet = vm.Box.SheetGeometry!;
        SurfaceMapper mapper = vm.Personalization.Manager.Surfaces;
        IReadOnlyList<PersonalizationElement> elements = vm.Personalization.Manager.ListElements();

        const int widthPx = 960;
        const int heightPx = 600;
        var info = new SKImageInfo(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var cachedBitmap = new SKBitmap(info);
        using var cachedCanvas = new SKCanvas(cachedBitmap);
        using var coldBitmap = new SKBitmap(info);
        using var coldCanvas = new SKCanvas(coldBitmap);
        using var controller = new SurfaceCanvas2DController();

        RenderManufacturingSheet2DOptions baseOptions = CreateSheetOptions(widthPx, heightPx);

        // 1) Paridade visual exata entre cache estático do SurfaceCanvas2DController e renderização fria (controller: null)
        OffscreenViewportRenderer.RenderManufacturingSheet2DToCanvas(
            cachedCanvas, widthPx, heightPx, sheet, mapper, elements, baseOptions, controller);
        OffscreenViewportRenderer.RenderManufacturingSheet2DToCanvas(
            coldCanvas, widthPx, heightPx, sheet, mapper, elements, baseOptions, controller: null);

        ReadOnlySpan<uint> cachedPixels = MemoryMarshal.Cast<byte, uint>(cachedBitmap.GetPixelSpan());
        ReadOnlySpan<uint> coldPixels = MemoryMarshal.Cast<byte, uint>(coldBitmap.GetPixelSpan());
        int mismatches = 0;
        for (int i = 0; i < cachedPixels.Length; i++)
        {
            if (cachedPixels[i] != coldPixels[i])
            {
                mismatches++;
            }
        }
        mismatches.Should().Be(0, "a camada em cache estático da aba Fabricação deve ser idêntica ao render frio");

        // 2) Benchmark de 60 frames com movimento contínuo de cursor (mira da régua)
        const int hoverFrames = 60;
        var hoverSamples = new double[hoverFrames];
        var sw = new Stopwatch();
        for (int i = 0; i < hoverFrames; i++)
        {
            RenderManufacturingSheet2DOptions hoverOpts = CreateSheetOptions(
                widthPx,
                heightPx,
                cursorMmX: 40.0 + i * 5.0,
                cursorMmY: 30.0 + (i % 15) * 4.0);

            sw.Restart();
            OffscreenViewportRenderer.RenderManufacturingSheet2DToCanvas(
                cachedCanvas, widthPx, heightPx, sheet, mapper, elements, hoverOpts, controller);
            sw.Stop();
            hoverSamples[i] = sw.Elapsed.TotalMilliseconds;
        }

        (double hoverAvgMs, double hoverP95Ms) = MeasurePercentiles(hoverSamples);
        _output.WriteLine($"[Stage1.10 Sheet Hover Bench] {hoverFrames} frames ({widthPx}x{heightPx}): avg={hoverAvgMs:0.000} ms/frame, p95={hoverP95Ms:0.000} ms");
        hoverAvgMs.Should().BeLessThan(3.0, "hover na folha completa via blit estático deve custar < 3 ms/frame");

        // 3) Benchmark de 30 frames com Pan/Zoom contínuos (usa SKPath/elemento mapeado em cache sem recalcular booleano nem SVG)
        const int panZoomFrames = 30;
        var panSamples = new double[panZoomFrames];
        for (int i = 0; i < panZoomFrames; i++)
        {
            RenderManufacturingSheet2DOptions panOpts = CreateSheetOptions(
                widthPx,
                heightPx,
                userZoom: 1.0 + i * 0.04,
                panPxX: (i - 15) * 3.0,
                panPxY: Math.Sin(i * 0.3) * 18.0);

            sw.Restart();
            OffscreenViewportRenderer.RenderManufacturingSheet2DToCanvas(
                cachedCanvas, widthPx, heightPx, sheet, mapper, elements, panOpts, controller);
            sw.Stop();
            panSamples[i] = sw.Elapsed.TotalMilliseconds;
        }

        (double panAvgMs, double panP95Ms) = MeasurePercentiles(panSamples);
        _output.WriteLine($"[Stage1.10 Sheet Pan/Zoom Bench] {panZoomFrames} frames ({widthPx}x{heightPx}): avg={panAvgMs:0.000} ms/frame, p95={panP95Ms:0.000} ms");
        panAvgMs.Should().BeLessThan(12.0, "pan/zoom contínuo na prancha deve se manter confortável para >= 60 FPS");
    }
}
