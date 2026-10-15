namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;

/// <summary>
/// The schedule of Codenotch <c>Sources/Model/UsageStore.swift</c>: tick 15 s, busy 30 s, idle 5 min,
/// staleAfter 15 min, fetch deadline 60 s, look spacing 15 s — and the pure rules that decide whether
/// a tick is worth a request: a window that fell between the last attempt and now is fetched at once,
/// and otherwise the busy or idle interval applies.
/// </summary>
public class RefreshScheduleTests
{
    [Fact]
    public void TheShippedDefaultsAreTheStoreDefaults()
    {
        Assert.Equal(15d, RefreshSchedule.TickSeconds);
        Assert.Equal(30d, RefreshSchedule.BusyIntervalSeconds);
        Assert.Equal(300d, RefreshSchedule.IdleIntervalSeconds);
        Assert.Equal(15d, RefreshSchedule.StaleAfterMinutes);
        Assert.Equal(60d, RefreshSchedule.FetchDeadlineSeconds);
        Assert.Equal(15d, RefreshSchedule.OnLookSpacingSeconds);
    }

    [Fact]
    public void ARolloverOutranksWhateverTimeHasPassed()
    {
        var now = TestData.Now;
        var windows = new[] { TestData.Window(resetsAt: now.AddSeconds(-1)) };

        // The last attempt was five seconds ago, far inside the idle interval, but the window
        // rolled over after it: refresh at once.
        Assert.True(RefreshSchedule.ShouldRefresh(now.AddSeconds(-5), now, busy: false, windows));
        Assert.True(RefreshSchedule.ShouldRefresh(now.AddSeconds(-5), now, busy: true, windows));
    }

    [Fact]
    public void TheRolloverBoundaryIsLastAttemptStrictlyBeforeResetsAtAtOrBeforeNow()
    {
        var now = TestData.Now;
        var since = now.AddMinutes(-1);

        Assert.True(RefreshSchedule.HasWindowRolledOver([TestData.Window(resetsAt: now)], since, now));
        Assert.True(RefreshSchedule.HasWindowRolledOver([TestData.Window(resetsAt: since.AddTicks(1))], since, now));

        // Exactly at the last attempt is the boundary that attempt already saw; a future deadline
        // has not happened yet.
        Assert.False(RefreshSchedule.HasWindowRolledOver([TestData.Window(resetsAt: since)], since, now));
        Assert.False(RefreshSchedule.HasWindowRolledOver([TestData.Window(resetsAt: now.AddSeconds(1))], since, now));
        Assert.False(RefreshSchedule.HasWindowRolledOver([TestData.Window(resetsAt: null)], since, now));
        Assert.False(RefreshSchedule.HasWindowRolledOver([], since, now));

        // Without a last attempt there is no boundary to have crossed.
        Assert.False(RefreshSchedule.HasWindowRolledOver([TestData.Window(resetsAt: now)], null, now));
    }

    [Fact]
    public void BusyPollsAtThirtySeconds()
    {
        var now = TestData.Now;

        Assert.False(RefreshSchedule.ShouldRefresh(now.AddSeconds(-29), now, busy: true, []));
        Assert.True(RefreshSchedule.ShouldRefresh(now.AddSeconds(-30), now, busy: true, []));
        Assert.True(RefreshSchedule.ShouldRefresh(now.AddSeconds(-31), now, busy: true, []));

        // Busy does not make the idle interval apply: twenty-nine seconds in, idle still waits.
        Assert.False(RefreshSchedule.ShouldRefresh(now.AddSeconds(-29), now, busy: false, []));
    }

    [Fact]
    public void IdlePollsAtFiveMinutes()
    {
        var now = TestData.Now;

        Assert.False(RefreshSchedule.ShouldRefresh(now.AddSeconds(-299), now, busy: false, []));
        Assert.True(RefreshSchedule.ShouldRefresh(now.AddSeconds(-300), now, busy: false, []));
        Assert.True(RefreshSchedule.ShouldRefresh(now.AddSeconds(-301), now, busy: false, []));
    }

    /// <summary>A provider's own intervals replace the defaults: QuotaDeck keeps them per reader.</summary>
    [Fact]
    public void TheIntervalsComeFromTheReadersConfig()
    {
        var now = TestData.Now;

        Assert.False(RefreshSchedule.ShouldRefresh(now.AddSeconds(-44), now, busy: true, [], busyIntervalSeconds: 45));
        Assert.True(RefreshSchedule.ShouldRefresh(now.AddSeconds(-45), now, busy: true, [], busyIntervalSeconds: 45));
        Assert.True(RefreshSchedule.ShouldRefresh(now.AddSeconds(-600), now, busy: false, [], idleIntervalSeconds: 600));
    }

    [Fact]
    public void AFirstRunIsNeverHeldBack()
    {
        var now = TestData.Now;

        Assert.True(RefreshSchedule.ShouldRefresh(null, now, busy: false, []));
        Assert.True(RefreshSchedule.ShouldRefresh(null, now, busy: true, []));
    }

    [Fact]
    public void LooksAreSpacedFifteenSecondsApart()
    {
        Assert.False(RefreshSchedule.ShouldRefreshOnEvent(14.9));
        Assert.True(RefreshSchedule.ShouldRefreshOnEvent(15));
        Assert.True(RefreshSchedule.ShouldRefreshOnEvent(15.001));

        Assert.False(RefreshSchedule.ShouldRefreshOnEvent(9.9, spacingSeconds: 10));
        Assert.True(RefreshSchedule.ShouldRefreshOnEvent(10, spacingSeconds: 10));
    }
}
