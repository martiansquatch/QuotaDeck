namespace QuotaDeck.Readers.Tests.OpenCode;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;
using QuotaDeck.Readers.OpenCode;

/// <summary>
/// The Go plan windows, pinned to the live response shape (secret redacted) — the same figures the
/// OpenCode dashboard shows. Port of Codenotch <c>Tests/OpenCodeUsageTests.swift</c>.
/// </summary>
public class OpenCodeUsageTests
{
    [Fact]
    public void ReadsAllThreeWindows()
    {
        var windows = OpenCodeUsage.Windows(OpenCodeTestReader.LivePayload);

        Assert.Equal(
            [TimeSpan.FromSeconds(18000), TimeSpan.FromSeconds(604800), TimeSpan.FromDays(30)],
            windows.Select(window => window.Duration!.Value));
        Assert.Equal(["rolling", "weekly", "monthly"], windows.Select(window => window.Id));
        Assert.Equal(["5h limit", "Weekly limit", "Monthly limit"], windows.Select(window => window.Label));
        Assert.All(windows, window => Assert.Equal(0.0, window.UsedFraction));
    }

    /// <summary>
    /// <c>percent</c> is used, matching the dashboard's "X% used" — the ring must not invert it.
    /// </summary>
    [Fact]
    public void PercentIsUsedNotRemaining()
    {
        const string Json = """
            {"usage":{"rolling":{"status":"ok","percent":65,"resetsAt":"2026-09-06T12:31:06.611Z"}}}
            """;

        var window = Assert.Single(OpenCodeUsage.Windows(Json));
        Assert.Equal(0.65, window.UsedFraction!.Value, precision: 4);
    }

    /// <summary>
    /// The reset carries milliseconds, which the plain ISO8601 formatter refuses — reading only that
    /// form silently loses every reset time.
    /// </summary>
    [Fact]
    public void ReadsAMillisecondResetTime()
    {
        var rolling = Assert.Single(OpenCodeUsage.Windows(OpenCodeTestReader.LivePayload), window => window.Id == "rolling");
        var reset = rolling.ResetsAt;

        Assert.NotNull(reset);
        var plain = DateTimeOffset.Parse("2026-09-06T12:31:06Z");
        Assert.True(Math.Abs((reset.Value - plain).TotalSeconds) < 1);
    }

    /// <summary>
    /// A window without a percent is dropped rather than invented; with none left at all that is a
    /// bad response, not zeros.
    /// </summary>
    [Fact]
    public void WindowsWithoutAPercentAreDropped()
    {
        const string Json = """
            {"usage":{"rolling":{"status":"ok"},"weekly":{"status":"ok","percent":3}}}
            """;

        var window = Assert.Single(OpenCodeUsage.Windows(Json));
        Assert.Equal("weekly", window.Id);
    }

    /// <summary>
    /// <c>status</c> is ignored, and a reset means nothing without a percent to draw.
    /// </summary>
    [Fact]
    public void AWindowWithoutAPercentIsDroppedEvenWithAStatusAndAReset()
    {
        const string Json = """
            {"usage":{"rolling":{"status":"limited","resetsAt":"2026-09-06T12:31:06.611Z"},"weekly":{"percent":""}}}
            """;

        Assert.Throws<ReaderException>(() => OpenCodeUsage.Windows(Json));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"usage":{}}""")]
    [InlineData("""{"usage":{"rolling":{"status":"ok"}}}""")]
    [InlineData("not json at all")]
    public void AnEmptyUsageIsNotAReading(string json)
    {
        var error = Assert.Throws<ReaderException>(() => OpenCodeUsage.Windows(json));
        Assert.Equal(ReaderErrorKind.BadResponse, error.Error.Kind);
        Assert.Equal(0, error.Error.Status);
    }

    /// <summary>
    /// The fixed windows keep their length even without a reset; monthly derives from the reset and
    /// has nothing to derive from without one.
    /// </summary>
    [Fact]
    public void DurationsFollowTheSwiftRules()
    {
        const string Json = """
            {"usage":{"rolling":{"percent":1},"weekly":{"percent":1},"monthly":{"percent":1}}}
            """;

        var windows = OpenCodeUsage.Windows(Json);
        Assert.Equal(TimeSpan.FromSeconds(18000), windows.Single(window => window.Id == "rolling").Duration);
        Assert.Equal(TimeSpan.FromSeconds(604800), windows.Single(window => window.Id == "weekly").Duration);
        Assert.Null(windows.Single(window => window.Id == "monthly").Duration);
        Assert.Null(windows.Single(window => window.Id == "rolling").ResetsAt);
    }

    /// <summary>
    /// One UTC Gregorian month, not thirty days: a January reset is 31 days after its December start.
    /// </summary>
    [Fact]
    public void TheMonthlyDurationIsACalendarMonth()
    {
        const string Json = """
            {"usage":{"monthly":{"percent":0,"resetsAt":"2027-01-15T10:00:00.000Z"}}}
            """;

        var window = Assert.Single(OpenCodeUsage.Windows(Json));
        Assert.Equal(TimeSpan.FromDays(31), window.Duration);
    }
}
