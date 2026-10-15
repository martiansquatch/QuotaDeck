namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Readers.Claude;

/// <summary>
/// Guards the shape of <c>GET /api/oauth/usage</c>. It is not a published API, so these are the
/// tests that will fail first if Anthropic changes it.
///
/// Ported from Codenotch <c>Tests/UsageResponseTests.swift</c> (class UsageResponseTests), with the
/// same fixtures — synthetic percentages and an invented organization, no account data.
/// </summary>
public class ClaudeUsageResponseTests
{
    /// <summary>
    /// Trimmed from a real response — the endpoint returns a long tail of null-valued keys that must
    /// not trip decoding.
    /// </summary>
    private const string Live = """
    {
      "five_hour": { "utilization": 52.0, "resets_at": "2026-08-28T09:50:00.316290+00:00",
                     "limit_dollars": null, "used_dollars": null },
      "seven_day": { "utilization": 17.0, "resets_at": "2026-09-02T17:00:00.316321+00:00",
                     "limit_dollars": null },
      "seven_day_opus": null,
      "nimbus_quill": { "utilization": 0.0, "resets_at": null },
      "limits": [
        { "kind": "session", "group": "session", "percent": 52, "severity": "normal",
          "resets_at": "2026-08-28T09:50:00.316290+00:00", "scope": null, "is_active": true },
        { "kind": "weekly_all", "group": "weekly", "percent": 17, "severity": "normal",
          "resets_at": "2026-09-02T17:00:00.316321+00:00", "scope": null, "is_active": false }
      ]
    }
    """;

    /// <summary>Trimmed from the real response of an Enterprise seat billed through a marketplace.</summary>
    private const string Enterprise = """
    {
      "limits": [],
      "five_hour": null,
      "seven_day": null,
      "seven_day_opus": null,
      "amber_ladder": { "limit_dollars": 25000, "used_dollars": 0,
                        "remaining_dollars": 25000, "utilization": 0,
                        "resets_at": "2026-10-02T06:59:59.000000+00:00", "locked_reason": null },
      "nimbus_quill": { "limit_dollars": null, "used_dollars": null, "utilization": 0,
                        "resets_at": null, "locked_reason": null },
      "tangelo": null,
      "extra_usage": { "is_enabled": true, "currency": "USD", "monthly_limit": 20000,
                       "used_credits": 297, "utilization": 1.485, "decimal_places": 2 },
      "spend": {
        "enabled": true, "percent": 1, "severity": "normal",
        "limit": { "amount_minor": 20000, "currency": "USD", "exponent": 2 },
        "used":  { "amount_minor": 297, "currency": "USD", "exponent": 2 },
        "cap": { "credits": { "amount_minor": 20000, "exponent": 2 }, "money": null },
        "balance": null, "auto_reload": null, "can_purchase_credits": false
      }
    }
    """;

