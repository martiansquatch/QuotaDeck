namespace QuotaDeck.Readers.Tests.Codex;

using QuotaDeck.Core.Readers;
using QuotaDeck.Readers.Codex;

/// <summary>
/// Port of Codenotch <c>Tests/CodexResetCreditsTests.swift</c> (lines 24-67) and the reset-credit
/// cases of <c>Tests/CodexUsageTests.swift</c> (lines 383-458). Banked rate-limit resets: the count
/// is its own field — the details list can be shorter — and the next expiry is the soonest credit,
/// not the first row.
/// </summary>
public class CodexResetCreditsTests
{
    private static CodexResetCredits Credits(string json) => CodexUsage.ResetCredits(json);

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    /// <summary>Recorded shape: a count plus one row per credit, expiry as an internet-date string.</summary>
    [Fact]
    public void DecodesAvailableCountAndExpiryFromTheFixture()
    {
        var result = Credits("""
        {"available_count":2,"credits":[
          {"id":"credit-a","reset_type":"codex_rate_limits","status":"available",
           "title":"Full reset","granted_at":"2026-06-12T01:33:14Z",
           "expires_at":"2026-07-12T01:33:14Z"},
          {"id":"credit-b","reset_type":"codex_rate_limits","status":"available",
           "title":"Full reset","granted_at":"2026-06-18T08:00:00Z",
           "expires_at":"2026-07-18T08:00:00Z"}]}
        """);

        Assert.Equal(2, result.AvailableCount);
        Assert.Equal(Utc(2026, 7, 12, 1, 33, 14), result.Credits[0].ExpiresAt);
        Assert.Equal(Utc(2026, 7, 18, 8, 0, 0), result.Credits[1].ExpiresAt);
        Assert.Equal(Utc(2026, 7, 12, 1, 33, 14), result.NextExpiry);
    }

    /// <summary>Arrival order is not expiry order — the next one is the soonest date.</summary>
    [Fact]
    public void NextExpiryIsTheSoonestCredit()
    {
        var result = Credits("""
        {"available_count":2,"credits":[
          {"status":"available","expires_at":"2026-08-01T00:00:00Z"},
          {"status":"available","expires_at":"2026-07-01T00:00:00Z"}]}
        """);

        Assert.Equal(Utc(2026, 7, 1), result.NextExpiry);
    }

    /// <summary>The details list can be capped, so the count field is the total rather than <c>credits.count</c>.</summary>
    [Fact]
    public void AvailableCountIsTrustedWhenTheCreditsArrayIsShorter()
    {
        var result = Credits("""
        {"available_count":5,"credits":[
          {"status":"available","expires_at":"2026-07-12T01:33:14Z"}]}
        """);

        Assert.Equal(5, result.AvailableCount);
        Assert.Single(result.Credits);
        Assert.True(result.Credits.Count < result.AvailableCount);
    }

    /// <summary>
    /// Both ISO-8601 forms the backend mixes: whole seconds and fractional; the available list is
    /// soonest first and a spent credit is not available.
    /// </summary>
    [Fact]
    public void ReadsAvailableCountAndSoonestExpiryAcrossMixedDateForms()
    {
        var result = Credits("""
        {"credits":[
          {"id":"later","reset_type":"rate_limit","status":"available",
           "granted_at":"2026-09-01T12:00:00Z",
           "expires_at":"2026-09-20T12:00:00.250Z",
           "title":"Reset","description":"Unused reset","extra":true},
          {"id":"spent","reset_type":"rate_limit","status":"redeemed",
           "granted_at":"2026-08-01T00:00:00Z",
           "expires_at":"2026-09-12T00:00:00Z"},
          {"id":"sooner","reset_type":"rate_limit","status":"available",
           "granted_at":"2026-09-02T00:00:00Z",
           "expires_at":"2026-09-15T08:00:00Z"}
         ],
         "available_count":2,
         "server_time":"2026-09-10T00:00:00Z"}
        """);

        Assert.Equal(2, result.AvailableCount);
        Assert.Equal(new[] { "later", "spent", "sooner" }, result.Credits.Select(credit => credit.Id));
        Assert.Equal(new[] { "sooner", "later" }, result.Available.Select(credit => credit.Id));
        Assert.Equal(Utc(2026, 9, 15, 8, 0, 0), result.NextExpiry);
        Assert.Equal(Utc(2026, 9, 20, 12, 0, 0).AddMilliseconds(250), result.Available[1].ExpiresAt);
    }

