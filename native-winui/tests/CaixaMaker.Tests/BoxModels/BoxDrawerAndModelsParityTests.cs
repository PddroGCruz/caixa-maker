using System.Security.Cryptography;
using System.Text;
using CaixaMaker.Core.BoxModels;
using CaixaMaker.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.BoxModels;

public sealed class BoxDrawerAndModelsParityTests
{
    private const double ToleranceMm = 1e-6;

    [Fact]
    public void DefaultRegistry_ExposesCatalogInCanonicalOrderAndSupportsExtensibility()
    {
        BoxModelRegistry registry = BoxModelRegistry.Default;
        registry.ListIds().Should().Equal(BoxWithLidAndHandlesModel.Id, OpenBoxModel.Id);

        IReadOnlyList<BoxModelCatalogItem> catalog = registry.ListCatalogItems();
        catalog.Should().HaveCount(2);

        BoxModelCatalogItem lidModel = catalog[0];
        lidModel.Id.Should().Be("box_with_lid_and_handles");
        lidModel.Name.Should().Be("Caixa com Tampa e Alças");
        lidModel.HasLid.Should().BeTrue();
        lidModel.HasHandles.Should().BeTrue();
        lidModel.SupportsLidArticulation.Should().BeTrue();
        lidModel.TotalPanels.Should().Be(6);
        lidModel.SurfaceIds.Should().Equal("front", "back", "left", "right", "lid", "bottom");

        BoxModelCatalogItem openModel = catalog[1];
        openModel.Id.Should().Be("open_box");
        openModel.Name.Should().Be("Caixa Aberta");
        openModel.HasLid.Should().BeFalse();
        openModel.HasHandles.Should().BeFalse();
        openModel.SupportsLidArticulation.Should().BeFalse();
        openModel.TotalPanels.Should().Be(5);
        openModel.SurfaceIds.Should().Equal("front", "back", "left", "right", "bottom");

        // Registro isolado suporta extensão sem contaminar o Default
        BoxModelRegistry customRegistry = BoxModelRegistry.CreateDefault();
        customRegistry.Register(new MockCustomTrayModel());
        customRegistry.Contains("custom_tray").Should().BeTrue();
        BoxModelRegistry.Default.Contains("custom_tray").Should().BeFalse();

        Action missing = () => registry.Get("unknown_model");
        missing.Should().Throw<KeyNotFoundException>();
    }

    [Theory]
    [InlineData("box_with_lid_and_handles", 3.0, 200.0, 150.0, 100.0, 0.0, 8735, "a8fd93d6342365b3")]
    [InlineData("box_with_lid_and_handles", 3.0, 200.0, 150.0, 100.0, 0.4, 7823, "351b21aec012721a")]
    [InlineData("box_with_lid_and_handles", 3.0, 200.0, 150.0, 100.0, -0.35, 8143, "22f822f347e9c4d2")]
    [InlineData("box_with_lid_and_handles", 6.0, 320.0, 240.0, 145.0, 0.2, 7373, "09f69dfd367bb2f4")]
    [InlineData("open_box", 3.0, 200.0, 150.0, 100.0, 0.0, 8171, "d723cc6f5f34637b")]
    [InlineData("open_box", 3.0, 200.0, 150.0, 100.0, 0.4, 7259, "9386f750fccb189a")]
    [InlineData("open_box", 4.5, 120.0, 80.0, 60.0, -0.25, 7475, "42e3e867c614116e")]
    public void DrawBoxSvg_MatchesPythonReferenceByteForByteAcrossModelsAndFolgas(
        string modelId,
        double thickness,
        double width,
        double depth,
        double height,
        double folga,
        int expectedLength,
        string expectedSha256Prefix)
    {
        IBoxModelDefinition model = BoxModelRegistry.Default.Get(modelId);
        var parameters = new BoxParameters
        {
            BoxThickness = thickness,
            BoxWidth = width,
            BoxDepth = depth,
            BoxHeight = height,
            Folga = folga,
            FingerJointMode = FingerJointSizingMode.FixedCount,
            FixedFingerCount = 5,
        };

        string svg = model.DrawBoxSvg(parameters);

        svg.Length.Should().Be(expectedLength);
        ComputeSha256Prefix(svg).Should().Be(expectedSha256Prefix);
    }

