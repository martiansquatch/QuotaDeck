namespace QuotaDeck.Readers.Antigravity;

using System.Text.Json;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Core.Settings;

/// <summary>
/// Antigravity's reader: three sources in order, and a count when none of them answers.
///
/// Port of Codenotch <c>Sources/Providers/AntigravityProvider.swift</c> (<c>fetchSnapshot()</c>,
/// lines 100-174; headline and weekly selection, lines 176-229; the direct call, lines 300-314).
/// The local language server is asked first because it already holds the credential and the client
/// identity Google insists on, and answers with the same figure Antigravity's own panel shows; the
/// direct Cloud Code call comes second, the OMP history third, and the request count last — as a
/// plain number, never a percentage, because there is no published limit to divide by.
///
/// Once the server has answered at least once, its going away is Antigravity being closed, not an
/// account that cannot be read: the reader throws <see cref="ReaderErrorKind.CredentialExpired"/>,
/// which the base ages into a stale reading instead of replacing a percentage with a count.
/// </summary>
public sealed class AntigravityReader : UsageReaderBase, IDisposable
{
    public const string BridgedFlagFileName = "antigravity-bridged";

    private readonly AntigravityOptions _options;
    private readonly AntigravityCredentialStore _credentials;
    private readonly AntigravityServer _server;
    private readonly IHttpTransport _loopback;

    private AntigravityBridge.Endpoint? _bridge;
    private bool _everBridged;

    public AntigravityReader(
        AntigravityOptions options,
        ReaderConfig config,
        IReaderStateStore stateStore,
        TimeProvider timeProvider)
        : base(config, stateStore, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);

