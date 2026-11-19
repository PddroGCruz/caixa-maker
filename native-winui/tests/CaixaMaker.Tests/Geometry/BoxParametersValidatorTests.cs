using CaixaMaker.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Geometry;

public sealed class BoxParametersValidatorTests
{
    [Fact]
    public void DefaultAndFromCentimeters_ProduceExpectedMillimeters()
    {
        BoxParameters defaults = BoxParameters.Default;
        defaults.BoxThickness.Should().Be(3.0);
        defaults.BoxWidth.Should().Be(200.0);
        defaults.BoxDepth.Should().Be(150.0);
        defaults.BoxHeight.Should().Be(100.0);
        defaults.Folga.Should().Be(0.0);

        BoxParameters fromCliCm = BoxParameters.FromCentimeters(3.0, 20.0, 15.0, 10.0, -0.2);
        fromCliCm.BoxThickness.Should().Be(3.0);
        fromCliCm.BoxWidth.Should().Be(200.0);
        fromCliCm.BoxDepth.Should().Be(150.0);
        fromCliCm.BoxHeight.Should().Be(100.0);
        fromCliCm.Folga.Should().Be(-0.2);

        BoxParameters fromAllCm = BoxParameters.FromAllCentimeters(0.3, 20.0, 15.0, 10.0, 0.1);
        fromAllCm.BoxThickness.Should().BeApproximately(3.0, 1e-12);
        fromAllCm.BoxWidth.Should().BeApproximately(200.0, 1e-12);
    }

    [Theory]
    [InlineData(0.0, 200.0, 150.0, 100.0, 0.0)]
    [InlineData(20.5, 200.0, 150.0, 100.0, 0.0)]
    [InlineData(3.0, 9.9, 150.0, 100.0, 0.0)]
    [InlineData(3.0, 2000.1, 150.0, 100.0, 0.0)]
    [InlineData(3.0, 200.0, 9.9, 100.0, 0.0)]
    [InlineData(3.0, 200.0, 2000.1, 100.0, 0.0)]
    [InlineData(3.0, 200.0, 150.0, 4.9, 0.0)]
    [InlineData(3.0, 200.0, 150.0, 2000.1, 0.0)]
    [InlineData(3.0, 200.0, 150.0, 100.0, -2.01)]
    [InlineData(3.0, 200.0, 150.0, 100.0, 2.01)]
    public void Validate_RejectsOutOfRangeParameters(
        double t,
        double w,
        double d,
        double h,
        double folga)
    {
        var parameters = new BoxParameters
        {
            BoxThickness = t,
            BoxWidth = w,
            BoxDepth = d,
            BoxHeight = h,
            Folga = folga,
        };

        BoxValidationResult result = BoxParametersValidator.Validate(parameters);
        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "OUT_OF_RANGE");
    }

    [Fact]
    public void Validate_RejectsNonFiniteParameters()
    {
        var parameters = BoxParameters.Default with { Folga = double.NaN };
        BoxValidationResult result = BoxParametersValidator.Validate(parameters);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "NON_FINITE" && issue.Parameter == "folga");
    }

    [Theory]
    [InlineData(5.0, 50.0, 50.0, 5.0, 0.0, "HEIGHT_BELOW_THICKNESS")]
    [InlineData(10.0, 20.0, 50.0, 20.0, 0.0, "WIDTH_BELOW_DOUBLE_THICKNESS")]
    [InlineData(10.0, 50.0, 20.0, 20.0, 0.0, "DEPTH_BELOW_DOUBLE_THICKNESS")]
    [InlineData(0.5, 200.0, 150.0, 100.0, -0.5, "SLOT_WIDTH_TOO_SMALL")]
    [InlineData(0.5, 200.0, 150.0, 100.0, 1.0, "OUTER_MARGIN_TOO_SMALL")]
    [InlineData(4.3, 10.0, 150.0, 100.0, 2.0, "SEGMENT_TOO_SMALL_FOR_FOLGA")]
    [InlineData(3.2, 50.0, 50.0, 5.0, 2.0, "SEGMENT_TOO_SMALL_FOR_FOLGA")]
    public void Validate_EnforcesAllGeometricInvariantsFromBackend(
        double t,
        double w,
        double d,
        double h,
        double folga,
        string expectedCode)
    {
        var parameters = new BoxParameters
        {
            BoxThickness = t,
            BoxWidth = w,
            BoxDepth = d,
            BoxHeight = h,
            Folga = folga,
        };

        BoxValidationResult result = BoxParametersValidator.Validate(parameters);
        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == expectedCode);

        Action act = () => BoxParametersValidator.ValidateAndThrow(parameters);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(3.0, 200.0, 150.0, 100.0, -0.3)]
    [InlineData(3.0, 200.0, 150.0, 100.0, 2.0)]
    [InlineData(3.0, 200.0, 150.0, 100.0, -2.0)]
    [InlineData(0.5, 50.0, 50.0, 20.0, 0.0)]
    [InlineData(6.0, 320.0, 240.0, 145.0, 0.2)]
    public void Validate_AcceptsValidGeometriesAcrossSignedFolgaBounds(
        double t,
        double w,
        double d,
        double h,
        double folga)
    {
        var parameters = new BoxParameters
        {
            BoxThickness = t,
            BoxWidth = w,
            BoxDepth = d,
            BoxHeight = h,
            Folga = folga,
        };

        BoxValidationResult result = BoxParametersValidator.Validate(parameters);
        result.IsValid.Should().BeTrue();
        result.Issues.Should().BeEmpty();

        BoxCalculator calc = BoxParametersValidator.ValidateAndCreateCalculator(parameters);
        calc.Thickness.Should().Be(t);
        calc.Folga.Should().Be(folga);
    }

    [Fact]
    public void GetRecommendedRangeAdvisories_ReportsAdvisoriesForLargeValidParameters()
    {
        var largeValid = new BoxParameters
        {
            BoxThickness = 12.0,
            BoxWidth = 600.0,
            BoxDepth = 600.0,
            BoxHeight = 350.0,
            Folga = 1.5,
        };

        BoxParametersValidator.Validate(largeValid).IsValid.Should().BeTrue();
        IReadOnlyList<BoxValidationIssue> advisories = BoxParametersValidator.GetRecommendedRangeAdvisories(largeValid);
        advisories.Should().HaveCount(5);
    }
}
