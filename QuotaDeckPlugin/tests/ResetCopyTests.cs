namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Text;

/// <summary>
/// Mirrors the countdown half of Codenotch <c>Tests/ResetCopyTests.swift</c>
/// (<c>testCountdownBoundaries</c>, <c>testTheLastMinuteCountsInSeconds</c>,
/// <c>testTheCountdownStepsByMinutesUntilTheLastOne</c>, <c>testTheNextCountdownChangeIsWhereTheTextChanges</c>).
/// The longer "Resets in …" sentence and its calendar formatting are not ported.
/// </summary>
public class ResetCopyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static DateTimeOffset At(double seconds) => Now.AddSeconds(seconds);

    [Theory]
    [InlineData(-30, null)]
    [InlineData(0, null)]
    [InlineData(0.5, "00s")]
    [InlineData(1, "01s")]
    [InlineData(9.9, "09s")]
    [InlineData(42, "42s")]
    [InlineData(59, "59s")]
    [InlineData(59.9, "59s")]
    [InlineData(60, "1m")]
    [InlineData(8 * 60 + 3, "8m")]
    [InlineData(47 * 60 + 50, "47m")]
    [InlineData(59 * 60 + 59, "59m")]
    [InlineData(3600, "1h 00m")]
    [InlineData(2 * 3600 + 5 * 60, "2h 05m")]
    [InlineData(2 * 3600 + 18 * 60 + 20, "2h 18m")]
    [InlineData(4 * 3600 + 59 * 60 + 59, "4h 59m")]
    [InlineData(5 * 3600, "5h 00m")]
    [InlineData(3 * 86400, "72h 00m")]
    public void CountdownBoundaries(double seconds, string? expected)
    {
        Assert.Equal(expected, ResetCopy.Countdown(At(seconds), Now));
    }

    /// <summary>
    /// Truncated, never rounded up: "09s" is always at least nine seconds, never ten. The last minute
    /// counts in seconds because it is the one somebody is actually watching.
    /// </summary>
    [Fact]
    public void TheLastMinuteCountsInSeconds()
    {
        var readings = new[] { 59, 30, 5, 1 }
            .Select(seconds => ResetCopy.Countdown(At(seconds), Now))
            .ToArray();
        Assert.Equal(new[] { "59s", "30s", "05s", "01s" }, readings);
        Assert.Equal(readings.Length, readings.Distinct().Count());
    }

    /// <summary>There is no days unit: three days out is 72 hours on the clock.</summary>
    [Fact]
    public void ThereIsNoDaysUnit()
    {
        Assert.Equal("72h 00m", ResetCopy.Countdown(At(3 * 86400), Now));
        Assert.Equal("26h 00m", ResetCopy.Countdown(At(26 * 3600), Now));
    }

    /// <summary>
    /// The days form of the countdown, a QuotaDeck option rather than a Codenotch port: days once
    /// at least one is left ("5d 23h"), truncated like the hours form, and exactly the same text
    /// below a day.
    /// </summary>
    [Theory]
    [InlineData(5 * 86400 + 23 * 3600 + 59 * 60, "5d 23h")]
    [InlineData(3 * 86400, "3d 00h")]
    [InlineData(24 * 3600, "1d 00h")]
    [InlineData(26 * 3600, "1d 02h")]
    [InlineData(23 * 3600 + 59 * 60 + 59, "23h 59m")]
    [InlineData(2 * 3600 + 5 * 60, "2h 05m")]
    [InlineData(42, "42s")]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    public void CountdownWithDaysUsesDaysAtADayAndDefersBelowIt(double seconds, string? expected)
    {
        Assert.Equal(expected, ResetCopy.CountdownWithDays(At(seconds), Now));
    }

    /// <summary>
    /// The step is a whole minute above the last one and a whole second inside it, so a ticker can
    /// wake itself on this and redraw nothing early.
    /// </summary>
    [Theory]
    [InlineData(5 * 3600)]
    [InlineData(61)]
    public void TheCountdownStepsByMinutesAboveTheLastOne(double seconds)
    {
        var change = ResetCopy.NextCountdownChange(At(seconds), Now);
        Assert.NotNull(change);
        Assert.True(change!.Value <= At(60), $"{seconds}s left should step by a minute");
    }

    [Theory]
    [InlineData(60)]
    [InlineData(42)]
    [InlineData(1)]
    public void TheCountdownStepsBySecondsInsideTheLastOne(double seconds)
    {
        var change = ResetCopy.NextCountdownChange(At(seconds), Now);
        Assert.NotNull(change);
        Assert.True(change!.Value <= At(1), $"{seconds}s left should step by a second");
    }

    /// <summary>
    /// The countdown changes exactly where the next change is said to be: the same just before it,
    /// different just after — so a timer set for it neither wakes early for nothing nor leaves a
    /// stale minute on screen.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(119.5)]
    [InlineData(3600)]
    [InlineData(2 * 3600 + 18 * 60 + 20)]
    public void TheNextCountdownChangeIsWhereTheTextChanges(double seconds)
    {
        var resetsAt = At(seconds);
        var change = ResetCopy.NextCountdownChange(resetsAt, Now);
        Assert.NotNull(change);
        Assert.True(change!.Value >= Now);
        Assert.True(change.Value - Now < TimeSpan.FromSeconds(60), $"more than a minute away for {seconds}s");

        var before = ResetCopy.Countdown(resetsAt, Max(Now, change.Value.AddMilliseconds(-50)));
        Assert.Equal(ResetCopy.Countdown(resetsAt, Now), before);

        var after = ResetCopy.Countdown(resetsAt, change.Value.AddMilliseconds(50));
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void APassedResetHasNoNextChange()
    {
        Assert.Null(ResetCopy.NextCountdownChange(Now, Now));
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
