namespace QuotaDeck.Readers.Tests.Claude;

using System.Net;
using System.Net.Http.Headers;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Readers.Claude;

/// <summary>
/// The token path: the request, what its answers mean, when the held reading is reused, and how the
/// failures reach the status the face draws. Ported from Codenotch
/// <c>Tests/ClaudeOAuthProviderTests.swift</c> (the endpoint-floors, retry and error suites).
/// </summary>
public class ClaudeReaderEndpointTests
{
    private const string UsagePayload = """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z"}]}""";

    /// <summary>The same request shape Claude Code's token path uses, with no client id anywhere.</summary>
    [Fact]
    public async Task TheRequestCarriesTheTokenAndTheBetaHeader()
    {
        var rig = new ClaudeRig();
        rig.AddCredential(accessToken: "secret-token");
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);

        _ = await rig.Reader().FetchAsync(Freshness.Standard);

        var request = Assert.Single(rig.Http.Requests);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.ToString());
        Assert.Equal("Bearer secret-token", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(rig.Http.Timeouts));
    }

    /// <summary>The held reading answers every freshness inside its own floor, `.fromSource` included.</summary>
    [Theory]
    [InlineData(Freshness.Standard)]
    [InlineData(Freshness.Live)]
    [InlineData(Freshness.FromSource)]
    public async Task TheEndpointReusesWithinEveryFreshnessFloor(Freshness freshness)
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = rig.Reader();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var snapshot = await reader.FetchAsync(freshness);
            Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
        }

        Assert.Single(rig.Http.Requests);
    }

    [Fact]
    public async Task TheLiveEndpointFloorCanBeShorterThanTheStandardOne()
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = rig.Reader(options =>
        {
            options.MinEndpointInterval = TimeSpan.FromMinutes(5);
            options.LiveMinEndpointInterval = TimeSpan.Zero;
        });

        _ = await reader.FetchAsync(Freshness.Standard);
        _ = await reader.FetchAsync(Freshness.Standard);
        Assert.Single(rig.Http.Requests);

        _ = await reader.FetchAsync(Freshness.Live);

        Assert.Equal(2, rig.Http.Requests.Count);
    }

    /// <summary>A reading whose window has already rolled over describes a period that is over.</summary>
    [Fact]
    public async Task AnExpiredEndpointWindowIsNotReused()
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.OK, """{"limits":[{"kind":"session","percent":80,"resets_at":"2000-01-01T00:00:00Z"}]}""");
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = rig.Reader(options => options.MinEndpointInterval = TimeSpan.FromMinutes(5));

        var first = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(0.80, first.UsedFraction!.Value, 4);

        var next = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.42, next.UsedFraction!.Value, 4);
        Assert.Equal(2, rig.Http.Requests.Count);
    }

    /// <summary>A 401 must not stop the next tick from trying: the token was rotated behind us.</summary>
    [Fact]
    public async Task A401DoesNotStopTheNextTickFromTrying()
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.Unauthorized);
        rig.Http.Respond(HttpStatusCode.Unauthorized);
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = rig.Reader();

        var rejected = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatus.NeedsAuth, rejected.Status);
        Assert.Equal(2, rig.Http.Requests.Count);

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(3, rig.Http.Requests.Count);
        Assert.Equal("session", snapshot.Windows[0].Id);
    }

    /// <summary>It re-reads the credential once before giving up: the token may have been re-minted.</summary>
    [Fact]
    public async Task A401ReReadsTheCredentialOnce()
    {
        var rig = new ClaudeRig();
        var item = rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.Forbidden);
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = rig.Reader();

        // The second attempt reads the keychain again, which is where a rotation shows up.
        item.Modified = rig.Clock.Now.AddMinutes(1);
        item.Data = FakeKeychain.Credential("rotated", rig.Clock.Now.AddHours(8));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(2, rig.Http.Requests.Count);
        Assert.Equal(2, rig.Keychain.ReadCount);
    }

    /// <summary>
    /// The reading is recorded against the token that *answered*. A 401 re-reads the item and may
    /// come back with a rotated token; recording the one the fetch started with would make the next
    /// tick inside the floor spend a request to rediscover that it already has the answer.
    /// </summary>
    [Fact]
    public async Task A401RetryRecordsTheTokenThatAnswered()
    {
        var rig = new ClaudeRig();
        var item = rig.AddCredential(accessToken: "old-token");
        var transport = new RawTransport(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UsagePayload) });

        // The token rotates under the 401, which is what a 401 means here.
        transport.OnRequest = _ =>
        {
            item.Modified = rig.Clock.Now.AddMinutes(1);
            item.Data = FakeKeychain.Credential("rotated", rig.Clock.Now.AddHours(8));
        };

        var reader = rig.Reader(options => options.Http = transport);

        var first = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Equal(2, transport.Requests.Count);

        // Inside the endpoint's floor the held reading answers, so no third request is spent.
        var second = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(0.42, second.UsedFraction!.Value, 4);
    }

    /// <summary>A keychain failure must not stop the next tick from reading again.</summary>
    [Fact]
    public async Task AKeychainFailureDoesNotStopTheNextTickFromReading()
    {
        var rig = new ClaudeRig();
        var reader = rig.Reader();

        var first = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatus.NeedsAuth, first.Status);
        Assert.Empty(rig.Http.Requests);

        // The machine woke up: an item exists now. The credential cache waits out its window before
        // asking macOS about a version it has already failed on, which is the same rule the archive
        // follows, so the clock is moved past it.
        rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Single(rig.Http.Requests);
    }

    /// <summary>A rotated token is not the previous account's reading: the held answer is keyed on it.</summary>
    [Fact]
    public async Task AChangedTokenDoesNotReuseThePreviousAccountReading()
    {
        var rig = new ClaudeRig();
        var item = rig.AddCredential(expiresAt: rig.Clock.Now.AddMinutes(1));
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = rig.Reader(options => options.MinEndpointInterval = TimeSpan.FromMinutes(5));

        _ = await reader.FetchAsync(Freshness.Standard);

        // The token aged out and Claude Code rotated it: a different token is a different question.
        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        item.Modified = rig.Clock.Now.AddMinutes(-1);
        item.Data = FakeKeychain.Credential("new-token", rig.Clock.Now.AddHours(8));

        _ = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(2, rig.Http.Requests.Count);
    }

    /// <summary>A poll that keeps firing into a 429 is how you stay rate limited.</summary>
    [Fact]
    public async Task ARateLimitIsReportedAndHeldOff()
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.TooManyRequests, string.Empty);
        var reader = rig.Reader();

        _ = await reader.FetchAsync(Freshness.Standard);

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.True(snapshot.Status.IsStale);
        Assert.Single(rig.Http.Requests);

        // The deadline outlives the process, so a relaunch during a penalty does not spend an attempt
        // extending it.
        var until = rig.Store.LoadBackoffUntil("claude");
        Assert.NotNull(until);
        Assert.True(until!.Value >= rig.Clock.Now + TimeSpan.FromSeconds(60));
    }

    /// <summary>The server's own hint raises the floor: `Retry-After: 120` buys two minutes.</summary>
    [Fact]
    public async Task ARetryAfterHeaderRaisesTheFloor()
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        tooMany.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        var reader = rig.Reader(options => options.Http = new RawTransport(tooMany));

        _ = await reader.FetchAsync(Freshness.Standard);

        var until = rig.Store.LoadBackoffUntil("claude");
        Assert.NotNull(until);
        Assert.True(until!.Value >= rig.Clock.Now + TimeSpan.FromSeconds(119));
        Assert.True(until.Value <= rig.Clock.Now + TimeSpan.FromSeconds(121));
    }

    /// <summary>The server's own hint is honoured as a floor, never obeyed literally.</summary>
    [Fact]
    public async Task AZeroRetryAfterStillWaitsAMinute()
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.TooManyRequests, string.Empty);
        var reader = rig.Reader();

        _ = await reader.FetchAsync(Freshness.Standard);

        var until = rig.Store.LoadBackoffUntil("claude");
        Assert.True(until!.Value >= rig.Clock.Now + Backoff.Compute(0));
    }

    /// <summary>Answered, signed in, nothing metered — not an empty reading drawn forever.</summary>
    [Fact]
    public async Task AnAnswerWithNoLimitsSaysSo()
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.OK, """{"extra_usage":null}""");
        var reader = rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Unsupported, snapshot.Status.Kind);
        Assert.Contains("no usage limits", snapshot.Status.Message);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public async Task AMalformedAnswerIsAnApiError()
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.OK, "{not json at all");
        var reader = rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Error, snapshot.Status.Kind);
        Assert.Equal("usage response did not decode", snapshot.Status.Message);
    }

    [Fact]
    public async Task ANonTwoHundredIsABadResponse()
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        rig.Http.Respond(HttpStatusCode.ServiceUnavailable);
        var reader = rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatus.Error("HTTP 503"), snapshot.Status);
    }

    /// <summary>Expired is not signed out: the reading ages rather than clearing.</summary>
    [Fact]
    public async Task AnExpiredTokenAgesTheReadingRatherThanClearingIt()
    {
        var rig = new ClaudeRig();
        rig.AddCredential(expiresAt: rig.Clock.Now.AddMinutes(-1));
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = rig.Reader();

        // A good reading first, from a token that is still valid.
        rig.Keychain.AddCredential(
            rig.DefaultSuffixedService,
            "fresh",
            rig.Clock.Now.AddHours(8),
            rig.Clock.Now.AddMinutes(1),
            "max",
            rig.Account);
        _ = await reader.FetchAsync(Freshness.Standard);

        rig.Clock.Advance(TimeSpan.FromHours(9));
        rig.Keychain.AddCredential(
            rig.DefaultSuffixedService,
            "expired",
            rig.Clock.Now.AddMinutes(-1),
            rig.Clock.Now.AddMinutes(2),
            "max",
            rig.Account);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.True(snapshot.Status.IsStale);
        Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
    }

    [Fact]
    public async Task ThePlanComesFromTheCredential()
    {
        var rig = new ClaudeRig();
        rig.AddCredential(subscriptionType: "enterprise");
        rig.Http.Respond(HttpStatusCode.OK, UsagePayload);

        var snapshot = await rig.Reader().FetchAsync(Freshness.Standard);

        Assert.Equal("Enterprise", snapshot.Plan);
    }

    /// <summary>A session missing from the response leaves the headline empty rather than promoting the weekly.</summary>
    [Fact]
    public async Task AMissingSessionShowsNoReadingRatherThanAnotherWindow()
    {
        var rig = new ClaudeRig();
        rig.AddCredential();
        rig.Http.Respond(
            HttpStatusCode.OK,
            """{"limits":[{"kind":"weekly_all","percent":33,"resets_at":"2099-01-02T00:00:00Z"}]}""");

        var snapshot = await rig.Reader().FetchAsync(Freshness.Standard);

        Assert.Null(snapshot.Headline);
        Assert.Single(snapshot.Windows);
        Assert.Equal(0.33, snapshot.WeeklyFraction!.Value, 4);
    }

    /// <summary>An Enterprise seat reports only its balance, and the ring means the balance.</summary>
    [Fact]
    public async Task AnEnterpriseSeatDrawsItsBalance()
    {
        var rig = new ClaudeRig();
        rig.AddCredential(subscriptionType: "enterprise");
        rig.Http.Respond(HttpStatusCode.OK, """
        { "limits": [], "five_hour": null, "seven_day": null,
          "extra_usage": { "monthly_limit": 20000 },
          "spend": { "enabled": true,
            "limit": { "amount_minor": 20000, "currency": "USD", "exponent": 2 },
            "used": { "amount_minor": 297, "currency": "USD", "exponent": 2 } } }
        """);

        var snapshot = await rig.Reader().FetchAsync(Freshness.Standard);

        Assert.Equal("spend", snapshot.HeadlineId);
        Assert.Equal("spend", Assert.Single(snapshot.Windows).Id);
        Assert.Equal("Enterprise", snapshot.Plan);
        Assert.NotNull(snapshot.Headline!.Money);
    }
}
