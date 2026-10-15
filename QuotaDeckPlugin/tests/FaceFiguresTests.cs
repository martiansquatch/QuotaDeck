namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Settings;

/// <summary>
/// FaceFigures gathers the drawing-site rules of Codenotch's <c>ProviderRing</c> (its <c>band</c>,
/// <c>displayFraction</c> and <c>displayWeeklyFraction</c>) and
/// <c>ProviderSnapshot.headlineText(showingRemaining:)</c>: the remaining/used toggle flips the big
/// figure and the ring sweep, and the weekly figure flips with them — but the band never moves,
/// because a band judges what was spent, not which end is displayed.
/// </summary>
public class FaceFiguresTests
{
    private static ProviderSnapshot Snapshot(double session, double weekly) =>
        TestData.SessionAndWeekly(session, weekly);

    [Fact]
    public void ShowingRemainingFlipsTheHeadlineTheRingAndTheWeeklyFigure()
    {
        var figures = FaceFigures.For(Snapshot(0.73, 0.41), showRemaining: true);

        Assert.Equal("27%", figures.Headline);
        Assert.Equal("wk 59%", figures.Secondary);
        Assert.Equal(0.27, figures.RingSweep!.Value, 10);
        Assert.Equal(UsageBand.Critical, figures.Band);
    }

    [Fact]
    public void ShowingUsedIsTheMirror()
    {
        var figures = FaceFigures.For(Snapshot(0.73, 0.41), showRemaining: false);

        Assert.Equal("73%", figures.Headline);
        Assert.Equal("wk 41%", figures.Secondary);
        Assert.Equal(0.73, figures.RingSweep!.Value, 10);
        Assert.Equal(UsageBand.Critical, figures.Band);
    }

    /// <summary>0.73 is past the critical limit (0.70) whichever end is shown; 0.45 is in the watch
    /// band under custom limits even though more than half remains.</summary>
    [Fact]
    public void TheBandJudgesByUsedWhicheverEndIsShown()
    {
        var settings = new QuotaDeckSettings { ShowRemaining = false, WatchLimit = 0.30, CriticalLimit = 0.60 };

        Assert.Equal(UsageBand.Critical, FaceFigures.For(Snapshot(0.73, 0.41), settings).Band);
        Assert.Equal(UsageBand.Watch, FaceFigures.For(Snapshot(0.45, 0.41), settings).Band);
    }

    [Fact]
    public void ASnapshotWithoutAHeadlineYieldsNoPercentText()
    {
        var snapshot = TestData.Snapshot(windows: [], headlineId: null);

        var figures = FaceFigures.For(snapshot, showRemaining: true);

        Assert.Equal("—", figures.Headline);
        Assert.Null(figures.Secondary);
        Assert.Null(figures.RingSweep);
        Assert.Null(figures.Band);
    }
}