        this._options = options;
        this._credentials = new AntigravityCredentialStore(options);
        this._server = new AntigravityServer(options);
        this._loopback = options.Loopback ?? new LoopbackHttpTransport();
        this._everBridged = this.ReadBridgedFlag();
    }

    public override string Id => this._options.Profile.Id;

    public override string DisplayName => this._options.Profile.DisplayName;

    /// <summary>Which source answered the last fetch: bridge, cloud, omp or requests. Diagnostics.</summary>
    public string? LastSource { get; private set; }

    /// <summary>The pid of a language server this reader started, for the live check.</summary>
    public int? OwnedServerPid => this._server.Pid;

    public void Dispose() => this._server.Stop();

    protected override async Task<ProviderSnapshot> ReadAsync(Freshness freshness, CancellationToken cancellationToken)
    {
        var now = this._options.TimeProvider.GetUtcNow();

        if (this._options.Profile.Slug is null)
        {
            var local = await this.LocalQuotaAsync(cancellationToken).ConfigureAwait(false);
            if (local is { Count: > 0 })
            {
                this.SetBridged();
                this.LastSource = "bridge";
                return this.Snapshot(local, Fidelity.Official);
            }

            // The override is a test seam: once the real server has answered in this instance, a
            // silent override means it went away, exactly like the production path below.
            if (this._options.LocalQuotaOverride is not null && this._everBridged)
            {
                throw new ReaderException(ReaderError.CredentialExpired);
            }
        }

        // A failed credential read is swallowed: the request count is a better answer than an auth
        // error for an account Google will not read.
        AntigravityCredentials? credentials = null;
        try
        {
            credentials = await this._credentials.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            credentials = null;
        }

        if (credentials is not null)
        {
            var cloud = await this.CloudQuotaAsync(credentials, now, cancellationToken).ConfigureAwait(false);
            if (cloud is { Count: > 0 })
            {
                this.LastSource = "cloud";
                return this.Snapshot(cloud, Fidelity.Official);
            }
        }

        if (this._options.Profile.Slug is null)
        {
            var omp = AntigravityOmp.UsageWindows(this._options.HomeDirectory, credentials?.Email);
            if (omp.Count > 0)
            {
                this.LastSource = "omp";
                return this.Snapshot(omp, Fidelity.Official);
            }
        }

        if (this._everBridged)
        {
            throw new ReaderException(ReaderError.CredentialExpired);
        }

        // The only number left, reported as a *count* with no usedFraction: the cell prints the
        // number and the ring draws its track with no arc, because there is no limit.
        var roots = this._options.Profile.Slug is null
            ? AntigravityActivity.TranscriptRoots(this._options.HomeDirectory)
            : [this._options.Profile.BrainDirectory];
        var activity = AntigravityActivity.Read(roots, now);
        this.LastSource = "requests";

        return new ProviderSnapshot
        {
            Id = this.Id,
            DisplayName = this.DisplayName,
            Fidelity = Fidelity.Derived,
            Status = ProviderStatus.Ok,
            Windows =
            [
                new LimitWindow
                {
                    Id = "requests",
                    Label = activity.Label(now),
                    Used = activity.RequestsToday,
                },
            ],
            HeadlineId = "requests",
        };
    }

    private async Task<IReadOnlyList<LimitWindow>?> LocalQuotaAsync(CancellationToken cancellationToken)
    {
        if (this._options.LocalQuotaOverride is { } localOverride)
        {
            return await localOverride(cancellationToken).ConfigureAwait(false);
        }

        if (this._bridge is { } cached)
        {
            var windows = await AntigravityBridge
                .QuotaAsync(cached, this._loopback, this._options.TimeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            if (windows is { Count: > 0 })
            {
                return windows;
            }

            // Quitting the IDE invalidates a cached port; rediscover after a failure.
            this._bridge = null;
        }

        var fresh = await this.ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        if (fresh is null)
        {
            return null;
        }

        this._bridge = fresh;
        return await AntigravityBridge
            .QuotaAsync(fresh, this._loopback, this._options.TimeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Where to ask: the IDE's own server while it runs, otherwise one we start. A started server
    /// carries the same command line, so later polls find it through discovery like any other.
    /// </summary>
    private async Task<AntigravityBridge.Endpoint?> ResolveEndpointAsync(CancellationToken cancellationToken)
    {
        var discovered = await AntigravityBridge.DiscoverAsync(this._options.Process, cancellationToken).ConfigureAwait(false);
        return discovered ?? await this._server.EndpointOrStartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<LimitWindow>?> CloudQuotaAsync(
        AntigravityCredentials credentials,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credentials.AccessToken}");
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "antigravity/hub/2.8.0 (aidev_client; os_type=darwin; arch=arm64; cl=963137146)");
            request.Headers.TryAddWithoutValidation(
                "Client-Metadata",
                "ideType=IDE_UNSPECIFIED,platform=PLATFORM_UNSPECIFIED,pluginType=GEMINI");
            request.Content = new StringContent(
                CloudRequestBody(credentials.ProjectId),
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await this._options.Http
                .SendAsync(request, TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return AntigravityQuotaParser.Parse(body, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private ProviderSnapshot Snapshot(IReadOnlyList<LimitWindow> windows, Fidelity fidelity) => new()
    {
        Id = this.Id,
        DisplayName = this.DisplayName,
        Fidelity = fidelity,
        Status = ProviderStatus.Ok,
        Windows = windows,
        HeadlineId = ResolveHeadlineId(windows, this._options.HeadlineModel, this._options.HeadlineLimit),
        WeeklyId = ResolveWeeklyId(windows, this._options.HeadlineModel),
    };

    /// <summary>
    /// The direct call's JSON body: <c>{"project":"…"}</c> when the credential names one, <c>{}</c>
    /// otherwise — Swift's exact shape. Built with <see cref="JsonSerializer"/> rather than
    /// interpolation because a project id is another app's string and may contain quotes,
    /// backslashes or anything else JSON must escape.
    /// </summary>
    public static string CloudRequestBody(string? projectId) =>
        projectId is { Length: > 0 }
            ? JsonSerializer.Serialize(new Dictionary<string, string> { ["project"] = projectId })
            : "{}";

    /// <summary>
    /// Which window leads the face. Port of <c>resolveHeadlineID(for:)</c>: candidates are the
    /// preferred model's windows (all of them when none match); with a limit preference the most
    /// used matching window wins, otherwise the most used under 100% — an exhausted lane does not
    /// displace a usable one unless every lane is exhausted — with ties going to the smaller id.
    /// </summary>
    public static string ResolveHeadlineId(
        IReadOnlyList<LimitWindow> windows,
        string preferredModel = "gemini",
        string preferredLimit = "automatic")
    {
        ArgumentNullException.ThrowIfNull(windows);

        var modelCandidates = windows.Where(window => window.Id.StartsWith(preferredModel, StringComparison.Ordinal)).ToList();
        var candidates = modelCandidates.Count > 0 ? modelCandidates : [.. windows];

        if (!string.Equals(preferredLimit, "automatic", StringComparison.OrdinalIgnoreCase))
        {
            var limitCandidates = candidates.Where(window => Matches(window, preferredLimit)).ToList();
            if (limitCandidates.Count > 0)
            {
                return MostConstrained(limitCandidates)!.Id;
            }
        }

        var fractional = candidates.Where(window => window.UsedFraction is not null).ToList();
        var usable = fractional.Where(window => window.UsedFraction < 1).ToList();
        var pool = usable.Count > 0 ? usable : fractional;
        return MostConstrained(pool)?.Id
            ?? candidates.FirstOrDefault()?.Id
            ?? $"{preferredModel}-hourly";
    }

    /// <summary>
    /// The window the second ring draws. Port of <c>resolveWeeklyID(for:)</c>: a seven-day
    /// duration, an <c>-weekly</c> id or a label naming the week; most used wins.
    /// </summary>
    public static string? ResolveWeeklyId(IReadOnlyList<LimitWindow> windows, string preferredModel = "gemini")
    {
        ArgumentNullException.ThrowIfNull(windows);

        var modelCandidates = windows.Where(window => window.Id.StartsWith(preferredModel, StringComparison.Ordinal)).ToList();
        var candidates = modelCandidates.Count > 0 ? modelCandidates : windows;

        var weekly = candidates.Where(window =>
            window.Duration == TimeSpan.FromDays(7)
            || window.Id.EndsWith("-weekly", StringComparison.OrdinalIgnoreCase)
            || window.Label.Contains("weekly", StringComparison.OrdinalIgnoreCase)).ToList();

        return MostConstrained(weekly)?.Id;
    }

    private static bool Matches(LimitWindow window, string cadence)
    {
        var value = $"{window.Id} {window.Label}".ToLowerInvariant();
        return cadence.ToLowerInvariant() switch
        {
            "weekly" => window.Duration == TimeSpan.FromDays(7) || value.Contains("weekly", StringComparison.Ordinal),
            _ => window.Duration == TimeSpan.FromHours(5)
                 || new[] { "5h", "5-hour", "five hour", "five-hour", "hourly", "session" }
                     .Any(token => value.Contains(token, StringComparison.Ordinal)),
        };
    }

    private static LimitWindow? MostConstrained(IReadOnlyList<LimitWindow> windows) =>
        windows
            .OrderByDescending(window => window.UsedFraction ?? 0)
            .ThenBy(window => window.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    private bool ReadBridgedFlag()
    {
        if (this._options.Profile.Slug is not null)
        {
            return false;
        }

        var flag = Path.Combine(this._options.DataDirectory, BridgedFlagFileName);
        bool present;
        try
        {
            present = File.Exists(flag);
        }
        catch
        {
            return false;
        }

        if (!present)
        {
            return false;
        }

        // Codenotch's `everBridged` dies with the launch; the persisted flag stands in for it
        // across restarts, but only while something that could have bridged is still installed.
        // With neither the IDE's language-server binary nor `agy` on the machine the flag describes
        // a source that cannot come back, so it is deleted and the request count — not a stale
        // percentage — is the honest answer.
        if (AntigravityBridge.ServerBinary(this._options.ApplicationDirectories) is null
            && !AgyOnPath(this._options.EffectivePath))
        {
            try
            {
                File.Delete(flag);
            }
            catch
            {
                // An undeletable flag is ignored this launch and re-checked the next.
            }

            return false;
        }

        return true;
    }

    /// <summary>`agy` on PATH, the way the module's presence probe looks for it.</summary>
    private static bool AgyOnPath(string? path)
    {
        if (path is null)
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, "agy")))
                {
                    return true;
                }
            }
            catch
            {
                // An unreadable PATH entry is simply not one.
            }
        }

        return false;
    }

    private void SetBridged()
    {
        if (this._everBridged)
        {
            return;
        }

        this._everBridged = true;
        try
        {
            File.WriteAllText(Path.Combine(this._options.DataDirectory, BridgedFlagFileName), "1");
        }
        catch
        {
            // The flag is an optimisation for the next launch; losing it costs one extra count.
        }
    }
}
