using System.Xml.Linq;
using CaixaMaker.App.ViewModels;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests;

public sealed class Stage25AlignmentAndDistributionTests
{
    private static (PersonalizationWorkspaceViewModel Vm, SurfaceDefinition Front) CreateRig()
    {
        var calc = new BoxCalculator(3.0, 240.0, 160.0, 110.0);
        IBoxModelDefinition model = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var vm = new PersonalizationWorkspaceViewModel(calc, model);
        return (vm, vm.ActiveSurface!);
    }

    [Theory]
    [InlineData(AlignmentAction.AlignLeft)]
    [InlineData(AlignmentAction.AlignCenterX)]
    [InlineData(AlignmentAction.AlignRight)]
    [InlineData(AlignmentAction.AlignTop)]
    [InlineData(AlignmentAction.AlignCenterY)]
    [InlineData(AlignmentAction.AlignBottom)]
    public void AlignmentAxes_WorkDeterministicallyAcrossAllFourReferenceModes(AlignmentAction action)
    {
        foreach (AlignmentReferenceMode refMode in Enum.GetValues<AlignmentReferenceMode>())
        {
            (PersonalizationWorkspaceViewModel vm, SurfaceDefinition front) = CreateRig();

            ShapePersonalizationElement el1 = vm.AddShape(GeometricShapeKind.Rectangle, new GeometricShapeSpec
            {
                Kind = GeometricShapeKind.Rectangle,
                WidthMm = 30.0,
                HeightMm = 16.0,
            });
            el1.Anchor = AnchorMode.TopLeft;
            vm.Manager.MoveElement(el1.Id, 25.0, 20.0);

            ShapePersonalizationElement el2 = vm.AddShape(GeometricShapeKind.Circle, new GeometricShapeSpec
            {
                Kind = GeometricShapeKind.Circle,
                DiameterMm = 12.0,
            });
            el2.Anchor = AnchorMode.Center;
            vm.Manager.MoveElement(el2.Id, 95.0, 48.0);

            ShapePersonalizationElement el3 = vm.AddShape(GeometricShapeKind.Slot, new GeometricShapeSpec
            {
                Kind = GeometricShapeKind.Slot,
                WidthMm = 24.0,
                HeightMm = 8.0,
            });
            vm.Manager.MoveElement(el3.Id, 160.0, 72.0);

            vm.SetSelectedElements([el1.Id, el2.Id, el3.Id]);
            vm.SelectedAlignmentReference = refMode;

            ElementBounds2D origB1 = ElementAlignmentService.GetElementBounds(el1);
            ElementBounds2D origB2 = ElementAlignmentService.GetElementBounds(el2);
            ElementBounds2D origB3 = ElementAlignmentService.GetElementBounds(el3);
            Rect2D safe = front.SafeAreaForMargin(vm.SafetyMarginMm);

            double expectedTarget = (refMode, action) switch
            {
                (AlignmentReferenceMode.SurfaceSafeArea, AlignmentAction.AlignLeft) => safe.XMm,
                (AlignmentReferenceMode.SurfaceSafeArea, AlignmentAction.AlignCenterX) => safe.Center.X,
                (AlignmentReferenceMode.SurfaceSafeArea, AlignmentAction.AlignRight) => safe.Right,
                (AlignmentReferenceMode.SurfaceSafeArea, AlignmentAction.AlignTop) => safe.YMm,
                (AlignmentReferenceMode.SurfaceSafeArea, AlignmentAction.AlignCenterY) => safe.Center.Y,
                (AlignmentReferenceMode.SurfaceSafeArea, AlignmentAction.AlignBottom) => safe.Bottom,

                (AlignmentReferenceMode.FirstSelected, AlignmentAction.AlignLeft) => origB1.MinX,
                (AlignmentReferenceMode.FirstSelected, AlignmentAction.AlignCenterX) => origB1.CenterX,
                (AlignmentReferenceMode.FirstSelected, AlignmentAction.AlignRight) => origB1.MaxX,
                (AlignmentReferenceMode.FirstSelected, AlignmentAction.AlignTop) => origB1.MinY,
                (AlignmentReferenceMode.FirstSelected, AlignmentAction.AlignCenterY) => origB1.CenterY,
                (AlignmentReferenceMode.FirstSelected, AlignmentAction.AlignBottom) => origB1.MaxY,

                (AlignmentReferenceMode.LastSelected, AlignmentAction.AlignLeft) => origB3.MinX,
                (AlignmentReferenceMode.LastSelected, AlignmentAction.AlignCenterX) => origB3.CenterX,
                (AlignmentReferenceMode.LastSelected, AlignmentAction.AlignRight) => origB3.MaxX,
                (AlignmentReferenceMode.LastSelected, AlignmentAction.AlignTop) => origB3.MinY,
                (AlignmentReferenceMode.LastSelected, AlignmentAction.AlignCenterY) => origB3.CenterY,
                (AlignmentReferenceMode.LastSelected, AlignmentAction.AlignBottom) => origB3.MaxY,

                (AlignmentReferenceMode.SelectionBounds, AlignmentAction.AlignLeft) => Math.Min(origB1.MinX, Math.Min(origB2.MinX, origB3.MinX)),
                (AlignmentReferenceMode.SelectionBounds, AlignmentAction.AlignCenterX) => (Math.Min(origB1.MinX, Math.Min(origB2.MinX, origB3.MinX)) + Math.Max(origB1.MaxX, Math.Max(origB2.MaxX, origB3.MaxX))) * 0.5,
                (AlignmentReferenceMode.SelectionBounds, AlignmentAction.AlignRight) => Math.Max(origB1.MaxX, Math.Max(origB2.MaxX, origB3.MaxX)),
                (AlignmentReferenceMode.SelectionBounds, AlignmentAction.AlignTop) => Math.Min(origB1.MinY, Math.Min(origB2.MinY, origB3.MinY)),
                (AlignmentReferenceMode.SelectionBounds, AlignmentAction.AlignCenterY) => (Math.Min(origB1.MinY, Math.Min(origB2.MinY, origB3.MinY)) + Math.Max(origB1.MaxY, Math.Max(origB2.MaxY, origB3.MaxY))) * 0.5,
                (AlignmentReferenceMode.SelectionBounds, AlignmentAction.AlignBottom) => Math.Max(origB1.MaxY, Math.Max(origB2.MaxY, origB3.MaxY)),
                _ => 0.0,
            };

            vm.AlignSelectedElements(action).Should().BeTrue();

            foreach (PersonalizationElement el in new PersonalizationElement[] { el1, el2, el3 })
            {
                ElementBounds2D b = ElementAlignmentService.GetElementBounds(el);
                double actual = action switch
                {
                    AlignmentAction.AlignLeft => b.MinX,
                    AlignmentAction.AlignCenterX => b.CenterX,
                    AlignmentAction.AlignRight => b.MaxX,
                    AlignmentAction.AlignTop => b.MinY,
                    AlignmentAction.AlignCenterY => b.CenterY,
                    AlignmentAction.AlignBottom => b.MaxY,
                    _ => 0.0,
                };
                actual.Should().BeApproximately(expectedTarget, 1e-3);
            }

            // Garantia estrita de imobilidade do elemento-âncora
            if (refMode == AlignmentReferenceMode.FirstSelected)
            {
                ElementAlignmentService.GetElementBounds(el1).Should().Be(origB1);
            }
            else if (refMode == AlignmentReferenceMode.LastSelected)
            {
                ElementAlignmentService.GetElementBounds(el3).Should().Be(origB3);
            }
        }
    }

