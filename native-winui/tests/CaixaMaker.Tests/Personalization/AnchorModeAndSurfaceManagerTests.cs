using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Personalization;

public sealed class AnchorModeAndSurfaceManagerTests
{
    private static TextPersonalizationElement CreateSampleText(
        string content = "IFMAKER",
        SurfaceId panelId = SurfaceId.Front,
        double widthMm = 40.0,
        double heightMm = 12.0,
        AnchorMode anchor = AnchorMode.Absolute)
    {
        return new TextPersonalizationElement
        {
            Name = content,
            Content = content,
            PanelId = panelId,
            WidthMm = widthMm,
            HeightMm = heightMm,
            Anchor = anchor,
            Vectorized = true,
            VectorPaths =
            [
                new VectorPathItem
                {
                    D = $"M 0 0 L {widthMm} 0 L {widthMm} {heightMm} L 0 {heightMm} Z",
                    Operation = CncOperationType.EngraveVector,
                },
            ],
        };
    }

    [Fact]
    public void CrudOperations_AddUpdateMoveResizeRotateRepositionDuplicateRemove_WorkAsExpected()
    {
        var manager = new SurfaceManager(new BoxCalculator(3.0, 200.0, 150.0, 100.0));

        TextPersonalizationElement el = (TextPersonalizationElement)manager.AddElement(CreateSampleText());
        SurfaceDefinition front = manager.Surfaces.GetSurface(SurfaceId.Front);
        el.XMm.Should().BeApproximately(front.SafeRectMm.Center.X - 20.0, 1e-4);
        el.YMm.Should().BeApproximately(front.SafeRectMm.Center.Y - 6.0, 1e-4);

        manager.MoveElement(el.Id, 25.0, 30.0);
        el.XMm.Should().Be(25.0);
        el.YMm.Should().Be(30.0);
        el.RelX.Should().BeApproximately(45.0 / 200.0, 1e-6);
        el.RelY.Should().BeApproximately(36.0 / 100.0, 1e-6);

        manager.ResizeElement(el.Id, widthMm: 80.0);
        el.WidthMm.Should().BeApproximately(80.0, 1e-4);
        el.HeightMm.Should().BeApproximately(24.0, 1e-4);

        manager.RotateElement(el.Id, -45.0);
        el.RotationDeg.Should().BeApproximately(315.0, 1e-6);

        PersonalizationElement dupSame = manager.DuplicateElement(el.Id, offsetMm: 5.0);
        dupSame.PanelId.Should().Be(SurfaceId.Front);
        dupSame.XMm.Should().Be(30.0);
        dupSame.YMm.Should().Be(35.0);

        manager.RepositionElement(dupSame.Id, SurfaceId.Back, centerOnSurface: true);
        dupSame.PanelId.Should().Be(SurfaceId.Back);
        dupSame.XMm.Should().BeApproximately(manager.Surfaces.GetSurface(SurfaceId.Back).SafeRectMm.Center.X - 40.0, 1e-4);

        manager.RemoveElement(dupSame.Id).Should().BeTrue();
        manager.RemoveElement(dupSame.Id).Should().BeFalse();
        manager.ListElements().Should().ContainSingle(x => x.Id == el.Id);
    }

