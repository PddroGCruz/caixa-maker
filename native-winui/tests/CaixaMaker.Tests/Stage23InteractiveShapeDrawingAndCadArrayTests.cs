using System.Xml.Linq;
using CaixaMaker.App.Editor2D;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Cam.Raster;
using CaixaMaker.Cam.Svg;
using CaixaMaker.Cam.Text;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage23InteractiveShapeDrawingAndCadArrayTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly AppPaths _paths;

    public Stage23InteractiveShapeDrawingAndCadArrayTests()
    {
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_Stage23Tests_" + Guid.NewGuid().ToString("N")[..10]);
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
            // Melhor esforço na limpeza do diretório temporário.
        }
    }

    private static (PersonalizationWorkspaceViewModel Vm, SurfaceDefinition FrontSurface, SurfaceCanvas2DController Controller) CreateEditorRig()
    {
        var calc = new BoxCalculator(3.0, 220.0, 160.0, 100.0);
        IBoxModelDefinition model = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var vm = new PersonalizationWorkspaceViewModel(calc, model);

        SurfaceDefinition front = vm.ActiveSurface!;
        var controller = new SurfaceCanvas2DController();
        controller.FitToView(front, 800, 600);
        return (vm, front, controller);
    }

    [Fact]
    public void ActiveToolMode_SetAndProperties_ShouldSynchronizeAllBooleans()
    {
        (PersonalizationWorkspaceViewModel vm, _, _) = CreateEditorRig();

        vm.ActiveToolMode.Should().Be(Canvas2DToolMode.Select);
        vm.IsSelectToolActive.Should().BeTrue();
        vm.IsDrawRectangleToolActive.Should().BeFalse();

        vm.SetActiveToolModeCommand.Execute(Canvas2DToolMode.DrawCircle);
        vm.ActiveToolMode.Should().Be(Canvas2DToolMode.DrawCircle);
        vm.IsSelectToolActive.Should().BeFalse();
        vm.IsDrawCircleToolActive.Should().BeTrue();

        vm.SetActiveToolMode(Canvas2DToolMode.DrawSlot);
        vm.IsDrawSlotToolActive.Should().BeTrue();

        vm.SetActiveToolMode(Canvas2DToolMode.DrawPolygon);
        vm.IsDrawPolygonToolActive.Should().BeTrue();

        vm.SetActiveToolMode(Canvas2DToolMode.DrawCrosshair);
        vm.IsDrawCrosshairToolActive.Should().BeTrue();

        vm.SetActiveToolMode(Canvas2DToolMode.Select);
        vm.IsSelectToolActive.Should().BeTrue();
    }

    [Theory]
    [InlineData(Canvas2DToolMode.DrawRectangle, GeometricShapeKind.Rectangle, OperationMode.Cut)]
    [InlineData(Canvas2DToolMode.DrawCircle, GeometricShapeKind.Circle, OperationMode.Cut)]
    [InlineData(Canvas2DToolMode.DrawSlot, GeometricShapeKind.Slot, OperationMode.Cut)]
    [InlineData(Canvas2DToolMode.DrawPolygon, GeometricShapeKind.Polygon, OperationMode.Cut)]
    [InlineData(Canvas2DToolMode.DrawCrosshair, GeometricShapeKind.Crosshair, OperationMode.Engrave)]
    public void ClickAndDrag_AllFivePrimitives_ShouldCreateSelectRevertToSelectAndSupportUndoRedo(
        Canvas2DToolMode tool,
        GeometricShapeKind expectedKind,
        OperationMode expectedOp)
    {
        (PersonalizationWorkspaceViewModel vm, _, SurfaceCanvas2DController controller) = CreateEditorRig();

        vm.SetActiveToolMode(tool);
        (double pxStartX, double pxStartY) = controller.MmToPx(40.0, 20.0);
        (double pxEndX, double pxEndY) = controller.MmToPx(72.0, 40.0);

        controller.OnPointerPressed(vm, pxStartX, pxStartY);
        controller.IsDrawingShape.Should().BeTrue();
        vm.IsInteractiveTransformActive.Should().BeTrue();

        bool moved = controller.OnPointerMoved(vm, pxEndX, pxEndY);
        moved.Should().BeTrue();

        bool committed = controller.OnPointerReleased(vm);
        committed.Should().BeTrue();

        controller.IsDrawingShape.Should().BeFalse();
        vm.IsInteractiveTransformActive.Should().BeFalse();
        vm.ActiveToolMode.Should().Be(Canvas2DToolMode.Select);
        controller.ActiveToolMode.Should().Be(Canvas2DToolMode.Select);

        ShapePersonalizationElement created = vm.SelectedElement.Should().BeOfType<ShapePersonalizationElement>().Subject;
        created.ShapeSpec!.Kind.Should().Be(expectedKind);
        created.OperationMode.Should().Be(expectedOp);
        vm.ElementsOnActiveSurface.Should().ContainSingle(e => e.Id == created.Id);

        if (expectedKind == GeometricShapeKind.Rectangle)
        {
            created.WidthMm.Should().BeApproximately(32.0, 0.2);
            created.HeightMm.Should().BeApproximately(20.0, 0.2);
        }
        else if (expectedKind == GeometricShapeKind.Slot)
        {
            created.ShapeSpec.WidthMm.Should().BeApproximately(32.0, 0.2);
            created.ShapeSpec.HeightMm.Should().BeApproximately(20.0, 0.2);
        }
        else
        {
            double expectedDiameter = Math.Round(2.0 * double.Hypot(32.0, 20.0), 1);
            created.ShapeSpec.DiameterMm.Should().BeApproximately(expectedDiameter, 0.2);
        }

        // Undo em 1 passo remove a forma criada; Redo restaura.
        vm.UndoCount.Should().Be(1);
        vm.Undo();
        vm.ElementsOnActiveSurface.Should().BeEmpty();

        vm.Redo();
        vm.ElementsOnActiveSurface.Should().ContainSingle();
    }

    [Fact]
    public void ClickAndDrag_RectangleWithShift_ShouldLockSquare_AndClickOnlyShouldApplyDefaultDimensions()
    {
        (PersonalizationWorkspaceViewModel vm, _, SurfaceCanvas2DController controller) = CreateEditorRig();

        // 1. Shift durante arraste de retângulo trava quadrado perfeito
        vm.SetActiveToolMode(Canvas2DToolMode.DrawRectangle);
        (double sPxX, double sPxY) = controller.MmToPx(50.0, 35.0);
        (double ePxX, double ePxY) = controller.MmToPx(85.0, 48.0); // dx = 35, dy = 13 -> quadrado 35x35

        controller.OnPointerPressed(vm, sPxX, sPxY);
        controller.OnPointerMoved(vm, ePxX, ePxY, shiftHeld: true);
        controller.OnPointerReleased(vm);

        ShapePersonalizationElement square = vm.SelectedElement.Should().BeOfType<ShapePersonalizationElement>().Subject;
        square.ShapeSpec!.WidthMm.Should().BeApproximately(35.0, 0.2);
        square.ShapeSpec.HeightMm.Should().BeApproximately(35.0, 0.2);

        // 2. Clique curto (< 1.0 mm) aplica dimensões padrão centradas no clique
        vm.SetActiveToolMode(Canvas2DToolMode.DrawCircle);
        (double cPxX, double cPxY) = controller.MmToPx(120.0, 70.0);
        controller.OnPointerPressed(vm, cPxX, cPxY);
        controller.OnPointerReleased(vm);

        ShapePersonalizationElement defaultCircle = vm.SelectedElement.Should().BeOfType<ShapePersonalizationElement>().Subject;
        defaultCircle.ShapeSpec!.Kind.Should().Be(GeometricShapeKind.Circle);
        defaultCircle.ShapeSpec.DiameterMm.Should().BeApproximately(10.0, 1e-4);
        defaultCircle.CenterMm().X.Should().BeApproximately(120.0, 0.2);
        defaultCircle.CenterMm().Y.Should().BeApproximately(70.0, 0.2);
    }

    [Fact]
    public void CancelDrawing_ViaEscapeOrRightClick_ShouldDiscardShapeAndNotPolluteUndo()
    {
        (PersonalizationWorkspaceViewModel vm, _, SurfaceCanvas2DController controller) = CreateEditorRig();

        // Cancelamento via Esc
        vm.SetActiveToolMode(Canvas2DToolMode.DrawRectangle);
        (double p1x, double p1y) = controller.MmToPx(30.0, 25.0);
        (double p2x, double p2y) = controller.MmToPx(65.0, 55.0);
        controller.OnPointerPressed(vm, p1x, p1y);
        controller.OnPointerMoved(vm, p2x, p2y);
        vm.ElementsOnActiveSurface.Should().HaveCount(1);

        bool cancelledEsc = controller.CancelActiveDrawing(vm);
        cancelledEsc.Should().BeTrue();
        controller.IsDrawingShape.Should().BeFalse();
        vm.IsInteractiveTransformActive.Should().BeFalse();
        vm.ActiveToolMode.Should().Be(Canvas2DToolMode.Select);
        vm.ElementsOnActiveSurface.Should().BeEmpty();
        vm.UndoCount.Should().Be(0);

        // Cancelamento via Botão Direito
        vm.SetActiveToolMode(Canvas2DToolMode.DrawPolygon);
        controller.OnPointerPressed(vm, p1x, p1y);
        controller.OnPointerMoved(vm, p2x, p2y);
        controller.OnPointerPressed(vm, p2x, p2y, Editor2DMouseButton.Right);

        controller.IsDrawingShape.Should().BeFalse();
        vm.IsInteractiveTransformActive.Should().BeFalse();
        vm.ActiveToolMode.Should().Be(Canvas2DToolMode.Select);
        vm.ElementsOnActiveSurface.Should().BeEmpty();
        vm.UndoCount.Should().Be(0);
    }

    [Fact]
    public void MagneticSnapping_ToOriginAndElements_AndStaticCachePreservationWithLiveBadgeRender()
    {
        (PersonalizationWorkspaceViewModel vm, SurfaceDefinition front, SurfaceCanvas2DController controller) = CreateEditorRig();

        // Adiciona elemento prévio estático para popular o Editor2DStaticCache
        ShapePersonalizationElement anchorShape = vm.AddShape(GeometricShapeKind.Circle);
        vm.Manager.MoveElement(anchorShape.Id, 20.0, 20.0);
        vm.SelectElement(null);

        using SKBitmap warmBmp = OffscreenViewportRenderer.RenderEditor2DBitmap(
            controller,
            front,
            vm.AllElements,
            null,
            new RenderEditor2DOptions { WidthPx = 800, HeightPx = 600 });
        warmBmp.Width.Should().Be(800);

        // Captura a contagem de rebuilds do cache estático antes do desenho interativo
        int rebuildsAfterWarmup = controller.StaticRenderCacheRebuildCount;
        rebuildsAfterWarmup.Should().Be(1);

        // Inicia desenho próximo a (0, 0) dentro da tolerância de 1.5 mm
        vm.SetActiveToolMode(Canvas2DToolMode.DrawRectangle);
        (double nearZeroPxX, double nearZeroPxY) = controller.MmToPx(0.8, 0.9);
        controller.OnPointerPressed(vm, nearZeroPxX, nearZeroPxY);

        controller.SnapGuideXActive.Should().BeTrue();
        controller.SnapGuideYActive.Should().BeTrue();
        controller.SnapGuideX.Should().Be(0.0);
        controller.SnapGuideY.Should().Be(0.0);

        // Primeiro render com a forma ativa selecionada pode isolar o estático anterior (anchorShape),
        // e o segundo arraste contínuo deve manter 100% cache hit sem reconstruir o Editor2DStaticCache!
        using (SKBitmap firstDrawBmp = OffscreenViewportRenderer.RenderEditor2DBitmap(
            controller,
            front,
            vm.AllElements,
            null,
            new RenderEditor2DOptions
            {
                WidthPx = 800,
                HeightPx = 600,
                SelectedElementId = vm.SelectedElementId,
                ShowLiveDimensionBadge = true,
            }))
        {
            firstDrawBmp.Width.Should().Be(800);
        }

        int rebuildsDuringDraw = controller.StaticRenderCacheRebuildCount;

        // Arrasta até perto do centro do anchorShape (25, 25) -> snap magnético!
        (double nearAnchorPxX, double nearAnchorPxY) = controller.MmToPx(25.6, 24.4);
        controller.OnPointerMoved(vm, nearAnchorPxX, nearAnchorPxY);
        controller.SnapGuideX.Should().BeApproximately(anchorShape.CenterMm().X, 1e-4);
        controller.SnapGuideY.Should().BeApproximately(anchorShape.CenterMm().Y, 1e-4);

        // Renderiza durante o arraste contínuo: badge ao vivo + guias #00E5FF sem invalidar Editor2DStaticCache
        using SKBitmap liveBmp = OffscreenViewportRenderer.RenderEditor2DBitmap(
            controller,
            front,
            vm.AllElements,
            null,
            new RenderEditor2DOptions
            {
                WidthPx = 800,
                HeightPx = 600,
                SelectedElementId = vm.SelectedElementId,
                ShowLiveDimensionBadge = true,
            });
        liveBmp.Width.Should().Be(800);
        controller.StaticRenderCacheRebuildCount.Should().Be(rebuildsDuringDraw);

        // Verifica formatação do badge de cotas ao vivo para as primitivas e texto
        var rectEl = vm.SelectedElement.Should().BeOfType<ShapePersonalizationElement>().Subject;
        OffscreenViewportRenderer.FormatLiveDimensionBadgeText(rectEl).Should().StartWith("W: ");
        OffscreenViewportRenderer.FormatLiveDimensionBadgeText(anchorShape).Should().Contain("Ø 10.0 mm");
        OffscreenViewportRenderer.FormatLiveDimensionBadgeText(
            new ShapePersonalizationElement(new GeometricShapeSpec { Kind = GeometricShapeKind.Slot, WidthMm = 24.0, HeightMm = 8.0 }))
            .Should().Be("Slot 24.0 × 8.0 mm");
        OffscreenViewportRenderer.FormatLiveDimensionBadgeText(
            new ShapePersonalizationElement(new GeometricShapeSpec { Kind = GeometricShapeKind.Polygon, Sides = 6, DiameterMm = 12.0 }))
            .Should().Be("6 lados • Ø 12.0 mm");
        OffscreenViewportRenderer.FormatLiveDimensionBadgeText(
            new ShapePersonalizationElement(new GeometricShapeSpec { Kind = GeometricShapeKind.Crosshair, DiameterMm = 10.0 })
            { XMm = 45.0, YMm = 25.0 })
            .Should().Be("Centro (50.0, 30.0) mm");

        controller.OnPointerReleased(vm);
    }

    [Fact]
    public void MirrorFourCornersSelectedElement_ShouldCreateSymmetricClonesAndUndoInSingleStep()
    {
        (PersonalizationWorkspaceViewModel vm, SurfaceDefinition front, _) = CreateEditorRig();

        ShapePersonalizationElement hole = vm.AddShape(GeometricShapeKind.Circle, new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Circle,
            DiameterMm = 4.0,
        });
        // Posiciona no canto superior esquerdo (centro em 25, 25 numa chapa 220x100)
        vm.Manager.MoveElement(hole.Id, 23.0, 23.0);
        vm.SelectElement(hole.Id);
        vm.FlushHistoryCoalescing();

        int undoBeforeMirror = vm.UndoCount;
        vm.MirrorFourCornersCommand.Execute(null);

        vm.UndoCount.Should().Be(undoBeforeMirror + 1);
        vm.ElementsOnActiveSurface.Should().HaveCount(4);

        double cx = front.WidthMm * 0.5;
        double cy = front.HeightMm * 0.5;
        double dx = Math.Abs(25.0 - cx);
        double dy = Math.Abs(25.0 - cy);

        List<Point2D> centers = vm.ElementsOnActiveSurface.Select(e => e.CenterMm()).ToList();
        centers.Should().Contain(p => Math.Abs(p.X - (cx - dx)) < 0.1 && Math.Abs(p.Y - (cy - dy)) < 0.1);
        centers.Should().Contain(p => Math.Abs(p.X - (cx + dx)) < 0.1 && Math.Abs(p.Y - (cy - dy)) < 0.1);
        centers.Should().Contain(p => Math.Abs(p.X - (cx - dx)) < 0.1 && Math.Abs(p.Y - (cy + dy)) < 0.1);
        centers.Should().Contain(p => Math.Abs(p.X - (cx + dx)) < 0.1 && Math.Abs(p.Y - (cy + dy)) < 0.1);

        // Chamar novamente não deve duplicar elementos já existentes (tolerância 0,1 mm)
        vm.MirrorFourCornersSelectedElement();
        vm.ElementsOnActiveSurface.Should().HaveCount(4);

        // Undo atômico remove todos os 3 clones gerados de uma só vez
        vm.Undo();
        vm.ElementsOnActiveSurface.Should().ContainSingle(e => e.Id == hole.Id);
    }

    [Fact]
    public void CreateLinearArraySelectedElement_ShouldReplicateValidateSatAndUndoInSingleStep()
    {
        (PersonalizationWorkspaceViewModel vm, _, _) = CreateEditorRig();

        ShapePersonalizationElement slot = vm.AddShape(GeometricShapeKind.Slot, new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Slot,
            WidthMm = 14.0,
            HeightMm = 5.0,
        });
        vm.Manager.MoveElement(slot.Id, 30.0, 20.0);
        vm.SelectElement(slot.Id);
        vm.FlushHistoryCoalescing();

        vm.ArrayCount = 4;
        vm.ArrayStepXMm = 20.0;
        vm.ArrayStepYMm = 0.0;

        int undoBeforeArray = vm.UndoCount;
        vm.CreateLinearArrayCommand.Execute(null);

        vm.UndoCount.Should().Be(undoBeforeArray + 1);
        vm.ElementsOnActiveSurface.Should().HaveCount(4);
        vm.ElementsOnActiveSurface.Select(e => e.XMm).Should().BeEquivalentTo(new[] { 30.0, 50.0, 70.0, 90.0 });
        vm.IsValidationValid.Should().BeTrue();

        // Grade que extrapola os limites da chapa deve acusar erro SAT imediatamente
        vm.ArrayCount = 5;
        vm.ArrayStepXMm = 50.0;
        vm.CreateLinearArraySelectedElement();
        vm.IsValidationValid.Should().BeFalse();
        vm.ValidationErrorCount.Should().BeGreaterThan(0);

        vm.Undo();
        vm.ElementsOnActiveSurface.Should().HaveCount(4);
        vm.IsValidationValid.Should().BeTrue();
    }

    [Fact]
    public void KeyboardShortcutGuard_AndStage23XamlViews_ShouldProtectTextInputsAndExposeControls()
    {
        var textBoxContext = new KeyboardFocusContext(FocusControlKind.TextBox);
        var numberBoxContext = new KeyboardFocusContext(FocusControlKind.NumberBox);
        var canvasContext = new KeyboardFocusContext(FocusControlKind.NonEditable);

        foreach (string toolCmd in new[]
        {
            DesktopCommandIds.ToolSelect,
            DesktopCommandIds.ToolDrawRectangle,
            DesktopCommandIds.ToolDrawCircle,
            DesktopCommandIds.ToolDrawSlot,
            DesktopCommandIds.ToolDrawPolygon,
            DesktopCommandIds.ToolDrawCrosshair,
            DesktopCommandIds.ToolCancel,
        })
        {
            KeyboardShortcutGuard.ShouldDispatchCommand(textBoxContext, toolCmd).Should().BeFalse();
            KeyboardShortcutGuard.ShouldDispatchCommand(numberBoxContext, toolCmd).Should().BeFalse();
            KeyboardShortcutGuard.ShouldDispatchCommand(canvasContext, toolCmd).Should().BeTrue();
        }

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string editorXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "CaixaMaker.App", "Controls", "Surface2DEditorControl.xaml"));
        string leftXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "CaixaMaker.App", "Views", "Areas", "PersonalizationAreaLeftPanel.xaml"));
        string rightXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "CaixaMaker.App", "Views", "Areas", "PersonalizationAreaRightPanel.xaml"));

        XDocument.Parse(editorXaml).Should().NotBeNull();
        XDocument.Parse(leftXaml).Should().NotBeNull();
        XDocument.Parse(rightXaml).Should().NotBeNull();

        editorXaml.Should().Contain("DrawToolStripBorder").And.Contain("ToolDrawRectBtn").And.Contain("ToolDrawCrossBtn");
        leftXaml.Should().Contain("LeftToolSelectBtn").And.Contain("LeftToolDrawCircleBtn").And.Contain("AddRectangleShapeBtn");
        rightXaml.Should().Contain("CadRepetitionSection").And.Contain("MirrorFourCornersBtn").And.Contain("CreateLinearArrayBtn");
    }
}
