namespace QuotaDeck.Readers.Tests.Claude;

using System.Net;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Readers.Claude;

/// <summary>
/// The three sources, in order, and what each freshness allowance means. Ported from Codenotch
/// <c>Tests/ClaudeOAuthProviderTests.swift</c> (the CLI path, the Desktop cache path and the
/// Deskop/CLI/token preference tests), with the counters kept: "what did the next tick actually
/// do?" is the question every one of these answers.
/// </summary>
public class ClaudeReaderTests
{
    private const string UsagePayload = """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z"}]}""";

    private readonly ClaudeRig _rig = new();

    public ClaudeReaderTests()
    {
        this._rig.AddAccountFile();
        this._rig.AddCredential();
    }

    private static string Berlin(DateTimeOffset at) => FakeClaudeUsageCli.Berlin(at);

    /// <summary>A CLI answer whose windows are still running, dated from the test's clock.</summary>
    private string CliAnswer(int session = 38, int weekly = 4) =>
        FakeClaudeUsageCli.RealAnswer(this._rig.Clock.Now, session, weekly);

    // MARK: - The Desktop cache

    /// <summary>Why the source exists: both other paths can fail while Desktop holds the real numbers.</summary>
    [Fact]
    public async Task AFreshDesktopSnapshotNeedsNoKeychainAndNoRequest()
    {
        this._rig.AddDesktopEntry();
        var reader = this._rig.Reader(options => options.CliFactory = () => new FakeClaudeUsageCli(() => this.CliAnswer()));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(["session", "weekly_all"], snapshot.Windows.Select(window => window.Id));
        Assert.Equal(0.30, snapshot.UsedFraction!.Value, 4);
        Assert.Empty(this._rig.Http.Requests);
    }

