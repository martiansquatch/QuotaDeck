namespace QuotaDeck.Readers.Tests.OpenCode;

using System.Globalization;
using System.Net;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Core.Settings;
using QuotaDeck.Readers.OpenCode;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// The reader over its scripted endpoints: which route each credential earns, what every status
/// means, and that nothing on the way mutates the store it read.
/// </summary>
public class OpenCodeReaderTests
{
    private const string UsageUrl = OpenCodeUsage.Endpoint;

    private const string OAuthUsageUrl = OpenCodeUsage.OAuthEndpoint;

    private const string ConsoleUrl = "https://opencode.ai/console/api/user";

    [Fact]
    public async Task AGoKeyReadsTheZenRouteAndPublishesTheSnapshot()
    {
        using var directory = DirectoryWithGoKey();
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(HttpStatusCode.OK, OpenCodeTestReader.LivePayload);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        var request = Assert.Single(transport.Requests);
        Assert.Equal(UsageUrl, request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer sk-go-live", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("application/json", request.Headers.GetValues("Accept").Single());
        Assert.False(request.Headers.Contains("x-opencode-org-id"));
        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(transport.Timeouts));

        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
        Assert.Equal(Fidelity.Official, snapshot.Fidelity);
        Assert.Equal("Go", snapshot.Plan);
        Assert.Null(snapshot.AccountLabel);
        Assert.Equal("rolling", snapshot.HeadlineId);
        Assert.Equal("weekly", snapshot.WeeklyId);
        Assert.Equal(["rolling", "weekly", "monthly"], snapshot.Windows.Select(window => window.Id));
        Assert.Equal(TimeSpan.FromDays(30), snapshot.Windows.Single(window => window.Id == "monthly").Duration);
        Assert.Equal(0.0, snapshot.UsedFraction);
    }

