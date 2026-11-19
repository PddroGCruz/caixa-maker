using System.Diagnostics;
using System.Xml.Linq;
using CaixaMaker.App.Editor2D;
using CaixaMaker.App.Rendering;
using CaixaMaker.App.Services;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using CaixaMaker.Core.Storage;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage24MultiSelectionAndMarqueeTests : IDisposable
{
    private readonly string _sandboxRoot;

    public Stage24MultiSelectionAndMarqueeTests()
    {
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "CaixaMaker_Stage24Tests_" + Guid.NewGuid().ToString("N")[..10]);
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
            // Melhor esforço na limpeza do diretório temporário.
        }
    }

    private static (PersonalizationWorkspaceViewModel Vm, SurfaceDefinition Front, SurfaceCanvas2DController Controller) CreateRigWithThreeShapes()
    {
        var calc = new BoxCalculator(3.0, 240.0, 160.0, 110.0);
        IBoxModelDefinition model = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var vm = new PersonalizationWorkspaceViewModel(calc, model);

        ShapePersonalizationElement elA = vm.AddShape(GeometricShapeKind.Rectangle, new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Rectangle,
            WidthMm = 20.0,
            HeightMm = 14.0,
        });
        vm.Manager.MoveElement(elA.Id, 25.0, 22.0);

        ShapePersonalizationElement elB = vm.AddShape(GeometricShapeKind.Circle, new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Circle,
            DiameterMm = 12.0,
        });
        vm.Manager.MoveElement(elB.Id, 80.0, 24.0);

        ShapePersonalizationElement elC = vm.AddShape(GeometricShapeKind.Slot, new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Slot,
            WidthMm = 24.0,
            HeightMm = 8.0,
        });
        vm.Manager.MoveElement(elC.Id, 140.0, 26.0);

        vm.SelectSingleElement(null);
        vm.FlushHistoryCoalescing();

        SurfaceDefinition front = vm.ActiveSurface!;
        var controller = new SurfaceCanvas2DController();
        controller.FitToView(front, 840, 540);
        return (vm, front, controller);
    }

    [Fact]
    public void ShiftClick_PreservesChronologicalOrder_AcrossAdditionsRemovalsAndSurfaceSwitch()
    {
        (PersonalizationWorkspaceViewModel vm, _, SurfaceCanvas2DController controller) = CreateRigWithThreeShapes();
        IReadOnlyList<PersonalizationElement> items = vm.ElementsOnActiveSurface.ToList();
        PersonalizationElement elA = items[0];
        PersonalizationElement elB = items[1];
        PersonalizationElement elC = items[2];

        // Seleciona na ordem C -> A -> B via Shift+Clique no canvas
        (double cx, double cy) = controller.MmToPx(elC.CenterMm().X, elC.CenterMm().Y);
        controller.OnPointerPressed(vm, cx, cy, Editor2DMouseButton.Left, shiftHeld: true);
        controller.OnPointerReleased(vm);

        (double ax, double ay) = controller.MmToPx(elA.CenterMm().X, elA.CenterMm().Y);
        controller.OnPointerPressed(vm, ax, ay, Editor2DMouseButton.Left, shiftHeld: true);
        controller.OnPointerReleased(vm);

        (double bx, double by) = controller.MmToPx(elB.CenterMm().X, elB.CenterMm().Y);
        controller.OnPointerPressed(vm, bx, by, Editor2DMouseButton.Left, shiftHeld: true);
        controller.OnPointerReleased(vm);

        vm.SelectedElementIds.Should().Equal(elC.Id, elA.Id, elB.Id);
        vm.SelectedElementCount.Should().Be(3);
        vm.HasMultipleSelection.Should().BeTrue();
        vm.HasSelectedElement.Should().BeTrue();
        vm.FirstSelectedElementId.Should().Be(elC.Id);
        vm.LastSelectedElementId.Should().Be(elB.Id);
        vm.SelectedElement!.Id.Should().Be(elC.Id);

        // Shift+Clique em A remove apenas A preservando a ordem [C, B]
        controller.OnPointerPressed(vm, ax, ay, Editor2DMouseButton.Left, shiftHeld: true);
        controller.OnPointerReleased(vm);

        vm.SelectedElementIds.Should().Equal(elC.Id, elB.Id);
        vm.SelectedElementCount.Should().Be(2);
        vm.FirstSelectedElementId.Should().Be(elC.Id);
        vm.LastSelectedElementId.Should().Be(elB.Id);

        // Trocar de chapa limpa a seleção múltipla
        vm.SelectSurface(SurfaceId.Back);
        vm.SelectedElementIds.Should().BeEmpty();
        vm.SelectedElementCount.Should().Be(0);
        vm.HasMultipleSelection.Should().BeFalse();
        vm.SelectedElement.Should().BeNull();
    }

    [Fact]
    public void MarqueeSelection_CapturesIntersectingElements_AppendsWithShift_CancelsWithEsc_AndClearsOnEmptyClick()
    {
        (PersonalizationWorkspaceViewModel vm, _, SurfaceCanvas2DController controller) = CreateRigWithThreeShapes();
        IReadOnlyList<PersonalizationElement> items = vm.ElementsOnActiveSurface.ToList();
        PersonalizationElement elA = items[0];
        PersonalizationElement elB = items[1];
        PersonalizationElement elC = items[2];

        // 1. Marquee no fundo vazio envolvendo A e B (deixando C fora)
        (double mStartX, double mStartY) = controller.MmToPx(15.0, 12.0);
        (double mEndX, double mEndY) = controller.MmToPx(105.0, 48.0);

        controller.OnPointerPressed(vm, mStartX, mStartY);
        controller.IsMarqueeSelecting.Should().BeTrue();
        controller.OnPointerMoved(vm, mEndX, mEndY);
        controller.OnPointerReleased(vm);

        controller.IsMarqueeSelecting.Should().BeFalse();
        vm.SelectedElementIds.Should().Equal(elA.Id, elB.Id);

        // 2. Shift + Marquee sobre C acrescenta C sem perder [A, B]
        (double mCStartX, double mCStartY) = controller.MmToPx(130.0, 15.0);
        (double mCEndX, double mCEndY) = controller.MmToPx(175.0, 42.0);
        controller.OnPointerPressed(vm, mCStartX, mCStartY, Editor2DMouseButton.Left, shiftHeld: true);
        controller.OnPointerMoved(vm, mCEndX, mCEndY, shiftHeld: true);
        controller.OnPointerReleased(vm, shiftHeld: true);

        vm.SelectedElementIds.Should().Equal(elA.Id, elB.Id, elC.Id);

        // 3. Esc durante Marquee cancela sem alterar seleção existente
        (double emptyX, double emptyY) = controller.MmToPx(180.0, 75.0);
        (double emptyEndX, double emptyEndY) = controller.MmToPx(200.0, 88.0);
        controller.OnPointerPressed(vm, emptyX, emptyY);
        controller.OnPointerMoved(vm, emptyEndX, emptyEndY);
        controller.IsMarqueeSelecting.Should().BeTrue();
        controller.CancelActiveMarquee().Should().BeTrue();
        controller.IsMarqueeSelecting.Should().BeFalse();
        vm.SelectedElementIds.Should().Equal(elA.Id, elB.Id, elC.Id);

        // 4. Clique vazio (< 1.0 mm) sem Shift limpa seleção
        controller.OnPointerPressed(vm, emptyX, emptyY);
        controller.OnPointerReleased(vm);
        vm.SelectedElementIds.Should().BeEmpty();
        vm.HasSelectedElement.Should().BeFalse();
    }

    [Fact]
    public void GroupDrag_MovesAllSelectedElementsAtomically_PreservesStaticCache_AndCollapsesOnClickWithoutDrag()
    {
        (PersonalizationWorkspaceViewModel vm, SurfaceDefinition front, SurfaceCanvas2DController controller) = CreateRigWithThreeShapes();
        IReadOnlyList<PersonalizationElement> items = vm.ElementsOnActiveSurface.ToList();
        PersonalizationElement elA = items[0];
        PersonalizationElement elB = items[1];
        PersonalizationElement elC = items[2];

        vm.SetSelectedElements([elB.Id, elA.Id]);
        double origAx = elA.XMm;
        double origAy = elA.YMm;
        double origBx = elB.XMm;
        double origBy = elB.YMm;
        double origCx = elC.XMm;

        // Warmup do Editor2DStaticCache com seleção múltipla
        using (SKBitmap warmup = OffscreenViewportRenderer.RenderEditor2DBitmap(
            controller,
            front,
            vm.AllElements,
            null,
            new RenderEditor2DOptions
            {
                WidthPx = 840,
                HeightPx = 540,
                SelectedElementId = vm.SelectedElementId,
                SelectedElementIds = vm.SelectedElementIds,
            }))
        {
            warmup.Width.Should().Be(840);
        }

        int rebuildsBeforeGroupDrag = controller.StaticRenderCacheRebuildCount;
        int undoBefore = vm.UndoCount;

        // Pressiona em A (já pertencente ao grupo [B, A]): NÃO colapsa no Pressed!
        Point2D centerA = elA.CenterMm();
        (double startPxX, double startPxY) = controller.MmToPx(centerA.X, centerA.Y);
        controller.OnPointerPressed(vm, startPxX, startPxY);

        vm.HasMultipleSelection.Should().BeTrue();
        controller.IsDraggingGroup.Should().BeTrue();

        // Arrasta delta (+12 mm, +28 mm) fora da zona de snap (1.5 mm) de elC e safe.Center
        (double endPxX, double endPxY) = controller.MmToPx(centerA.X + 12.0, centerA.Y + 28.0);
        controller.OnPointerMoved(vm, endPxX, endPxY).Should().BeTrue();

        // Renderiza overlay durante arraste em grupo preservando 100% Editor2DStaticCache
        using (SKBitmap dragBmp = OffscreenViewportRenderer.RenderEditor2DBitmap(
            controller,
            front,
            vm.AllElements,
            null,
            new RenderEditor2DOptions
            {
                WidthPx = 840,
                HeightPx = 540,
                SelectedElementId = vm.SelectedElementId,
                SelectedElementIds = vm.SelectedElementIds,
                ShowLiveDimensionBadge = true,
            }))
        {
            dragBmp.Width.Should().Be(840);
        }
        controller.StaticRenderCacheRebuildCount.Should().Be(rebuildsBeforeGroupDrag);

        bool committed = controller.OnPointerReleased(vm);
        committed.Should().BeTrue();
        controller.IsDraggingGroup.Should().BeFalse();

        // Todos os selecionados moveram pelo mesmo delta; não-selecionado C ficou intacto
        elA.XMm.Should().BeApproximately(origAx + 12.0, 0.2);
        elA.YMm.Should().BeApproximately(origAy + 28.0, 0.2);
        elB.XMm.Should().BeApproximately(origBx + 12.0, 0.2);
        elB.YMm.Should().BeApproximately(origBy + 28.0, 0.2);
        elC.XMm.Should().BeApproximately(origCx, 1e-4);

        // 1 único passo atômico de Undo/Redo reverte e reaplica ambos
        vm.UndoCount.Should().Be(undoBefore + 1);
        vm.Undo();
        vm.Manager.GetElement(elA.Id).XMm.Should().BeApproximately(origAx, 1e-4);
        vm.Manager.GetElement(elB.Id).XMm.Should().BeApproximately(origBx, 1e-4);

        vm.Redo();
        vm.Manager.GetElement(elA.Id).XMm.Should().BeApproximately(origAx + 12.0, 0.2);
        vm.Manager.GetElement(elB.Id).XMm.Should().BeApproximately(origBx + 12.0, 0.2);

        // Clique sem arraste (< 1.0 mm) sobre elemento pertencente à seleção múltipla colapsa para apenas esse item
        vm.SetSelectedElements([elB.Id, elA.Id]);
        Point2D curCenterA = vm.Manager.GetElement(elA.Id).CenterMm();
        (double clickPxX, double clickPxY) = controller.MmToPx(curCenterA.X, curCenterA.Y);
        int undoBeforeCollapseClick = vm.UndoCount;

        controller.OnPointerPressed(vm, clickPxX, clickPxY);
        controller.OnPointerReleased(vm);

        vm.SelectedElementIds.Should().Equal(elA.Id);
        vm.HasMultipleSelection.Should().BeFalse();
        vm.UndoCount.Should().Be(undoBeforeCollapseClick);
    }

    [Fact]
    public void DeleteSelectedElements_AndSelectAll_AndTranslateSelectedElements_WorkAtomicallyWithShortcutGuard()
    {
        (PersonalizationWorkspaceViewModel vm, _, _) = CreateRigWithThreeShapes();

        // Ctrl+A protegido contra foco editável no KeyboardShortcutGuard
        var textCtx = new KeyboardFocusContext(FocusControlKind.TextBox);
        var numCtx = new KeyboardFocusContext(FocusControlKind.NumberBox);
        var canvasCtx = new KeyboardFocusContext(FocusControlKind.NonEditable);

        KeyboardShortcutGuard.ShouldDispatchCommand(textCtx, DesktopCommandIds.EditSelectAll).Should().BeFalse();
        KeyboardShortcutGuard.ShouldDispatchCommand(numCtx, DesktopCommandIds.EditSelectAll).Should().BeFalse();
        KeyboardShortcutGuard.ShouldDispatchCommand(canvasCtx, DesktopCommandIds.EditSelectAll).Should().BeTrue();

        // SelectAll seleciona os 3 elementos na ordem da chapa
        vm.SelectAllElementsOnActiveSurface();
        vm.SelectedElementCount.Should().Be(3);

        // TranslateSelectedElements move o grupo em 1 passo e valida SAT
        double[] origYs = vm.ElementsOnActiveSurface.Select(e => e.YMm).ToArray();
        int undoBeforeTranslate = vm.UndoCount;
        vm.TranslateSelectedElements(5.0, 15.0);
        vm.UndoCount.Should().Be(undoBeforeTranslate + 1);
        for (int i = 0; i < origYs.Length; i++)
        {
            vm.ElementsOnActiveSurface[i].YMm.Should().BeApproximately(origYs[i] + 15.0, 0.05);
        }

        // Excluir 2 selecionados remove ambos em 1 passo de Undo
        string id0 = vm.ElementsOnActiveSurface[0].Id;
        string id1 = vm.ElementsOnActiveSurface[1].Id;
        string id2 = vm.ElementsOnActiveSurface[2].Id;
        vm.SetSelectedElements([id0, id1]);

        int undoBeforeDelete = vm.UndoCount;
        vm.DeleteSelectedElements();
        vm.UndoCount.Should().Be(undoBeforeDelete + 1);
        vm.ElementsOnActiveSurface.Select(e => e.Id).Should().Equal(id2);
        vm.SelectedElementIds.Should().BeEmpty();

        vm.Undo();
        vm.ElementsOnActiveSurface.Should().HaveCount(3);
    }

    [Fact]
    public void MultiSelectionOverlay_AndMarqueeRender_KeepSub6MsBudget_AndXamlContainsMultiSelectionBanner()
    {
        (PersonalizationWorkspaceViewModel vm, SurfaceDefinition front, SurfaceCanvas2DController controller) = CreateRigWithThreeShapes();
        vm.SetSelectedElements(vm.ElementsOnActiveSurface.Take(2).Select(e => e.Id));

        OffscreenViewportRenderer.FormatGroupLiveDimensionBadgeText(2, 12.5, -4.0)
            .Should().Be("2 itens • Δ(+12.5, -4.0) mm");

        var info = new SKImageInfo(840, 540, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info)!;

        // Warmup inicial
        var opts = new RenderEditor2DOptions
        {
            WidthPx = 840,
            HeightPx = 540,
            DarkTheme = true,
            SelectedElementId = vm.SelectedElementId,
            SelectedElementIds = vm.SelectedElementIds,
            ShowLiveDimensionBadge = true,
        };
        OffscreenViewportRenderer.RenderEditor2DToCanvas(surface.Canvas, 840, 540, controller, front, vm.AllElements, null, opts);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 30; i++)
        {
            OffscreenViewportRenderer.RenderEditor2DToCanvas(surface.Canvas, 840, 540, controller, front, vm.AllElements, null, opts);
        }
        sw.Stop();
        (sw.Elapsed.TotalMilliseconds / 30.0).Should().BeLessThan(6.0);

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string rightXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "CaixaMaker.App", "Views", "Areas", "PersonalizationAreaRightPanel.xaml"));
        XDocument.Parse(rightXaml).Should().NotBeNull();
        rightXaml.Should().Contain("MultiSelectionBanner").And.Contain("MultiSelectionBannerText");
    }
}
