namespace QuotaDeck.Readers.Claude;

using System.Globalization;
using System.Text.Json;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Readers.Shared;

// Port of Codenotch Sources/Providers/ClaudeOAuthProvider.swift — the actor's own logic, not the
// decoding half (ClaudeUsageResponse.cs) — with the three sources and their freshness allowances:
// fetchSnapshot(freshness:) (lines 194-291), cliEstimateApplies(slug:loginCount:), loginCount,
// fetchFromKeychain(reusableFor:) (303-353), snapshot(windows:plan:resetCredits:), planName(_:),
// desktopReading() (396-431), hasExpiredWindow(_:at:) (436-441), cliWindows(reusableFor:) (451-489),
// fetch(retryingOnUnauthorized:) (491-543) and currentToken() (545-567). The rate-limit bookkeeping
// (hold-off, consecutive 429s, the archive) is UsageReaderBase's, as that class documents.

/// <summary>
/// Everything the reader needs, defaulted to the real machine so production passes only a
/// <see cref="ReaderContext"/> and tests can pass fakes and a temporary home.
/// </summary>
public sealed record ClaudeReaderOptions
{
    public required ReaderContext Context { get; init; }

    /// <summary>The home directory the config directories and the Desktop cache hang off.</summary>
    public string? HomeDirectory { get; init; }

    /// <summary>Defaults to the login keychain.</summary>
    public IKeychain? Keychain { get; init; }

    /// <summary>Defaults to the real filesystem.</summary>
    public IClaudeFileSystem? FileSystem { get; init; }

    /// <summary>The profile to read. Defaults to the default profile under the home directory.</summary>
    public ClaudeProfile? Profile { get; init; }

    /// <summary>
    /// How the CLI is obtained, resolved once on first use. The default locates the real
    /// <c>claude</c>; a test passes a factory that scripts the answer or returns nil for a machine
    /// with no Claude Code.
    /// </summary>
    public Func<IClaudeUsageCli?>? CliFactory { get; init; }

    /// <summary>
    /// The standalone <c>claude</c> the token refresher runs, resolved once on first use. The
    /// default locates the real one; nil means the machine has none.
    /// </summary>
    public Func<string?>? RefreshCliFactory { get; init; }

    /// <summary>
    /// How recent a Desktop snapshot has to be to stand in for a live reading. Thirty minutes, from
    /// watching a real cache: while Claude Desktop is open it rewrites this entry every five to
    /// fifteen minutes, with the occasional half-hour gap when it is left in the background.
    /// </summary>
    public TimeSpan? DesktopFreshness { get; init; }

    /// <summary>What a <see cref="Freshness.Live"/> fetch will accept instead: two minutes.</summary>
    public TimeSpan? LiveDesktopFreshness { get; init; }

    /// <summary>How long a Desktop miss suppresses the next scan.</summary>
    public TimeSpan? DesktopRescanInterval { get; init; }

    /// <summary>A subprocess is far more expensive than an HTTP call, so the CLI's last answer is reused in between.</summary>
    public TimeSpan? CliRefreshInterval { get; init; }

    /// <summary>Still an interval, not zero: a run of ticks inside one busy minute must not each spawn their own <c>claude</c>.</summary>
    public TimeSpan? LiveCliRefreshInterval { get; init; }

    /// <summary>The endpoint's own floor, which applies even when a caller explicitly asks for the source.</summary>
    public TimeSpan? MinEndpointInterval { get; init; }

    /// <summary>The shorter floor a <see cref="Freshness.Live"/> fetch is allowed.</summary>
    public TimeSpan? LiveMinEndpointInterval { get; init; }
}