    [Fact]
    public void BoxWithLidAndHandles_SummaryAndInMemoryGeometry_MatchPythonExactValues()
    {
        IBoxModelDefinition model = BoxModelRegistry.Default.Get(BoxWithLidAndHandlesModel.Id);
        BoxParameters parameters = BoxParameters.Default with
        {
            FingerJointMode = FingerJointSizingMode.FixedCount,
            FixedFingerCount = 5,
        };

        BoxCalculationSummary summary = model.BuildSummary(parameters);
        summary.ComputedDimensions.InnerWidth.Should().BeApproximately(194.0, ToleranceMm);
        summary.ComputedDimensions.InnerDepth.Should().BeApproximately(144.0, ToleranceMm);
        summary.ComputedDimensions.InnerHeight.Should().BeApproximately(97.0, ToleranceMm);
        summary.ComputedDimensions.IncreasedWidth.Should().BeApproximately(206.0, ToleranceMm);
        summary.ComputedDimensions.IncreasedDepth.Should().BeApproximately(156.0, ToleranceMm);
        summary.ComputedDimensions.JointlessSpan.Should().BeApproximately(12.125, ToleranceMm);
        summary.ComputedDimensions.VerticalJointSpan.Should().BeApproximately(72.75, ToleranceMm);
        summary.ComputedDimensions.OuterDepthWithHinge.Should().BeApproximately(168.0, ToleranceMm);
        summary.ComputedDimensions.TotalHeightWithLid.Should().BeApproximately(103.0, ToleranceMm);
        summary.ComputedDimensions.HingeHoleRadius.Should().BeApproximately(2.4, ToleranceMm);
        summary.ComputedDimensions.LidWidth.Should().BeApproximately(206.0, ToleranceMm);
        summary.ComputedDimensions.LidDepth.Should().BeApproximately(163.5, ToleranceMm);

        summary.Joints.Vertical.Count.Should().Be(11);
        summary.Joints.Vertical.TabCount.Should().Be(5);
        summary.Joints.Vertical.SlotCount.Should().Be(6);
        summary.Joints.Vertical.SegmentLengthMm.Should().BeApproximately(6.6136, 1e-4);
        summary.Joints.FrontBottom.Count.Should().Be(11);
        summary.Joints.FrontBottom.SegmentLengthMm.Should().BeApproximately(17.6364, 1e-4);
        summary.Joints.SideBottom.Count.Should().Be(11);
        summary.Joints.SideBottom.SegmentLengthMm.Should().BeApproximately(13.0909, 1e-4);

        summary.SvgCanvas.WidthMm.Should().BeApproximately(432.0, ToleranceMm);
        summary.SvgCanvas.HeightMm.Should().BeApproximately(401.0, ToleranceMm);
        summary.SvgCanvas.SingleColumnWidthMm.Should().BeApproximately(216.0, ToleranceMm);
        summary.Warnings.Should().BeEmpty();

        // Área estimada: 400 + 361.2 + 321.36 + 336.81 = 1419.37 cm²
        summary.Parts.Should().HaveCount(4);
        summary.EstimatedCuttingAreaCm2.Should().BeApproximately(1419.37, 1e-3);

        BoxSheetGeometry2D sheet = model.BuildSheetGeometry(parameters);
        sheet.Panels.Should().HaveCount(6);
        sheet.Layout.Panels.Keys.Should().Contain(["bottom", "lid", "front", "back", "right", "left"]);

        PanelGeometry2D bottom = sheet.PanelsById["bottom"];
        bottom.LocalBounds.Width.Should().BeApproximately(206.0, ToleranceMm);
        bottom.LocalBounds.Height.Should().BeApproximately(156.0, ToleranceMm);
        bottom.InternalHoles.Should().HaveCount(20); // 4 fileiras * 5 rasgos
        bottom.CircularHoles.Should().BeEmpty();

        PanelGeometry2D front = sheet.PanelsById["front"];
        front.LocalBounds.Width.Should().BeApproximately(200.0, ToleranceMm);
        front.LocalBounds.Height.Should().BeApproximately(100.0, ToleranceMm);
        front.InternalHoles.Should().BeEmpty();

        PanelGeometry2D rightSide = sheet.PanelsById["right"];
        rightSide.LocalBounds.Width.Should().BeApproximately(168.0, ToleranceMm);
        rightSide.LocalBounds.Height.Should().BeApproximately(107.5, ToleranceMm);
        rightSide.InternalHoles.Should().HaveCount(10); // 2 colunas * 5 rasgos verticais
        rightSide.CircularHoles.Should().ContainSingle();
        rightSide.CircularHoles[0].CenterX.Should().BeApproximately(167.0, ToleranceMm);
        rightSide.CircularHoles[0].CenterY.Should().BeApproximately(3.5, ToleranceMm);
        rightSide.CircularHoles[0].Radius.Should().BeApproximately(2.4, ToleranceMm);

        PanelGeometry2D leftSide = sheet.PanelsById["left"];
        leftSide.Placement.MirrorX.Should().BeTrue();
        leftSide.Placement.MirrorSpanWidth.Should().BeApproximately(178.0, ToleranceMm);
        leftSide.SheetBounds.Width.Should().BeApproximately(168.0, ToleranceMm);

        PanelGeometry2D lid = sheet.PanelsById["lid"];
        lid.LocalBounds.Width.Should().BeApproximately(206.0, ToleranceMm);
        lid.LocalBounds.Height.Should().BeApproximately(163.5, ToleranceMm);

        Polygon2D centeredBottom = bottom.GetCenteredOuterContour(flipY: true);
        centeredBottom.Bounds.Center.X.Should().BeApproximately(0.0, ToleranceMm);
        centeredBottom.Bounds.Center.Y.Should().BeApproximately(0.0, ToleranceMm);
    }

