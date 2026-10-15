namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;

/// <summary>
/// Mirrors Codenotch <c>Tests/UsageResetWatcherTests.swift</c>: a reset is announced when the
/// headline window's own deadline elapsed, or when its usage dropped far enough for the drop itself
/// to be the evidence — never for a drifting future timestamp or a correction before the deadline.
/// </summary>
public class UsageResetWatcherTests
{
    private readonly List<UsageAlertEvent> _events = [];
    private readonly HashSet<string> _muted = [];
    private readonly UsageResetWatcher _watcher;
    private DateTimeOffset _clock = TestData.Now;

    public UsageResetWatcherTests() =>
        this._watcher = new UsageResetWatcher(
            isMuted: id => this._muted.Contains(id),
            deliver: this._events.Add,
            now: () => this._clock);

    private static ProviderSnapshot Snapshot(
        string id,
        string name,
        double fraction,
        DateTimeOffset? resetsAt = null,
        string label = "5-hour limit") =>
        TestData.Snapshot(
            id: id,
            displayName: name,
            windows: [TestData.Window(id: "session", label: label, usedFraction: fraction, resetsAt: resetsAt)],
            headlineId: "session");

    [Fact]
    public void NoAlertOnInitialObservation()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", 0.85));
        Assert.Empty(this._events);
    }

    /// <summary>At launch the archived reading is published, marked stale, and the live one follows.
    /// A newer reset date between the two is not a reset that happened; it is the archive being old.</summary>
    [Fact]
    public void AnArchivedReadingIsNotTheBaseline()
    {
        var old = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var archived = Snapshot("claude", "Claude", 0.85, resetsAt: old) with { Status = ProviderStatus.Stale(old) };
        this._clock = old.AddHours(1);

        this._watcher.Observe(archived);
        this._watcher.Observe(Snapshot("claude", "Claude", 0.10, resetsAt: old.AddHours(5)));
        Assert.Empty(this._events);

        // The next roll-over, seen live against live once the window has elapsed, is announced.
        this._watcher.Observe(Snapshot("claude", "Claude", 0.60, resetsAt: old.AddHours(5)));
        this._clock = old.AddHours(6);
        this._watcher.Observe(Snapshot("claude", "Claude", 0.05, resetsAt: old.AddHours(10)));
        Assert.Single(this._events);
    }

    [Fact]
    public void AlertsWhenUsageDropsSignificantly()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", 0.90));
        this._watcher.Observe(Snapshot("claude", "Claude", 0.05));

        Assert.Single(this._events);
        Assert.Equal("claude", this._events[0].ProviderId);
        Assert.Equal("Claude", this._events[0].ProviderName);
        Assert.Equal("5-hour limit", this._events[0].WindowLabel);
        Assert.Equal(UsageAlertKind.Reset, this._events[0].Kind);
        Assert.Equal(0.90, this._events[0].PreviousFraction, 4);
        Assert.Equal(0.05, this._events[0].CurrentFraction, 4);
    }

    [Fact]
    public void AlertsWhenResetsAtRolledOver()
    {
        var date1 = DateTimeOffset.FromUnixTimeSeconds(900);
        var date2 = DateTimeOffset.FromUnixTimeSeconds(2_000);
        this._clock = DateTimeOffset.FromUnixTimeSeconds(3_000);

        this._watcher.Observe(Snapshot("claude", "Claude", 0.40, resetsAt: date1));
        this._watcher.Observe(Snapshot("claude", "Claude", 0.05, resetsAt: date2));

        Assert.Single(this._events);
        Assert.Equal(date2, this._events[0].ResetsAt);
    }

    [Fact]
    public void DoesNotAlertWhenAFutureResetTimestampDriftsLater()
    {
        var firstDeadline = DateTimeOffset.FromUnixTimeSeconds(2_000);
        var shiftedDeadline = DateTimeOffset.FromUnixTimeSeconds(2_001);

        this._watcher.Observe(Snapshot("codex", "Codex", 0.29, resetsAt: firstDeadline));
        this._watcher.Observe(Snapshot("codex", "Codex", 0.29, resetsAt: shiftedDeadline));

        Assert.Empty(this._events);
    }

    [Fact]
    public void DoesNotAlertForALargeDropBeforeAKnownResetDeadline()
    {
        var deadline = DateTimeOffset.FromUnixTimeSeconds(2_000);

        this._watcher.Observe(Snapshot("codex", "Codex", 0.51, resetsAt: deadline));
        this._watcher.Observe(Snapshot("codex", "Codex", 0.29, resetsAt: deadline));

        Assert.Empty(this._events);
    }

    /// <summary>The window's deadline passed and the fresh reading carries no reset date of its own:
    /// the drop is the only evidence, and it is enough.</summary>
    [Fact]
    public void AlertsWhenTheDeadlinePassedAndTheNewWindowHasNoDate()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", 0.80, resetsAt: DateTimeOffset.FromUnixTimeSeconds(900)));
        this._watcher.Observe(Snapshot("claude", "Claude", 0.04, resetsAt: null));
        Assert.Single(this._events);
    }

    [Fact]
    public void NoAlertForNegligibleFluctuation()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", 0.05));
        this._watcher.Observe(Snapshot("claude", "Claude", 0.01));
        Assert.Empty(this._events);
    }

    /// <summary>
    /// The two edges of "dropped significantly": a full 20 points, or a peak that was real and a
    /// reading back near the floor. A peak under 15% is never enough evidence of anything.
    /// </summary>
    [Fact]
    public void TheDropEdgesAreTwentyPointsOrAPeakThenAFloor()
    {
        // Exactly twenty points: a reset.
        this._watcher.Observe(Snapshot("a", "A", 0.30));
        this._watcher.Observe(Snapshot("a", "A", 0.10));
        Assert.Single(this._events);

        // Nineteen points, peak above 30% but the reading not near the floor: nothing.
        this._watcher.Observe(Snapshot("b", "B", 0.60));
        this._watcher.Observe(Snapshot("b", "B", 0.41));
        Assert.Single(this._events);

        // Peak 40% then back to 10%: a reset, because a real climb collapsed.
        this._watcher.Observe(Snapshot("c", "C", 0.40));
        this._watcher.Observe(Snapshot("c", "C", 0.10));
        Assert.Equal(2, this._events.Count);

        // A peak of 14% is below the significance bar, even dropping to nothing.
        this._watcher.Observe(Snapshot("d", "D", 0.14));
        this._watcher.Observe(Snapshot("d", "D", 0.00));
        Assert.Equal(2, this._events.Count);
    }

    [Fact]
    public void MutedProviderDoesNotAlert()
    {
        this._muted.Add("claude");
        this._watcher.Observe(Snapshot("claude", "Claude", 0.95));
        this._watcher.Observe(Snapshot("claude", "Claude", 0.00));
        Assert.Empty(this._events);
    }

    [Fact]
    public void MultipleProvidersTrackedIndependently()
    {
        this._watcher.Observe([Snapshot("claude", "Claude", 0.80), Snapshot("cursor", "Cursor", 0.10)]);
        this._watcher.Observe([Snapshot("claude", "Claude", 0.02), Snapshot("cursor", "Cursor", 0.70)]);
        Assert.Single(this._events);
        Assert.Equal("claude", this._events[0].ProviderId);

        this._watcher.Observe([Snapshot("claude", "Claude", 0.05), Snapshot("cursor", "Cursor", 0.05)]);
        Assert.Equal(2, this._events.Count);
        Assert.Equal("cursor", this._events[1].ProviderId);
    }

    /// <summary>After an alert the peak resets to the current fraction, so the same drop cannot be
    /// announced twice.</summary>
    [Fact]
    public void ThePeakResetsAfterAnAlert()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", 0.90));
        this._watcher.Observe(Snapshot("claude", "Claude", 0.05));
        Assert.Single(this._events);

        this._watcher.Observe(Snapshot("claude", "Claude", 0.04));
        Assert.Single(this._events);
    }

    [Fact]
    public void AStaleSnapshotWipesTheState()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", 0.90));
        this._watcher.Observe(ProviderStatus.Stale(TestData.Now) is var status
            ? Snapshot("claude", "Claude", 0.90) with { Status = status }
            : Snapshot("claude", "Claude", 0.90));

        this._watcher.Observe(Snapshot("claude", "Claude", 0.05));
        Assert.Empty(this._events);
    }

    /// <summary>The reset is also raised as its own event, which is what a face or a notification
    /// will subscribe to.</summary>
    [Fact]
    public void TheResetIsRaisedAsAnEvent()
    {
        var raised = 0;
        this._watcher.Reset += _ => raised++;

        this._watcher.Observe(Snapshot("claude", "Claude", 0.90));
        this._watcher.Observe(Snapshot("claude", "Claude", 0.05));

        Assert.Equal(1, raised);
    }
}
