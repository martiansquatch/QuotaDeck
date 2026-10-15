namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;

/// <summary>
/// Mirrors Codenotch <c>Tests/ThresholdNotifierTests.swift</c>: 80% and 100% are announced once as
/// they are crossed, never twice while they stay crossed, and again only after the window has
/// genuinely rolled over.
/// </summary>
public class ThresholdNotifierTests
{
    private readonly List<ThresholdAlert> _alerts = [];
    private readonly HashSet<string> _muted = [];
    private readonly ThresholdNotifier _notifier;

    public ThresholdNotifierTests() =>
        this._notifier = new ThresholdNotifier(
            isMuted: id => this._muted.Contains(id),
            deliver: this._alerts.Add);

    private static ProviderSnapshot Snapshot(string id, string name, double fraction, string label = "Current session") =>
        TestData.Snapshot(
            id: id,
            displayName: name,
            windows: [TestData.Window(id: "session", label: label, usedFraction: fraction)],
            headlineId: "session");

    private static ProviderSnapshot Stale(ProviderSnapshot snapshot) =>
        snapshot with { Status = ProviderStatus.Stale(TestData.Now) };

    [Fact]
    public void CrossingEightyAlertsOnce()
    {
        this._notifier.Observe(Snapshot("claude", "Claude", 0.5));
        Assert.Empty(this._alerts);

        this._notifier.Observe(Snapshot("claude", "Claude", 0.82));
        this._notifier.Observe(Snapshot("claude", "Claude", 0.91));
        Assert.Single(this._alerts);
        Assert.Equal(80, this._alerts[0].Threshold);
        Assert.Equal(82, this._alerts[0].UsedPercent);
        Assert.Equal("Current session", this._alerts[0].WindowLabel);
    }

    [Fact]
    public void CrossingHundredAfterEightyAlertsAgain()
    {
        this._notifier.Observe(Snapshot("claude", "Claude", 0.5));
        this._notifier.Observe(Snapshot("claude", "Claude", 0.85));
        this._notifier.Observe(Snapshot("claude", "Claude", 1.02));
        Assert.Equal(new[] { 80, 100 }, this._alerts.Select(alert => alert.Threshold));
    }

    /// <summary>One reading that crosses both thresholds sends both, in order.</summary>
    [Fact]
    public void ZeroToAHundredSendsTwoAlerts()
    {
        this._notifier.Observe(Snapshot("claude", "Claude", 0.0));
        this._notifier.Observe(Snapshot("claude", "Claude", 1.0));
        Assert.Equal(new[] { 80, 100 }, this._alerts.Select(alert => alert.Threshold));
        Assert.Equal(100, this._alerts[1].UsedPercent);
    }

    /// <summary>No rounding: 79.99 is level 0, and 85 is a crossing.</summary>
    [Fact]
    public void TheLevelIsNotRounded()
    {
        this._notifier.Observe(Snapshot("claude", "Claude", 0.7999));
        this._notifier.Observe(Snapshot("claude", "Claude", 0.79995));
        Assert.Empty(this._alerts);

        this._notifier.Observe(Snapshot("claude", "Claude", 0.85));
        this._notifier.Observe(Snapshot("claude", "Claude", 0.91));
        Assert.Single(this._alerts);
        Assert.Equal(85, this._alerts[0].UsedPercent);
    }

    /// <summary>The archived reading published at launch is marked stale, and the live one that
    /// follows is the baseline, not a crossing.</summary>
    [Fact]
    public void AnArchivedReadingIsNotTheBaseline()
    {
        this._notifier.Observe(Stale(Snapshot("codex", "Codex", 0.5)));
        this._notifier.Observe(Snapshot("codex", "Codex", 1.0));
        Assert.Empty(this._alerts);

        this._notifier.Observe(Snapshot("codex", "Codex", 0.1));
        this._notifier.Observe(Snapshot("codex", "Codex", 0.9));
        Assert.Equal(new[] { 80 }, this._alerts.Select(alert => alert.Threshold));
    }

    /// <summary>The first reading is the baseline, whatever it says: a launch is not a crossing.</summary>
    [Fact]
    public void TheFirstReadingOnlyRecordsEvenAtTheLimit()
    {
        this._notifier.Observe(Snapshot("codex", "Codex", 1.0));
        Assert.Empty(this._alerts);

        this._notifier.Observe(Snapshot("codex", "Codex", 1.0));
        Assert.Empty(this._alerts);
        this._notifier.Observe(Snapshot("codex", "Codex", 0.05));
        this._notifier.Observe(Snapshot("codex", "Codex", 0.85));
        Assert.Equal(new[] { 80 }, this._alerts.Select(alert => alert.Threshold));
    }

