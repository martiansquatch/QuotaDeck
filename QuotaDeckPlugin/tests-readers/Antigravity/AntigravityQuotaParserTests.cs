namespace QuotaDeck.Readers.Tests.Antigravity;

using QuotaDeck.Readers.Antigravity;

/// <summary>
/// The quota parser, ported from Codenotch <c>Tests/AntigravityTests.swift</c>
/// (<c>AntigravityQuotaTests</c> lines 361-529 and <c>AntigravityBridgeTests</c>'s language-server
/// body, lines 573-621). Written from message names, so what is tested is mostly its refusal to
/// believe things: a shape it does not recognise yields nothing rather than a confident ring.
/// </summary>
public class AntigravityQuotaParserTests
{
    [Fact]
    public void ItReadsUsedLimitBucketsIntoWindows()
    {
        var windows = AntigravityQuotaParser.Parse(
            """
            {"quotaGroups":[{"displayName":"Gemini","buckets":[
              {"name":"daily","displayName":"Daily","used":250,"limit":1000,
               "resetTime":"2026-09-01T00:00:00Z"}]}]}
            """,
            DateTimeOffset.Parse("2026-08-31T00:00:00Z"));

        Assert.Single(windows);
        Assert.Equal(0.25, windows[0].UsedFraction!.Value, 4);
        Assert.Equal("Daily", windows[0].Label);
    }

