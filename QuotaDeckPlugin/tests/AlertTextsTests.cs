namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Alerts;
using QuotaDeck.Core.Model;

/// <summary>
/// The notification copy of Codenotch <c>Sources/Model/ThresholdNotifier.swift</c>: crossing titles
/// change at 100%, the window label is lowercased inside the sentence, a spent limit names its
/// reset when it knows one, and a reset says the limit is available again.
/// </summary>
public class AlertTextsTests
{
    private static readonly DateTimeOffset Resets = new(new DateTime(2027, 3, 13, 15, 30, 0, DateTimeKind.Local));

    [Fact]
    public void TheEightyPercentCrossingSaysHowMuchIsUsed()
    {
        var alert = new ThresholdAlert(80, "claude", "Claude", "Session", 85, null);

        var message = AlertTexts.ForThreshold(alert);

        Assert.Equal("Claude is at 85%", message.Title);
        Assert.Equal("85% of its session limit used.", message.Body);
    }

    [Fact]
    public void TheHundredPercentCrossingNamesItsReset()
    {
        var alert = new ThresholdAlert(100, "claude", "Claude", "5-hour", 100, Resets);

        var message = AlertTexts.ForThreshold(alert);

        Assert.Equal("Claude limit reached", message.Title);
        Assert.Equal("Its 5-hour limit is spent — resets 3:30 PM", message.Body);
    }

    [Fact]
    public void ASpentLimitWithoutAResetSaysSoPlainly()
    {
        var alert = new ThresholdAlert(100, "claude", "Claude", "Weekly", 100, null);

        Assert.Equal("Its weekly limit is spent.", AlertTexts.ForThreshold(alert).Body);
    }

    [Fact]
    public void AResetSaysTheLimitIsAvailableAgain()
    {
        var usageEvent = new UsageAlertEvent(UsageAlertKind.Reset, "claude", "Claude", "Weekly", 0.95, 0.02, null);

        var message = AlertTexts.ForUsage(usageEvent);

        Assert.Equal("Claude has reset", message.Title);
        Assert.Equal("Its weekly limit is available again.", message.Body);
    }

    [Fact]
    public void ReachingTheWeeklyLimitNamesItsReset()
    {
        var usageEvent = new UsageAlertEvent(UsageAlertKind.WeeklyLimitReached, "claude", "Claude", "Weekly", 0.96, 1.0, Resets);

        var message = AlertTexts.ForUsage(usageEvent);

        Assert.Equal("Claude limit reached", message.Title);
        Assert.Equal("Its weekly limit is spent — resets 3:30 PM", message.Body);
    }

    [Fact]
    public void ATimedSessionLimitWithoutAResetSaysSoPlainly()
    {
        var usageEvent = new UsageAlertEvent(UsageAlertKind.SessionLimitReached, "codex", "Codex", "5-hour", 0.9, 1.0, null);

        var message = AlertTexts.ForUsage(usageEvent);

        Assert.Equal("Codex limit reached", message.Title);
        Assert.Equal("Its 5-hour limit is spent.", message.Body);
    }
}