/// <summary>
/// One Claude login's limits, read from whichever source can answer without interrupting anyone.
///
/// Three sources, in order. Claude Desktop's HTTP cache is read first, because it is the one that
/// costs nothing and cannot be refused: no subprocess, no keychain, no network. It answers only
/// while Desktop is running, and only for the account Desktop is signed into, so where it is silent
/// <c>claude "/usage"</c> is asked next. Where that fails or Claude Code is not installed, the usage
/// endpoint is called directly with the OAuth token from the keychain. Every source's failure is a
/// reason to ask the next one, never a reason to fail the refresh.
/// </summary>
public sealed class ClaudeReader : UsageReaderBase
{
    public const string ProviderId = "claude";

    /// <summary>Not a published API, so every failure path degrades to a status that is honest.</summary>
    public static readonly string EndpointUrl = "https://api.anthropic.com/api/oauth/usage";

    private static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(15);

    private readonly ClaudeReaderOptions _options;
    private readonly string _home;
    private readonly IClaudeFileSystem _fileSystem;
    private readonly IKeychain _keychain;
    private readonly ClaudeProfile _profile;
    private readonly ClaudeKeychain _claudeKeychain;
    private readonly ClaudeDesktopUsageCache _desktopCache;
    private readonly TimeProvider _time;
    /// <summary>Lazily made, so a reader that never refreshes a token never touches the filesystem.</summary>
    private readonly Lazy<string> _scratchDirectory;

    private readonly TimeSpan _desktopFreshness;
    private readonly TimeSpan _liveDesktopFreshness;
    private readonly TimeSpan _desktopRescanInterval;
    private readonly TimeSpan _cliRefreshInterval;
    private readonly TimeSpan _liveCliRefreshInterval;
    private readonly TimeSpan _minEndpointInterval;
    private readonly TimeSpan _liveMinEndpointInterval;

    private readonly ClaudeTokenRefresher _refresher;

    private IClaudeUsageCli? _cli;
    private bool _cliResolved;
    private int? _loginCount;

    /// <summary>Suppresses the next few cache walks. Set by a miss, cleared by a successful read.</summary>
    private DateTimeOffset? _lastDesktopMiss;

    private IReadOnlyList<LimitWindow>? _lastCliWindows;
    private DateTimeOffset? _lastCliWindowsAt;
    private string? _lastCliPlan;

    /// <summary>
    /// Stamped when the CLI fails with <c>needsAuth</c>. Without it, a Claude Code that is installed
    /// but signed out costs a process on every tick, forever. Transient failures do not set it, so
    /// a momentary blip can retry on the next tick.
    /// </summary>
    private DateTimeOffset? _lastCliFailure;

    private (ProviderSnapshot Snapshot, DateTimeOffset At, string Token)? _lastEndpoint;

    /// <summary>
    /// The held token, so the keychain is read once per token rather than once per minute.
    /// </summary>
    private ClaudeCredentials? _credentials;

    /// <summary>
    /// When the token runs out, as of the last keychain read — expired or not.
    ///
    /// Read-only bookkeeping for the token refresher, which has to know how long is left *before*
    /// deciding to do anything, and kept here because this is already the one place that reads the
    /// item. Held as ticks so the background renewal reads one atomic value rather than a nullable
    /// struct that a fetch may be writing at the same moment; -1 is "nothing read yet".
    /// </summary>
    private long _tokenExpiryTicks = -1;

    public ClaudeReader(ReaderContext context)
        : this(new ClaudeReaderOptions { Context = context })
    {
    }