    private static List<QuotaDeck.Core.Model.LimitWindow> Windows(string json, DateTimeOffset? readAt = null) =>
        ClaudeUsageResponse.Decode(json).LimitWindows(readAt ?? new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero));

    private static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(text, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public void TheLiveShapeDecodes()
    {
        var windows = Windows(Live);

        Assert.Equal(2, windows.Count);
        Assert.Equal([TimeSpan.FromHours(5), TimeSpan.FromDays(7)], windows.Select(window => window.Duration));
        Assert.Equal("session", windows[0].Id);
        Assert.Equal("Current session", windows[0].Label);
        Assert.Equal(0.52, windows[0].UsedFraction!.Value, 4);
        Assert.Equal("All models", windows[1].Label);
        Assert.Equal(0.17, windows[1].UsedFraction!.Value, 4);
    }

    /// <summary>The session window always sorts above the weekly one, whatever order the endpoint lists them in.</summary>
    [Fact]
    public void TheSessionSortsFirst()
    {
        const string Reversed = """
        { "limits": [
            { "kind": "weekly_all", "percent": 17, "resets_at": "2026-09-02T17:00:00.316321+00:00" },
            { "kind": "session", "percent": 52, "resets_at": "2026-08-28T09:50:00.316290+00:00" } ] }
        """;

        Assert.Equal(["session", "weekly_all"], Windows(Reversed).Select(window => window.Id));
    }

    /// <summary>A window with no reset time is not one we can render a countdown for.</summary>
    [Fact]
    public void WindowsWithoutAResetTimeAreDropped()
    {
        const string Json = """
        { "limits": [ { "kind": "session", "percent": 5, "resets_at": null } ],
          "five_hour": { "utilization": 5.0, "resets_at": null } }
        """;

        Assert.Empty(Windows(Json));
    }

    /// <summary>Older responses without `limits` still render from the named windows.</summary>
    [Fact]
    public void TheNamedWindowsAreAFallback()
    {
        const string Json = """
        { "five_hour": { "utilization": 48.0, "resets_at": "2026-08-28T09:50:00.316290+00:00" },
          "seven_day": { "utilization": 16.0, "resets_at": "2026-09-02T17:00:00.316321+00:00" } }
        """;

        Assert.Equal(["Current session", "All models"], Windows(Json).Select(window => window.Label));
    }

    /// <summary>The model-scoped weekly window is named in the entry's own scope, nowhere else.</summary>
    [Fact]
    public void TheScopedWindowIsNamedAfterItsModel()
    {
        const string Json = """
        { "limits": [
            { "kind": "session", "percent": 52, "resets_at": "2026-08-28T09:50:00.316290+00:00",
              "scope": null },
            { "kind": "weekly_scoped", "percent": 61,
              "resets_at": "2026-09-02T17:00:00.316321+00:00",
              "scope": { "model": { "display_name": "Fable", "id": "claude-fable-5-1" } } } ] }
        """;
        var windows = Windows(Json);

        Assert.Equal(["Current session", "Fable"], windows.Select(window => window.Label));

        // The id stays the API's own kind: it keys the archive and the cell.
        Assert.Equal(["session", "weekly_scoped"], windows.Select(window => window.Id));
    }

    [Fact]
    public void AnUnscopedModelWindowFallsBackToTheKind()
    {
        const string Json = """
        { "limits": [ { "kind": "weekly_scoped", "percent": 61,
                        "resets_at": "2026-09-02T17:00:00.316321+00:00", "scope": null } ] }
        """;

        Assert.Equal(["Scoped"], Windows(Json).Select(window => window.Label));
    }

    /// <summary>The scope is a nicer name for a reading, not the reading.</summary>
    [Fact]
    public void AMalformedScopeDoesNotCostTheReading()
    {
        const string Json = """
        { "limits": [ { "kind": "weekly_scoped", "percent": 61,
                        "resets_at": "2026-09-02T17:00:00.316321+00:00",
                        "scope": "fable" } ] }
        """;
        var window = Assert.Single(Windows(Json));

        Assert.Equal(0.61, window.UsedFraction!.Value, 4);
        Assert.Equal("Scoped", window.Label);
    }

    [Fact]
    public void AnEnterpriseSeatReportsItsBalance()
    {
        var windows = Windows(Enterprise);

        var balance = Assert.Single(windows);
        Assert.Equal("spend", balance.Id);

        var money = Assert.IsType<QuotaDeck.Core.Model.UsageMoneyBreakdown>(balance.Money);
        Assert.Equal(2.97, money.Spent, 6);
        Assert.Equal(200, money.Funded, 6);
        Assert.Equal("USD", money.Currency);

        // 297/20000, not the `percent: 1` the response rounds it to.
        Assert.Equal(0.01485, balance.UsedFraction!.Value, 5);
        Assert.NotNull(balance.ResetsAt);
    }

    /// <summary>`spend` names no period, `extra_usage.monthly_limit` does: midnight UTC on the 1st.</summary>
    [Fact]
    public void AMonthlyBalanceRunsToTheFirstOfNextMonthInUtc()
    {
        var balance = Assert.Single(Windows(Enterprise, Utc("2026-10-12T07:29:56Z")));
        Assert.Equal(Utc("2026-11-01T00:00:00Z"), balance.ResetsAt);
        Assert.Equal(TimeSpan.FromDays(31), balance.Duration);

        var february = Assert.Single(Windows(Enterprise, Utc("2027-02-10T12:00:00Z")));
        Assert.Equal(Utc("2027-03-01T00:00:00Z"), february.ResetsAt);
        Assert.Equal(TimeSpan.FromDays(28), february.Duration);
    }

    /// <summary>The month is the one the reading was written in, not the one it is decoded in.</summary>
    [Fact]
    public void TheMonthIsTheReadingsOwn()
    {
        var windows = Windows(Enterprise, Utc("2026-09-30T23:59:00Z"));

        Assert.Equal(Utc("2026-10-01T00:00:00Z"), windows[0].ResetsAt);
        Assert.True(ClaudeReader.HasExpiredWindow(windows, Utc("2026-10-01T00:00:01Z")));
    }

    [Fact]
    public void ABalanceWithoutAMonthlyLimitHasNoReset()
    {
        const string Json = """
        { "limits": [], "spend": { "enabled": true,
            "limit": { "amount_minor": 20000, "currency": "USD", "exponent": 2 },
            "used": { "amount_minor": 297, "currency": "USD", "exponent": 2 } } }
        """;
        var balance = Assert.Single(Windows(Json));

        Assert.Null(balance.ResetsAt);
        Assert.Null(balance.Duration);
    }

    /// <summary>Declaring "session" on a seat that has none left the ring showing a dash beside numbers.</summary>
    [Fact]
    public void TheBalanceIsTheHeadlineWhenThereIsNoSession()
    {
        Assert.Equal("spend", ClaudeUsageResponse.HeadlineId(Windows(Enterprise)));
    }

    [Fact]
    public void TheSessionStillLeadsWhereThereIsOne()
    {
        Assert.Equal("session", ClaudeUsageResponse.HeadlineId(Windows(Live)));

        var weeklyOnly = new[]
        {
            new QuotaDeck.Core.Model.LimitWindow { Id = "weekly_all", Label = "All models", UsedFraction = 0.3 },
        };
        Assert.Equal("session", ClaudeUsageResponse.HeadlineId(weeklyOnly));
    }

    /// <summary>Codenamed blocks carry `limit_dollars` and `resets_at` and are not windows.</summary>
    [Fact]
    public void CodenamedBlocksAreNotReadAsWindows()
    {
        var windows = Windows(Enterprise);

        Assert.DoesNotContain(windows, window => window.Id.Contains("amber") || window.Id.Contains("nimbus"));
        Assert.DoesNotContain(windows, window => window.Money?.Funded == 25000);
    }

    [Fact]
    public void ASeatWithoutCreditsHasNoBalanceWindow()
    {
        const string Json = """
        { "limits": [], "spend": { "enabled": false,
            "limit": { "amount_minor": 0, "currency": "USD", "exponent": 2 },
            "used": { "amount_minor": 0, "currency": "USD", "exponent": 2 } } }
        """;

        Assert.Empty(Windows(Json));
    }

    /// <summary>A malformed balance must not cost a plan seat its windows.</summary>
    [Fact]
    public void AMalformedSpendBlockDoesNotCostThePlanWindows()
    {
        const string Json = """
        { "limits": [ { "kind": "session", "percent": 52,
                        "resets_at": "2026-08-28T09:50:00.316290+00:00" } ],
          "spend": "unexpected" }
        """;

        Assert.Equal(["session"], Windows(Json).Select(window => window.Id));
    }

    [Fact]
    public void ASubscriptionSeatIsUnaffected()
    {
        Assert.Equal(["session", "weekly_all"], Windows(Live).Select(window => window.Id));
    }

    /// <summary>On a seat that has both, the balance sorts last: it is not one of the plan's periods.</summary>
    [Fact]
    public void TheBalanceSortsAfterThePlansWindows()
    {
        const string Json = """
        { "limits": [
            { "kind": "weekly_all", "percent": 17,
              "resets_at": "2026-09-02T17:00:00.316321+00:00" },
            { "kind": "session", "percent": 52,
              "resets_at": "2026-08-28T09:50:00.316290+00:00" } ],
          "spend": { "enabled": true,
            "limit": { "amount_minor": 20000, "currency": "USD", "exponent": 2 },
            "used": { "amount_minor": 297, "currency": "USD", "exponent": 2 } } }
        """;

        Assert.Equal(["session", "weekly_all", "spend"], Windows(Json).Select(window => window.Id));
    }

    [Fact]
    public void UnknownKindsGetAReadableLabel()
    {
        Assert.Equal("Opus", ClaudeUsageResponse.LabelForKind("weekly_opus"));
        Assert.Equal("Cowork", ClaudeUsageResponse.LabelForKind("weekly_cowork"));
    }

    /// <summary>A zero utilisation is a reading, not a missing one.</summary>
    [Fact]
    public void ZeroUtilisationIsAWindow()
    {
        const string Json = """
        { "limits": [ { "kind": "session", "percent": 0, "resets_at": "2026-09-02T17:00:00.316321+00:00" } ] }
        """;
        var window = Assert.Single(Windows(Json));

        Assert.Equal(0, window.UsedFraction!.Value);
    }

    /// <summary>An expired window still parses; it is the caller that refuses to reuse it.</summary>
    [Fact]
    public void ExpiredWindowsParseAndAreReportedAsExpired()
    {
        const string Json = """
        { "limits": [ { "kind": "session", "percent": 80, "resets_at": "2000-01-01T00:00:00Z" } ] }
        """;
        var windows = Windows(Json);

        Assert.Single(windows);
        Assert.True(ClaudeReader.HasExpiredWindow(windows, Utc("2026-09-07T06:00:00Z")));
    }

    /// <summary>#178's shape: answered, signed in, nothing metered.</summary>
    [Fact]
    public void AnAnswerWithNoLimitsHasNoWindows()
    {
        Assert.Empty(Windows("""{"extra_usage":null}"""));
    }

    [Fact]
    public void TimestampsParseWithAndWithoutFractionalSeconds()
    {
        Assert.True(ClaudeUsageResponse.TryDate("2026-08-28T09:50:00.316290+00:00", out var fraction));
        Assert.Equal(Utc("2026-08-28T09:50:00.316290Z"), fraction);

        Assert.True(ClaudeUsageResponse.TryDate("2099-01-01T00:00:00Z", out var plain));
        Assert.Equal(Utc("2099-01-01T00:00:00Z"), plain);

        Assert.False(ClaudeUsageResponse.TryDate("not a date", out _));
    }

    [Theory]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("max", "Max")]
    [InlineData("team", "Team")]
    [InlineData("extra usage", "extra usage")]
    [InlineData("Team", "Team")]
    public void PlanNamesAreCapitalisedOnlyWhenTheyAreOneLowercaseWord(string raw, string expected)
    {
        Assert.Equal(expected, ClaudeUsageResponse.PlanName(raw));
    }

    [Fact]
    public void AnEmptyPlanIsNoPlan()
    {
        Assert.Null(ClaudeUsageResponse.PlanName("  "));
        Assert.Null(ClaudeUsageResponse.PlanName(null));
    }
}
