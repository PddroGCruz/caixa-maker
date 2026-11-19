using System.Diagnostics;
using System.Numerics;
using System.Text;
using CaixaMaker.App.Models;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.App.Viewport3D;
using CaixaMaker.Cam.Calibration;
using CaixaMaker.Cam.Exporters;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage17RefinementsTests
{
    private static AppPaths CreateTempPaths(out string sandboxRoot)
        => Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateTempPaths(out sandboxRoot);

    private static MainWindowViewModel CreateMainVm(AppPaths paths)
        => Stage16Phase3Interactive3DDimensionsAndLiveCamTests.CreateIsolatedMainVm(paths);

    private static void SafeDeleteDirectory(string path)
        => Stage16Phase3Interactive3DDimensionsAndLiveCamTests.SafeDeleteDirectory(path);

    [Fact]
    public void Camera3DController_OrbitAndPan_FollowIntuitiveScreenSpaceHorizontalDirections()
    {
        AppPaths paths = CreateTempPaths(out string root);
        try
        {
            MainWindowViewModel vm = CreateMainVm(paths);
            vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

            BoxScene3D scene = BoxScene3DBuilder.BuildScene(
                vm.Box.CurrentCalculator!,
                vm.Box.SelectedModel,
                vm.Box.SheetGeometry!,
                vm.Personalization.Manager,
                lidAngleDeg: 0.0,
                isExplodedView: false);

            var camera = new Camera3DController();
            camera.ApplyPreset(CameraPreset.Front);

            ExtrudedPanel3D front = scene.FindPanel(SurfaceId.Front)!;
            ExtrudedPanel3D back = scene.FindPanel(SurfaceId.Back)!;

            CameraFrame baselineFrame = camera.BuildFrame(scene, 900, 600);
            baselineFrame.TryProject(front.Center3D, out Vector2 frontBefore, out _).Should().BeTrue();
            baselineFrame.TryProject(back.Center3D, out Vector2 backBefore, out _).Should().BeTrue();

            // Arrastar mouse para direita (deltaPxX > 0) puxa a face frontal para direita da tela
            double initialYaw = camera.YawDeg;
            camera.Orbit(deltaPxX: 25.0, deltaPxY: 0.0);
            camera.YawDeg.Should().BeLessThan(initialYaw);

            CameraFrame orbitedFrame = camera.BuildFrame(scene, 900, 600);
            orbitedFrame.TryProject(front.Center3D, out Vector2 frontAfterOrbit, out _).Should().BeTrue();
            orbitedFrame.TryProject(back.Center3D, out Vector2 backAfterOrbit, out _).Should().BeTrue();
            frontAfterOrbit.X.Should().BeGreaterThan(frontBefore.X + 5f);
            backAfterOrbit.X.Should().BeLessThan(backBefore.X - 5f);

            // Pan para direita/baixo desloca o modelo projetado para direita/baixo na tela
            camera.ApplyPreset(CameraPreset.Front);
            CameraFrame prePan = camera.BuildFrame(scene, 900, 600);
            prePan.TryProject(front.Center3D, out Vector2 prePanPx, out _).Should().BeTrue();

            camera.Pan(deltaPxX: 30.0, deltaPxY: 20.0, sceneScaleMm: 200.0);
            CameraFrame postPan = camera.BuildFrame(scene, 900, 600);
            postPan.TryProject(front.Center3D, out Vector2 postPanPx, out _).Should().BeTrue();

            postPanPx.X.Should().BeGreaterThan(prePanPx.X + 10f);
            postPanPx.Y.Should().BeGreaterThan(prePanPx.Y + 5f);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void VectorAntialiasedRenderer_SupportsHiDpiHitTestingAndFastMultiFrameCameraRendering()
    {
        AppPaths paths = CreateTempPaths(out string root);
        try
        {
            MainWindowViewModel vm = CreateMainVm(paths);
            vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
            vm.Personalization.SelectSurface(SurfaceId.Front);
            vm.Personalization.AddTextElement("LASER CUT");
            vm.Box.LidAngle = 35.0;

            BoxScene3D scene = BoxScene3DBuilder.BuildScene(
                vm.Box.CurrentCalculator!,
                vm.Box.SelectedModel,
                vm.Box.SheetGeometry!,
                vm.Personalization.Manager,
                lidAngleDeg: 35.0,
                isExplodedView: false);

            var camera = new Camera3DController();
            camera.ApplyPreset(CameraPreset.Isometric);

            // 1. Renderiza em HiDPI (ScaleFactor = 1.75) com overlays de gravação e cotas 3D
            int hiW = 1400;
            int hiH = 875;
            const float scale = 1.75f;
            using SKBitmap bmpHi = OffscreenViewportRenderer.RenderScene3DBitmap(
                scene,
                camera,
                vm.Personalization.Manager,
                new RenderScene3DOptions
                {
                    WidthPx = hiW,
                    HeightPx = hiH,
                    ScaleFactor = scale,
                    DarkTheme = true,
                    ShowDimensions = true,
                    ShowSurfaceGuides = true,
                    ActiveSurfaceId = SurfaceId.Front,
                });

            bmpHi.Width.Should().Be(hiW);
            bmpHi.Height.Should().Be(hiH);

            SKColor bg = bmpHi.GetPixel(2, 2);
            var distinctColors = new HashSet<uint>();
            for (int y = 0; y < hiH; y += 3)
            {
                for (int x = 0; x < hiW; x += 3)
                {
                    distinctColors.Add((uint)bmpHi.GetPixel(x, y));
                }
            }
            distinctColors.Should().Contain((uint)bg);
            distinctColors.Count.Should().BeGreaterThan(24, "anti-aliasing e sombreamento 3D devem produzir múltiplos tons intermediários suaves");

            IReadOnlyList<DimensionCalloutHitResult> calloutsHi = OffscreenViewportRenderer.ProjectDimensionCalloutBounds(
                scene,
                camera,
                hiW,
                hiH,
                showDimensions: true,
                scaleFactor: scale);
            calloutsHi.Should().HaveCount(3);

            foreach (DimensionCalloutHitResult callout in calloutsHi)
            {
                DimensionCalloutHitResult? hit = OffscreenViewportRenderer.HitTestDimensionCallout(
                    scene,
                    camera,
                    hiW,
                    hiH,
                    callout.CenterScreen.X,
                    callout.CenterScreen.Y,
                    showDimensions: true,
                    scaleFactor: scale);

                hit.Should().NotBeNull();
                hit!.Value.Axis.Should().Be(callout.Axis);
            }

            // 2. Aquecimento + 10 frames sequenciais com órbita contínua (pipeline Skia vetorial)
            var surfaceInfo = new SKImageInfo(960, 640, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKSurface? surface = SKSurface.Create(surfaceInfo);
            surface.Should().NotBeNull();
            SKCanvas canvas = surface!.Canvas;
            var opts = new RenderScene3DOptions
            {
                WidthPx = 960,
                HeightPx = 640,
                ScaleFactor = 1.0f,
                DarkTheme = true,
                ShowDimensions = true,
                ShowSurfaceGuides = false,
            };

            OffscreenViewportRenderer.RenderScene3DToCanvas(
                canvas, 960, 640, scene, camera, vm.Personalization.Manager, opts);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                camera.Orbit(deltaPxX: 7.5, deltaPxY: 2.0);
                OffscreenViewportRenderer.RenderScene3DToCanvas(
                    canvas, 960, 640, scene, camera, vm.Personalization.Manager, opts);
            }
            sw.Stop();

            sw.ElapsedMilliseconds.Should().BeLessThan(1500, "pipeline vetorial SkiaSharp deve sustentar 10 quadros fluidos com folga");
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void CustomLaserBed_AutoSwitchesOnEditEvaluatesFitAndPersistsAcrossSessions()
    {
        AppPaths paths = CreateTempPaths(out string root);
        try
        {
            MainWindowViewModel vm1 = CreateMainVm(paths);
            vm1.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

            // Folha padrão 200x150x100 ~= 432x401 mm
            vm1.Manufacturing.SelectedLaserBedPreset = LaserBedPreset.Laser600x400;
            vm1.Manufacturing.IsCustomLaserBedSelected.Should().BeFalse();

            // Editar largura customizada muda automaticamente para Custom e aplica clamp [50..5000]
            vm1.Manufacturing.CustomBedWidthMm = 500.0;
            vm1.Manufacturing.CustomBedHeightMm = 420.0;
            vm1.Manufacturing.SelectedLaserBedPreset.Should().Be(LaserBedPreset.Custom);
            vm1.Manufacturing.IsCustomLaserBedSelected.Should().BeTrue();
            vm1.Manufacturing.SelectedLaserBed.WidthMm.Should().Be(500.0);
            vm1.Manufacturing.SelectedLaserBed.HeightMm.Should().Be(420.0);

            LaserBedFitResult fitOk = vm1.Manufacturing.EvaluateLaserBedFit(vm1.Box.SheetGeometry);
            fitOk.Severity.Should().Be(LaserBedFitSeverity.Fits);

            vm1.Manufacturing.CustomBedWidthMm = 400.0;
            vm1.Manufacturing.CustomBedHeightMm = 380.0;
            LaserBedFitResult fitExceed = vm1.Manufacturing.EvaluateLaserBedFit(vm1.Box.SheetGeometry);
            fitExceed.Severity.Should().Be(LaserBedFitSeverity.ExceedsBed);

            // Clamp inferior e superior
            vm1.Manufacturing.CustomBedWidthMm = 10.0;
            vm1.Manufacturing.CustomBedHeightMm = 9000.0;
            vm1.Manufacturing.CustomBedWidthMm.Should().Be(UiSettingsData.MinLaserBedDimensionMm);
            vm1.Manufacturing.CustomBedHeightMm.Should().Be(UiSettingsData.MaxLaserBedDimensionMm);

            // Persistência em ui_settings.json
            vm1.Manufacturing.CustomBedWidthMm = 720.0;
            vm1.Manufacturing.CustomBedHeightMm = 480.0;

            MainWindowViewModel vm2 = CreateMainVm(paths);
            vm2.Manufacturing.SelectedLaserBedPreset.Should().Be(LaserBedPreset.Custom);
            vm2.Manufacturing.CustomBedWidthMm.Should().BeApproximately(720.0, 0.01);
            vm2.Manufacturing.CustomBedHeightMm.Should().BeApproximately(480.0, 0.01);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void SplitViewportOrientationAndRatio_ClampsResetsAndPersistsAcrossSessions()
    {
        AppPaths paths = CreateTempPaths(out string root);
        try
        {
            MainWindowViewModel vm1 = CreateMainVm(paths);
            vm1.SplitOrientation.Should().Be(SplitViewportOrientation.VerticalStacked);
            vm1.IsSplitVertical.Should().BeTrue();
            vm1.IsSplitHorizontal.Should().BeFalse();

            vm1.ToggleSplitOrientation();
            vm1.SplitOrientation.Should().Be(SplitViewportOrientation.HorizontalSideBySide);
            vm1.IsSplitHorizontal.Should().BeTrue();

            vm1.SplitViewportRatio = 0.05;
            vm1.SplitViewportRatio.Should().BeApproximately(UiSettingsData.MinSplitViewportRatio, 1e-4);

            vm1.SplitViewportRatio = 0.99;
            vm1.SplitViewportRatio.Should().BeApproximately(UiSettingsData.MaxSplitViewportRatio, 1e-4);

            vm1.ResetSplitRatio();
            vm1.SplitViewportRatio.Should().BeApproximately(0.5, 1e-4);

            vm1.SplitViewportRatio = 0.63;

            MainWindowViewModel vm2 = CreateMainVm(paths);
            vm2.SplitOrientation.Should().Be(SplitViewportOrientation.HorizontalSideBySide);
            vm2.SplitViewportRatio.Should().BeApproximately(0.63, 1e-4);
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task KerfCalibrationComb_SupportsExpandedRangeWithoutOverlapAndExportsConfiguredOutputs()
    {
        AppPaths paths = CreateTempPaths(out string root);
        try
        {
            // Teste direto do gerador com 11, 15 e 21 rasgos e passo amplo
            KerfCalibrationCombResult wide11 = KerfCalibrationCombGenerator.Default.Generate(
                thicknessMm: 3.0,
                centerFolgaMm: 0.0,
                slotCount: 11,
                stepMm: 0.10);
            wide11.Slots.Should().HaveCount(11);
            wide11.Slots[0].FolgaMm.Should().BeApproximately(-0.50, 1e-4);
            wide11.Slots[^1].FolgaMm.Should().BeApproximately(0.50, 1e-4);

            KerfCalibrationCombResult max21 = KerfCalibrationCombGenerator.Default.Generate(
                thicknessMm: 3.0,
                centerFolgaMm: 0.0,
                slotCount: 21,
                stepMm: 0.04);
            max21.Slots.Should().HaveCount(21);
            max21.Slots[0].FolgaMm.Should().BeApproximately(-0.40, 1e-4);
            max21.Slots[^1].FolgaMm.Should().BeApproximately(0.40, 1e-4);

            for (int i = 1; i < max21.Slots.Count; i++)
            {
                double pitch = max21.Slots[i].CenterXMm - max21.Slots[i - 1].CenterXMm;
                pitch.Should().BeGreaterThanOrEqualTo(9.0, "espacamento entre centros deve evitar colisao entre rotulos gravados");
            }

            string svg = Encoding.UTF8.GetString(max21.ToExportBytes(VectorExportFormat.Svg));
            string dxf = Encoding.ASCII.GetString(max21.ToExportBytes(VectorExportFormat.Dxf));
            byte[] pdf = max21.ToExportBytes(VectorExportFormat.Pdf);
            svg.Should().Contain("<svg");
            dxf.Should().Contain("ENTITIES").And.Contain("EOF");
            Encoding.ASCII.GetString(pdf.Take(5).ToArray()).Should().Be("%PDF-");

            // Integração no ManufacturingWorkspaceViewModel
            MainWindowViewModel vm = CreateMainVm(paths);
            vm.Manufacturing.KerfSlotCount.Should().Be(11);
            vm.Manufacturing.KerfStepMm.Should().BeApproximately(0.05, 1e-4);
            vm.Manufacturing.KerfMinFolgaMm.Should().BeApproximately(-0.25, 1e-4);
            vm.Manufacturing.KerfMaxFolgaMm.Should().BeApproximately(0.25, 1e-4);
            vm.Manufacturing.KerfCombRangeSummary.Should().Contain("11 rasgos").And.Contain("-0.25").And.Contain("+0.25");

            // Coerção automática para ímpar [5..21] e clamp de passo [0.02..0.25]
            vm.Manufacturing.KerfSlotCount = 12;
            vm.Manufacturing.KerfSlotCount.Should().Be(13);
            vm.Manufacturing.KerfSlotCount = 2;
            vm.Manufacturing.KerfSlotCount.Should().Be(5);
            vm.Manufacturing.KerfSlotCount = 30;
            vm.Manufacturing.KerfSlotCount.Should().Be(21);

            vm.Manufacturing.KerfStepMm = 0.005;
            vm.Manufacturing.KerfStepMm.Should().BeApproximately(0.02, 1e-4);
            vm.Manufacturing.KerfStepMm = 0.90;
            vm.Manufacturing.KerfStepMm.Should().BeApproximately(0.25, 1e-4);

            vm.Manufacturing.ApplyKerfCombPreset("wide");
            vm.Manufacturing.KerfSlotCount.Should().Be(11);
            vm.Manufacturing.KerfStepMm.Should().BeApproximately(0.10, 1e-4);

            vm.Manufacturing.ApplyKerfCombPreset("extended");
            vm.Manufacturing.KerfSlotCount.Should().Be(15);
            vm.Manufacturing.KerfStepMm.Should().BeApproximately(0.05, 1e-4);

            string? exportedSvg = await vm.Manufacturing.ExportKerfCalibrationCombAsync("svg");
            exportedSvg.Should().NotBeNullOrWhiteSpace();
            File.Exists(exportedSvg!).Should().BeTrue();
            vm.Manufacturing.RecentOutputs.Should().NotBeEmpty();
        }
        finally
        {
            SafeDeleteDirectory(root);
        }
    }
}
