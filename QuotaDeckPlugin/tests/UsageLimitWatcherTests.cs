namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;

/// <summary>
/// Mirrors Codenotch <c>Tests/UsageLimitWatcherTests.swift</c>: the session and weekly latches fire
/// once when a limit is reached, clear when the window actually rolls over or usage drops, and never
/// treat a launch or an archived reading as a crossing.
/// </summary>
public class UsageLimitWatcherTests
{
    private readonly List<UsageAlertEvent> _events = [];
    private readonly HashSet<string> _muted = [];
    private readonly UsageLimitWatcher _watcher;
    private DateTimeOffset _clock = TestData.Now;

    public UsageLimitWatcherTests() =>
        this._watcher = new UsageLimitWatcher(
            isMuted: id => this._muted.Contains(id),
            deliver: this._events.Add,
            now: () => this._clock);

    private static ProviderSnapshot Snapshot(
        string id,
        string name,
        double sessionFraction,
        double weeklyFraction = 0.50,
        DateTimeOffset? sessionResetsAt = null,
        DateTimeOffset? weeklyResetsAt = null,
        UsageBlock? block = null)
    {
        var windows = new List<LimitWindow>
        {
            TestData.Window(id: "session", label: "5-hour limit", usedFraction: sessionFraction, resetsAt: sessionResetsAt),
        };

        if (weeklyFraction > 0)
        {
            windows.Add(TestData.Window(id: "weekly", label: "Weekly limit", usedFraction: weeklyFraction, resetsAt: weeklyResetsAt));
        }

        return TestData.Snapshot(
            id: id,
            displayName: name,
            windows: windows,
            headlineId: "session",
            weeklyId: weeklyFraction > 0 ? "weekly" : null,
            block: block);
    }

    private static ProviderSnapshot Stale(ProviderSnapshot snapshot) =>
        snapshot with { Status = ProviderStatus.Stale(TestData.Now) };

