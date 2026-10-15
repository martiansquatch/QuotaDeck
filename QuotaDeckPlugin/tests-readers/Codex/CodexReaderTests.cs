namespace QuotaDeck.Readers.Tests.Codex;

using System.Net;
using System.Text;
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
/// of <c>Tests/CodexProfileTests.swift:243-272</c> are scripted on <see cref="FakeTransport"/> —
/// responses by URL, headers (a <c>Retry-After</c> among them) on the response, and the request
/// headers asserted through <c>Recorded</c>.
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
        var usage = transport.Recorded[0];
        Assert.Equal(UsageUrl, usage.Url);
        Assert.Equal("GET", usage.Method);
        Assert.Equal("Bearer personal-token", usage.Header("Authorization"));
        Assert.Equal("personal", usage.Header("ChatGPT-Account-Id"));
        Assert.Equal("application/json", usage.Header("Accept"));
        var cacheControl = usage.Header("Cache-Control") ?? string.Empty;
        Assert.Contains("no-cache", cacheControl);
        Assert.Contains("no-store", cacheControl);
        Assert.Equal(TimeSpan.FromSeconds(15), transport.Timeouts[0]);

        // The reset-credits fetch is concurrent and carries its own beta header.
        var credits = transport.Recorded[1];
        Assert.Equal(ResetCreditsUrl, credits.Url);
        Assert.Equal("Bearer personal-token", credits.Header("Authorization"));
        Assert.Equal("personal", credits.Header("ChatGPT-Account-Id"));
        Assert.Equal("codex-1", credits.Header("OpenAI-Beta"));
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

    /// <summary>
    /// Codex waits flat (CodexLocalProvider.swift:161-173): a second 429 in a row is another
    /// minute, never the ladder's two.
    /// </summary>
    [Fact]
    public async Task ConsecutiveRateLimitsStayFlatOnCodex()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var transport = new FakeTransport();
        transport.Respond(UsageUrl, HttpStatusCode.TooManyRequests);
        transport.Respond(UsageUrl, HttpStatusCode.TooManyRequests);
        var store = new InMemoryReaderStateStore();
        var clock = new FakeClock(Now);
        var reader = Reader(home.Root, transport, store, clock);

        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(Now.AddSeconds(60), store.LoadBackoffUntil("codex"));

        clock.Advance(61);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(Now.AddSeconds(61 + 60), store.LoadBackoffUntil("codex"));
    }

    [Fact]
    public async Task A429WithAShortHintStillWaitsTheSixtySecondFloor()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var transport = new FakeTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, "{}", "application/json", ("Retry-After", "10"));
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

        var transport = new FakeTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, "{}", "application/json", ("Retry-After", "300"));
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

        var transport = new FakeTransport();
        transport.Respond(
            HttpStatusCode.TooManyRequests,
            "{}",
            "application/json",
            ("Retry-After", Now.AddSeconds(300).UtcDateTime.ToString("r")));
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

        var transport = new FakeTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, "{}", "application/json", ("Retry-After", "300"));
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

        var transport = new FakeTransport();
        transport.Respond(
            UsageUrl,
            HttpStatusCode.OK,
            """{"rate_limit":{"primary_window":{"used_percent":75,"limit_window_seconds":18000}}}""");
        transport.Respond(
            UsageUrl,
            HttpStatusCode.OK,
            """{"rate_limit":{"primary_window":{"used_percent":80,"limit_window_seconds":18000}}}""");
        var reader = Reader(home.Root, transport, new InMemoryReaderStateStore(), new FakeClock(Now));

        var first = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(0.75, first.UsedFraction!.Value, precision: 4);

        CodexTestData.WriteAuth(profile, "work", token: "rotated-token");
        var rotated = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.80, rotated.UsedFraction!.Value, precision: 4);
        var usage = transport.Recorded.Where(request => request.Url == UsageUrl).ToList();
        Assert.Equal(2, usage.Count);
        Assert.Equal("Bearer work-token", usage[0].Header("Authorization"));
        Assert.Equal("Bearer rotated-token", usage[1].Header("Authorization"));
    }

    [Fact]
    public async Task ASiblingProfilesCredentialIsNeverUsedByTheDefaultReader()
    {
        using var home = new CodexTestHome();
        home.WithLayout(new Dictionary<string, string[]> { [".codex-work"] = ["config.toml"] });
        var sibling = CodexProfile.Discover(home.Root).Single(profile => profile.Slug == "work");
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");
        CodexTestData.WriteAuth(sibling, "work", token: "work-token");

        var transport = new FakeTransport();
        transport.Respond(
            UsageUrl,
            HttpStatusCode.OK,
            """{"rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":18000}}}""");
        var reader = Reader(home.Root, transport, new InMemoryReaderStateStore(), new FakeClock(Now));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        // 10% is personal; work's 75% would mean the sibling's credential had leaked in.
        Assert.Equal(0.10, snapshot.UsedFraction!.Value, precision: 4);
        Assert.Equal("personal@example.test", snapshot.AccountLabel);
        var usage = transport.Recorded.First(request => request.Url == UsageUrl);
        Assert.Equal("Bearer personal-token", usage.Header("Authorization"));
        Assert.Equal("personal", usage.Header("ChatGPT-Account-Id"));
    }

    [Fact]
    public async Task ALimitedTokenBacksOffWithoutRetryingImmediately()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "work", token: "limited-token");

        var transport = new FakeTransport();
        transport.Respond(
            UsageUrl,
            HttpStatusCode.TooManyRequests,
            "{}",
            "application/json",
            ("Retry-After", "300"));
        var store = new InMemoryReaderStateStore();
        var reader = Reader(home.Root, transport, store, new FakeClock(Now));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Equal(Now.AddSeconds(300), store.LoadBackoffUntil("codex"));
        Assert.Single(transport.Requests);
    }

    /// <summary>
    /// The hint is measured when the 429 arrives, not when the request was built
    /// (CodexLocalProvider.swift:64): a slow exchange must not stretch an HTTP-date wait.
    /// </summary>
    [Fact]
    public async Task AnHttpDateRetryAfterIsMeasuredWhenThe429Arrives()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var clock = new FakeClock(Now);
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("Retry-After", Now.AddSeconds(300).UtcDateTime.ToString("r"));
        var transport = new InFlightClockTransport(clock, TimeSpan.FromSeconds(60), response);
        var store = new InMemoryReaderStateStore();
        var reader = Reader(home.Root, transport, store, clock);

        await reader.FetchAsync(Freshness.Standard);

        // Requested at Now, the 429 arrives at Now+60, the hint points at Now+300 ⇒ a 240 s wait,
        // so the deadline is Now+300. Measuring the hint at the request would give Now+360.
        Assert.Equal(Now.AddSeconds(300), store.LoadBackoffUntil("codex"));
    }

    /// <summary>
    /// A successful read still waits for the concurrent reset-credits fetch (CodexLocalProvider
    /// awaits it while building the snapshot): the cycle cannot return while the detail is pending.
    /// </summary>
    [Fact]
    public async Task TheResetCreditsFetchIsAwaitedBeforeASuccessfulReadingReturns()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new GatedResetCreditsTransport(BothWindowsBody, gate);
        var reader = Reader(home.Root, transport, new InMemoryReaderStateStore(), new FakeClock(Now));

        var fetch = reader.FetchAsync(Freshness.Standard);
        await transport.CreditsReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fetch.IsCompleted);

        gate.SetResult();
        var snapshot = await fetch;
        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
    }

    /// <summary>
    /// A usage body that decides the reading (here: nothing metered) cancels the reset-credits
    /// request rather than waiting out a slow endpoint to its own 15 s timeout.
    /// </summary>
    [Fact]
    public async Task AUsageParseFailureCancelsTheResetCreditsRequest()
    {
        using var home = new CodexTestHome();
        CodexTestData.WriteAuth(CodexProfile.Default(home.Root), "personal", token: "personal-token");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new GatedResetCreditsTransport("{}", gate);
        var reader = Reader(home.Root, transport, new InMemoryReaderStateStore(), new FakeClock(Now));

        var snapshot = await reader.FetchAsync(Freshness.Standard).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProviderStatusKind.Unsupported, snapshot.Status.Kind);
        await transport.CreditsCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    /// <summary>A transport that moves the clock while the request is in flight, then answers.</summary>
    private sealed class InFlightClockTransport : IHttpTransport
    {
        private readonly FakeClock _clock;
        private readonly TimeSpan _advance;
        private readonly HttpResponseMessage _response;

        public InFlightClockTransport(FakeClock clock, TimeSpan advance, HttpResponseMessage response)
        {
            this._clock = clock;
            this._advance = advance;
            this._response = response;
        }

        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            this._clock.Advance(this._advance);
            return Task.FromResult(this._response);
        }
    }

    /// <summary>A transport whose reset-credits answer waits for the test's gate and records when
    /// it is cancelled instead; usage answers with the body it was given.</summary>
    private sealed class GatedResetCreditsTransport : IHttpTransport
    {
        private readonly string _usageBody;
        private readonly TaskCompletionSource _gate;

        public GatedResetCreditsTransport(string usageBody, TaskCompletionSource gate)
        {
            this._usageBody = usageBody;
            this._gate = gate;
        }

        public TaskCompletionSource CreditsReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CreditsCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var body = this._usageBody;
            if (request.RequestUri!.AbsolutePath.EndsWith("rate-limit-reset-credits", StringComparison.Ordinal))
            {
                this.CreditsReached.SetResult();
                try
                {
                    await this._gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    this.CreditsCancelled.SetResult();
                    throw;
                }

                body = "{}";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