    /// <summary>A spent window that comes back is a new fact, not a re-announcement of the old one —
    /// so the memory clears and the next climb alerts again. Re-arm is below 80, the first threshold.</summary>
    [Fact]
    public void ARolledOverWindowAlertsAgain()
    {
        this._notifier.Observe(Snapshot("claude", "Claude", 0.2));
        this._notifier.Observe(Snapshot("claude", "Claude", 0.9));
        this._notifier.Observe(Snapshot("claude", "Claude", 0.1));
        Assert.Single(this._alerts);

        this._notifier.Observe(Snapshot("claude", "Claude", 0.84));
        Assert.Equal(2, this._alerts.Count);
        Assert.Equal(80, this._alerts[1].Threshold);
    }

    [Fact]
    public void MutedProvidersAreSilentButRemembered()
    {
        this._notifier.Observe(Snapshot("claude", "Claude", 0.1));
        this._muted.Add("claude");
        this._notifier.Observe(Snapshot("claude", "Claude", 0.85));
        Assert.Empty(this._alerts);

        // Unmuting must not replay the crossing that happened in the silence.
        this._muted.Clear();
        this._notifier.Observe(Snapshot("claude", "Claude", 0.86));
        Assert.Empty(this._alerts);

        // But the next real crossing still reaches the user.
        this._notifier.Observe(Snapshot("claude", "Claude", 1.0));
        Assert.Equal(new[] { 100 }, this._alerts.Select(alert => alert.Threshold));
    }

    [Fact]
    public void ProvidersWithoutARingAreIgnored()
    {
        var snapshot = TestData.Snapshot(
            id: "p",
            displayName: "P",
            windows: [TestData.Window(id: "q", label: "Free queries", remaining: 12)],
            headlineId: "q");

        this._notifier.Observe([snapshot, snapshot]);
        Assert.Empty(this._alerts);
    }

    [Fact]
    public void SeveralProvidersAlertIndependently()
    {
        this._notifier.Observe([Snapshot("claude", "Claude", 0.3), Snapshot("cursor", "Cursor", 0.4)]);
        this._notifier.Observe([Snapshot("claude", "Claude", 0.3), Snapshot("cursor", "Cursor", 0.95)]);
        Assert.Equal(new[] { "cursor" }, this._alerts.Select(alert => alert.ProviderId));

        this._notifier.Observe([Snapshot("claude", "Claude", 0.99), Snapshot("cursor", "Cursor", 0.96)]);
        Assert.Equal(new[] { "cursor", "claude" }, this._alerts.Select(alert => alert.ProviderId));
        Assert.Equal(80, this._alerts[^1].Threshold);
    }

    /// <summary>Extras at 100% are not a headline crossing: the notifier reads the declared headline.</summary>
    [Fact]
    public void ExtrasDoNotCountAsAHeadlineCrossing()
    {
        static ProviderSnapshot Snap(double primary, double extra) =>
            TestData.Snapshot(
                id: "codex",
                displayName: "Codex",
                windows:
                [
                    TestData.Window(id: "primary", label: "5h limit", usedFraction: primary),
                    TestData.Window(id: "spark", label: "5h limit", usedFraction: extra, group: "Spark"),
                ],
                headlineId: "primary");

        this._notifier.Observe(Snap(0.50, 0.50));
        this._notifier.Observe(Snap(0.50, 1.00));
        Assert.Empty(this._alerts);

        this._notifier.Observe(Snap(0.85, 1.00));
        Assert.Equal(new[] { 80 }, this._alerts.Select(alert => alert.Threshold));
        Assert.Equal("5h limit", this._alerts[0].WindowLabel);
    }

    /// <summary>The alert carries the reset time the headline reported, and the name the reader
    /// declares.</summary>
    [Fact]
    public void TheAlertCarriesTheHeadlineResetTime()
    {
        var resetsAt = TestData.Now.AddHours(2);
        var snapshot = TestData.Snapshot(
            id: "claude",
            displayName: "Claude",
            windows: [TestData.Window(id: "session", label: "Session", usedFraction: 1.0, resetsAt: resetsAt)],
            headlineId: "session");

        this._notifier.Observe(snapshot with { Windows = [TestData.Window(id: "session", label: "Session", usedFraction: 0.1)] });
        this._notifier.Observe(snapshot);

        Assert.Equal(2, this._alerts.Count);
        Assert.All(this._alerts, alert => Assert.Equal(resetsAt, alert.ResetsAt));
        Assert.All(this._alerts, alert => Assert.Equal("Claude", alert.ProviderName));
        Assert.Equal(100, this._alerts[0].UsedPercent);
    }
}