    [Theory]
    [InlineData(AnchorMode.Absolute)]
    [InlineData(AnchorMode.Relative)]
    [InlineData(AnchorMode.Center)]
    [InlineData(AnchorMode.TopLeft)]
    [InlineData(AnchorMode.TopRight)]
    [InlineData(AnchorMode.BottomLeft)]
    [InlineData(AnchorMode.BottomRight)]
    public void UpdateBoxCalculator_AllSevenAnchorModes_FollowExactPythonRules(AnchorMode mode)
    {
        var calcInitial = new BoxCalculator(3.0, 200.0, 150.0, 100.0);
        var manager = new SurfaceManager(calcInitial);

        // Posição inicial intencionalmente assimétrica dentro da safe_rect da face frontal
        var el = CreateSampleText(widthMm: 30.0, heightMm: 10.0, anchor: mode);
        manager.AddElement(el, xMm: 22.0, yMm: 18.0, centerOnSurface: false);

        SurfaceDefinition oldFront = manager.Surfaces.GetSurface(SurfaceId.Front);
        Rect2D oldSafe = oldFront.SafeRectMm;
        double oldX = el.XMm;
        double oldY = el.YMm;
        double oldRelX = el.RelX;
        double oldRelY = el.RelY;

        // Cresce largura para 320mm e altura para 160mm
        var calcGrown = new BoxCalculator(3.0, 320.0, 150.0, 160.0);
        manager.UpdateBoxCalculator(calcGrown);

        SurfaceDefinition newFront = manager.Surfaces.GetSurface(SurfaceId.Front);
        Rect2D newSafe = newFront.SafeRectMm;

        switch (mode)
        {
            case AnchorMode.Absolute:
                el.XMm.Should().Be(22.0);
                el.YMm.Should().Be(18.0);
                break;

            case AnchorMode.Relative:
                el.RelX.Should().BeApproximately(oldRelX, 1e-9);
                el.RelY.Should().BeApproximately(oldRelY, 1e-9);
                el.XMm.Should().BeApproximately(PythonMath.Round(oldRelX * 320.0 - 15.0, 4), 1e-4);
                el.YMm.Should().BeApproximately(PythonMath.Round(oldRelY * 160.0 - 5.0, 4), 1e-4);
                break;

            case AnchorMode.Center:
                el.XMm.Should().BeApproximately(PythonMath.Round(newSafe.Center.X - 15.0, 4), 1e-4);
                el.YMm.Should().BeApproximately(PythonMath.Round(newSafe.Center.Y - 5.0, 4), 1e-4);
                break;

            case AnchorMode.TopLeft:
                (el.XMm - newSafe.XMm).Should().BeApproximately(oldX - oldSafe.XMm, 1e-4);
                (el.YMm - newSafe.YMm).Should().BeApproximately(oldY - oldSafe.YMm, 1e-4);
                break;

            case AnchorMode.TopRight:
                (newSafe.Right - (el.XMm + el.WidthMm)).Should().BeApproximately(oldSafe.Right - (oldX + el.WidthMm), 1e-4);
                (el.YMm - newSafe.YMm).Should().BeApproximately(oldY - oldSafe.YMm, 1e-4);
                break;

            case AnchorMode.BottomLeft:
                (el.XMm - newSafe.XMm).Should().BeApproximately(oldX - oldSafe.XMm, 1e-4);
                (newSafe.Bottom - (el.YMm + el.HeightMm)).Should().BeApproximately(oldSafe.Bottom - (oldY + el.HeightMm), 1e-4);
                break;

            case AnchorMode.BottomRight:
                (newSafe.Right - (el.XMm + el.WidthMm)).Should().BeApproximately(oldSafe.Right - (oldX + el.WidthMm), 1e-4);
                (newSafe.Bottom - (el.YMm + el.HeightMm)).Should().BeApproximately(oldSafe.Bottom - (oldY + el.HeightMm), 1e-4);
                break;
        }
    }