    [Fact]
    public void RotatedElements_AndAnchorModes_AlignUsingTrueOrientedBoundingBox()
    {
        (PersonalizationWorkspaceViewModel vm, SurfaceDefinition front) = CreateRig();

        ShapePersonalizationElement rect45 = vm.AddShape(GeometricShapeKind.Rectangle, new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Rectangle,
            WidthMm = 30.0,
            HeightMm = 10.0,
        });
        rect45.Anchor = AnchorMode.TopLeft;
        rect45.RotationDeg = 45.0;
        vm.Manager.MoveElement(rect45.Id, 40.0, 35.0);

        ShapePersonalizationElement rect90 = vm.AddShape(GeometricShapeKind.Rectangle, new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Rectangle,
            WidthMm = 40.0,
            HeightMm = 12.0,
        });
        rect90.Anchor = AnchorMode.Center;
        rect90.RotationDeg = 90.0; // largura visual efetiva passa a ser 12 mm, altura visual 40 mm
        vm.Manager.MoveElement(rect90.Id, 110.0, 28.0);

        // 1. Alinha rect90 à esquerda da área útil da chapa: borda esquerda visual deve encostar em safe.XMm
        Rect2D safe = front.SafeAreaForMargin(vm.SafetyMarginMm);
        vm.SetSelectedElements([rect90.Id]);
        vm.SelectedAlignmentReference = AlignmentReferenceMode.SurfaceSafeArea;
        vm.AlignLeftCommand.Execute(null);

        ElementBounds2D b90 = ElementAlignmentService.GetElementBounds(rect90);
        b90.MinX.Should().BeApproximately(safe.XMm, 1e-3);
        b90.Width.Should().BeApproximately(12.0, 1e-3);
        b90.Height.Should().BeApproximately(40.0, 1e-3);
        rect90.Anchor.Should().Be(AnchorMode.Center);

        // 2. Alinha rect45 (âncora TopLeft, rotação 45°) à direita de rect90 usando FirstSelected ([rect90, rect45])
        vm.SetSelectedElements([rect90.Id, rect45.Id]);
        vm.SelectedAlignmentReference = AlignmentReferenceMode.FirstSelected;
        vm.AlignRightCommand.Execute(null);

        ElementBounds2D b45 = ElementAlignmentService.GetElementBounds(rect45);
        double expectedSpan45 = (30.0 + 10.0) / Math.Sqrt(2.0);
        b45.Width.Should().BeApproximately(expectedSpan45, 1e-3);
        b45.MaxX.Should().BeApproximately(b90.MaxX, 1e-3);
        rect45.Anchor.Should().Be(AnchorMode.TopLeft);
    }

    [Fact]
    public void DistributeHorizontalAndVertical_WithThreeAndFourElements_SpacesCentersEquidistantlyKeepingEndsFixed()
    {
        (PersonalizationWorkspaceViewModel vm, _) = CreateRig();

        var items = new List<ShapePersonalizationElement>(4);
        double[] initialCentersX = [30.0, 48.0, 72.0, 180.0];
        double[] initialCentersY = [25.0, 32.0, 66.0, 85.0];

        for (int i = 0; i < 4; i++)
        {
            ShapePersonalizationElement hole = vm.AddShape(GeometricShapeKind.Circle, new GeometricShapeSpec
            {
                Kind = GeometricShapeKind.Circle,
                DiameterMm = 8.0,
            });
            vm.Manager.MoveElement(hole.Id, initialCentersX[i] - 4.0, initialCentersY[i] - 4.0);
            items.Add(hole);
        }

        // Com apenas 2 itens selecionados, distribuição deve permanecer desabilitada
        vm.SetSelectedElements([items[0].Id, items[1].Id]);
        vm.CanDistributeSelectedElements.Should().BeFalse();
        vm.AlignSelectedElements(AlignmentAction.DistributeHorizontal).Should().BeFalse();

        // Distribuição com 3 elementos (centros em 30, 48, 180 -> passo 75 -> 30, 105, 180)
        vm.SetSelectedElements([items[0].Id, items[1].Id, items[3].Id]);
        vm.CanDistributeSelectedElements.Should().BeTrue();
        vm.DistributeHorizontalCommand.Execute(null);

        items[0].CenterMm().X.Should().BeApproximately(30.0, 1e-3);
        items[1].CenterMm().X.Should().BeApproximately(105.0, 1e-3);
        items[3].CenterMm().X.Should().BeApproximately(180.0, 1e-3);

        // Distribuição vertical com 4 elementos (centros Y de 25 a 85 -> passo 20 -> 25, 45, 65, 85)
        vm.SetSelectedElements(items.Select(e => e.Id));
        vm.DistributeVerticalCommand.Execute(null);

        items[0].CenterMm().Y.Should().BeApproximately(25.0, 1e-3);
        items[1].CenterMm().Y.Should().BeApproximately(45.0, 1e-3);
        items[2].CenterMm().Y.Should().BeApproximately(65.0, 1e-3);
        items[3].CenterMm().Y.Should().BeApproximately(85.0, 1e-3);
    }

    [Fact]
    public void ViewModel_UndoRedoPreservesSelectionOrder_TriggersSatValidation_AndRightPanelXamlExposesControls()
    {
        (PersonalizationWorkspaceViewModel vm, _) = CreateRig();

        ShapePersonalizationElement a = vm.AddShape(GeometricShapeKind.Rectangle, new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Rectangle,
            WidthMm = 12.0,
            HeightMm = 14.0,
        });
        vm.Manager.MoveElement(a.Id, 10.0, 40.0);

        ShapePersonalizationElement b = vm.AddShape(GeometricShapeKind.Rectangle, new GeometricShapeSpec
        {
            Kind = GeometricShapeKind.Rectangle,
            WidthMm = 40.0,
            HeightMm = 14.0,
        });
        vm.Manager.MoveElement(b.Id, 110.0, 55.0);

        // Com apenas 1 selecionado e referência FirstSelected, CanAlignSelectedElements é false
        vm.SetSelectedElements([a.Id]);
        vm.SelectedAlignmentReference = AlignmentReferenceMode.FirstSelected;
        vm.CanAlignSelectedElements.Should().BeFalse();

        vm.SetSelectedElements([a.Id, b.Id]);
        vm.CanAlignSelectedElements.Should().BeTrue();
        vm.RunValidation();
        vm.IsValidationValid.Should().BeTrue();
        vm.FlushHistoryCoalescing();

        double origBx = b.XMm;
        int undoBefore = vm.UndoCount;

        // Alinhar B (40 mm de largura) pela direita de A (X=10..22) desloca B para X=-18..22 (invade borda/zona proibida -> erro SAT)
        vm.AlignRightCommand.Execute(null);
        vm.UndoCount.Should().Be(undoBefore + 1);
        vm.IsValidationValid.Should().BeFalse();
        vm.ValidationErrorCount.Should().BeGreaterThan(0);

        // Undo reverte em 1 passo e preserva exatamente SelectedElementIds [a.Id, b.Id]
        vm.Undo();
        vm.SelectedElementIds.Should().Equal(a.Id, b.Id);
        vm.Manager.GetElement(b.Id).XMm.Should().BeApproximately(origBx, 1e-3);
        vm.IsValidationValid.Should().BeTrue();

        // Verifica estrutura XAML de PersonalizationAreaRightPanel
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string rightXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "CaixaMaker.App", "Views", "Areas", "PersonalizationAreaRightPanel.xaml"));
        XDocument.Parse(rightXaml).Should().NotBeNull();
        rightXaml.Should().Contain("AlignAndDistributeSection")
            .And.Contain("AlignmentReferenceCombo")
            .And.Contain("AlignLeftBtn")
            .And.Contain("AlignCenterXBtn")
            .And.Contain("AlignRightBtn")
            .And.Contain("AlignTopBtn")
            .And.Contain("AlignCenterYBtn")
            .And.Contain("AlignBottomBtn")
            .And.Contain("DistributeHorizontalBtn")
            .And.Contain("DistributeVerticalBtn");
    }
}
