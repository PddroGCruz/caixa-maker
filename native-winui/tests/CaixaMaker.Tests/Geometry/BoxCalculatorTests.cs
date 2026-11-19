using CaixaMaker.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace CaixaMaker.Tests.Geometry;

public sealed class BoxCalculatorTests
{
    [Fact]
    public void StructuralConstants_MatchLegacyPythonValues()
    {
        BoxCalculator.JointSpan.Should().Be(0.75);
        BoxCalculator.JointlessSpan.Should().BeApproximately(0.25, 1e-12);
        BoxCalculator.TargetProportion.Should().Be(0.1);
        BoxCalculator.MinJointLength.Should().Be(5.0);
        BoxCalculator.DefaultGetJointMinLength.Should().Be(3.0);

        BoxDrawer.StartingCoordinate.Should().Be(5.0);
        BoxDrawer.IncrementedMargin.Should().Be(10.0);
        BoxDrawer.HoleFactor.Should().Be(0.80);
        BoxDrawer.HingePinOverhangFactor.Should().Be(1.0);
        BoxDrawer.StrokeWidth.Should().Be("0.03mm");
        BoxDrawer.StrokeColor.Should().Be("black");
        BoxDrawer.FillStyle.Should().Be("none");
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 2)]
    [InlineData(3.5, 4)]
    [InlineData(4.5, 4)]
    [InlineData(5.5, 6)]
    [InlineData(9.5, 10)]
    [InlineData(10.5, 10)]
    [InlineData(11.5, 12)]
    [InlineData(-2.5, -2)]
    [InlineData(-3.5, -4)]
    public void PythonMath_RoundHalfToEven_MatchesPythonBankersRounding(double input, int expected)
    {
        PythonMath.RoundHalfToEven(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(7.5, 3)]
    [InlineData(10.5, 5)]
    [InlineData(13.5, 5)]
    [InlineData(16.5, 7)]
    [InlineData(30.0, 11)]
    [InlineData(72.75, 11)]
    [InlineData(144.0, 11)]
    [InlineData(194.0, 11)]
    [InlineData(480.0, 11)]
    public void GetJointLength_ProducesOddCountsMatchingPython(double span, int expectedCount)
    {
        var calc = new BoxCalculator(3.0, 200.0, 150.0, 100.0);

        JointDivision division = calc.GetJointLength(span);

        division.Count.Should().Be(expectedCount);
        (division.Count % 2).Should().Be(1);
        division.ActualLength.Should().BeApproximately(span / expectedCount, 1e-12);
    }

    [Theory]
    [InlineData(-0.4)]
    [InlineData(-0.35)]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(0.4)]
    public void BilateralFolgaCompensation_PreservesZeroErrorInvariantAcrossEntireEdge(double folga)
    {
        const double thickness = 3.0;
        var calc = new BoxCalculator(thickness, 200.0, 150.0, 100.0, folga);
        var drawer = new BoxDrawer(calc);

        calc.CalcSlotWidth().Should().BeApproximately(thickness + folga, 1e-12);

        double[] spans = [calc.CalcHeightJointSpan(), calc.DecreasedWidth, calc.DecreasedDepth];
        foreach (double span in spans)
        {
            JointDivision division = calc.GetJointLength(span);
            double p = division.ActualLength;

            // Invariante fundamental: l_tab + l_slot == 2 * p (zero acúmulo de erro dimensional)
            (calc.CalcTabLength(p) + calc.CalcSlotLength(p)).Should().BeApproximately(2.0 * p, 1e-12);

            double sumSegments = 0.0;
            for (int i = 0; i < division.Count; i++)
            {
                double seg = drawer.GetJointSegmentLength(i, division.Count, p, folga);
                seg.Should().BeGreaterThan(0.0);

                if (i % 2 == 1)
                {
                    seg.Should().BeApproximately(p - folga / 2.0, 1e-6);
                }
                else if (i == 0 || i == division.Count - 1)
                {
                    seg.Should().BeApproximately(p + folga / 4.0, 1e-6);
                }
                else
                {
                    seg.Should().BeApproximately(p + folga / 2.0, 1e-6);
                }

                sumSegments += seg;
            }

            sumSegments.Should().BeApproximately(span, 1e-5);
        }
    }

    [Fact]
    public void SlotGeometry_CentersCompensationAroundNominalSlot()
    {
        var calc = new BoxCalculator(3.0, 200.0, 150.0, 100.0, folga: 0.4);
        var drawer = new BoxDrawer(calc);

        SlotGeometry slot = drawer.GetSlotGeometry(10.0, 3.0, 0.4);

        slot.SlotLength.Should().BeApproximately(10.2, 1e-6);
        slot.SlotWidth.Should().BeApproximately(3.4, 1e-6);
        slot.OffsetLength.Should().BeApproximately(0.1, 1e-6);
        slot.OffsetWidth.Should().BeApproximately(0.2, 1e-6);
    }

    [Theory]
    [InlineData(1.0, 200.0, 150.0, 100.0, -1.0)]
    [InlineData(0.5, 200.0, 150.0, 100.0, 1.0)]
    [InlineData(3.0, 200.0, 150.0, 100.0, 6.0)]
    public void Constructor_ThrowsArgumentException_WhenFolgaViolatesGeometry(
        double t,
        double w,
        double d,
        double h,
        double folga)
    {
        Action act = () => _ = new BoxCalculator(t, w, d, h, folga);
        act.Should().Throw<ArgumentException>();
    }
}
