using CaixaMaker.Core.Geometry;
using CaixaMaker.Core.Personalization;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Personalization;

public sealed class GeometryValidatorSatTests
{
    [Fact]
    public void Validate_CenteredElementWithinSafeRect_ReturnsValidWithoutIssues()
    {
        var manager = new SurfaceManager(new BoxCalculator(3.0, 200.0, 150.0, 100.0), safetyMarginMm: 2.0);
        manager.AddElement(new VectorSvgPersonalizationElement
        {
            Name = "Emblema",
            PanelId = SurfaceId.Front,
            WidthMm = 40.0,
            HeightMm = 20.0,
            Paths = [new VectorPathItem { D = "M 0 0 L 40 0 L 40 20 L 0 20 Z" }],
        });

        ValidationReport report = manager.Validate();

        report.IsValid.Should().BeTrue();
        report.CanExport.Should().BeTrue();
        report.ErrorCount.Should().Be(0);
        report.WarningCount.Should().Be(0);
    }

    [Fact]
    public void Validate_OutOfBoundsCompleteAndPartial_ReportsExpectedErrorCodes()
    {
        var mapper = new SurfaceMapper(new BoxCalculator(3.0, 200.0, 150.0, 100.0));
        var validator = new GeometryValidator(2.0);

        var partial = new TextPersonalizationElement
        {
            Id = "el_partial",
            PanelId = SurfaceId.Front,
            XMm = -5.0,
            YMm = 30.0,
            WidthMm = 20.0,
            HeightMm = 10.0,
            Vectorized = true,
            VectorPaths = [new VectorPathItem { D = "M 0 0 L 20 10" }],
        };

        var complete = new TextPersonalizationElement
        {
            Id = "el_outside",
            PanelId = SurfaceId.Front,
            XMm = 250.0,
            YMm = 20.0,
            WidthMm = 20.0,
            HeightMm = 10.0,
            Vectorized = true,
            VectorPaths = [new VectorPathItem { D = "M 0 0 L 20 10" }],
        };

        ValidationReport report = validator.Validate(mapper, [partial, complete]);

        report.IsValid.Should().BeFalse();
        report.Errors.Should().Contain(e => e.ElementId == "el_partial" && e.Code == "OUT_OF_BOUNDS_PARTIAL");
        report.Errors.Should().Contain(e => e.ElementId == "el_outside" && e.Code == "OUT_OF_BOUNDS_COMPLETE");
    }

    [Fact]
    public void Validate_CollisionWithForbiddenZone_AndSafetyMarginViolation_AreDetected()
    {
        var mapper = new SurfaceMapper(new BoxCalculator(3.0, 200.0, 150.0, 100.0));
        var validator = new GeometryValidator(safetyMarginMm: 3.0);

        // Colide com front_left_joints [0..3, 0..100]
        var hitsFingerJoint = new VectorSvgPersonalizationElement
        {
            Id = "el_joint",
            PanelId = SurfaceId.Front,
            XMm = 2.0,
            YMm = 40.0,
            WidthMm = 20.0,
            HeightMm = 10.0,
            Paths = [new VectorPathItem { D = "M 0 0 L 20 10" }],
        };

        // Dentro dos limites, fora de forbidden_zone (safe_rect x >= 3), mas viola margem de 3mm (safe = x >= 6)
        var violatesMargin = new VectorSvgPersonalizationElement
        {
            Id = "el_margin",
            PanelId = SurfaceId.Front,
            XMm = 4.0,
            YMm = 40.0,
            WidthMm = 20.0,
            HeightMm = 10.0,
            Paths = [new VectorPathItem { D = "M 0 0 L 20 10" }],
        };

        ValidationReport report = validator.Validate(mapper, [hitsFingerJoint, violatesMargin]);

        report.IsValid.Should().BeFalse();
        report.Errors.Should().Contain(e =>
            e.ElementId == "el_joint"
            && e.Code == "COLLISION_FORBIDDEN_ZONE"
            && e.ZoneId == "left_corner_tabs");
        report.Warnings.Should().Contain(w =>
            w.ElementId == "el_margin"
            && w.Code == "SAFETY_MARGIN_VIOLATION");
    }