    public ClaudeReader(ClaudeReaderOptions options)
        : base(
            (options ?? throw new ArgumentNullException(nameof(options))).Context.Config,
            options.Context.StateStore,
            options.Context.TimeProvider)
    {
        this._options = options;
        this._time = options.Context.TimeProvider;
        this._home = options.HomeDirectory ?? ClaudeProfile.HomeDirectory;
        this._fileSystem = options.FileSystem ?? new SystemClaudeFileSystem();
        this._keychain = options.Keychain ?? new MacKeychain();
        this._profile = options.Profile ?? ClaudeProfile.Default(this._home, this._fileSystem);
        this._claudeKeychain = new ClaudeKeychain(
            this._profile.KeychainServices,
            this._profile.Account,
            this._keychain,
            options.Context.Process,
            this._time);
        this._desktopCache = new ClaudeDesktopUsageCache(
            ClaudeDesktopUsageCache.DefaultDirectory(this._home),
            this._fileSystem);

        this._desktopFreshness = options.DesktopFreshness ?? TimeSpan.FromMinutes(30);
        this._liveDesktopFreshness = options.LiveDesktopFreshness ?? TimeSpan.FromMinutes(2);
        this._desktopRescanInterval = options.DesktopRescanInterval ?? TimeSpan.FromMinutes(5);
        this._cliRefreshInterval = options.CliRefreshInterval ?? TimeSpan.FromMinutes(5);
        this._liveCliRefreshInterval = options.LiveCliRefreshInterval ?? TimeSpan.FromSeconds(90);
        this._minEndpointInterval = options.MinEndpointInterval ?? TimeSpan.FromMinutes(5);
        this._liveMinEndpointInterval = options.LiveMinEndpointInterval ?? TimeSpan.FromMinutes(2);
        this._scratchDirectory = new Lazy<string>(() =>
            ClaudeUsageCli.ScratchDirectory(options.Context.DataDirectory, this._fileSystem));

        var refreshCli = new Lazy<string?>(options.RefreshCliFactory
            ?? (() => ClaudeStandaloneCli.Locate(this._home, this._fileSystem)));
        this._refresher = new ClaudeTokenRefresher(
            expiry: () => this.TokenExpiry,
            reload: this.ReloadTokenExpiryAsync,
            cli: () => refreshCli.Value,
            process: options.Context.Process,
            scratchDirectory: () => this._scratchDirectory.Value);
    }

    public override string Id => this._profile.Id;

    public override string DisplayName => this._profile.DisplayName;

    /// <summary>
    /// The hold-off covers the endpoint step alone.
    ///
    /// Codenotch checks <c>retryNoEarlierThan</c> inside <c>fetchFromKeychain</c>
    /// (<c>ClaudeOAuthProvider.swift:303-309</c>), *after* the Desktop cache and the CLI have had
    /// their chance: the deadline is the endpoint's, and a 429 on it is no reason to darken a ring a
    /// cheaper local source can still fill. See <see cref="ReadSnapshotAsync"/> for how the reader
    /// applies it.
    /// </summary>
    protected override HoldOffScope HoldOffScope => HoldOffScope.ReaderManaged;

    /// <summary>The profile this reader draws, exposed so the module can name it in a log line.</summary>
    public ClaudeProfile Profile => this._profile;

    /// <summary>Whether any window in a reading names a reset time that has already passed.</summary>
    public static bool HasExpiredWindow(IReadOnlyList<LimitWindow> windows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows.Any(window => window.ResetsAt is { } resetsAt && resetsAt <= now);
    }

    /// <summary>The three sources' freshness allowances for one fetch.</summary>
    public (TimeSpan Desktop, TimeSpan Cli, TimeSpan Endpoint) Allowances(Freshness freshness)
    {
        // The endpoint's floor applies even when a caller explicitly asks for the source — which in
        // Codenotch means `.live` and `.fromSource` share the shorter floor rather than the skipped
        // source bypassing it. `case .fromSource` sets the two local allowances to zero and leaves
        // the endpoint's alone.
        var endpoint = freshness == Freshness.Standard ? this._minEndpointInterval : this._liveMinEndpointInterval;
        return freshness switch
        {
            Freshness.Standard => (this._desktopFreshness, this._cliRefreshInterval, endpoint),
            Freshness.Live => (
                Min(this._liveDesktopFreshness, this._desktopFreshness),
                Min(this._liveCliRefreshInterval, this._cliRefreshInterval),
                endpoint),
            _ => (TimeSpan.Zero, TimeSpan.Zero, endpoint),
        };
    }