    [Fact]
    public void SnapshotAndRestore_RetainsRawRasterCacheAcrossRemoveAndUndo()
    {
        var manager = new SurfaceManager(new BoxCalculator(3.0, 200.0, 150.0, 100.0));
        var raster = new RasterImagePersonalizationElement
        {
            Name = "Logo IFAC",
            PanelId = SurfaceId.Lid,
            WidthMm = 40.0,
            HeightMm = 20.0,
            RawImageB64 = "RAW_BASE64_PAYLOAD_V1",
            ProcessedImageB64 = "PROCESSED_BASE64_PAYLOAD",
            OriginalPixelSize = [400, 200],
            ProcessedPixelSize = [400, 200],
        };

        manager.AddElement(raster);

        // Snapshot leve (undo/redo) omite RawImageB64 para economizar memória
        PersonalizationSnapshot lightweightSnapshot = manager.CreateSnapshot(includeRawBase64: false);
        RasterImagePersonalizationElement snapEl = (RasterImagePersonalizationElement)lightweightSnapshot.Elements.Single();
        snapEl.RawImageB64.Should().BeEmpty();

        // Remove o elemento, mas o cache mantém o payload original para Undo
        manager.RemoveElement(raster.Id).Should().BeTrue();
        manager.ElementCount.Should().Be(0);

        manager.RestoreSnapshot(lightweightSnapshot);
        manager.ElementCount.Should().Be(1);
        RasterImagePersonalizationElement restored = (RasterImagePersonalizationElement)manager.GetElement(raster.Id);
        restored.RawImageB64.Should().Be("RAW_BASE64_PAYLOAD_V1");
    }

    [Fact]
    public void PersonalizationHistory_CapsAt40StepsAndSupportsUndoRedo()
    {
        var manager = new SurfaceManager(new BoxCalculator(3.0, 200.0, 150.0, 100.0));
        var history = new PersonalizationHistory(maxSteps: PersonalizationHistory.DefaultMaxSteps);

        var el = CreateSampleText();
        manager.AddElement(el, xMm: 20.0, yMm: 20.0, centerOnSurface: false);

        for (int i = 1; i <= 45; i++)
        {
            history.Record(new PersonalizationHistoryEntry(manager.CreateSnapshot(), SurfaceId.Front, el.Id));
            manager.MoveElement(el.Id, 20.0 + i, 20.0);
        }

        history.UndoCount.Should().Be(40);
        history.CanUndo.Should().BeTrue();
        history.CanRedo.Should().BeFalse();

        // Estado atual está em x = 65.0
        var current = new PersonalizationHistoryEntry(manager.CreateSnapshot(), SurfaceId.Front, el.Id);
        PersonalizationHistoryEntry previous = history.Undo(current);
        manager.RestoreSnapshot(previous.Snapshot);
        manager.GetElement(el.Id).XMm.Should().Be(64.0);
        history.CanRedo.Should().BeTrue();

        PersonalizationHistoryEntry redone = history.Redo(new PersonalizationHistoryEntry(manager.CreateSnapshot(), SurfaceId.Front, el.Id));
        manager.RestoreSnapshot(redone.Snapshot);
        manager.GetElement(el.Id).XMm.Should().Be(65.0);
    }

    [Fact]
    public void UpdateBoxCalculator_SwitchingToOpenBoxWithElementOnLid_IsAtomicWhenDroppingDisabled()
    {
        var manager = new SurfaceManager(new BoxCalculator(3.0, 200.0, 150.0, 100.0));
        manager.AddElement(CreateSampleText(panelId: SurfaceId.Lid));

        IBoxModelDefinition openModel = BoxModelRegistry.Default.Get(OpenBoxModel.Id);
        var nextCalc = openModel.CreateCalculator(3.0, 200.0, 150.0, 100.0);

        Action act = () => manager.UpdateBoxCalculator(nextCalc, boxModel: openModel, dropUnsupportedSurfaceElements: false);
        act.Should().Throw<KeyNotFoundException>();

        // Estado anterior preservado atomicamente
        manager.BoxModel.ModelId.Should().Be(BoxWithLidAndHandlesModel.Id);
        manager.ElementCount.Should().Be(1);

        // Com dropUnsupportedSurfaceElements = true, remove apenas elementos incompatíveis e troca o modelo
        manager.UpdateBoxCalculator(nextCalc, boxModel: openModel, dropUnsupportedSurfaceElements: true);
        manager.BoxModel.ModelId.Should().Be(OpenBoxModel.Id);
        manager.ElementCount.Should().Be(0);
    }
}