    [Fact]
    public void NoAlertOnInitialObservation()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.0, weeklyFraction: 1.0));
        Assert.Empty(this._events);
    }

    /// <summary>
    /// The store's first publication for a provider can be a placeholder with no window at all.
    /// Counting that as the baseline made the first real reading a "crossing".
    /// </summary>
    [Fact]
    public void APlaceholderIsNotTheBaseline()
    {
        var placeholder = TestData.Snapshot(
            id: "codex",
            displayName: "Codex",
            windows: [],
            headlineId: null,
            status: ProviderStatus.Stale(TestData.Now));

        this._watcher.Observe(placeholder);
        this._watcher.Observe(Snapshot("codex", "Codex", sessionFraction: 1.0, weeklyFraction: 1.0));
        Assert.Empty(this._events);

        this._watcher.Observe(Snapshot("codex", "Codex", sessionFraction: 0.2, weeklyFraction: 0.2));
        this._watcher.Observe(Snapshot("codex", "Codex", sessionFraction: 1.0, weeklyFraction: 0.2));
        Assert.Equal(new[] { UsageAlertKind.SessionLimitReached }, this._events.Select(usageEvent => usageEvent.Kind));
    }

    /// <summary>The archived reading published at launch is marked stale and is not a baseline.</summary>
    [Fact]
    public void AnArchivedReadingIsNotTheBaseline()
    {
        this._watcher.Observe(Stale(Snapshot("codex", "Codex", sessionFraction: 0.9)));
        this._watcher.Observe(Snapshot("codex", "Codex", sessionFraction: 1.0));
        Assert.Empty(this._events);
    }

    /// <summary>The weekly window can arrive a fetch after the session one; its own first reading is
    /// its baseline.</summary>
    [Fact]
    public void AWeeklyWindowThatArrivesSpentIsSilent()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.5, weeklyFraction: 0));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.5, weeklyFraction: 1.0));
        Assert.Empty(this._events);
    }

    [Fact]
    public void AlertsWhenSessionLimitReached()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.80));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00));

        Assert.Single(this._events);
        Assert.Equal(UsageAlertKind.SessionLimitReached, this._events[0].Kind);
        Assert.Equal("claude", this._events[0].ProviderId);
        Assert.Equal("Claude", this._events[0].ProviderName);
        Assert.Equal(1.00, this._events[0].CurrentFraction, 4);
        Assert.Equal(0.80, this._events[0].PreviousFraction, 4);
    }

    [Fact]
    public void AlertsWhenWeeklyLimitReached()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.50, weeklyFraction: 0.80));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.50, weeklyFraction: 1.00));

        Assert.Single(this._events);
        Assert.Equal(UsageAlertKind.WeeklyLimitReached, this._events[0].Kind);
        Assert.Equal("Weekly limit", this._events[0].WindowLabel);
    }

    [Fact]
    public void AlertsWhenBlocked()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.50));
        this._watcher.Observe(Snapshot(
            "claude",
            "Claude",
            sessionFraction: 0.50,
            block: new UsageBlock { Reason = "Rate limited" }));

        Assert.Single(this._events);
        Assert.Equal(UsageAlertKind.SessionLimitReached, this._events[0].Kind);
    }

    /// <summary>A spent week blocks the headline with it, but the session card must not claim the
    /// session itself spent: the weekly alert is the accurate one.</summary>
    [Fact]
    public void WeeklyCausedBlockDoesNotFireSessionAlert()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.50, weeklyFraction: 0.80));
        this._watcher.Observe(Snapshot(
            "claude",
            "Claude",
            sessionFraction: 0.50,
            weeklyFraction: 1.00,
            block: new UsageBlock { Reason = "Weekly limit reached", IsWeeklyExhaustion = true }));

        Assert.Equal(new[] { UsageAlertKind.WeeklyLimitReached }, this._events.Select(usageEvent => usageEvent.Kind));
    }

    /// <summary>The weekly id points at the headline window, so no weekly alert fires — and the
    /// weekly-caused block must not fire a session one with the wrong copy either.</summary>
    [Fact]
    public void WeeklyCausedBlockUnderASwappedHeadlineStaysSilent()
    {
        static ProviderSnapshot Swapped(UsageBlock? block) =>
            TestData.Snapshot(
                windows:
                [
                    TestData.Window(id: "pace", label: "Daily pace", usedFraction: 0.5),
                    TestData.Window(id: "session", label: "Session", usedFraction: 0.6),
                ],
                headlineId: "pace",
                weeklyId: "session",
                block: block);

        this._watcher.Observe(Swapped(block: null));
        this._watcher.Observe(Swapped(block: new UsageBlock { Reason = "Weekly limit reached", IsWeeklyExhaustion = true }));
        Assert.Empty(this._events);
    }

    [Fact]
    public void NoRepeatAlertWhileExhausted()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.80));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00));
        Assert.Single(this._events);

        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.02));
        Assert.Single(this._events);
    }

    [Fact]
    public void ReArmsWhenUsageDropsBelowTheLatch()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.80));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00));
        Assert.Single(this._events);

        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.90));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00));
        Assert.Equal(2, this._events.Count);
    }

    /// <summary>0.95 is the re-arm edge: 0.96 is still the same spent window.</summary>
    [Fact]
    public void TheReArmEdgeIsNinetyFive()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.80));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00));

        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.96));
        Assert.Single(this._events);

        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.94));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00));
        Assert.Equal(2, this._events.Count);
    }

    [Fact]
    public void ReArmsWhenResetsAtRollsOver()
    {
        var date1 = DateTimeOffset.FromUnixTimeSeconds(1000);
        var date2 = DateTimeOffset.FromUnixTimeSeconds(5000);
        this._clock = DateTimeOffset.FromUnixTimeSeconds(6000);

        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.80, sessionResetsAt: date1));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00, sessionResetsAt: date1));
        Assert.Single(this._events);

        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00, sessionResetsAt: date2));
        Assert.Equal(2, this._events.Count);
    }

    /// <summary>
    /// A countdown-based reset date drifts forward a few seconds on every refresh. While the window
    /// has not elapsed that is the same window, and a limit that stays spent must not be announced
    /// again on every fetch.
    /// </summary>
    [Fact]
    public void NoRepeatAlertWhileResetDateDrifts()
    {
        var resetsAt = TestData.Now.AddDays(3);
        this._watcher.Observe(Snapshot("codex", "Codex", sessionFraction: 0.50, weeklyFraction: 0.90, weeklyResetsAt: resetsAt));
        this._watcher.Observe(Snapshot("codex", "Codex", sessionFraction: 0.50, weeklyFraction: 1.00, weeklyResetsAt: resetsAt));
        Assert.Equal(new[] { UsageAlertKind.WeeklyLimitReached }, this._events.Select(usageEvent => usageEvent.Kind));

        for (var drift = 1; drift <= 5; drift++)
        {
            this._watcher.Observe(Snapshot(
                "codex",
                "Codex",
                sessionFraction: 0.50,
                weeklyFraction: 1.00,
                weeklyResetsAt: resetsAt.AddSeconds(drift)));
        }

        Assert.Single(this._events);
    }

    [Fact]
    public void MutedProviderDoesNotAlert()
    {
        this._muted.Add("claude");
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.80));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00));
        Assert.Empty(this._events);
    }

    [Fact]
    public void TracksMultipleProvidersIndependently()
    {
        this._watcher.Observe([
            Snapshot("claude", "Claude", sessionFraction: 0.50),
            Snapshot("cursor", "Cursor", sessionFraction: 0.50),
        ]);

        this._watcher.Observe([
            Snapshot("claude", "Claude", sessionFraction: 1.00),
            Snapshot("cursor", "Cursor", sessionFraction: 0.60),
        ]);
        Assert.Single(this._events);
        Assert.Equal("claude", this._events[0].ProviderId);

        this._watcher.Observe([
            Snapshot("claude", "Claude", sessionFraction: 1.00),
            Snapshot("cursor", "Cursor", sessionFraction: 1.00),
        ]);
        Assert.Equal(2, this._events.Count);
        Assert.Equal("cursor", this._events[1].ProviderId);
    }

    /// <summary>Extras reuse the same "5h limit" label as the headline; the watcher must still key
    /// off the declared headline and weekly ids.</summary>
    [Fact]
    public void ExtrasAtLimitDoNotFireSessionOrWeeklyCards()
    {
        static ProviderSnapshot Snap(double session, double weekly, double spark) =>
            TestData.Snapshot(
                id: "codex",
                displayName: "Codex",
                windows:
                [
                    TestData.Window(id: "primary", label: "5h limit", usedFraction: session),
                    TestData.Window(id: "secondary", label: "Weekly limit", usedFraction: weekly),
                    TestData.Window(id: "spark", label: "5h limit", usedFraction: spark, group: "Spark"),
                    TestData.Window(id: "spark-secondary", label: "Weekly limit", usedFraction: 1.0, group: "Spark"),
                    TestData.Window(id: "code-review", label: "Weekly limit", usedFraction: 1.0, group: "Code review"),
                ],
                headlineId: "primary",
                weeklyId: "secondary");

        this._watcher.Observe(Snap(session: 0.50, weekly: 0.40, spark: 0.80));
        this._watcher.Observe(Snap(session: 0.50, weekly: 0.40, spark: 1.00));
        Assert.Empty(this._events);

        this._watcher.Observe(Snap(session: 1.00, weekly: 0.40, spark: 1.00));
        Assert.Equal(new[] { UsageAlertKind.SessionLimitReached }, this._events.Select(usageEvent => usageEvent.Kind));
        Assert.Equal("5h limit", this._events[0].WindowLabel);

        this._watcher.Observe(Snap(session: 1.00, weekly: 1.00, spark: 1.00));
        Assert.Equal(
            new[] { UsageAlertKind.SessionLimitReached, UsageAlertKind.WeeklyLimitReached },
            this._events.Select(usageEvent => usageEvent.Kind));
    }

    /// <summary>The watcher exposes the two kinds as separate events, which is what the panel pulse
    /// and a macOS notification will each subscribe to.</summary>
    [Fact]
    public void TheEventsAreRaisedAsSeparateSubscriptions()
    {
        var session = 0;
        var weekly = 0;
        this._watcher.SessionLimitReached += _ => session++;
        this._watcher.WeeklyLimitReached += _ => weekly++;

        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.5, weeklyFraction: 0.5));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.0, weeklyFraction: 1.0));

        Assert.Equal(1, session);
        Assert.Equal(1, weekly);
    }

    /// <summary>A stale snapshot wipes the provider's state, so the next live reading only seeds.</summary>
    [Fact]
    public void AStaleSnapshotWipesTheState()
    {
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 0.50));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00));
        Assert.Single(this._events);

        this._watcher.Observe(Stale(Snapshot("claude", "Claude", sessionFraction: 0.50)));
        this._watcher.Observe(Snapshot("claude", "Claude", sessionFraction: 1.00));
        Assert.Single(this._events);
    }
}