    [Fact]
    public void OpenBox_SummaryAndInMemoryGeometry_ExcludeLidAndHingeEars()
    {
        IBoxModelDefinition model = BoxModelRegistry.Default.Get(OpenBoxModel.Id);
        BoxParameters parameters = BoxParameters.Default with
        {
            FingerJointMode = FingerJointSizingMode.FixedCount,
            FixedFingerCount = 5,
        };

        BoxCalculationSummary summary = model.BuildSummary(parameters);
        summary.ComputedDimensions.OuterDepthWithHinge.Should().BeApproximately(156.0, ToleranceMm);
        summary.ComputedDimensions.TotalHeightWithLid.Should().BeApproximately(100.0, ToleranceMm);
        summary.ComputedDimensions.HingeHoleRadius.Should().BeNull();
        summary.ComputedDimensions.LidWidth.Should().BeNull();
        summary.ComputedDimensions.LidDepth.Should().BeNull();
        summary.SvgCanvas.WidthMm.Should().BeApproximately(432.0, ToleranceMm);
        summary.SvgCanvas.HeightMm.Should().BeApproximately(386.0, ToleranceMm);

        // Área estimada: 400 + 312 + 321.36 = 1033.36 cm²
        summary.Parts.Should().HaveCount(3);
        summary.EstimatedCuttingAreaCm2.Should().BeApproximately(1033.36, 1e-3);

        BoxSheetGeometry2D sheet = model.BuildSheetGeometry(parameters);
        sheet.Panels.Should().HaveCount(5);
        sheet.PanelsById.ContainsKey("lid").Should().BeFalse();

        PanelGeometry2D rightSide = sheet.PanelsById["right"];
        rightSide.LocalBounds.Width.Should().BeApproximately(156.0, ToleranceMm);
        rightSide.LocalBounds.Height.Should().BeApproximately(100.0, ToleranceMm);
        rightSide.CircularHoles.Should().BeEmpty();
        rightSide.InternalHoles.Should().HaveCount(10);

        PanelGeometry2D leftSide = sheet.PanelsById["left"];
        leftSide.Placement.MirrorX.Should().BeTrue();
        leftSide.Placement.MirrorSpanWidth.Should().BeApproximately(166.0, ToleranceMm);
    }

    [Fact]
    public void BuildSummary_EmitsWarning_WhenVerticalSegmentDropsBelow3mm()
    {
        IBoxModelDefinition model = BoxModelRegistry.Default.Get(OpenBoxModel.Id);
        // height = 10, thickness = 3 -> decreased_height = 7 -> vertical_span = 5.25 -> ideal = 3 -> 5.25/3 = 1.75 -> count=3 -> segment = 1.75 mm < 3.0 mm
        var shallowBox = new BoxParameters
        {
            BoxThickness = 3.0,
            BoxWidth = 100.0,
            BoxDepth = 100.0,
            BoxHeight = 10.0,
            Folga = 0.0,
        };

        BoxCalculationSummary summary = model.BuildSummary(shallowBox);
        summary.Joints.Vertical.SegmentLengthMm.Should().BeLessThan(3.0);
        summary.Warnings.Should().ContainSingle(w => w.Contains("abaixo de 3,0 mm"));
    }

    private static string ComputeSha256Prefix(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private sealed class MockCustomTrayModel : BoxModelDefinitionBase
    {
        public override string ModelId => "custom_tray";
        public override string DisplayName => "Bandeja de Teste";
        public override string Description => "Modelo sintético para validar extensibilidade do catálogo.";
        public override IReadOnlyList<string> Features => ["Base", "Laterais"];
        public override bool HasLid => false;
        public override bool HasHandles => false;
        public override bool SupportsLidArticulation => false;
        public override int TotalPanels => 5;
        public override IReadOnlyList<string> SurfaceIds => ["front", "back", "left", "right", "bottom"];

        public override BoxLayoutMetrics GetLayoutMetrics(BoxCalculator calc, BoxDrawer drawer)
            => new OpenBoxModel().GetLayoutMetrics(calc, drawer);

        public override IReadOnlyList<PartSummary> BuildPartsSummary(
            BoxCalculator calc,
            BoxDrawer drawer,
            BoxLayoutMetrics layout,
            JointsSummary joints,
            string folgaNote,
            double effectiveSlotWidth)
            => new OpenBoxModel().BuildPartsSummary(calc, drawer, layout, joints, folgaNote, effectiveSlotWidth);
    }
}