    [Fact]
    public void AvailableCountIsCountedWhenThePayloadOmitsIt()
    {
        var result = Credits("""
        {"credits":[
          {"id":"a","status":"available","expires_at":"2026-09-18T00:00:00Z"},
          {"id":"b","status":"redeemed","expires_at":"2026-09-10T00:00:00Z"}
        ]}
        """);

        Assert.Equal(1, result.AvailableCount);
        Assert.Equal(new[] { "a" }, result.Available.Select(credit => credit.Id));
    }

    /// <summary>A non-object entry is skipped; an unreadable date becomes no expiry.</summary>
    [Fact]
    public void SkipsMalformedCreditsRatherThanFailing()
    {
        var result = Credits("""
        {"credits":[
          "nope",
          {"id":"ok","status":"available","expires_at":null}
        ],"available_count":1}
        """);

        Assert.Equal(new[] { "ok" }, result.Credits.Select(credit => credit.Id));
        Assert.Null(result.Credits[0].ExpiresAt);
        Assert.Equal(1, result.AvailableCount);
        Assert.Null(result.NextExpiry);
    }

    /// <summary>A row whose id or status is the wrong type fails that row, not the list.</summary>
    [Fact]
    public void AWronglyTypedIdDropsOnlyThatCredit()
    {
        var result = Credits("""
        {"credits":[
          {"id":5,"status":"available","expires_at":"2026-07-12T01:33:14Z"},
          {"id":"ok","status":true,"expires_at":"2026-07-12T01:33:14Z"},
          {"id":"good","status":"available","expires_at":"2026-07-12T01:33:14Z"}
        ]}
        """);

        Assert.Equal(new[] { "good" }, result.Credits.Select(credit => credit.Id));
        Assert.Equal(1, result.AvailableCount);
    }

    [Fact]
    public void ThrowsOnlyOnInvalidJSONOrScalars()
    {
        var exception = Assert.Throws<ReaderException>(() => Credits("not-json"));
        Assert.Equal(ReaderErrorKind.BadResponse, exception.Error.Kind);
        Assert.Equal(0, exception.Error.Status);

        Assert.Equal(0, Credits("{}").AvailableCount);
        Assert.Empty(Credits("[]").Credits);
    }

    /// <summary>
    /// Swift's <c>ISO8601DateFormatter</c> with <c>.withInternetDateTime</c> rejects a stamp with no
    /// zone designator — "2026-07-12T01:33:14" is nil, not local time — so that credit has no
    /// expiry. An offset is a zone designator and still parses.
    /// </summary>
    [Fact]
    public void AZoneLessExpiryIsNoExpiryLikeSwiftsInternetDateFormatter()
    {
        var result = Credits("""
        {"credits":[
          {"id":"zoneless","status":"available","expires_at":"2026-07-12T01:33:14"},
          {"id":"zoned","status":"available","expires_at":"2026-07-12T01:33:14Z"},
          {"id":"offset","status":"available","expires_at":"2026-07-12T01:33:14+02:00"}
        ]}
        """);

        Assert.Null(result.Credits[0].ExpiresAt);
        Assert.Equal(Utc(2026, 7, 12, 1, 33, 14), result.Credits[1].ExpiresAt);
        Assert.Equal(Utc(2026, 7, 11, 23, 33, 14), result.Credits[2].ExpiresAt);
        Assert.Equal(Utc(2026, 7, 11, 23, 33, 14), result.NextExpiry);
    }
}