    [Fact]
    public async Task AnOAuthSignInReadsTheInferenceRouteWithTheOrgHeader()
    {
        using var directory = DirectoryWithOAuth(expiresInFuture: true, org: "org_1");
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(HttpStatusCode.OK, OpenCodeTestReader.LivePayload);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        var request = Assert.Single(transport.Requests);
        Assert.Equal(OAuthUsageUrl, request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer st_abc", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("org_1", request.Headers.GetValues("x-opencode-org-id").Single());
        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
    }

    [Fact]
    public async Task AnOAuthSignInWithoutAnOrgSendsNoOrgHeader()
    {
        using var directory = DirectoryWithOAuth(expiresInFuture: true, org: null);
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(HttpStatusCode.OK, OpenCodeTestReader.LivePayload);

        await reader.FetchAsync(Freshness.FromSource);

        var request = Assert.Single(transport.Requests);
        Assert.False(request.Headers.Contains("x-opencode-org-id"));
    }

    /// <summary>
    /// A database-only sign-in is claimed like a file one, and the endpoint follows it: the OAuth
    /// row means the <c>inference/</c> route.
    /// </summary>
    [Fact]
    public async Task ADatabaseOnlySignInIsClaimed()
    {
        using var directory = new TempDirectory();
        OpenCodeTestReader.CreateCredentialDatabase(
            directory,
            ("opencode", """{"type":"oauth","access":"st-db","expires":1792854570201,"metadata":{"orgID":"org_1"}}""", 1, 3));
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(HttpStatusCode.OK, OpenCodeTestReader.LivePayload);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(OAuthUsageUrl, request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer st-db", request.Headers.GetValues("Authorization").Single());
    }

    /// <summary>
    /// The credential is re-read on every fetch, so a sign-in that changed on disk is picked up
    /// without restarting anything.
    /// </summary>
    [Fact]
    public async Task TheCredentialIsReReadOnEveryFetch()
    {
        using var directory = DirectoryWithGoKey();
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(HttpStatusCode.OK, OpenCodeTestReader.LivePayload);

        await reader.FetchAsync(Freshness.FromSource);

        directory.Write("auth.json", """{"opencode-go":{"type":"api","key":"sk-go-rotated"}}""");
        transport.Respond(HttpStatusCode.OK, OpenCodeTestReader.LivePayload);

        await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal("Bearer sk-go-rotated", transport.Requests[1].Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task NoCredentialIsANeedsAuthWithoutARequest()
    {
        using var directory = new TempDirectory();
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.NeedsAuth, snapshot.Status.Kind);
        Assert.Empty(transport.Requests);
    }

    // ---- The 401 fork. ----

    /// <summary>
    /// A 401 on an OAuth sign-in is checked against the console before it is called a sign-out: the
    /// console answers 401 only to a bad token, so a 200 here means the account has no Go plan.
    /// </summary>
    [Fact]
    public async Task AnOAuth401WithAConsole200IsNothingMetered()
    {
        using var directory = DirectoryWithOAuth(expiresInFuture: true, org: "org_1");
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(OAuthUsageUrl, HttpStatusCode.Unauthorized);
        transport.Respond(ConsoleUrl, HttpStatusCode.OK, "{}");

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Unsupported, snapshot.Status.Kind);
        Assert.Equal("No OpenCode Go subscription on this account", snapshot.Status.Message);

        Assert.Equal(2, transport.Requests.Count);
        var probe = transport.Requests[1];
        Assert.Equal(ConsoleUrl, probe.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer st_abc", probe.Headers.GetValues("Authorization").Single());
        Assert.Equal("org_1", probe.Headers.GetValues("x-opencode-org-id").Single());
        Assert.Equal(TimeSpan.FromSeconds(15), transport.Timeouts[1]);
    }

    /// <summary>
    /// The probe carries the credential's own console, with its query stripped — and falls back to
    /// the default console when the sign-in names none.
    /// </summary>
    [Fact]
    public async Task TheConsoleProbeStripsTheServersQuery()
    {
        using var directory = DirectoryWithOAuth(
            expiresInFuture: true,
            org: null,
            server: "https://console.example.test/base?tenant=secret");
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(OAuthUsageUrl, HttpStatusCode.Unauthorized);
        transport.Respond("https://console.example.test/base/api/user", HttpStatusCode.OK);

        await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal("https://console.example.test/base/api/user", transport.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task AnOAuth401WithoutAConsoleNameProbesTheDefaultConsole()
    {
        using var directory = DirectoryWithOAuth(expiresInFuture: true, org: null, server: null);
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(OAuthUsageUrl, HttpStatusCode.Unauthorized);
        transport.Respond(ConsoleUrl, HttpStatusCode.InternalServerError);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.NeedsAuth, snapshot.Status.Kind);
        Assert.Equal(ConsoleUrl, transport.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task AnOAuth401WithAFailedProbeAndAPastExpiryIsCredentialExpired()
    {
        using var directory = DirectoryWithOAuth(expiresInFuture: false, org: null);
        var (reader, transport, _, clock) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(OAuthUsageUrl, HttpStatusCode.Unauthorized);
        transport.Respond(ConsoleUrl, HttpStatusCode.Unauthorized);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Equal(clock.Now, snapshot.Status.StaleSince);
    }

    /// <summary>The console probe comes first: a live sign-in with no plan is not an expiry.</summary>
    [Fact]
    public async Task AnExpiredOAuthTokenWithAConsole200IsStillNothingMetered()
    {
        using var directory = DirectoryWithOAuth(expiresInFuture: false, org: null);
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(OAuthUsageUrl, HttpStatusCode.Unauthorized);
        transport.Respond(ConsoleUrl, HttpStatusCode.OK);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Unsupported, snapshot.Status.Kind);
        Assert.Equal("No OpenCode Go subscription on this account", snapshot.Status.Message);
    }

    /// <summary>The Go key has no console: its 401 stays a sign-out, and no second request is made.</summary>
    [Fact]
    public async Task AGoKey401IsANeedsAuthWithoutAProbe()
    {
        using var directory = DirectoryWithGoKey();
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(HttpStatusCode.Unauthorized);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.NeedsAuth, snapshot.Status.Kind);
        Assert.Single(transport.Requests);
    }

    // ---- The other statuses. ----

    /// <summary>
    /// A network error is not translated: it surfaces as whatever the transport threw, and the base
    /// maps it to an Error status carrying its message.
    /// </summary>
    [Fact]
    public async Task ANetworkErrorSurfacesAsAnErrorStatus()
    {
        using var directory = DirectoryWithGoKey();
        var transport = new ScriptedTransport();
        transport.Throw(new HttpRequestException("boom"));
        var reader = new OpenCodeReader(ContextWith(transport), directory.Path);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Error, snapshot.Status.Kind);
        Assert.Equal("boom", snapshot.Status.Message);
    }

    [Fact]
    public async Task A403IsNothingMeteredForKey()
    {
        using var directory = DirectoryWithGoKey();
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(HttpStatusCode.Forbidden);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Unsupported, snapshot.Status.Kind);
        Assert.Equal("No OpenCode Go subscription on this key", snapshot.Status.Message);
    }

    [Fact]
    public async Task AServerErrorIsABadResponse()
    {
        using var directory = DirectoryWithGoKey();
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(HttpStatusCode.InternalServerError);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Error, snapshot.Status.Kind);
        Assert.Equal("HTTP 500", snapshot.Status.Message);
    }

    [Fact]
    public async Task AnUnreadableUsageBodyIsABadResponseZero()
    {
        using var directory = DirectoryWithGoKey();
        var transport = new ScriptedTransport();
        transport.RespondBytes(HttpStatusCode.OK, [0xC0, 0x80]); // never valid UTF-8

        var reader = new OpenCodeReader(ContextWith(transport), directory.Path);
        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Error, snapshot.Status.Kind);
        Assert.Equal("HTTP 0", snapshot.Status.Message);
    }