    [Fact]
    public void ItParsesDirectCloudCodeRetrieveUserQuotaBuckets()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_788_900_000);
        var windows = AntigravityQuotaParser.Parse(
            """
            {
              "buckets": [
                { "tokenType": "WTUS", "modelId": "claude-sonnet-4-6", "remainingFraction": 0.75, "resetTime": "2026-09-09T10:39:57Z" },
                { "tokenType": "WTUS", "modelId": "claude-opus-4-6-thinking", "remainingFraction": 0.85, "resetTime": "2026-09-16T18:00:12Z" },
                { "tokenType": "WTUS", "modelId": "gemini-3.7-flash-tiered", "remainingFraction": 0.90, "resetTime": "2026-09-09T10:39:57Z" },
                { "tokenType": "WTUS", "modelId": "gemini-2.5-pro", "remainingFraction": 0.80, "resetTime": "2026-09-16T10:39:57Z" }
              ]
            }
            """,
            now);

        Assert.Equal(4, windows.Count);

        var geminiHourly = windows.Single(window => window.Id == "gemini-hourly");
        Assert.Equal("Gemini Models", geminiHourly.Group);
        Assert.Equal("5-hour Limit", geminiHourly.Label);
        Assert.Equal(0.10, geminiHourly.UsedFraction!.Value, 4);

        var geminiWeekly = windows.Single(window => window.Id == "gemini-weekly");
        Assert.Equal("Gemini Models", geminiWeekly.Group);
        Assert.Equal("Weekly Limit", geminiWeekly.Label);
        Assert.Equal(0.20, geminiWeekly.UsedFraction!.Value, 4);

        var thirdPartyHourly = windows.Single(window => window.Id == "3p-hourly");
        Assert.Equal("Claude and GPT models", thirdPartyHourly.Group);
        Assert.Equal("5-hour Limit", thirdPartyHourly.Label);
        Assert.Equal(0.25, thirdPartyHourly.UsedFraction!.Value, 4);

        var thirdPartyWeekly = windows.Single(window => window.Id == "3p-weekly");
        Assert.Equal("Claude and GPT models", thirdPartyWeekly.Group);
        Assert.Equal("Weekly Limit", thirdPartyWeekly.Label);
        Assert.Equal(0.15, thirdPartyWeekly.UsedFraction!.Value, 4);
    }

    [Fact]
    public void TheQuotaSummaryEnvelopeIsNormalizedForEverySource()
    {
        var windows = AntigravityQuotaParser.Parse(
            """
            {
              "groups": [
                {
                  "displayName": "Gemini Models",
                  "buckets": [
                    { "bucketId": "gemini-5h", "displayName": "5-hour Limit", "remaining": {"case": "remainingFraction", "value": 0.86} },
                    { "bucketId": "gemini-weekly", "displayName": "Weekly Limit", "remaining": {"remainingFraction": 0.55} }
                  ]
                },
                {
                  "displayName": "Claude and GPT models",
                  "buckets": [
                    { "bucketId": "3p-5h", "displayName": "5-hour Limit", "remainingFraction": 1 },
                    { "bucketId": "3p-weekly", "displayName": "Weekly Limit", "remainingFraction": 1 }
                  ]
                }
              ]
            }
            """,
            DateTimeOffset.Parse("2026-08-31T00:00:00Z"));

        Assert.Equal(4, windows.Count);
        Assert.Equal("gemini-5h", windows[0].Id);
        Assert.Equal("gemini-weekly", windows[1].Id);
        Assert.Equal("3p-5h", windows[2].Id);
        Assert.Equal("3p-weekly", windows[3].Id);
        Assert.Equal(TimeSpan.FromHours(5), windows[0].Duration);
        Assert.Equal(TimeSpan.FromDays(7), windows[1].Duration);
        Assert.Equal(TimeSpan.FromHours(5), windows[2].Duration);
        Assert.Equal(TimeSpan.FromDays(7), windows[3].Duration);
        Assert.Equal(0.14, windows[0].UsedFraction!.Value, 4);
        Assert.Equal(0.45, windows[1].UsedFraction!.Value, 4);
        Assert.Equal(0, windows[2].UsedFraction!.Value, 4);
        Assert.Equal(0, windows[3].UsedFraction!.Value, 4);
    }

    /// <summary>The language server reports what is *left*; the face shows what is spent.</summary>
    [Fact]
    public void TheLanguageServerBodyIsInvertedAndNormalized()
    {
        var windows = AntigravityQuotaParser.Parse(
            """
            {"response":{"groups":[
              {"displayName":"Gemini Models",
               "description":"Models within this group: Gemini Flash, Gemini Pro",
               "buckets":[{"bucketId":"gemini-weekly","displayName":"Weekly Limit Remaining",
                           "window":"weekly","remainingFraction":0.96262,
                           "resetTime":"2026-09-07T14:12:34Z"}]},
              {"displayName":"Claude and GPT models",
               "buckets":[{"bucketId":"3p-weekly","displayName":"Weekly Limit Remaining",
                           "window":"weekly","remainingFraction":1,
                           "resetTime":"2026-09-08T09:12:10Z"}]}]}}
            """,
            DateTimeOffset.Parse("2026-08-31T00:00:00Z"));

        Assert.Equal(2, windows.Count);
        Assert.Equal("gemini-weekly", windows[0].Id);
        Assert.Equal(1 - 0.96262, windows[0].UsedFraction!.Value, 5);
        Assert.Equal("Weekly Limit", windows[0].Label);
        Assert.Equal("Gemini Models", windows[0].Group);
        Assert.Equal(TimeSpan.FromDays(7), windows[0].Duration);
        Assert.Equal(0, windows[1].UsedFraction!.Value, 4);
        Assert.Equal(DateTimeOffset.Parse("2026-09-07T14:12:34Z"), windows[0].ResetsAt);
    }

    [Fact]
    public void DirectCloudCodeIgnoresInvalidFractions()
    {
        var windows = AntigravityQuotaParser.Parse(
            """
            {
              "buckets": [
                {"modelId": "gemini-bad-over", "remainingFraction": 1.5},
                {"modelId": "gemini-bad-under", "remainingFraction": -0.1},
                {"modelId": "gemini-3.7-flash", "remainingFraction": 0.4}
              ]
            }
            """,
            DateTimeOffset.Parse("2026-08-31T00:00:00Z"));

        Assert.Single(windows);
        Assert.Equal("gemini-hourly", windows[0].Id);
        Assert.Equal(0.6, windows[0].UsedFraction!.Value, 4);
    }

    /// <summary>A zero limit is dropped rather than divided by — nothing with a zero limit is a percentage.</summary>
    [Fact]
    public void AZeroLimitIsDroppedRatherThanDividedBy()
    {
        Assert.Empty(AntigravityQuotaParser.Parse(
            """{"buckets":[{"name":"x","used":0,"limit":0}]}""",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void NonsenseValuesAreDropped()
    {
        Assert.Empty(AntigravityQuotaParser.Parse(
            """{"buckets":[{"name":"x","used":9999,"limit":10}]}""",
            DateTimeOffset.UtcNow));
        Assert.Empty(AntigravityQuotaParser.Parse(
            """{"buckets":[{"name":"x","used":-5,"limit":10}]}""",
            DateTimeOffset.UtcNow));
    }

    /// <summary>The likeliest future: a shape this does not know. Empty routes to the fallback.</summary>
    [Fact]
    public void AnUnfamiliarShapeYieldsNothing()
    {
        Assert.Empty(AntigravityQuotaParser.Parse("""{"somethingElse":[1,2]}""", DateTimeOffset.UtcNow));
        Assert.Empty(AntigravityQuotaParser.Parse("not json", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ImpossibleFractionsAreDropped()
    {
        Assert.Empty(AntigravityQuotaParser.Parse(
            """{"response":{"groups":[{"displayName":"G","buckets":[{"bucketId":"a","remainingFraction":1.4},{"bucketId":"b","remainingFraction":-0.2}]}]}}""",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AMissingResetIsToleratedRatherThanFatal()
    {
        var windows = AntigravityQuotaParser.Parse(
            """{"buckets":[{"name":"d","used":1,"limit":4}]}""",
            DateTimeOffset.UtcNow);

        Assert.Single(windows);
        Assert.Null(windows[0].ResetsAt);
    }

    [Fact]
    public void ADisabledBucketIsSkipped()
    {
        var windows = AntigravityQuotaParser.Parse(
            """
            {"groups":[{"displayName":"Gemini","buckets":[
              {"bucketId":"a","remainingFraction":0.5,"disabled":true},
              {"bucketId":"b","remainingFraction":0.5}]}]}
            """,
            DateTimeOffset.UtcNow);

        Assert.Single(windows);
        Assert.Equal("b", windows[0].Id);
    }
}