    /// <summary>Desktop is preferred over the CLI: it is the cheaper of the two and cannot be refused.</summary>
    [Fact]
    public async Task DesktopIsPreferredOverTheCli()
    {
        this._rig.AddDesktopEntry();
        var cli = new FakeClaudeUsageCli(() => this.CliAnswer());
        var reader = this._rig.Reader(options => options.CliFactory = () => cli);

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.30, snapshot.UsedFraction!.Value, 4);
        Assert.Equal(0, cli.Spawns);
    }

    /// <summary>Everything Claude Desktop's cache cannot say comes from the profile's own record.</summary>
    [Fact]
    public async Task TheDesktopPlanComesFromTheOrganizationType()
    {
        this._rig.AddAccountFile(organizationType: "claude_enterprise");
        this._rig.AddDesktopEntry();
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal("Enterprise", snapshot.Plan);
        Assert.Equal("user@example.com", snapshot.AccountLabel);
    }

    /// <summary>Once Desktop stops updating, its numbers may not keep being presented as live.</summary>
    [Fact]
    public async Task AStaleDesktopSnapshotFallsThroughToTheToken()
    {
        this._rig.AddDesktopEntry(age: TimeSpan.FromHours(4));
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
        Assert.Single(this._rig.Http.Requests);
    }

    /// <summary>A stale cache with a working CLI answers without touching the keychain or the network.</summary>
    [Fact]
    public async Task AStaleDesktopSnapshotUsesCliWhenAvailable()
    {
        this._rig.AddDesktopEntry(age: TimeSpan.FromHours(4));
        var cli = new FakeClaudeUsageCli(() => this.CliAnswer());
        var reader = this._rig.Reader(options => options.CliFactory = () => cli);

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.38, snapshot.UsedFraction!.Value, 4);
        Assert.Equal(1, cli.Spawns);
        Assert.Empty(this._rig.Http.Requests);
    }

    /// <summary>A cache whose window has already reset describes a period that is over.</summary>
    [Fact]
    public async Task ADesktopEntryWithARolledOverWindowIsNotUsed()
    {
        this._rig.AddDesktopEntry(bytes: DesktopEntry.Build(body: DesktopEntry.Compress(
            """{"limits":[{"kind":"session","percent":80,"resets_at":"2000-01-01T00:00:00Z"}]}""")));
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
    }

    /// <summary>
    /// A reading that arrived but is too old is still a miss for the purpose of rescanning: without
    /// this an installed-but-closed Desktop re-walks the cache on every poll.
    /// </summary>
    [Fact]
    public async Task AMissSuppressesTheNextScan()
    {
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader(options => options.MinEndpointInterval = TimeSpan.Zero);

        // Nothing cached yet: a miss, which arms the throttle.
        _ = await reader.FetchAsync(Freshness.Standard);
        this._rig.AddDesktopEntry();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
        Assert.Equal(2, this._rig.Http.Requests.Count);
    }

    [Fact]
    public async Task TheScanHappensAgainOnceTheIntervalPasses()
    {
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader(options =>
        {
            options.MinEndpointInterval = TimeSpan.Zero;
            options.DesktopRescanInterval = TimeSpan.Zero;
        });

        _ = await reader.FetchAsync(Freshness.Standard);
        this._rig.AddDesktopEntry();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.30, snapshot.UsedFraction!.Value, 4);
    }

    /// <summary>Claude Desktop is signed into one account; a profile with another one gets nothing from it.</summary>
    [Fact]
    public async Task ACacheForAnotherOrganizationIsNotUsed()
    {
        this._rig.AddAccountFile(organization: "99999999-8888-7777-6666-555555555555");
        this._rig.AddDesktopEntry();
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
    }

    /// <summary>A profile Claude Code has never signed in to must not fall back to "whatever is in the cache".</summary>
    [Fact]
    public async Task AProfileWithNoRecordedOrganizationIsNotMatched()
    {
        this._rig.Files.Files.Remove(Path.Combine(this._rig.Home, ".claude.json"));
        this._rig.Files.Modified.Remove(Path.Combine(this._rig.Home, ".claude.json"));
        this._rig.AddDesktopEntry();
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
    }

    // MARK: - What a live fetch will accept

    /// <summary>Ten minutes is inside the thirty an idle ring may show and outside the two a watched one may.</summary>
    [Fact]
    public async Task ALiveFetchWillNotServeACacheAnIdleOneWould()
    {
        this._rig.AddDesktopEntry(age: TimeSpan.FromMinutes(10));
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader();

        var standard = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(0.30, standard.UsedFraction!.Value, 4);
        Assert.Empty(this._rig.Http.Requests);

        var live = await reader.FetchAsync(Freshness.Live);

        Assert.Equal(0.42, live.UsedFraction!.Value, 4);
        Assert.Single(this._rig.Http.Requests);
    }

    /// <summary>A cache written seconds ago is the best answer there is to either question.</summary>
    [Fact]
    public async Task ALiveFetchStillTakesACacheThatIsActuallyCurrent()
    {
        this._rig.AddDesktopEntry(age: TimeSpan.FromSeconds(5));
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Live);

        Assert.Equal(0.30, snapshot.UsedFraction!.Value, 4);
        Assert.Empty(this._rig.Http.Requests);
    }

    // MARK: - Ask the source itself

    /// <summary>`.fromSource` skips a cache written seconds ago — the setting's whole point.</summary>
    [Fact]
    public async Task FromSourceSkipsEvenACacheWrittenSecondsAgo()
    {
        this._rig.AddDesktopEntry(age: TimeSpan.FromSeconds(5));
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
        Assert.Single(this._rig.Http.Requests);
    }

    /// <summary>And it may not leave the ring emptier than not asking would have.</summary>
    [Fact]
    public async Task FromSourceFallsBackToTheCacheWhenNoLiveSourceCanAnswer()
    {
        this._rig.AddDesktopEntry(age: TimeSpan.FromMinutes(1));
        this._rig.Http.Respond(HttpStatusCode.TooManyRequests);
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(0.30, snapshot.UsedFraction!.Value, 4);
        Assert.Single(this._rig.Http.Requests);
    }

    /// <summary>A cache past the ordinary thirty minutes is not resurrected by the fallback.</summary>
    [Fact]
    public async Task TheFallbackDoesNotResurrectACacheTooOldToShow()
    {
        this._rig.AddDesktopEntry(age: TimeSpan.FromHours(4));
        this._rig.Http.Respond(HttpStatusCode.TooManyRequests);
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.True(snapshot.Status.IsStale);
        Assert.Empty(snapshot.Windows);
    }

    /// <summary>Skipping the cache is not the same as missing it: counting it as one armed the throttle.</summary>
    [Fact]
    public async Task SkippingTheCacheDoesNotSuppressTheNextScan()
    {
        this._rig.AddDesktopEntry(age: TimeSpan.FromSeconds(30));
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader(options => options.MinEndpointInterval = TimeSpan.Zero);

        _ = await reader.FetchAsync(Freshness.FromSource);
        var next = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.30, next.UsedFraction!.Value, 4);
    }

    // MARK: - The CLI's own throttling

    [Fact]
    public async Task TheCliIsNotSpawnedOnEveryTick()
    {
        var cli = new FakeClaudeUsageCli(() => this.CliAnswer());
        var reader = this._rig.Reader(options => options.CliFactory = () => cli);

        _ = await reader.FetchAsync(Freshness.Standard);
        _ = await reader.FetchAsync(Freshness.Standard);
        _ = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(1, cli.Spawns);
    }

    [Fact]
    public async Task TheCliIsAskedAgainOnceTheIntervalPasses()
    {
        var cli = new FakeClaudeUsageCli(() => this.CliAnswer());
        var reader = this._rig.Reader(options =>
        {
            options.CliFactory = () => cli;
            options.CliRefreshInterval = TimeSpan.Zero;
        });

        _ = await reader.FetchAsync(Freshness.Standard);
        _ = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(2, cli.Spawns);
    }

    [Fact]
    public async Task ALiveFetchAsksTheCliAgainInsideItsOrdinaryInterval()
    {
        var cli = new FakeClaudeUsageCli(() => this.CliAnswer());
        var reader = this._rig.Reader(options =>
        {
            options.CliFactory = () => cli;
            options.CliRefreshInterval = TimeSpan.FromMinutes(5);
            options.LiveCliRefreshInterval = TimeSpan.Zero;
        });

        _ = await reader.FetchAsync(Freshness.Standard);
        _ = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(1, cli.Spawns);

        _ = await reader.FetchAsync(Freshness.Live);

        Assert.Equal(2, cli.Spawns);
    }

    /// <summary>And a live fetch is still not licence to spawn one per poll.</summary>
    [Fact]
    public async Task ALiveFetchReusesTheCliInsideItsOwnInterval()
    {
        var cli = new FakeClaudeUsageCli(() => this.CliAnswer());
        var reader = this._rig.Reader(options => options.CliFactory = () => cli);

        _ = await reader.FetchAsync(Freshness.Live);
        _ = await reader.FetchAsync(Freshness.Live);

        Assert.Equal(1, cli.Spawns);
    }

    /// <summary>A signed-out CLI is asked once, then locked out for the full interval.</summary>
    [Fact]
    public async Task ASignedOutCliDoesNotSpawnOnEveryTick()
    {
        var cli = new FakeClaudeUsageCli(
            () => throw new QuotaDeck.Core.Readers.ReaderException(QuotaDeck.Core.Readers.ReaderError.NeedsAuth));
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader(options =>
        {
            options.CliFactory = () => cli;
            options.MinEndpointInterval = TimeSpan.Zero;
        });

        _ = await reader.FetchAsync(Freshness.Standard);
        _ = await reader.FetchAsync(Freshness.Standard);
        _ = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(1, cli.Spawns);
        Assert.Equal(3, this._rig.Http.Requests.Count);
    }

    /// <summary>A transient CLI failure falls back for that tick but does not lock the CLI out.</summary>
    [Fact]
    public async Task ATransientCliFailureAllowsImmediateRetryOnNextTick()
    {
        var failing = true;
        var cli = new FakeClaudeUsageCli(() =>
        {
            if (failing)
            {
                failing = false;
                return "Temporary blip: retry later";
            }

            return this.CliAnswer();
        });
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader(options => options.CliFactory = () => cli);

        var first = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal("session", first.Windows[0].Id);
        Assert.Equal(1, cli.Spawns);

        var second = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.38, second.UsedFraction!.Value, 4);
        Assert.Equal(2, cli.Spawns);
        Assert.Single(this._rig.Http.Requests);
    }

    /// <summary>
    /// `claude /usage` answers for the whole machine, so it can only stand in for a ring while there
    /// is one login for it to describe.
    /// </summary>
    [Fact]
    public async Task TheDefaultLoginStopsUsingTheCliOnceThereIsASecondOne()
    {
        var work = Path.Combine(this._rig.Home, ".claude-work");
        this._rig.Files.AddDirectory(work);
        this._rig.Files.AddFile(Path.Combine(work, "settings.json"), []);
        this._rig.Keychain.Add(
            ClaudeProfile.DefaultKeychainService + "-" + ClaudeProfile.KeychainSuffixForPath(work),
            FakeKeychain.Credential("work-token", this._rig.Clock.Now.AddHours(8)),
            this._rig.Clock.Now,
            this._rig.Account);
        var cli = new FakeClaudeUsageCli(() => this.CliAnswer());
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader(options => options.CliFactory = () => cli);

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0, cli.Spawns);
        Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
    }

    // MARK: - The hold-off covers the endpoint only

    /// <summary>
    /// The review's failing input: with the whole read held off, a penalty on the endpoint darkened a
    /// ring the cache could still fill. The cache is read first, exactly where Codenotch checks
    /// `retryNoEarlierThan` — after the Desktop step, not before it.
    /// </summary>
    [Fact]
    public async Task WhileHeldOffAFreshDesktopEntryStillAnswers()
    {
        this._rig.AddDesktopEntry();
        var deadline = this._rig.Clock.Now.AddMinutes(30);
        this._rig.Store.SaveBackoffUntil("claude", deadline);
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
        Assert.Equal(0.30, snapshot.UsedFraction!.Value, 4);
        Assert.Empty(this._rig.Http.Requests);

        // A cache hit says nothing about the endpoint's rate limit, so the penalty stands.
        Assert.Equal(deadline, this._rig.Store.LoadBackoffUntil("claude"));
    }

    /// <summary>The CLI is the second source the hold-off must not darken.</summary>
    [Fact]
    public async Task WhileHeldOffTheCliStillAnswers()
    {
        var cli = new FakeClaudeUsageCli(() => this.CliAnswer());
        var deadline = this._rig.Clock.Now.AddMinutes(30);
        this._rig.Store.SaveBackoffUntil("claude", deadline);
        var reader = this._rig.Reader(options => options.CliFactory = () => cli);

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.38, snapshot.UsedFraction!.Value, 4);
        Assert.Equal(1, cli.Spawns);
        Assert.Empty(this._rig.Http.Requests);
        Assert.Equal(deadline, this._rig.Store.LoadBackoffUntil("claude"));
    }

    /// <summary>
    /// Nothing else can answer: the last reading ages rather than being replaced, no request is spent,
    /// and the penalty is neither extended nor lifted.
    /// </summary>
    [Fact]
    public async Task WhileHeldOffWithNothingElseAnsweringTheReadingAges()
    {
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader();
        var fresh = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(0.42, fresh.UsedFraction!.Value, 4);

        // Old enough to be dimmed, and the endpoint is held off for half an hour.
        this._rig.Clock.Advance(TimeSpan.FromMinutes(16));
        var deadline = this._rig.Clock.Now.AddMinutes(30);
        this._rig.Store.SaveBackoffUntil("claude", deadline);

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Equal(fresh.FetchedAt, snapshot.Status.StaleSince);
        Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
        Assert.Single(this._rig.Http.Requests);
        Assert.Equal(deadline, this._rig.Store.LoadBackoffUntil("claude"));
    }

    /// <summary>With nothing remembered, the held-off read is a placeholder carrying the status.</summary>
    [Fact]
    public async Task WhileHeldOffWithNoReadingAtAllTheFaceIsEmpty()
    {
        this._rig.Store.SaveBackoffUntil("claude", this._rig.Clock.Now.AddMinutes(30));
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Empty(snapshot.Windows);
        Assert.Empty(this._rig.Http.Requests);
    }

    // MARK: - Where the CLI is not what answers

    /// <summary>A machine with no CLI at all keeps working exactly as it did.</summary>
    [Fact]
    public async Task NoCliLeavesTheTokenPathIntact()
    {
        this._rig.Http.Respond(HttpStatusCode.OK, UsagePayload);
        var reader = this._rig.Reader();

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0.42, snapshot.UsedFraction!.Value, 4);
    }

    /// <summary>The snapshot's shape is the same whichever source answered.</summary>
    [Fact]
    public async Task EverySourceProducesTheSameDeclaredShape()
    {
        this._rig.AddDesktopEntry();
        var fromDesktop = await this._rig.Reader().FetchAsync(Freshness.Standard);

        Assert.Equal("claude", fromDesktop.Id);
        Assert.Equal("Claude Example", fromDesktop.DisplayName);
        Assert.Equal(Fidelity.Official, fromDesktop.Fidelity);
        Assert.Equal("session", fromDesktop.HeadlineId);
        Assert.Equal("weekly_all", fromDesktop.WeeklyId);
        Assert.Equal("session", fromDesktop.Headline!.Id);
        Assert.Equal(0.74, fromDesktop.WeeklyFraction!.Value, 4);
    }
}
