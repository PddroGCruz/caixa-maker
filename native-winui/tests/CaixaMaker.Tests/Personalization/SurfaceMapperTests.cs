using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Personalization;

public sealed class SurfaceMapperTests
{
    [Fact]
    public void ListSurfaces_BoxWithLidAndHandles_Returns6SurfacesInCanonicalOrder()
    {
        var calc = new BoxCalculator(boxThickness: 3.0, boxWidth: 200.0, boxDepth: 150.0, boxHeight: 100.0, folga: 0.2);
        var model = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        var mapper = new SurfaceMapper(calc, boxModel: model);

        IReadOnlyList<SurfaceDefinition> surfaces = mapper.ListSurfaces();

        surfaces.Select(s => s.PanelId).Should().Equal(
            SurfaceId.Front,
            SurfaceId.Back,
            SurfaceId.Left,
            SurfaceId.Right,
            SurfaceId.Lid,
            SurfaceId.Bottom);

        SurfaceDefinition front = mapper.GetSurface(SurfaceId.Front);
        front.WidthMm.Should().Be(200.0);
        front.HeightMm.Should().Be(100.0);
        front.CncRotationDeg.Should().Be(0.0);

        SurfaceDefinition left = mapper.GetSurface(SurfaceId.Left);
        left.WidthMm.Should().Be(156.0);
        left.HeightMm.Should().Be(100.0);

        SurfaceDefinition lid = mapper.GetSurface(SurfaceId.Lid);
        lid.WidthMm.Should().Be(206.0);
        lid.HeightMm.Should().Be(154.5);
        lid.CncRotationDeg.Should().Be(180.0);

        SurfaceDefinition bottom = mapper.GetSurface(SurfaceId.Bottom);
        bottom.WidthMm.Should().Be(206.0);
        bottom.HeightMm.Should().Be(156.0);
    }

    [Fact]
    public void ListSurfaces_OpenBox_ExcludesLidAndOmitsHingeEars()
    {
        var calc = new BoxCalculator(boxThickness: 3.0, boxWidth: 180.0, boxDepth: 120.0, boxHeight: 80.0, folga: 0.0);
        var model = BoxModelRegistry.Default.Get(OpenBoxModel.Id);
        var mapper = new SurfaceMapper(calc, boxModel: model);

        IReadOnlyList<SurfaceDefinition> surfaces = mapper.ListSurfaces();

        surfaces.Select(s => s.PanelId).Should().Equal(
            SurfaceId.Front,
            SurfaceId.Back,
            SurfaceId.Left,
            SurfaceId.Right,
            SurfaceId.Bottom);

        Action act = () => mapper.GetSurface(SurfaceId.Lid);
        act.Should().Throw<KeyNotFoundException>();

        SurfaceDefinition left = mapper.GetSurface(SurfaceId.Left);
        left.ForbiddenZones.Select(z => z.Id).Should().NotContain("left_hinge_ear");
        left.SvgBodyOffsetMm[0].Should().Be(left.SvgCellOriginMm[0] + BoxDrawer.StartingCoordinate);
    }

    [Fact]
    public void SurfaceToPanelSvg_LidApplies180DegRotationAndMirrorsWithinLidBounds()
    {
        var calc = new BoxCalculator(boxThickness: 3.0, boxWidth: 200.0, boxDepth: 150.0, boxHeight: 100.0);
        var mapper = new SurfaceMapper(calc);
        SurfaceDefinition lid = mapper.GetSurface(SurfaceId.Lid);

        PanelSvgTransformResult mapped = mapper.SurfaceToPanelSvg(SurfaceId.Lid, 10.0, 20.0, rotationDeg: 15.0);

        mapped.XMm.Should().BeApproximately(lid.SvgBodyOffsetMm[0] + (lid.WidthMm - 10.0), 1e-6);
        mapped.YMm.Should().BeApproximately(lid.SvgBodyOffsetMm[1] + (lid.HeightMm - 20.0), 1e-6);
        mapped.RotationDeg.Should().BeApproximately(195.0, 1e-6);

        PanelSvgTransformResult front = mapper.SurfaceToPanelSvg(SurfaceId.Front, 10.0, 20.0, rotationDeg: 350.0);
        SurfaceDefinition frontDef = mapper.GetSurface(SurfaceId.Front);
        front.XMm.Should().BeApproximately(frontDef.SvgBodyOffsetMm[0] + 10.0, 1e-6);
        front.YMm.Should().BeApproximately(frontDef.SvgBodyOffsetMm[1] + 20.0, 1e-6);
        front.RotationDeg.Should().BeApproximately(350.0, 1e-6);
    }

    [Fact]
    public void SurfaceToPanelSvg_IndividualPanelWithOriginOffset_IncludesEarOffsetOnLeftWhenHasHandles()
    {
        var calc = new BoxCalculator(boxThickness: 3.0, boxWidth: 200.0, boxDepth: 150.0, boxHeight: 100.0);
        var mapper = new SurfaceMapper(calc, boxModel: BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id));

        PanelSvgTransformResult left = mapper.SurfaceToPanelSvg(
            SurfaceId.Left,
            xMm: 25.0,
            yMm: 30.0,
            rotationDeg: 0.0,
            originOffsetMm: new Point2D(5.0, 5.0));

        // HasHandles = true => earOffset = 4 * t = 12.0 mm.
        left.XMm.Should().BeApproximately(5.0 + 12.0 + 25.0, 1e-6);
        left.YMm.Should().BeApproximately(5.0 + 30.0, 1e-6);
    }

    [Fact]
    public void SurfaceTo3DLocal_MapsAll6FacesCorrectly()
    {
        var calc = new BoxCalculator(boxThickness: 3.0, boxWidth: 200.0, boxDepth: 150.0, boxHeight: 100.0);
        var mapper = new SurfaceMapper(calc);

        Point3D front = mapper.SurfaceTo3DLocal(SurfaceId.Front, 100.0, 50.0);
        front.X.Should().BeApproximately(0.0, 1e-6);
        front.Y.Should().BeApproximately(50.0, 1e-6);
        front.Z.Should().BeApproximately(75.0, 1e-6);

        Point3D back = mapper.SurfaceTo3DLocal(SurfaceId.Back, 100.0, 50.0);
        back.X.Should().BeApproximately(0.0, 1e-6);
        back.Y.Should().BeApproximately(50.0, 1e-6);
        back.Z.Should().BeApproximately(-75.0, 1e-6);

        Point3D lid = mapper.SurfaceTo3DLocal(SurfaceId.Lid, 103.0, 77.25);
        lid.X.Should().BeApproximately(0.0, 1e-6);
        lid.Y.Should().BeApproximately(103.0, 1e-6);
        lid.Z.Should().BeApproximately(-76.5 + 77.25, 1e-6);
    }
}