    [Fact]
    public async Task AnUnparseableUsageBodyIsABadResponseZero()
    {
        using var directory = DirectoryWithGoKey();
        var (reader, transport, _, _) = OpenCodeTestReader.Create(directory.Path);
        transport.Respond(HttpStatusCode.OK, """{"usage":{}}""");

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Error, snapshot.Status.Kind);
        Assert.Equal("HTTP 0", snapshot.Status.Message);
    }

    // ---- Rate limits. ----

    /// <summary>
    /// The reader reports the server's hint raw; the base computes the wait for OpenCode's capped
    /// ladder (a minute doubling, the hint as a floor, the whole wait capped at fifteen minutes) and
    /// persists the deadline, and the next fetch inside it spends nothing.
    /// </summary>
    [Fact]
    public async Task A429WithAHintRaisesTheFloorAndPersistsTheDeadline()
    {
        using var directory = DirectoryWithGoKey();
        var transport = new ScriptedTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "120"));
        var clock = OpenCodeTestReader.DefaultClock;
        var store = new InMemoryReaderStateStore();
        var reader = new OpenCodeReader(ContextWith(transport, store, clock), directory.Path);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Equal(clock.Now.AddSeconds(120), store.LoadBackoffUntil("opencode"));

        // Inside the hold-off the transport is not touched again...
        clock.Advance(60);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Single(transport.Requests);

        // ...and past it, the next 429 takes the next rung of the ladder: a doubling to two minutes,
        // with no hint to raise it this time.
        clock.Advance(120);
        transport.Respond(HttpStatusCode.TooManyRequests, "{}");
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(clock.Now.AddSeconds(120), store.LoadBackoffUntil("opencode"));
    }

    [Fact]
    public async Task A429WithoutAHintStartsAtAMinute()
    {
        using var directory = DirectoryWithGoKey();
        var transport = new ScriptedTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, "{}");
        var clock = OpenCodeTestReader.DefaultClock;
        var store = new InMemoryReaderStateStore();
        var reader = new OpenCodeReader(ContextWith(transport, store, clock), directory.Path);

        await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(clock.Now.AddSeconds(60), store.LoadBackoffUntil("opencode"));
    }

    /// <summary>
    /// OpenCode's own rule: the whole wait, the server's hint included, is capped at fifteen
    /// minutes — an hour-long hint still parks the reader for 900 s. The default ladder would have
    /// honoured the full hour.
    /// </summary>
    [Fact]
    public async Task A429WithAnHourHintIsCappedAtFifteenMinutes()
    {
        using var directory = DirectoryWithGoKey();
        var transport = new ScriptedTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "3600"));
        var clock = OpenCodeTestReader.DefaultClock;
        var store = new InMemoryReaderStateStore();
        var reader = new OpenCodeReader(ContextWith(transport, store, clock), directory.Path);

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Equal(clock.Now.AddSeconds(900), store.LoadBackoffUntil("opencode"));
    }

    /// <summary>
    /// The capped ladder still doubles per consecutive 429: a minute, two, four.
    /// </summary>
    [Fact]
    public async Task Consecutive429sClimbTheCappedLadder()
    {
        using var directory = DirectoryWithGoKey();
        var transport = new ScriptedTransport();
        var clock = OpenCodeTestReader.DefaultClock;
        var store = new InMemoryReaderStateStore();
        var reader = new OpenCodeReader(ContextWith(transport, store, clock), directory.Path);

        foreach (var expected in new[] { 60, 120, 240 })
        {
            transport.Respond(HttpStatusCode.TooManyRequests, "{}");
            await reader.FetchAsync(Freshness.FromSource);
            Assert.Equal(clock.Now.AddSeconds(expected), store.LoadBackoffUntil("opencode"));

            clock.Advance(expected + 1);
        }

        Assert.Equal(3, transport.Requests.Count);
    }

    /// <summary>A fresh reading is the end of the penalty: the persisted deadline is cleared.</summary>
    [Fact]
    public async Task ASuccessClearsTheHoldOff()
    {
        using var directory = DirectoryWithGoKey();
        var transport = new ScriptedTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, "{}");
        var clock = OpenCodeTestReader.DefaultClock;
        var store = new InMemoryReaderStateStore();
        var reader = new OpenCodeReader(ContextWith(transport, store, clock), directory.Path);

        await reader.FetchAsync(Freshness.FromSource);
        Assert.NotNull(store.LoadBackoffUntil("opencode"));

        clock.Advance(61);
        transport.Respond(HttpStatusCode.OK, OpenCodeTestReader.LivePayload);
        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
        Assert.Null(store.LoadBackoffUntil("opencode"));
    }

    // ---- The module. ----

    [Fact]
    public void TheModuleDeclaresItsIdentity()
    {
        var module = new OpenCodeModule();

        Assert.Equal("opencode", module.ProviderId);
        Assert.Equal("OpenCode", module.DisplayName);
        Assert.Equal(new QuotaDeck.Core.Readers.ProviderPalette(0xF5, 0xA5, 0x24), module.Palette);
        Assert.Equal("opencode", module.CreateReader(ContextWith(new ScriptedTransport())).Id);
        Assert.Equal("OpenCode", module.CreateReader(ContextWith(new ScriptedTransport())).DisplayName);
    }

    private static TempDirectory DirectoryWithGoKey()
    {
        var directory = new TempDirectory();
        directory.Write("auth.json", """{"opencode-go":{"type":"api","key":"sk-go-live"}}""");
        return directory;
    }

    private static TempDirectory DirectoryWithOAuth(bool expiresInFuture, string? org, string? server = "https://opencode.ai/console")
    {
        var directory = new TempDirectory();
        var expires = expiresInFuture ? 4_102_444_800_000 : 1_600_000_000_000; // 2100 vs 2020, epoch ms
        var metadata = new List<string>();
        if (org is not null)
        {
            metadata.Add($"\"orgID\":\"{org}\"");
        }

        if (server is not null)
        {
            metadata.Add($"\"server\":\"{server}\"");
        }

        var metadataJson = "{" + string.Join(",", metadata) + "}";
        directory.Write(
            "auth.json",
            "{\"opencode\":{\"type\":\"oauth\",\"access\":\"st_abc\",\"expires\":"
                + expires.ToString(CultureInfo.InvariantCulture)
                + ",\"metadata\":" + metadataJson + "}}");
        return directory;
    }

    /// <summary>A context wired to one transport, with fresh fakes around it.</summary>
    internal static ReaderContext ContextWith(
        IHttpTransport transport,
        InMemoryReaderStateStore? store = null,
        FakeClock? clock = null)
    {
        return new ReaderContext
        {
            Config = new ReaderConfig(),
            StateStore = store ?? new InMemoryReaderStateStore(),
            TimeProvider = clock ?? OpenCodeTestReader.DefaultClock,
            DataDirectory = Path.Combine(Path.GetTempPath(), "quotadeck-reader-data"),
            Http = transport,
            Process = new FakeProcessRunner(),
        };
    }
}
