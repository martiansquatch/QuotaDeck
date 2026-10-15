namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Text;

/// <summary>
/// Mirrors the percentage cases Codenotch pins in <c>Tests/SnapshotTests.swift</c>
/// (<c>WindowSummaryTests</c>, <c>RemainingHeadlineTests</c>, <c>MeteredFractionTests</c>).
/// </summary>
public class PercentTests
{
    [Theory]
    [InlineData(0.12, "12", "88")]
    [InlineData(0.0, "0", "100")]
    [InlineData(1.0, "100", "0")]
    [InlineData(1.04, "104", "0")]
    [InlineData(1.28, "128", "0")]
    public void WholePercentsRoundHalfAwayFromZero(double fraction, string used, string left)
    {
        Assert.Equal((used, left), Percent.Halves(fraction));
    }

    /// <summary>
    /// The left half derives from the <em>rounded</em> used half, not from one minus the fraction —
    /// 9.5% used is "10% Used · 90% left", because that is how the dashboard the user compares
    /// against does the maths.
    /// </summary>
    [Fact]
    public void TheLeftHalfDerivesFromTheRoundedUsedHalf()
    {
        Assert.Equal(("10", "90"), Percent.Halves(0.095));
    }

    /// <summary>Below one percent, whole percents collapse a real reading into "0%".</summary>
    [Fact]
    public void FractionsOfAPercentSurviveBelowOne()
    {
        Assert.Equal(("0.3", "99.7"), Percent.Halves(0.0034));
        Assert.Equal(("99.8", "0.2"), Percent.Halves(0.998));
    }

    /// <summary>A tenth of nothing is not zero: it says so rather than pretending.</summary>
    [Fact]
    public void VanishingFractionsSaySo()
    {
        Assert.Equal(("<0.1", ">99.9"), Percent.Halves(0.0004));
    }

    /// <summary>
    /// Just under the top: the used half has no room left to name a tenth, so it says "&gt;99.9" and
    /// the left half says "&lt;0.1" from its own end.
    /// </summary>
    [Fact]
    public void JustUnderTheTopReadsFromBothEnds()
    {
        Assert.Equal((">99.9", "<0.1"), Percent.Halves(0.9995));
        Assert.Equal(("99.9", "0.1"), Percent.Halves(0.999));
    }

    [Theory]
    [InlineData(0.125, "13")]
    [InlineData(0.12, "12")]
    [InlineData(0.095, "10")]
    [InlineData(0.0034, "0.3")]
    [InlineData(0.0004, "<0.1")]
    [InlineData(0.998, "100")]
    [InlineData(1.28, "128")]
    public void TextIsTheRingsLabel(double fraction, string expected)
    {
        Assert.Equal(expected, Percent.Text(fraction));
    }

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(0.0034, "<1")]
    [InlineData(0.998, "99")]
    [InlineData(0.5, "50")]
    [InlineData(1.0, "100")]
    [InlineData(1.28, "128")]
    public void WholeNeverClaimsZeroUsedOrFullWhileThereIsRoom(double fraction, string expected)
    {
        Assert.Equal(expected, Percent.Whole(fraction));
    }

    /// <summary>The halves always add up, which is what keeps the line from contradicting itself.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.07)]
    [InlineData(0.5)]
    [InlineData(0.93)]
    [InlineData(1.0)]
    public void TheHalvesAddUpToAHundred(double fraction)
    {
        var halves = Percent.Halves(fraction);
        Assert.Equal(100, int.Parse(halves.Used) + int.Parse(halves.Left));
    }

    /// <summary>A tiny negative fraction rounds to negative zero, which .NET prints as "-0%". A zero
    /// fraction has no direction; the sign is dropped before formatting.</summary>
    [Fact]
    public void ATinyNegativeFractionIsZeroNotNegativeZero()
    {
        Assert.Equal("0", Percent.Text(-0.001));
        Assert.Equal(("0", "100"), Percent.Halves(-0.001));

        Assert.Equal("0", Percent.Text(-0.0));
        Assert.Equal(("0", "100"), Percent.Halves(-0.0));
    }

    /// <summary>
    /// The one rule behind the ring's sweep and every flipped figure: spent by default, what is left
    /// when the face is set that way. Unclamped — callers draw 0…1, so an overspent limit reads empty
    /// rather than negative at the draw site, where the clamp belongs.
    /// </summary>
    [Theory]
    [InlineData(0.125, false, 0.125)]
    [InlineData(0.0, false, 0.0)]
    [InlineData(0.125, true, 0.875)]
    [InlineData(0.0, true, 1.0)]
    [InlineData(1.0, true, 0.0)]
    [InlineData(1.28, true, -0.28)]
    [InlineData(1.28, false, 1.28)]
    public void MeteredFlipsTheEndTheMetersDrawFrom(double fraction, bool showingRemaining, double expected)
    {
        Assert.Equal(expected, Percent.Metered(fraction, showingRemaining), 9);
    }
}
