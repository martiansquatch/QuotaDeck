namespace QuotaDeck.Readers.Tests.Codex;

using System.Net;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Core.Settings;
using QuotaDeck.Readers.Codex;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// The reader behaviour of Codenotch <c>Sources/Providers/CodexLocalProvider.swift</c>
/// (<c>fetchSnapshot()</c>, lines 40-104): the request contract, the HTTP mapping, the token being
/// re-read on every fetch, the rate-limit floor, and the extras option. The URLProtocol stub cases
/// of <c>Tests/CodexProfileTests.swift:243-272</c> run against <see cref="CodexEndpointStub"/>.
/// </summary>
public class CodexReaderTests
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private const string BothWindowsBody = """
    {"rate_limit":{
      "primary_window":{"used_percent":25,"limit_window_seconds":18000},
      "secondary_window":{"used_percent":10,"limit_window_seconds":604800}},
     "plan_type":"plus"}
    """;

    [Fact]
    public async Task A200ReadingCarriesTheWindowsThePlanAndTheAccountLabel()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var transport = new FakeTransport();
        transport.Respond(HttpStatusCode.OK, BothWindowsBody);
        var store = new InMemoryReaderStateStore();
        var clock = new FakeClock(Now);
        var reader = Reader(home.Root, transport, store, clock);

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
        Assert.Equal(Fidelity.Official, snapshot.Fidelity);
        Assert.Equal("codex", snapshot.Id);
        Assert.Equal("Codex", snapshot.DisplayName);
        Assert.Equal(new[] { "primary", "secondary" }, snapshot.Windows.Select(window => window.Id));
        Assert.Equal("primary", snapshot.HeadlineId);
        Assert.Equal("secondary", snapshot.WeeklyId);
        Assert.Equal("plus", snapshot.Plan);
        Assert.Equal("personal@example.test", snapshot.AccountLabel);
        Assert.Equal(0.25, snapshot.UsedFraction!.Value, precision: 4);
        Assert.Equal(clock.Now, snapshot.FetchedAt);

        // The usage request: the contract's headers and the 15 s budget.
        var usage = transport.Requests[0];
        Assert.Equal(UsageUrl, usage.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, usage.Method);
        Assert.Equal("Bearer personal-token", usage.Headers.GetValues("Authorization").Single());
        Assert.Equal("personal", usage.Headers.GetValues("ChatGPT-Account-Id").Single());
        Assert.Equal("application/json", usage.Headers.GetValues("Accept").Single());
        var cacheControl = string.Join(", ", usage.Headers.GetValues("Cache-Control"));
        Assert.Contains("no-cache", cacheControl);
        Assert.Contains("no-store", cacheControl);
        Assert.Equal(TimeSpan.FromSeconds(15), transport.Timeouts[0]);

        // The reset-credits fetch is concurrent and carries its own beta header.
        var credits = transport.Requests[1];
        Assert.Equal(ResetCreditsUrl, credits.RequestUri!.ToString());
        Assert.Equal("Bearer personal-token", credits.Headers.GetValues("Authorization").Single());
        Assert.Equal("personal", credits.Headers.GetValues("ChatGPT-Account-Id").Single());
        Assert.Equal("codex-1", credits.Headers.GetValues("OpenAI-Beta").Single());
    }

    [Fact]
    public async Task MissingOrExpiredCredentialsReadAsTheirStatusesWithoutARequest()
    {
        using var home = new CodexTestHome();
        var transport = new FakeTransport();
        var reader = Reader(home.Root, transport, new InMemoryReaderStateStore(), new FakeClock(Now));

        var missing = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.NeedsAuth, missing.Status.Kind);

        CodexTestData.WriteAuth(
            CodexProfile.Default(home.Root),
            "personal",
            accessTokenExpiresAt: Now.ToUnixTimeSeconds() - 1);

        var expired = await reader.FetchAsync(Freshness.FromSource);
        Assert.Equal(ProviderStatusKind.Stale, expired.Status.Kind);
        Assert.Equal(Now, expired.Status.StaleSince);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARefusedTokenReadsAsNeedsAuth(HttpStatusCode status)
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var transport = new FakeTransport();
        transport.Respond(status);
        var reader = Reader(home.Root, transport, new InMemoryReaderStateStore(), new FakeClock(Now));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.NeedsAuth, snapshot.Status.Kind);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task A429WithoutAHintWaitsTheSixtySecondFloor()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var transport = new FakeTransport();
        transport.Respond(HttpStatusCode.TooManyRequests);
        var store = new InMemoryReaderStateStore();
        var reader = Reader(home.Root, transport, store, new FakeClock(Now));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Equal(Now.AddSeconds(60), store.LoadBackoffUntil("codex"));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task A429WithAShortHintStillWaitsTheSixtySecondFloor()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var transport = new CodexScriptedTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, headers: ("Retry-After", "10"));
        var store = new InMemoryReaderStateStore();
        var reader = Reader(home.Root, transport, store, new FakeClock(Now));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Equal(Now.AddSeconds(60), store.LoadBackoffUntil("codex"));
    }

    [Fact]
    public async Task A429WithALongerHintHonoursTheHint()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var transport = new CodexScriptedTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, headers: ("Retry-After", "300"));
        var store = new InMemoryReaderStateStore();
        var reader = Reader(home.Root, transport, store, new FakeClock(Now));

        await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(Now.AddSeconds(300), store.LoadBackoffUntil("codex"));
    }

    [Fact]
    public async Task AnHttpDateRetryAfterIsParsedAgainstTheReadersClock()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var transport = new CodexScriptedTransport();
        transport.Respond(
            HttpStatusCode.TooManyRequests,
            headers: ("Retry-After", Now.AddSeconds(300).UtcDateTime.ToString("r")));
        var store = new InMemoryReaderStateStore();
        var reader = Reader(home.Root, transport, store, new FakeClock(Now));

        await reader.FetchAsync(Freshness.Standard);

        var until = store.LoadBackoffUntil("codex");
        Assert.NotNull(until);
        Assert.InRange(
            (until.Value - Now.AddSeconds(300)).TotalSeconds,
            -1.5,
            1.5);
    }

    [Fact]
    public async Task ABackoffHoldsTheNextFetchAndItsExpiryReleasesIt()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var transport = new CodexScriptedTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, headers: ("Retry-After", "300"));
        transport.Respond(HttpStatusCode.OK, BothWindowsBody);
        transport.Respond(HttpStatusCode.OK, "{}");
        var store = new InMemoryReaderStateStore();
        var clock = new FakeClock(Now);
        var reader = Reader(home.Root, transport, store, clock);

        await reader.FetchAsync(Freshness.Standard);
        Assert.Single(transport.Requests);

        // Inside the deadline: answered from memory, no request spent.
        clock.Advance(60);
        var held = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Stale, held.Status.Kind);
        Assert.Single(transport.Requests);

        // Past it: the read goes out again.
        clock.Advance(300);
        var released = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Ok, released.Status.Kind);
        Assert.Equal(3, transport.Requests.Count);
    }

    [Fact]
    public async Task OtherStatusesReadAsTheirStatusCodeOrTheirBody()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var serverError = new FakeTransport();
        serverError.Respond(HttpStatusCode.InternalServerError, "boom");
        var first = await Reader(home.Root, serverError, new InMemoryReaderStateStore(), new FakeClock(Now))
            .FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Error, first.Status.Kind);
        Assert.Equal("HTTP 500", first.Status.Message);

        var notJson = new FakeTransport();
        notJson.Respond(HttpStatusCode.OK, "not-json");
        var second = await Reader(home.Root, notJson, new InMemoryReaderStateStore(), new FakeClock(Now))
            .FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Error, second.Status.Kind);
        Assert.Equal("HTTP 0", second.Status.Message);

        var noWindows = new FakeTransport();
        noWindows.Respond(
            HttpStatusCode.OK,
            "{\"rate_limit\":{\"primary_window\":{\"used_percent\":null,\"limit_window_seconds\":18000}}}");
        var third = await Reader(home.Root, noWindows, new InMemoryReaderStateStore(), new FakeClock(Now))
            .FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Unsupported, third.Status.Kind);
        Assert.Equal("Codex reported no usage windows", third.Status.Message);
    }

    [Fact]
    public async Task TheExtrasOptionControlsSparkAndCodeReviewWindows()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        const string SparkOnlyBody = """
        {"additional_rate_limits":[{"limit_name":"Spark","rate_limit":{
          "primary_window":{"used_percent":40,"limit_window_seconds":18000},
          "secondary_window":{"used_percent":70,"limit_window_seconds":604800}}}]}
        """;

        var withExtras = new FakeTransport();
        withExtras.Respond(HttpStatusCode.OK, SparkOnlyBody);
        var shown = await Reader(home.Root, withExtras, new InMemoryReaderStateStore(), new FakeClock(Now))
            .FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Ok, shown.Status.Kind);
        Assert.Equal(new[] { "spark", "spark-secondary" }, shown.Windows.Select(window => window.Id));

        var withoutExtras = new FakeTransport();
        withoutExtras.Respond(HttpStatusCode.OK, SparkOnlyBody);
        var hidden = await Reader(
                home.Root,
                withoutExtras,
                new InMemoryReaderStateStore(),
                new FakeClock(Now),
                showExtraLimits: false)
            .FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Unsupported, hidden.Status.Kind);
    }

    [Fact]
    public async Task AResetCreditsFailureIsIgnored()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var failing = new FakeTransport();
        failing.Respond(HttpStatusCode.OK, BothWindowsBody);
        failing.Respond(HttpStatusCode.InternalServerError, "boom");
        var first = await Reader(home.Root, failing, new InMemoryReaderStateStore(), new FakeClock(Now))
            .FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Ok, first.Status.Kind);
        Assert.Equal(2, failing.Requests.Count);

        var unreadable = new FakeTransport();
        unreadable.Respond(HttpStatusCode.OK, BothWindowsBody);
        unreadable.Respond(HttpStatusCode.OK, "not-json");
        var second = await Reader(home.Root, unreadable, new InMemoryReaderStateStore(), new FakeClock(Now))
            .FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Ok, second.Status.Kind);
    }

    [Fact]
    public async Task TokensAreRereadOnEveryFetch()
    {
        using var home = new CodexTestHome();
        var profile = CodexProfile.Default(home.Root);
        CodexTestData.WriteAuth(profile, "work", token: "work-token");

        var stub = new CodexEndpointStub();
        var reader = Reader(home.Root, stub, new InMemoryReaderStateStore(), new FakeClock(Now));

        var first = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(0.75, first.UsedFraction!.Value, precision: 4);

        CodexTestData.WriteAuth(profile, "work", token: "rotated-token");
        var rotated = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.80, rotated.UsedFraction!.Value, precision: 4);
        Assert.Equal("Bearer rotated-token", stub.Requests[^2].Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task ASiblingProfilesCredentialIsNeverUsedByTheDefaultReader()
    {
        using var home = new CodexTestHome();
        home.WithLayout(new Dictionary<string, string[]> { [".codex-work"] = ["config.toml"] });
        var sibling = CodexProfile.Discover(home.Root).Single(profile => profile.Slug == "work");
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");
        CodexTestData.WriteAuth(sibling, "work", token: "work-token");

        var stub = new CodexEndpointStub();
        var reader = Reader(home.Root, stub, new InMemoryReaderStateStore(), new FakeClock(Now));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        // 10% is personal; 75% would mean the work sibling's credential had leaked in.
        Assert.Equal(0.10, snapshot.UsedFraction!.Value, precision: 4);
        Assert.Equal("personal@example.test", snapshot.AccountLabel);
    }

    [Fact]
    public async Task ALimitedTokenBacksOffThroughTheEndpointStub()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "work", token: "limited-token");

        var stub = new CodexEndpointStub();
        var store = new InMemoryReaderStateStore();
        var reader = Reader(home.Root, stub, store, new FakeClock(Now));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Equal(Now.AddSeconds(300), store.LoadBackoffUntil("codex"));
        Assert.Single(stub.Requests);
    }

    private static CodexReader Reader(
        string home,
        IHttpTransport transport,
        InMemoryReaderStateStore store,
        FakeClock clock,
        bool showExtraLimits = true) =>
        new(Context(transport, store, clock), home) { ShowExtraLimits = showExtraLimits };

    private static ReaderContext Context(
        IHttpTransport transport,
        InMemoryReaderStateStore store,
        FakeClock clock) => new()
        {
            Config = new ReaderConfig(),
            StateStore = store,
            TimeProvider = clock,
            DataDirectory = Path.Combine(Path.GetTempPath(), "quotadeck-reader-tests"),
            Http = transport,
            Process = new FakeProcessRunner(),
        };
}
