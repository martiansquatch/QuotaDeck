namespace QuotaDeck.Readers.Tests.Codex;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;
using QuotaDeck.Readers.Codex;

/// <summary>
/// Port of Codenotch <c>Tests/CodexUsageTests.swift</c> (lines 10-345) with the same fixtures —
/// including the free-plan 30-day primary window, the null <c>used_percent</c>, the Spark and
/// GPT-5.3-Codex-Spark extras, code review, and the duplicate-Spark payload. The credit-seat and
/// per-field-tolerance cases have no Swift fixture and are new coverage of
/// <c>Sources/Providers/CodexUsage.swift</c> (lines 127-149 and 320-328).
/// </summary>
public class CodexUsageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static List<LimitWindow> Windows(string json, bool includeExtras = true) =>
        CodexUsage.Windows(json, Now, includeExtras);

    private static double?[] Fractions(IEnumerable<LimitWindow> windows) =>
        windows.Select(window => window.UsedFraction).ToArray();

    [Fact]
    public void BothWindowsAreReadWhenBothArePresent()
    {
        var result = Windows("""
        {"rate_limit":{
          "primary_window":{"used_percent":25,"limit_window_seconds":18000,"reset_at":1800001000},
          "secondary_window":{"used_percent":10,"limit_window_seconds":604800,"reset_at":1800600000}},
         "additional_rate_limits":[{"limit_name":"Spark","rate_limit":{
          "primary_window":{"used_percent":99,"limit_window_seconds":18000}}}],
         "code_review_rate_limit":{
           "primary_window":{"used_percent":90,"limit_window_seconds":604800},
           "secondary_window":{"used_percent":15,"limit_window_seconds":18000}},
         "credits":{"balance":"100"},"model_usage":{"spark":99}}
        """);

        // Spark is 99% used and listed first in the extras, but the ring follows windows[0], which
        // has to stay the main primary.
        Assert.Equal(
            new TimeSpan?[]
            {
                TimeSpan.FromSeconds(18000),
                TimeSpan.FromSeconds(604800),
                TimeSpan.FromSeconds(18000),
                TimeSpan.FromSeconds(604800),
                TimeSpan.FromSeconds(18000),
            },
            result.Select(window => window.Duration));
        Assert.Equal(
            new[] { "primary", "secondary", "spark", "code-review", "code-review-secondary" },
            result.Select(window => window.Id));
        Assert.Equal(
            new string?[] { null, null, "Spark", "Code review", "Code review" },
            result.Select(window => window.Group));
        Assert.Equal(
            new[] { "5h limit", "Weekly limit", "5h limit", "Weekly limit", "5h limit" },
            result.Select(window => window.Label));
        Assert.Equal(new double?[] { 0.25, 0.10, 0.99, 0.90, 0.15 }, Fractions(result));
        Assert.Equal("primary", result[0].Id);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_001_000), result[0].ResetsAt);
    }

    /// <summary>
    /// The reported case: a free-plan account's primary window was 30 days, not 5 hours or 7. The
    /// old parser only recognised two fixed durations and silently dropped anything else.
    /// </summary>
    [Fact]
    public void AMonthlyPrimaryWindowIsNotDropped()
    {
        var result = Windows("""
        {"rate_limit":{"primary_window":{"used_percent":16,"limit_window_seconds":2592000,
        "reset_after_seconds":1838382,"reset_at":1790585722},"secondary_window":null},
         "plan_type":"free"}
        """);

        Assert.Equal(new[] { "primary" }, result.Select(window => window.Id));
        Assert.Equal("free", CodexUsage.Plan("""
        {"rate_limit":{"primary_window":{"used_percent":16,"limit_window_seconds":2592000}},
         "plan_type":"free"}
        """));
        Assert.Equal("Monthly limit", result[0].Label);
        Assert.Equal(0.16, result[0].UsedFraction!.Value, precision: 4);
    }

    /// <summary>A duration that is none of the named buckets still gets a usable label.</summary>
    [Fact]
    public void AnUnrecognisedDurationStillGetsALabel()
    {
        var result = Windows("""
        {"rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":259200}}}
        """);

        Assert.Equal("3d limit", result[0].Label);
    }

    /// <summary>The endpoint can put a weekly-only allowance in primary_window.</summary>
    [Fact]
    public void ANullSecondaryIsDropped()
    {
        var result = Windows("""
        {"rate_limit":{"primary_window":{"used_percent":1,"limit_window_seconds":604800,
        "reset_after_seconds":604119,"reset_at":1789308033},"secondary_window":null}}
        """);

        Assert.Equal(new[] { "primary" }, result.Select(window => window.Id));
        Assert.Equal("Weekly limit", result[0].Label);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_789_308_033), result[0].ResetsAt);
    }

    [Fact]
    public void StillReadsACountdownIfABuildEmitsOne()
    {
        var result = Windows("""
        {"rate_limit":{
        "primary_window":{"used_percent":8,"limit_window_seconds":604800},
        "secondary_window":{"used_percent":0,"limit_window_seconds":18000,"reset_after_seconds":120}}}
        """);

        Assert.Equal(
            new TimeSpan?[] { TimeSpan.FromSeconds(604800), TimeSpan.FromSeconds(18000) },
            result.Select(window => window.Duration));
        Assert.Equal(new[] { "primary", "secondary" }, result.Select(window => window.Id));
        Assert.Equal(0.08, result[0].UsedFraction!.Value, precision: 4);
        Assert.Null(result[0].ResetsAt);
        Assert.Equal(0, result[1].UsedFraction);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_120), result[1].ResetsAt);
    }

    /// <summary>
    /// A null <c>used_percent</c> on one window used to throw the whole fetch away, hiding a good
    /// weekly window behind the bad hourly one. One malformed window is skipped, not fatal.
    /// </summary>
    [Fact]
    public void AWindowMissingUsedPercentIsSkippedRatherThanFailing()
    {
        var result = Windows("""
        {"rate_limit":{
          "primary_window":{"used_percent":null,"limit_window_seconds":18000,"reset_at":1800001000},
          "secondary_window":{"used_percent":29,"limit_window_seconds":604800,"reset_at":1800600000}}}
        """);

        Assert.Equal(new[] { "secondary" }, result.Select(window => window.Id));
        Assert.Equal("Weekly limit", result[0].Label);
        Assert.Equal(0.29, result[0].UsedFraction!.Value, precision: 4);
    }

    /// <summary>A window without a duration still gets a fallback label instead of failing the decode.</summary>
    [Fact]
    public void AWindowMissingItsDurationStillParses()
    {
        var result = Windows("""
        {"rate_limit":{
          "primary_window":{"used_percent":8,"reset_at":1800001000},
          "secondary_window":{"used_percent":42,"limit_window_seconds":604800,"reset_at":1800600000}}}
        """);

        Assert.Equal(new[] { "primary", "secondary" }, result.Select(window => window.Id));
        Assert.Equal("Current session", result[0].Label);
        Assert.Equal("Weekly limit", result[1].Label);
    }

    /// <summary>
    /// Both windows malformed is still an error, not an empty success — the store turns it into
    /// "waiting", not a silent 0%.
    /// </summary>
    [Fact]
    public void BothWindowsMissingLeavesNothingMetered()
    {
        var exception = Assert.Throws<ReaderException>(() => Windows("""
        {"rate_limit":{
          "primary_window":{"used_percent":null,"limit_window_seconds":18000},
          "secondary_window":null}}
        """));

        Assert.Equal(ReaderErrorKind.NothingMetered, exception.Error.Kind);
        Assert.Equal("Codex reported no usage windows", exception.Error.Reason);
    }

    /// <summary>Spark meters a 5-hour window and a weekly one; extras alone are still a valid reading.</summary>
    [Fact]
    public void SparkFiveHourAndWeeklyWindowsAreRead()
    {
        var result = Windows("""
        {"additional_rate_limits":[{"limit_name":"Spark","rate_limit":{
          "primary_window":{"used_percent":40,"limit_window_seconds":18000},
          "secondary_window":{"used_percent":70,"limit_window_seconds":604800}}}]}
        """);

        Assert.Equal(new[] { "spark", "spark-secondary" }, result.Select(window => window.Id));
        Assert.Equal(new string?[] { "Spark", "Spark" }, result.Select(window => window.Group));
        Assert.Equal(new[] { "5h limit", "Weekly limit" }, result.Select(window => window.Label));
        Assert.Equal(new double?[] { 0.40, 0.70 }, Fractions(result));
    }

    /// <summary>An additional limit that is not Spark is not a window we show.</summary>
    [Fact]
    public void AnUnknownAdditionalLimitIsIgnored()
    {
        var result = Windows("""
        {"rate_limit":{
          "primary_window":{"used_percent":25,"limit_window_seconds":18000},
          "secondary_window":{"used_percent":10,"limit_window_seconds":604800}},
         "additional_rate_limits":[{"limit_name":"Credits","rate_limit":{
          "primary_window":{"used_percent":50,"limit_window_seconds":86400}}}]}
        """);

        Assert.Equal(new[] { "primary", "secondary" }, result.Select(window => window.Id));
        Assert.Equal(new double?[] { 0.25, 0.10 }, Fractions(result));
    }

    /// <summary>A null <c>used_percent</c> on Spark must not fail a fetch that already has a good main pair.</summary>
    [Fact]
    public void ANullExtraUsedPercentIsSkippedRatherThanFailing()
    {
        var result = Windows("""
        {"rate_limit":{
          "primary_window":{"used_percent":25,"limit_window_seconds":18000},
          "secondary_window":{"used_percent":10,"limit_window_seconds":604800}},
         "additional_rate_limits":[{"limit_name":"Spark","rate_limit":{
          "primary_window":{"used_percent":null,"limit_window_seconds":18000},
          "secondary_window":{"used_percent":40,"limit_window_seconds":604800}}}],
         "code_review_rate_limit":{
           "primary_window":{"limit_window_seconds":604800},
           "secondary_window":{"used_percent":8,"limit_window_seconds":18000}}}
        """);

        Assert.Equal(
            new[] { "primary", "secondary", "spark-secondary", "code-review-secondary" },
            result.Select(window => window.Id));
        Assert.Equal(
            new string?[] { null, null, "Spark", "Code review" },
            result.Select(window => window.Group));
        Assert.Equal(
            new[] { "5h limit", "Weekly limit", "Weekly limit", "5h limit" },
            result.Select(window => window.Label));
        Assert.Equal(new double?[] { 0.25, 0.10, 0.40, 0.08 }, Fractions(result));
    }

    /// <summary>A string percent used to fail the whole RateLimit decode, so code review's weekly never appeared.</summary>
    [Fact]
    public void AMalformedCodeReviewPrimaryDoesNotDropItsSecondary()
    {
        var result = Windows("""
        {"rate_limit":{"primary_window":{"used_percent":1,"limit_window_seconds":18000}},
         "code_review_rate_limit":{
           "primary_window":{"used_percent":"n/a","limit_window_seconds":604800},
           "secondary_window":{"used_percent":8,"limit_window_seconds":18000}}}
        """);

        Assert.Equal(new[] { "primary", "code-review-secondary" }, result.Select(window => window.Id));
        Assert.Equal("Code review", result[1].Group);
        Assert.Equal(0.08, result[1].UsedFraction!.Value, precision: 4);
    }

    /// <summary>Same skip rule on the main pair: a non-numeric percent must not fail the rest.</summary>
    [Fact]
    public void AMalformedMainWindowDoesNotFailTheRest()
    {
        var result = Windows("""
        {"rate_limit":{
          "primary_window":{"used_percent":"n/a","limit_window_seconds":18000},
          "secondary_window":{"used_percent":29,"limit_window_seconds":604800}},
         "additional_rate_limits":[{"limit_name":"Spark","rate_limit":{
          "primary_window":{"used_percent":40,"limit_window_seconds":18000}}}]}
        """);

        Assert.Equal(new[] { "secondary", "spark" }, result.Select(window => window.Id));
        Assert.Equal("secondary", result[0].Id);
        Assert.Equal(new double?[] { 0.29, 0.40 }, Fractions(result));
    }

    /// <summary>An empty additional_rate_limits array is the same as omitting it.</summary>
    [Fact]
    public void EmptyAdditionalRateLimitsLeaveTheMainWindowsUnchanged()
    {
        var result = Windows("""
        {"rate_limit":{
          "primary_window":{"used_percent":25,"limit_window_seconds":18000},
          "secondary_window":{"used_percent":10,"limit_window_seconds":604800}},
         "additional_rate_limits":[]}
        """);

        Assert.Equal(new[] { "primary", "secondary" }, result.Select(window => window.Id));
        Assert.Equal(new[] { "5h limit", "Weekly limit" }, result.Select(window => window.Label));
        Assert.Equal(new double?[] { 0.25, 0.10 }, Fractions(result));
        Assert.Equal(new string?[] { null, null }, result.Select(window => window.Group));
    }

    /// <summary>
    /// The additional limit is named after the model, not "Spark", and still maps to the spark
    /// window id — via <c>limit_name</c> or <c>metered_feature</c>.
    /// </summary>
    [Theory]
    [InlineData("limit_name")]
    [InlineData("metered_feature")]
    public void Gpt53CodexSparkStillMapsToSpark(string field)
    {
        var result = Windows("""
        {"rate_limit":{
          "primary_window":{"used_percent":25,"limit_window_seconds":18000}},
         "additional_rate_limits":[{"FIELD":"GPT-5.3-Codex-Spark","rate_limit":{
          "primary_window":{"used_percent":99,"limit_window_seconds":18000},
          "secondary_window":{"used_percent":5,"limit_window_seconds":604800}}}]}
        """.Replace("FIELD", field));

        Assert.Equal(new[] { "primary", "spark", "spark-secondary" }, result.Select(window => window.Id));
        Assert.Equal(new string?[] { null, "Spark", "Spark" }, result.Select(window => window.Group));
        Assert.Equal(new[] { "5h limit", "5h limit", "Weekly limit" }, result.Select(window => window.Label));
        Assert.Equal(new double?[] { 0.99, 0.05 }, Fractions(result.Skip(1)));
    }

    /// <summary>Credits and <c>codex_other</c> are real extras on some accounts; they have no home.</summary>
    [Fact]
    public void UnknownExtrasAloneLeaveNothingMetered()
    {
        var exception = Assert.Throws<ReaderException>(() => Windows("""
        {"additional_rate_limits":[{"limit_name":"Credits","metered_feature":"codex_other",
          "rate_limit":{"primary_window":{"used_percent":50,"limit_window_seconds":86400}}}]}
        """));

        Assert.Equal(ReaderErrorKind.NothingMetered, exception.Error.Kind);
    }

    /// <summary>Code review with no main pair is still a reading, same as Spark-only.</summary>
    [Fact]
    public void CodeReviewAloneIsStillMetered()
    {
        var result = Windows("""
        {"code_review_rate_limit":{
          "primary_window":{"used_percent":90,"limit_window_seconds":604800},
          "secondary_window":{"used_percent":15,"limit_window_seconds":18000}}}
        """);

        Assert.Equal(new[] { "code-review", "code-review-secondary" }, result.Select(window => window.Id));
        Assert.Equal(new string?[] { "Code review", "Code review" }, result.Select(window => window.Group));
        Assert.Equal(new double?[] { 0.90, 0.15 }, Fractions(result));
    }

    /// <summary>Extras listed first in the JSON must not become <c>windows[0]</c>.</summary>
    [Fact]
    public void SparkListedBeforeMainStillFollowsTheMainPair()
    {
        var result = Windows("""
        {"additional_rate_limits":[{"limit_name":"Spark","rate_limit":{
          "primary_window":{"used_percent":99,"limit_window_seconds":18000}}}],
         "code_review_rate_limit":{"primary_window":{"used_percent":90,"limit_window_seconds":604800}},
         "rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":18000},
          "secondary_window":{"used_percent":10,"limit_window_seconds":604800}}}
        """);

        Assert.Equal(new[] { "primary", "secondary", "spark", "code-review" }, result.Select(window => window.Id));
        Assert.Equal("primary", result[0].Id);
        Assert.Equal(0.25, result[0].UsedFraction!.Value, precision: 4);
    }

    /// <summary>A payload that names Spark twice must not emit two 5 h rows with the same id.</summary>
    [Fact]
    public void TwoSparkExtrasDoNotDuplicateWindowIds()
    {
        var result = Windows("""
        {"rate_limit":{
          "primary_window":{"used_percent":25,"limit_window_seconds":18000},
          "secondary_window":{"used_percent":10,"limit_window_seconds":604800}},
         "additional_rate_limits":[
           {"limit_name":"Spark","rate_limit":{
             "primary_window":{"used_percent":40,"limit_window_seconds":18000}}},
           {"limit_name":"GPT-5.3-Codex-Spark","metered_feature":"spark","rate_limit":{
             "primary_window":{"used_percent":99,"limit_window_seconds":18000},
             "secondary_window":{"used_percent":12,"limit_window_seconds":604800}}}
         ]}
        """);

        Assert.Equal(new[] { "primary", "secondary", "spark", "spark-secondary" }, result.Select(window => window.Id));
        Assert.Equal(result.Count, result.Select(window => window.Id).Distinct().Count());
        Assert.Equal(
            new string?[] { null, null, "Spark", "Spark" },
            result.Select(window => window.Group));
        Assert.Equal(
            new[] { "5h limit", "Weekly limit", "5h limit", "Weekly limit" },
            result.Select(window => window.Label));
        Assert.Equal(new double?[] { 0.25, 0.10, 0.40, 0.12 }, Fractions(result));
    }

    /// <summary>Hiding extras must drop Spark and code review, not leave an empty success.</summary>
    [Fact]
    public void HidingExtrasDropsSparkAndCodeReview()
    {
        const string Json = """
        {"rate_limit":{
          "primary_window":{"used_percent":25,"limit_window_seconds":18000},
          "secondary_window":{"used_percent":10,"limit_window_seconds":604800}},
         "additional_rate_limits":[{"limit_name":"Spark","rate_limit":{
          "primary_window":{"used_percent":99,"limit_window_seconds":18000}}}],
         "code_review_rate_limit":{"primary_window":{"used_percent":90,"limit_window_seconds":604800}}}
        """;

        var hidden = Windows(Json, includeExtras: false);
        Assert.Equal(new[] { "primary", "secondary" }, hidden.Select(window => window.Id));
        Assert.Throws<ReaderException>(() => Windows(
            """
            {"additional_rate_limits":[{"limit_name":"Spark","rate_limit":{
              "primary_window":{"used_percent":40,"limit_window_seconds":18000}}}]}
            """,
            includeExtras: false));
    }

    /// <summary>
    /// A credit-based seat (Business/Team) has no rolling windows; the spend control's own cap is
    /// the ring. Amounts arrive as decimal strings, and the percentage is clamped to 0…1.
    /// </summary>
    [Fact]
    public void ACreditSeatReadsTheSpendControlsIndividualLimit()
    {
        var result = Windows("""
        {"plan_type":"business",
         "spend_control":{"individual_limit":{"limit":"374.92","used":"100.00",
           "used_percent":26.67,"reset_at":1790585722}}}
        """);

        var window = Assert.Single(result);
        Assert.Equal("credits", window.Id);
        Assert.Equal("Credits", window.Label);
        Assert.Equal(0.2667, window.UsedFraction!.Value, precision: 4);
        Assert.Equal(275, window.Remaining);
        Assert.Equal(100, window.Used);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_585_722), window.ResetsAt);
    }

    [Fact]
    public void ACreditSeatClampsItsPercentageAndNeverReplacesRollingWindows()
    {
        var clamped = Assert.Single(Windows("""
        {"spend_control":{"individual_limit":{"limit":100,"used":120,"used_percent":120}}}
        """));
        Assert.Equal(1, clamped.UsedFraction);

        // With a readable main pair present the credit seat is not a window at all.
        var rolling = Windows("""
        {"rate_limit":{"primary_window":{"used_percent":25,"limit_window_seconds":18000}},
         "spend_control":{"individual_limit":{"limit":100,"used":50,"used_percent":50}}}
        """);
        Assert.Equal(new[] { "primary" }, rolling.Select(window => window.Id));

        // Without a percentage there is no denominator and nothing to meter.
        var exception = Assert.Throws<ReaderException>(() => Windows("""
        {"spend_control":{"individual_limit":{"limit":100,"used":50}}}
        """));
        Assert.Equal(ReaderErrorKind.NothingMetered, exception.Error.Kind);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void AnUnreadablePayloadIsBadResponseZero(string json)
    {
        var exception = Assert.Throws<ReaderException>(() => Windows(json));

        Assert.Equal(ReaderErrorKind.BadResponse, exception.Error.Kind);
        Assert.Equal(0, exception.Error.Status);
    }

    [Fact]
    public void AnEmptyObjectLeavesNothingMetered()
    {
        var exception = Assert.Throws<ReaderException>(() => Windows("{}"));

        Assert.Equal(ReaderErrorKind.NothingMetered, exception.Error.Kind);
    }

    [Theory]
    [InlineData("{\"plan_type\":\"  \"}")]
    [InlineData("{\"plan_type\":25}")]
    [InlineData("not-json")]
    [InlineData("[]")]
    public void PlanIsNilWhenThePayloadDoesNotNameAnEmptyTrimmedOne(string json)
    {
        Assert.Null(CodexUsage.Plan(json));
    }

    [Fact]
    public void PlanIsTrimmed()
    {
        Assert.Equal("plus", CodexUsage.Plan("{\"plan_type\":\"  plus  \"}"));
    }
}