    [Fact]
    public void PolygonsIntersectSat_DistinguishesTrueSeparationWhenAabbOverlaps()
    {
        var calc = new BoxCalculator(3.0, 200.0, 150.0, 100.0);
        var mapper = new SurfaceMapper(calc);
        var validator = new GeometryValidator(safetyMarginMm: 0.0);

        // Barra diagonal 40x6 centrada em (100, 50) rotacionada 45° -> seu AABB cobre ~[83.7..116.3] x [33.7..66.3]
        var rotatedBar = new VectorSvgPersonalizationElement
        {
            Id = "rot_bar",
            PanelId = SurfaceId.Front,
            XMm = 80.0,
            YMm = 47.0,
            WidthMm = 40.0,
            HeightMm = 6.0,
            RotationDeg = 45.0,
            Paths = [new VectorPathItem { D = "M 0 0 L 40 6" }],
        };

        // Caixa pequena 6x6 no canto superior direito do AABB da barra (109..115, 35..41):
        // O AABB de 'rot_bar' e 'corner_box' se sobrepoem fortemente, mas pelo SAT há ~12.7mm de folga perpendicular!
        var separatedCornerBox = new VectorSvgPersonalizationElement
        {
            Id = "corner_box",
            PanelId = SurfaceId.Front,
            XMm = 109.0,
            YMm = 35.0,
            WidthMm = 6.0,
            HeightMm = 6.0,
            Paths = [new VectorPathItem { D = "M 0 0 L 6 6" }],
        };

        GeometryValidator.PolygonsIntersectSat(rotatedBar.Corners2D(), separatedCornerBox.Corners2D())
            .Should().BeFalse();

        ValidationReport separatedReport = validator.Validate(mapper, [rotatedBar, separatedCornerBox]);
        separatedReport.Warnings.Should().NotContain(w => w.Code == "ELEMENT_OVERLAP");

        // Caixa cruzando a diagonal (97..103, 47..53): agora colide de verdade pelo SAT.
        var collidingBox = new VectorSvgPersonalizationElement
        {
            Id = "colliding_box",
            PanelId = SurfaceId.Front,
            XMm = 97.0,
            YMm = 47.0,
            WidthMm = 6.0,
            HeightMm = 6.0,
            Paths = [new VectorPathItem { D = "M 0 0 L 6 6" }],
        };

        GeometryValidator.PolygonsIntersectSat(rotatedBar.Corners2D(), collidingBox.Corners2D())
            .Should().BeTrue();

        ValidationReport overlappingReport = validator.Validate(mapper, [rotatedBar, collidingBox]);
        overlappingReport.Warnings.Should().Contain(w => w.Code == "ELEMENT_OVERLAP");
    }

    [Fact]
    public void Validate_RotatedElementCornerTouchingBottomThroughSlots_ReportsCollision()
    {
        var mapper = new SurfaceMapper(new BoxCalculator(3.0, 200.0, 150.0, 100.0));
        var validator = new GeometryValidator(safetyMarginMm: 0.0);
        SurfaceDefinition bottom = mapper.GetSurface(SurfaceId.Bottom);

        // No fundo (206 x 156), safe_rect começa em x=6, y=6; barra centrada perto do limite superior-esquerdo (y=20)
        // com 40x10 rotacionada 45° projeta canto acima de y=6 entrando em bottom_top_slots (y in [0..6]).
        var rotated = new VectorSvgPersonalizationElement
        {
            Id = "rot_bottom",
            PanelId = bottom.PanelId,
            XMm = 80.0,
            YMm = 13.0,
            WidthMm = 40.0,
            HeightMm = 10.0,
            RotationDeg = 45.0,
            Paths = [new VectorPathItem { D = "M 0 0 L 40 10" }],
        };

        ValidationReport report = validator.Validate(mapper, [rotated]);
        report.IsValid.Should().BeFalse();
        report.Errors.Should().Contain(e =>
            e.ElementId == "rot_bottom"
            && e.Code == "COLLISION_FORBIDDEN_ZONE"
            && e.ZoneId == "bottom_top_slots");
    }

    [Fact]
    public void Validate_EmitsManufacturingAndContentWarningsForSmallFallbackSvgAndRaster()
    {
        var mapper = new SurfaceMapper(new BoxCalculator(3.0, 200.0, 150.0, 100.0));
        var validator = new GeometryValidator(2.0);

        var smallFallbackText = new TextPersonalizationElement
        {
            Id = "txt_warn",
            PanelId = SurfaceId.Front,
            XMm = 60.0,
            YMm = 40.0,
            WidthMm = 0.8,
            HeightMm = 0.9,
            FontFallbackUsed = true,
            Vectorized = true,
            VectorPaths = [new VectorPathItem { D = "M 0 0 L 0.8 0.9" }],
        };

        var svgWarn = new VectorSvgPersonalizationElement
        {
            Id = "svg_warn",
            PanelId = SurfaceId.Back,
            XMm = 50.0,
            YMm = 35.0,
            WidthMm = 20.0,
            HeightMm = 10.0,
            Warnings = ["Elemento <text> convertido com aproximação"],
            Paths = [new VectorPathItem { D = "M 0 0 L 20 10" }],
        };

        var rasterWarn = new RasterImagePersonalizationElement
        {
            Id = "img_warn",
            PanelId = SurfaceId.Lid,
            XMm = 60.0,
            YMm = 50.0,
            WidthMm = 30.0,
            HeightMm = 20.0,
            Vectorize = false,
            ProcessedImageB64 = "ABC123",
        };

        ValidationReport report = validator.Validate(mapper, [smallFallbackText, svgWarn, rasterWarn]);

        report.IsValid.Should().BeTrue();
        report.Warnings.Should().Contain(w => w.ElementId == "txt_warn" && w.Code == "ELEMENT_TOO_SMALL");
        report.Warnings.Should().Contain(w => w.ElementId == "txt_warn" && w.Code == "FONT_FALLBACK_USED");
        report.Warnings.Should().Contain(w => w.ElementId == "svg_warn" && w.Code == "SVG_IMPORT_WARNING");
        report.Warnings.Should().Contain(w => w.ElementId == "img_warn" && w.Code == "RASTER_NOT_VECTORIZED");
    }
}
