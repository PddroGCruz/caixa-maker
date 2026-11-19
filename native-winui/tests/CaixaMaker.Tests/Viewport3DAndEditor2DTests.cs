using System.Numerics;
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

namespace CaixaMaker.Tests;

public sealed class Viewport3DAndEditor2DTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    public Viewport3DAndEditor2DTests()
    {
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_Vp2dTests_" + Guid.NewGuid().ToString("N")[..10]);
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

    private static byte[] CreateSamplePngBytes(int width = 24, int height = 16)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var darkPaint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            canvas.DrawRect(4, 3, width - 8, height - 6, darkPaint);
        }

        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static bool HasPngSignature(byte[] bytes)
        => bytes.Length > 16
           && bytes[0] == 0x89
           && bytes[1] == 0x50
           && bytes[2] == 0x4E
           && bytes[3] == 0x47;

    private static int CountNonUniformPixels(SKBitmap bmp, SKColor backgroundRef)
    {
        int diff = 0;
        for (int y = 0; y < bmp.Height; y += 2)
        {
            for (int x = 0; x < bmp.Width; x += 2)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (Math.Abs(c.Red - backgroundRef.Red) +
                    Math.Abs(c.Green - backgroundRef.Green) +
                    Math.Abs(c.Blue - backgroundRef.Blue) > 18)
                {
                    diff++;
                }
            }
        }

        return diff;
    }

    private static int CountDifferentPixels(SKBitmap a, SKBitmap b)
    {
        int w = Math.Min(a.Width, b.Width);
        int h = Math.Min(a.Height, b.Height);
        int count = 0;
        for (int y = 0; y < h; y += 2)
        {
            for (int x = 0; x < w; x += 2)
            {
                SKColor ca = a.GetPixel(x, y);
                SKColor cb = b.GetPixel(x, y);
                if (Math.Abs(ca.Red - cb.Red) +
                    Math.Abs(ca.Green - cb.Green) +
                    Math.Abs(ca.Blue - cb.Blue) +
                    Math.Abs(ca.Alpha - cb.Alpha) > 16)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void BuildScene_ExtrudesSixPanelsForBoxWithLidAndFiveForOpenBox_PreservingTriangulatedArea()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        BoxScene3D scene6 = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: 0.0,
            isExplodedView: false);

        scene6.Panels.Should().HaveCount(6);
        scene6.Panels.Select(p => p.SurfaceId).Should().BeEquivalentTo(
        [
            SurfaceId.Front,
            SurfaceId.Back,
            SurfaceId.Left,
            SurfaceId.Right,
            SurfaceId.Lid,
            SurfaceId.Bottom,
        ]);

        // Valida invariância matemática da triangulação (contorno Finger Joint + furos internos + circulares)
        const int circleSegments = 32;
        double ngonFactor = 0.5 * circleSegments * Math.Sin(2.0 * Math.PI / circleSegments);

        foreach (ExtrudedPanel3D panel3D in scene6.Panels)
        {
            string panelKey = panel3D.SurfaceId.ToString().ToLowerInvariant();
            PanelGeometry2D panel2D = vm.Box.SheetGeometry!.PanelsById[panelKey];

            double holeArea = panel2D.InternalHoles.Sum(h => h.AreaMm2)
                              + panel2D.CircularHoles.Sum(c => ngonFactor * c.Radius * c.Radius);
            double expectedFaceArea = panel2D.OuterContour.AreaMm2 - holeArea;

            double triangulatedOuterArea = panel3D.Triangles
                .Where(t => t.Kind == PanelMaterialKind.OuterFace)
                .Sum(t => 0.5 * Vector3.Cross(t.B - t.A, t.C - t.A).Length());

            triangulatedOuterArea.Should().BeApproximately(
                expectedFaceArea,
                precision: 0.75,
                because: $"face externa triangulada da chapa '{panelKey}' deve preservar a área exata com furos");

            panel3D.Triangles.Should().NotBeEmpty();
            panel3D.Triangles.Should().OnlyContain(t =>
                float.IsFinite(t.A.X) && float.IsFinite(t.A.Y) && float.IsFinite(t.A.Z) &&
                float.IsFinite(t.B.X) && float.IsFinite(t.B.Y) && float.IsFinite(t.B.Z) &&
                float.IsFinite(t.C.X) && float.IsFinite(t.C.Y) && float.IsFinite(t.C.Z));
            panel3D.CreaseEdges.Should().NotBeEmpty();
            panel3D.OverlayQuad.Should().NotBeNull();
        }

        // Caixa aberta tem 5 chapas (sem tampa) e mantém 5 mesmo com lidAngle > 0
        vm.StartNewProjectFromCatalog(OpenBoxModel.Id);
        BoxScene3D scene5 = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: 45.0,
            isExplodedView: false);

        scene5.Panels.Should().HaveCount(5);
        scene5.Panels.Select(p => p.SurfaceId).Should().NotContain(SurfaceId.Lid);
    }

    [Fact]
    public void BuildScene_LidArticulationAt45And90Degrees_PivotsAroundHingeWithoutMovingOtherPanels()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        BoxCalculator calc = vm.Box.CurrentCalculator!;
        IBoxModelDefinition model = vm.Box.SelectedModel;
        BoxSheetGeometry2D sheet = vm.Box.SheetGeometry!;

        BoxScene3D s0 = BoxScene3DBuilder.BuildScene(calc, model, sheet, vm.Personalization.Manager, lidAngleDeg: 0.0);
        BoxScene3D s45 = BoxScene3DBuilder.BuildScene(calc, model, sheet, vm.Personalization.Manager, lidAngleDeg: 45.0);
        BoxScene3D s90 = BoxScene3DBuilder.BuildScene(calc, model, sheet, vm.Personalization.Manager, lidAngleDeg: 90.0);

        ExtrudedPanel3D lid0 = s0.FindPanel(SurfaceId.Lid)!;
        ExtrudedPanel3D lid45 = s45.FindPanel(SurfaceId.Lid)!;
        ExtrudedPanel3D lid90 = s90.FindPanel(SurfaceId.Lid)!;

        // O raio em torno do eixo de dobradiça (Y, Z) deve ser exatamente preservado durante a articulação
        Vector2 RadiusYz(ExtrudedPanel3D lid, Vector3 hinge)
            => new(lid.Center3D.Y - hinge.Y, lid.Center3D.Z - hinge.Z);

        float r0 = RadiusYz(lid0, s0.LidHingeOrigin).Length();
        float r45 = RadiusYz(lid45, s45.LidHingeOrigin).Length();
        float r90 = RadiusYz(lid90, s90.LidHingeOrigin).Length();

        r45.Should().BeApproximately(r0, 0.05f);
        r90.Should().BeApproximately(r0, 0.05f);

        // Conforme tampa abre (0 -> 45 -> 90), o centro sobe em Y e recua em direção à dobradiça Z
        lid45.Center3D.Y.Should().BeGreaterThan(lid0.Center3D.Y + 20f);
        lid90.Center3D.Y.Should().BeGreaterThan(lid45.Center3D.Y + 20f);

        // As demais 5 chapas não sofrem rotação da tampa
        foreach (SurfaceId side in new[] { SurfaceId.Front, SurfaceId.Back, SurfaceId.Left, SurfaceId.Right, SurfaceId.Bottom })
        {
            s90.FindPanel(side)!.Center3D.Should().Be(s0.FindPanel(side)!.Center3D);
        }

        // Vista explodida desloca cada chapa para fora ao longo de sua normal
        BoxScene3D exploded = BoxScene3DBuilder.BuildScene(
            calc, model, sheet, vm.Personalization.Manager, lidAngleDeg: 0.0, isExplodedView: true);

        foreach (ExtrudedPanel3D expPanel in exploded.Panels)
        {
            ExtrudedPanel3D basePanel = s0.FindPanel(expPanel.SurfaceId)!;
            Vector3 delta = expPanel.Center3D - basePanel.Center3D;
            Vector3.Dot(delta, expPanel.ExplodeNormal).Should().BeGreaterThan(15f);
        }

        // Cotas dimensionais 3D contêm W, D e H
        s0.Dimensions.Should().HaveCount(3);
        s0.Dimensions.Select(d => d.Label).Should().Contain(l => l.Contains("200"));
        s0.Dimensions.Select(d => d.Label).Should().Contain(l => l.Contains("150"));
        s0.Dimensions.Select(d => d.Label).Should().Contain(l => l.Contains("100"));
    }

    [Fact]
    public void BuildScene_ProjectsEngravingsOntoAllSixSurfaces_AndCameraPresetsPickExpectedPanels()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);

        foreach (SurfaceId surf in Enum.GetValues<SurfaceId>())
        {
            vm.Personalization.SelectSurface(surf);
            vm.Personalization.AddTextElement($"Face {surf}");
        }

        BoxScene3D scene = BoxScene3DBuilder.BuildScene(
            vm.Box.CurrentCalculator!,
            vm.Box.SelectedModel,
            vm.Box.SheetGeometry!,
            vm.Personalization.Manager,
            lidAngleDeg: 0.0,
            isExplodedView: false);

        foreach (ExtrudedPanel3D panel in scene.Panels)
        {
            panel.OverlayQuad.Should().NotBeNull();
            panel.OverlayQuad!.ProjectedElements.Should().HaveCount(1);
            panel.OverlayQuad.ProjectedElements[0].Corners3D.Should().HaveCount(4);
        }

        var camera = new Camera3DController();
        foreach (CameraPreset preset in Enum.GetValues<CameraPreset>())
        {
            camera.ApplyPreset(preset);
            CameraFrame frame = camera.BuildFrame(scene, 800, 600);
            float.IsFinite(frame.Eye.X).Should().BeTrue();
            float.IsFinite(frame.Forward.Z).Should().BeTrue();
        }

        // Vista frontal deve clicar diretamente na chapa Front
        camera.ApplyPreset(CameraPreset.Front);
        CameraFrame frontFrame = camera.BuildFrame(scene, 800, 600);
        ExtrudedPanel3D frontPanel = scene.FindPanel(SurfaceId.Front)!;
        frontFrame.TryProject(frontPanel.OverlayQuad!.TopLeft * 0.5f + frontPanel.OverlayQuad.BottomRight * 0.5f, out Vector2 frontPx, out _).Should().BeTrue();
        ExtrudedPanel3D? pickedFront = camera.PickPanel(scene, frontPx.X, frontPx.Y, 800, 600);
        pickedFront?.SurfaceId.Should().Be(SurfaceId.Front);

        // Vista superior deve clicar diretamente na chapa Lid
        camera.ApplyPreset(CameraPreset.Top);
        CameraFrame topFrame = camera.BuildFrame(scene, 800, 600);
        ExtrudedPanel3D lidPanel = scene.FindPanel(SurfaceId.Lid)!;
        topFrame.TryProject(lidPanel.OverlayQuad!.TopLeft * 0.5f + lidPanel.OverlayQuad.BottomRight * 0.5f, out Vector2 lidPx, out _).Should().BeTrue();
        ExtrudedPanel3D? pickedLid = camera.PickPanel(scene, lidPx.X, lidPx.Y, 800, 600);
        pickedLid?.SurfaceId.Should().Be(SurfaceId.Lid);

        // Limites seguros de zoom
        camera.ZoomBy(100.0);
        camera.Zoom.Should().Be(Camera3DController.MaxZoom);
        camera.ZoomBy(0.001);
        camera.Zoom.Should().Be(Camera3DController.MinZoom);
        camera.Reset();
        camera.ActivePreset.Should().Be(CameraPreset.Isometric);
        camera.Zoom.Should().Be(1.0);
    }

    [Fact]
    public void Editor2DController_HitTestsAtZeroAnd45Degrees_AndCommitsOneAtomicUndoStepPerDrag()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);
        vm.Personalization.SelectSurface(SurfaceId.Front);

        TextPersonalizationElement el = vm.Personalization.AddTextElement("CAD2D");
        vm.Personalization.UpdateSelectedElementTransform(
            xMm: 70.0,
            yMm: 35.0,
            widthMm: 60.0,
            heightMm: 20.0,
            rotationDeg: 0.0,
            preserveAspectRatio: true,
            recordUndo: true);

        SurfaceDefinition frontDef = vm.Personalization.ActiveSurface!;
        PanelGeometry2D frontPanel = vm.Box.SheetGeometry!.PanelsById["front"];
        SurfaceContour2D contour = SurfaceCanvas2DController.MapPanelContourToSurface(frontPanel, frontDef);
        contour.InternalHoles.Count.Should().Be(frontPanel.InternalHoles.Count);

        var controller = new SurfaceCanvas2DController();
        controller.UpdateLayout(frontDef, 800, 500, contour);

        // Hit-test em 0°
        controller.HitTestElement([el], 100.0, 45.0)?.Id.Should().Be(el.Id);
        controller.HitTestElement([el], 25.0, 45.0).Should().BeNull();

        // Hit-test OBB em 45°: extremidade ao longo da diagonal rotacionada atinge, canto antigo do AABB não rotacionado falha
        vm.Personalization.UpdateSelectedElementTransform(rotationDeg: 45.0, recordUndo: false);
        double diagOffset = 22.0 / Math.Sqrt(2.0); // dentro de +-30 mm ao longo do eixo principal rotacionado em 45°
        controller.HitTestElement([el], 100.0 + diagOffset, 45.0 + diagOffset)?.Id.Should().Be(el.Id);
        controller.HitTestElement([el], 128.0, 36.0).Should().BeNull(); // fora da caixa estreita (h=20mm) inclinada a 45°

        // Alças 8 resize + 1 rotação presentes e detectáveis em pixel
        vm.Personalization.UpdateSelectedElementTransform(rotationDeg: 0.0, recordUndo: false);
        IReadOnlyDictionary<Editor2DHandleType, Point2D> handles = controller.GetHandlesMm(el);
        handles.Should().HaveCount(9);
        foreach ((Editor2DHandleType handleType, Point2D handleMm) in handles)
        {
            (double hx, double hy) = controller.MmToPx(handleMm.X, handleMm.Y);
            controller.HitTestHandle(el, hx, hy).Should().Be(handleType);
        }

        // Ciclo completo Pressed -> Moved x3 -> Released (Mover com snap central) gera EXATAMENTE 1 passo Undo
        double origX = el.XMm;
        double origY = el.YMm;
        int undoBeforeMove = vm.Personalization.UndoCount;

        (double startPxX, double startPxY) = controller.MmToPx(el.CenterMm().X, el.CenterMm().Y);
        controller.OnPointerPressed(vm.Personalization, startPxX, startPxY).Should().Be(Editor2DHandleType.Body);

        double scale = controller.EffectiveScalePxPerMm;
        controller.OnPointerMoved(vm.Personalization, startPxX + 4.0 * scale, startPxY + 2.0 * scale).Should().BeTrue();
        controller.OnPointerMoved(vm.Personalization, startPxX + 8.0 * scale, startPxY + 3.0 * scale).Should().BeTrue();

        // Mover para próximo ao centro da área segura aciona snap magnético (±1.5 mm)
        Point2D safeCenter = frontDef.SafeAreaForMargin(vm.Personalization.SafetyMarginMm).Center;
        double dxToCenterMm = safeCenter.X - (origX + el.WidthMm * 0.5) + 0.6;
        double dyToCenterMm = safeCenter.Y - (origY + el.HeightMm * 0.5) - 0.5;
        controller.OnPointerMoved(
            vm.Personalization,
            startPxX + dxToCenterMm * scale,
            startPxY + dyToCenterMm * scale).Should().BeTrue();
        controller.SnapGuideXActive.Should().BeTrue();
        controller.SnapGuideYActive.Should().BeTrue();

        vm.Personalization.UndoCount.Should().Be(undoBeforeMove, "movimento contínuo não deve poluir o histórico");
        controller.OnPointerReleased(vm.Personalization).Should().BeTrue();
        vm.Personalization.UndoCount.Should().Be(undoBeforeMove + 1, "release deve registrar exatamente 1 passo atômico de Undo");
        el.CenterMm().X.Should().BeApproximately(safeCenter.X, 0.05);
        el.CenterMm().Y.Should().BeApproximately(safeCenter.Y, 0.05);

        vm.Personalization.Undo();
        PersonalizationElement restoredAfterMove = vm.Personalization.SelectedElement!;
        restoredAfterMove.XMm.Should().BeApproximately(origX, 0.05);
        restoredAfterMove.YMm.Should().BeApproximately(origY, 0.05);
        el = (TextPersonalizationElement)restoredAfterMove;

        // Resize proporcional preserva aspect ratio e restaura PreserveAspectRatio no release + 1 passo Undo
        el.PreserveAspectRatio.Should().BeTrue();
        double aspectBefore = el.WidthMm / el.HeightMm;
        int undoBeforeResize = vm.Personalization.UndoCount;
        Point2D eastHandle = controller.GetHandlesMm(el)[Editor2DHandleType.East];
        (double ehPxX, double ehPxY) = controller.MmToPx(eastHandle.X, eastHandle.Y);
        controller.OnPointerPressed(vm.Personalization, ehPxX, ehPxY).Should().Be(Editor2DHandleType.East);
        controller.OnPointerMoved(vm.Personalization, ehPxX + 12.0 * scale, ehPxY).Should().BeTrue();
        controller.OnPointerReleased(vm.Personalization).Should().BeTrue();

        (el.WidthMm / el.HeightMm).Should().BeApproximately(aspectBefore, 0.08);
        el.PreserveAspectRatio.Should().BeTrue();
        vm.Personalization.UndoCount.Should().Be(undoBeforeResize + 1);

        // Rotação com Shift realiza snap a cada 15° e adiciona 1 passo Undo
        int undoBeforeRot = vm.Personalization.UndoCount;
        Point2D rotHandle = controller.GetHandlesMm(el)[Editor2DHandleType.Rotate];
        (double rhPxX, double rhPxY) = controller.MmToPx(rotHandle.X, rotHandle.Y);
        controller.OnPointerPressed(vm.Personalization, rhPxX, rhPxY).Should().Be(Editor2DHandleType.Rotate);

        // Arrasta ~32 graus à direita do centro -> com Shift deve encaixar em múltiplo de 15°
        (double cPxX, double cPxY) = controller.MmToPx(el.CenterMm().X, el.CenterMm().Y);
        double rad32 = 32.0 * Math.PI / 180.0;
        controller.OnPointerMoved(
            vm.Personalization,
            cPxX + Math.Sin(rad32) * 80.0,
            cPxY - Math.Cos(rad32) * 80.0,
            shiftHeld: true).Should().BeTrue();
        controller.OnPointerReleased(vm.Personalization).Should().BeTrue();

        (el.RotationDeg % 15.0).Should().BeApproximately(0.0, 0.01);
        vm.Personalization.UndoCount.Should().Be(undoBeforeRot + 1);

        // Zoom no cursor mantém a coordenada em mm exatamente sob o âncora em pixel
        (double anchorMmX, double anchorMmY) = controller.PxToMm(260.0, 190.0);
        controller.ZoomAtPoint(frontDef, 1.8, 260.0, 190.0, contour);
        (double pinnedPxX, double pinnedPxY) = controller.MmToPx(anchorMmX, anchorMmY);
        pinnedPxX.Should().BeApproximately(260.0, 1e-4);
        pinnedPxY.Should().BeApproximately(190.0, 1e-4);
    }

    [Fact]
    public void OffscreenRenderer_GeneratesDistinctValidBitmapsAcrossDarkLightAndBoxStates()
    {
        MainWindowViewModel vm = CreateMainVm();
        vm.StartNewProjectFromCatalog(BoxWithLidAndHandlesModel.Id);
        vm.SetWorkspaceArea(WorkspaceArea.Personalization);

        // Adiciona elementos heterogêneos nas faces (Texto Hatch, SVG vetorial, Raster PNG)
        vm.Personalization.SelectSurface(SurfaceId.Front);
        vm.Personalization.DraftRenderMode = TextRenderMode.Hatch;
        TextPersonalizationElement txt = vm.Personalization.AddTextElement("IFAC");
        vm.Personalization.UpdateSelectedElementTransform(yMm: 22.0, recordUndo: false);

        vm.Personalization.SelectSurface(SurfaceId.Lid);
        const string sampleSvg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><rect x="2" y="2" width="20" height="20" fill="#000" /></svg>""";
        vm.Personalization.ImportSvgFromString(sampleSvg, "badge.svg", CncOperationType.EngraveFill);

        vm.Personalization.SelectSurface(SurfaceId.Right);
        vm.Personalization.ImportRasterFromBytes(CreateSamplePngBytes(), "logo.png", vectorize: false);

        vm.Personalization.SelectSurface(SurfaceId.Front);

        BoxCalculator calc = vm.Box.CurrentCalculator!;
        IBoxModelDefinition model = vm.Box.SelectedModel;
        BoxSheetGeometry2D sheet = vm.Box.SheetGeometry!;

        BoxScene3D sceneClosed = BoxScene3DBuilder.BuildScene(calc, model, sheet, vm.Personalization.Manager, lidAngleDeg: 0.0, isExplodedView: false);
        BoxScene3D sceneOpenExploded = BoxScene3DBuilder.BuildScene(calc, model, sheet, vm.Personalization.Manager, lidAngleDeg: 90.0, isExplodedView: true);

        var camera = new Camera3DController();
        byte[] png3DDark = OffscreenViewportRenderer.RenderScene3DPng(
            sceneClosed,
            camera,
            vm.Personalization.Manager,
            new RenderScene3DOptions
            {
                WidthPx = 420,
                HeightPx = 300,
                DarkTheme = true,
                ShowDimensions = true,
                ShowSurfaceGuides = true,
                ActiveSurfaceId = SurfaceId.Front,
                SelectedElementId = txt.Id,
            });
        HasPngSignature(png3DDark).Should().BeTrue();

        using SKBitmap bmp3DDark = OffscreenViewportRenderer.RenderScene3DBitmap(
            sceneClosed, camera, vm.Personalization.Manager,
            new RenderScene3DOptions { WidthPx = 420, HeightPx = 300, DarkTheme = true, ShowDimensions = true });
        using SKBitmap bmp3DLight = OffscreenViewportRenderer.RenderScene3DBitmap(
            sceneClosed, camera, vm.Personalization.Manager,
            new RenderScene3DOptions { WidthPx = 420, HeightPx = 300, DarkTheme = false, ShowDimensions = true });
        using SKBitmap bmp3DOpen = OffscreenViewportRenderer.RenderScene3DBitmap(
            sceneOpenExploded, camera, vm.Personalization.Manager,
            new RenderScene3DOptions { WidthPx = 420, HeightPx = 300, DarkTheme = true, ShowDimensions = true });

        bmp3DDark.Width.Should().Be(420);
        bmp3DDark.Height.Should().Be(300);
        bmp3DDark.GetPixel(2, 2).Should().Be(new SKColor(0x11, 0x13, 0x17));
        bmp3DLight.GetPixel(2, 2).Should().Be(new SKColor(0xDF, 0xE4, 0xEC));
        CountNonUniformPixels(bmp3DDark, new SKColor(0x11, 0x13, 0x17)).Should().BeGreaterThan(2000);
        CountDifferentPixels(bmp3DDark, bmp3DLight).Should().BeGreaterThan(5000);
        CountDifferentPixels(bmp3DDark, bmp3DOpen).Should().BeGreaterThan(1500);

        // Gizmo XYZ (46 e 54 px)
        using SKBitmap gizmoBmp = OffscreenViewportRenderer.RenderAxisGizmoBitmap(camera, sizePx: 46, dark: true);
        gizmoBmp.Width.Should().Be(46);
        using SKBitmap gizmoBmp54 = OffscreenViewportRenderer.RenderAxisGizmoBitmap(camera, sizePx: 54, dark: true);
        gizmoBmp54.Width.Should().Be(54);
        int opaqueGizmoPixels = 0;
        for (int y = 0; y < gizmoBmp.Height; y++)
            for (int x = 0; x < gizmoBmp.Width; x++)
                if (gizmoBmp.GetPixel(x, y).Alpha > 40) opaqueGizmoPixels++;
        opaqueGizmoPixels.Should().BeGreaterThan(25);

        // Editor 2D offscreen (Dark/Light, seleção, réguas de 18 px + contorno real com rasgos e furos)
        SurfaceDefinition frontDef = vm.Personalization.ActiveSurface!;
        SurfaceContour2D contour = SurfaceCanvas2DController.MapPanelContourToSurface(sheet.PanelsById["front"], frontDef);
        var editor2D = new SurfaceCanvas2DController();
        SurfaceCanvas2DController.RulerThicknessPx.Should().Be(18.0);
        editor2D.UpdateLayout(frontDef, 440, 300, contour);
        editor2D.OriginPxX.Should().BeGreaterThan(SurfaceCanvas2DController.RulerThicknessPx);
        editor2D.OriginPxY.Should().BeGreaterThan(SurfaceCanvas2DController.RulerThicknessPx);

        byte[] png2DDark = OffscreenViewportRenderer.RenderEditor2DPng(
            editor2D, frontDef, vm.Personalization.Manager.ListElements(), contour,
            new RenderEditor2DOptions
            {
                WidthPx = 440,
                HeightPx = 300,
                DarkTheme = true,
                SelectedElementId = txt.Id,
            });
        HasPngSignature(png2DDark).Should().BeTrue();

        using SKBitmap bmp2DDarkSel = OffscreenViewportRenderer.RenderEditor2DBitmap(
            editor2D, frontDef, vm.Personalization.Manager.ListElements(), contour,
            new RenderEditor2DOptions { WidthPx = 440, HeightPx = 300, DarkTheme = true, SelectedElementId = txt.Id });
        using SKBitmap bmp2DDarkNone = OffscreenViewportRenderer.RenderEditor2DBitmap(
            editor2D, frontDef, vm.Personalization.Manager.ListElements(), contour,
            new RenderEditor2DOptions { WidthPx = 440, HeightPx = 300, DarkTheme = true, SelectedElementId = null });
        using SKBitmap bmp2DLightSel = OffscreenViewportRenderer.RenderEditor2DBitmap(
            editor2D, frontDef, vm.Personalization.Manager.ListElements(), contour,
            new RenderEditor2DOptions { WidthPx = 440, HeightPx = 300, DarkTheme = false, SelectedElementId = txt.Id });

        CountDifferentPixels(bmp2DDarkSel, bmp2DLightSel).Should().BeGreaterThan(5000);
        CountDifferentPixels(bmp2DDarkSel, bmp2DDarkNone).Should().BeGreaterThan(30, "gizmo verde de seleção deve aparecer sobre o elemento ativo");

        // Réguas top (y in [2..16]) e esquerda (x in [2..16]) devem conter marcações/rótulos além do fundo da régua
        SKColor rulerBgDark = new(0x1E, 0x23, 0x2B);
        int topRulerInk = 0;
        for (int y = 2; y <= 16; y++)
            for (int x = 22; x < 436; x++)
                if (bmp2DDarkSel.GetPixel(x, y) != rulerBgDark) topRulerInk++;
        topRulerInk.Should().BeGreaterThan(40, "régua horizontal 2D de 18 px deve renderizar traços e valores em mm");

        int leftRulerInk = 0;
        for (int y = 22; y < 296; y++)
            for (int x = 2; x <= 16; x++)
                if (bmp2DDarkSel.GetPixel(x, y) != rulerBgDark) leftRulerInk++;
        leftRulerInk.Should().BeGreaterThan(30, "régua vertical 2D de 18 px deve renderizar traços e valores em mm");

        // Cores exatas das 6 camadas CNC
        OffscreenViewportRenderer.ResolveCncLayerColor(CncOperationType.CutExternal).Should().Be(new SKColor(0x00, 0x00, 0x00));
        OffscreenViewportRenderer.ResolveCncLayerColor(CncOperationType.CutInternal).Should().Be(new SKColor(0xB7, 0x1C, 0x1C));
        OffscreenViewportRenderer.ResolveCncLayerColor(CncOperationType.EngraveVector).Should().Be(new SKColor(0x0D, 0x47, 0xA1));
        OffscreenViewportRenderer.ResolveCncLayerColor(CncOperationType.EngraveFill).Should().Be(new SKColor(0x6A, 0x1B, 0x9A));
        OffscreenViewportRenderer.ResolveCncLayerColor(CncOperationType.Mark).Should().Be(new SKColor(0x1B, 0x5E, 0x20));
        OffscreenViewportRenderer.ResolveCncLayerColor(CncOperationType.EngraveRaster).Should().Be(new SKColor(0x37, 0x47, 0x4F));
    }
}