    protected override async Task<ProviderSnapshot> ReadAsync(Freshness freshness, CancellationToken cancellationToken)
    {
        try
        {
            return await this.ReadSnapshotAsync(freshness, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // After the reading, never before it. The renewal can take up to the process timeout
            // (30 s) and this fetch's own deadline is 60, so consulting the gate first — which is
            // where Codenotch's independent one-minute timer puts it — would let a slow `claude`
            // start decide whether a reading arrives at all.
            if (!cancellationToken.IsCancellationRequested)
            {
                this.ConsiderTokenRenewal(this._time.GetUtcNow());
            }
        }
    }

    private async Task<ProviderSnapshot> ReadSnapshotAsync(Freshness freshness, CancellationToken cancellationToken)
    {
        var now = this._time.GetUtcNow();
        var (desktopAllowance, cliAllowance, endpointAllowance) = this.Allowances(freshness);

        // Ahead of both the CLI and the token: this is the cheapest source and the only one that can
        // never interrupt anyone.
        var desktop = this.DesktopReading(now);

        // Whether this reading would ever be shown, at any freshness — which is a different question
        // from whether it may be shown *now*, and the two must not be conflated: conflating them
        // made a caller that asked to skip the cache count as a cache miss, which armed the rescan
        // throttle and stopped the cache being read at all for the next five minutes.
        var showable = desktop is not null
            && desktop.IsFresh(now, this._desktopFreshness)
            && !HasExpiredWindow(desktop.Windows, now);

        if (desktop is not null && showable && desktop.IsFresh(now, desktopAllowance))
        {
            // The cache carries no plan, so the reading would say nothing about whose it is — the
            // one thing worth knowing when two Claude rings sit side by side.
            return this.Snapshot(desktop.Windows, this._profile.OrganizationPlan());
        }

        // A reading that arrived but is too old, or describes a window that has already reset, is
        // still a miss for the purpose of rescanning: without this an installed-but-closed Desktop
        // re-walks the cache every poll.
        if (desktop is not null && !showable)
        {
            this._lastDesktopMiss = now;
        }

        // Only for the default login, and only while it is the sole one: `claude /usage` in print
        // mode gives one answer for the whole machine whatever CLAUDE_CONFIG_DIR says, so with two
        // logins it would paint both rings with the same figure.
        if (this._profile.Slug is null && this.LoginCount() <= 1)
        {
            if (await this.CliWindowsAsync(cliAllowance, now, cancellationToken).ConfigureAwait(false) is { } windows)
            {
                return this.Snapshot(windows, this._lastCliPlan);
            }
        }

        try
        {
            // The hold-off covers the endpoint step alone, and it is checked here — after the cache
            // and the CLI, exactly where Codenotch checks `retryNoEarlierThan`
            // (ClaudeOAuthProvider.swift:303-309). Nothing is marked: this read never asks the
            // endpoint, so the base reads the throw as the replay it is — no ladder rung, no
            // deadline write — and degrades it to Stale with the reading's own date. The existing
            // catch below can still answer with a reading that was only skipped for being asked for
            // too fresh, which is Codenotch's own fallback.
            if (this.IsHeldOff(now) && this.LoadHoldOffUntil() is { } deadline)
            {
                throw new ReaderException(ReaderError.RateLimited(deadline - now));
            }

            // About to ask the endpoint, so the base may judge the outcome as the endpoint's own: a
            // 429 takes its rung and rewrites the deadline, a success clears both.
            this.MarkRateLimitedSourceConsulted();

            return await this.FetchFromEndpointAsync(endpointAllowance, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (desktop is { } reading
            && showable
            && desktopAllowance < this._desktopFreshness
            && !cancellationToken.IsCancellationRequested)
        {
            // Skipping a reading we are holding is only worth it while some live source can answer
            // instead. Where none can — no Claude Code, no usable token, a 429 — that reading is
            // still the truth about the account, and asking for a fresher one must never leave the
            // ring emptier than not asking would have. Only when something was actually skipped: at
            // .standard the reading was already offered above, so there is nothing to reconsider.
            return this.Snapshot(reading.Windows, this._profile.OrganizationPlan());
        }
    }

    /// <summary>
    /// Asks the token refresher whether this is the moment, on its own task.
    ///
    /// Fire-and-forget on purpose: awaiting the renewal — before or after the reading — would let a
    /// slow `claude` start spend the fetch's deadline. The gate is what makes calling it on every
    /// tick safe: nothing to do unless the expiry is within four minutes, one attempt per expiry
    /// value ever, a ten-minute cooldown, and the refresher's own guard against overlap. It also
    /// only ever runs for the default profile, whose token is the one Claude Code renews.
    /// </summary>
    private void ConsiderTokenRenewal(DateTimeOffset now)
    {
        if (this._profile.Slug is not null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await this._refresher.ConsiderRenewingAsync(now, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // A renewal never fails a fetch and never surfaces as an unobserved task exception:
                // the next tick simply consults the same gate again.
            }
        });
    }

    /// <summary>What the refresher reads: the expiry from the last keychain read, or nil.</summary>
    private DateTimeOffset? TokenExpiry
    {
        get
        {
            var ticks = Interlocked.Read(ref this._tokenExpiryTicks);
            return ticks < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ticks);
        }
    }

    /// <summary>One atomic store, because the renewal reads this from its own thread.</summary>
    private void SetTokenExpiry(DateTimeOffset expiry) =>
        Interlocked.Exchange(ref this._tokenExpiryTicks, expiry.ToUnixTimeMilliseconds());

    /// <summary>The CLI, resolved once — the answer only changes when someone installs or removes Claude Code.</summary>
    private IClaudeUsageCli? Cli()
    {
        if (!this._cliResolved)
        {
            this._cliResolved = true;
            this._cli = this._options.CliFactory?.Invoke()
                ?? (ClaudeUsageCli.Locate(this._home, this._fileSystem) is { } binary
                    ? new ClaudeUsageCli(
                        binary, this._options.Context.DataDirectory, this._options.Context.Process, this._fileSystem)
                    : null);
        }

        return this._cli;
    }

    /// <summary>
    /// How many Claude Code logins this machine has. Discovery is ported for its own sake (and
    /// tested); only the default profile is drawn in this PR, so this answer is used for one thing:
    /// whether the CLI's machine-wide estimate may stand in for this ring.
    /// </summary>
    private int LoginCount()
    {
        if (this._loginCount is { } cached)
        {
            return cached;
        }

        var profiles = ClaudeProfile.Discover(this._home, this._fileSystem, this._keychain, this._profile.Account);
        this._loginCount = profiles.Count;
        if (profiles.Count > 1)
        {
            // The other profiles are found and named, not drawn: multi-account keys need the module
            // system to register more than one class per provider, which is a later step.
            ClaudeLog.Note(
                $"claude: {profiles.Count - 1} extra profile(s) found, not drawn in this build: "
                + string.Join(", ", profiles.Skip(1).Select(profile => profile.Id)));
        }

        return profiles.Count;
    }

    /// <summary>What Claude Desktop's cache holds for this profile's account, or nil.</summary>
    private ClaudeDesktopUsageCache.Reading? DesktopReading(DateTimeOffset now)
    {
        // A recent miss means the next read would be a full scan for something that was not there a
        // moment ago. Wait it out.
        if (this._lastDesktopMiss is { } miss && now - miss < this._desktopRescanInterval)
        {
            return null;
        }

        // Read per refresh rather than held: switching account in Claude Code rewrites this, and a
        // held copy would keep matching the old organization's cache entry.
        if (this._profile.OrganizationId() is not { } organization)
        {
            this._lastDesktopMiss = now;
            return null;
        }

        var reading = this._desktopCache.Read(organization, now);
        if (reading is null)
        {
            this._lastDesktopMiss = now;
            return null;
        }

        this._lastDesktopMiss = null;
        return reading;
    }

    /// <summary>What <c>claude "/usage"</c> last said, or nil to mean "use the token path".</summary>
    private async Task<IReadOnlyList<LimitWindow>?> CliWindowsAsync(
        TimeSpan reuseInterval,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (this.Cli() is not { } cli)
        {
            return null;
        }

        // A cached answer is only reused inside the interval, and a reading whose window has already
        // rolled over is stale whatever its age: it describes a period that is over. Reusing one is
        // what made a reset show up minutes after it happened.
        if (this._lastCliWindows is { } last
            && this._lastCliWindowsAt is { } at
            && now - at < reuseInterval
            && !HasExpiredWindow(last, now))
        {
            return last;
        }

        // The lock-out after a signed-out CLI keeps its full interval whatever the caller asked for.
        if (this._lastCliFailure is { } failure && now - failure < this._cliRefreshInterval)
        {
            return null;
        }

        try
        {
            var reading = await cli.ReadWithPlanAsync(this._profile, now, cancellationToken).ConfigureAwait(false);
            this._lastCliWindows = reading.Windows;
            this._lastCliWindowsAt = now;
            this._lastCliPlan = reading.Plan;
            this._lastCliFailure = null;
            return reading.Windows;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ReaderException exception)
        {
            // Every way the CLI can fail — not installed, signed out, wording changed, wedged and
            // killed — is a reason to ask the endpoint instead, not a reason to fail the refresh.
            if (exception.Error.Kind == ReaderErrorKind.NeedsAuth)
            {
                this._lastCliFailure = now;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The endpoint, with the reuse rule and the held-credential bookkeeping around it.</summary>
    private async Task<ProviderSnapshot> FetchFromEndpointAsync(TimeSpan reuseInterval, CancellationToken cancellationToken)
    {
        var token = await this.CurrentTokenAsync(cancellationToken).ConfigureAwait(false);
        var now = this._time.GetUtcNow();

        if (this._lastEndpoint is { } last
            && last.Token == token
            && now - last.At < reuseInterval
            && !HasExpiredWindow(last.Snapshot.Windows, now))
        {
            return last.Snapshot;
        }

        try
        {
            var (snapshot, usedToken) = await this.EndpointFetchAsync(true, cancellationToken).ConfigureAwait(false);

            // The token that *answered*, not the one this method started with: a 401 re-reads the
            // item and may come back with a rotated token, and recording the old one would make the
            // next tick inside the floor spend a request to learn nothing.
            this._lastEndpoint = (snapshot, this._time.GetUtcNow(), usedToken);
            return snapshot;
        }
        catch (ReaderException exception)
            when (exception.Error.Kind is ReaderErrorKind.NeedsAuth or ReaderErrorKind.CredentialExpired)
        {
            // The held copy goes, so the next tick re-reads. Backing off is the credential cache's
            // job and it already does it correctly: it waits on the item's modification date rather
            // than on a clock, so a token Claude Code has just rotated is picked up at once.
            this._credentials = null;
            this._lastEndpoint = null;
            throw;
        }
    }

    private async Task<(ProviderSnapshot Snapshot, string Token)> EndpointFetchAsync(
        bool retryingOnUnauthorized,
        CancellationToken cancellationToken)
    {
        var token = await this.CurrentTokenAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(EndpointUrl));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        using var response = await this._options.Context.Http
            .SendAsync(request, EndpointTimeout, cancellationToken)
            .ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (status is 401 or 403)
        {
            // Rejected but unexpired: the held copy is wrong, which is what signing into a different
            // account looks like from here.
            this._claudeKeychain.ForgetCached();
            this._credentials = null;
            if (retryingOnUnauthorized)
            {
                // The retry's own token is the one that answered.
                return await this.EndpointFetchAsync(false, cancellationToken).ConfigureAwait(false);
            }

            throw new ReaderException(ReaderError.NeedsAuth);
        }

        if (status == 429)
        {
            throw new ReaderException(ReaderError.RateLimited(RetryAfter(response)));
        }

        if (status is < 200 or >= 300)
        {
            throw new ReaderException(ReaderError.BadResponse(status));
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            throw new ReaderException(ReaderError.BadResponse(status), exception);
        }

        ClaudeUsageResponse payload;
        try
        {
            payload = ClaudeUsageResponse.Decode(body);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new ReaderException(ReaderError.ApiError("usage response did not decode"), exception);
        }

        var windows = payload.LimitWindows(this._time.GetUtcNow());

        // Answered, and signed in, but no limit in it: some Enterprise and team accounts come back
        // this way. An empty reading drew nothing and left "Waiting for the first reading…" up for
        // good; say what happened instead.
        if (windows.Count == 0)
        {
            throw new ReaderException(ReaderError.NothingMetered(
                "Claude answered, but listed no usage limits for this account. "
                + "Some Enterprise and team plans don't report them."));
        }

        return (this.Snapshot(windows, this._credentials?.SubscriptionType), token);
    }

    /// <summary>
    /// The token, from the held copy when it is still good and from the keychain when it is not.
    ///
    /// Expired is not signed out: Claude Code rotates this token whenever it runs, and this app
    /// deliberately does not — minting one would mean writing a credential it does not own, and
    /// racing the owner for it. So after a machine restart the token is usually stale until Claude
    /// Code is next used, and the honest thing is to keep showing the last reading with its age
    /// rather than demand a sign-in that is not needed.
    /// </summary>
    private async Task<string> CurrentTokenAsync(CancellationToken cancellationToken)
    {
        var now = this._time.GetUtcNow();
        if (this._credentials is { } held && !held.IsExpired(now))
        {
            return held.AccessToken;
        }

        var fresh = await this._claudeKeychain.LoadAsync(cancellationToken).ConfigureAwait(false);
        this.SetTokenExpiry(fresh.ExpiresAt);
        if (fresh.IsExpired(now))
        {
            throw new ReaderException(ReaderError.CredentialExpired);
        }

        this._credentials = fresh;
        return fresh.AccessToken;
    }

    /// <summary>Read the keychain again, ignoring anything held, and report the expiry.</summary>
    private async Task<DateTimeOffset?> ReloadTokenExpiryAsync(CancellationToken cancellationToken)
    {
        this._claudeKeychain.ForgetCached();
        this._credentials = null;
        try
        {
            var fresh = await this._claudeKeychain.LoadAsync(cancellationToken).ConfigureAwait(false);
            this.SetTokenExpiry(fresh.ExpiresAt);
            return fresh.ExpiresAt;
        }
        catch (ReaderException)
        {
            return null;
        }
    }

    /// <summary>The snapshot shape every source produces — one place, so the three cannot drift.</summary>
    private ProviderSnapshot Snapshot(IReadOnlyList<LimitWindow> windows, string? plan) => new()
    {
        Id = this.Id,
        DisplayName = this.DisplayName,
        Fidelity = Fidelity.Official,
        Status = ProviderStatus.Ok,
        Windows = windows,
        HeadlineId = ClaudeUsageResponse.HeadlineId(windows),

        // The provider declares its weekly window by name, so a second ring draws the same figure
        // the compare card is talking about rather than whichever window happens to be second.
        WeeklyId = ClaudeUsageResponse.WeeklyAllId,
        Plan = ClaudeUsageResponse.PlanName(plan),
        AccountLabel = this._profile.SignedInAddress(),
    };

    /// <summary>`Retry-After` is either a number of seconds or an HTTP date; core reads both.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Retry-After", out var values)
            ? Backoff.RetryAfter(values.FirstOrDefault(), this._time.GetUtcNow())
            : null;

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
