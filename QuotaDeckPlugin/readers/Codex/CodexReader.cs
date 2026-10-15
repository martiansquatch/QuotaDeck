namespace QuotaDeck.Readers.Codex;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;

/// <summary>
/// Reads live account limits using the session owned and refreshed by Codex.
///
/// Port of Codenotch <c>Sources/Providers/CodexLocalProvider.swift</c> —
/// <c>fetchSnapshot()</c> (lines 40-104), <c>fetchResetCredits(session:credential:)</c>
/// (lines 134-158) and <c>retryAfter(from:now:)</c> (lines 161-173). The per-file lookups are the
/// ports of <c>Sources/Providers/CodexCredentials.swift</c> and <c>CodexUsage.swift</c> beside this
/// file; <c>fetchProfileUsage</c> (<c>/wham/profiles/me</c>, the token chart) is deliberately not
/// read: it has no face on the keypad.
///
/// Every file read goes through the injectable home directory and every clock read through
/// <see cref="ReaderContext.TimeProvider"/>; all HTTP goes through <see cref="ReaderContext.Http"/>.
/// This app never refreshes or writes the token.
/// </summary>
public sealed class CodexReader : UsageReaderBase
{
    public const string CodexId = "codex";

    /// <summary>The server's retry hint is honoured only as a floor: it can ask for less, never for the "retry now" that keeps you throttled (CodexLocalProvider.swift:65).</summary>
    public const double RateLimitFloorSeconds = 60;

    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    /// <summary>Every request gets the same 15 s budget (CodexLocalProvider.swift:51).</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ReaderContext _context;
    private readonly CodexProfile _profile;

    /// <param name="context">Services, config and the clock, handed in rather than reached for.</param>
    /// <param name="homeDirectory">
    /// Where <c>.codex</c> lives; the real home is computed at runtime. Injectable so tests use a
    /// temp directory.
    /// </param>
    public CodexReader(ReaderContext context, string? homeDirectory = null)
        : base(context.Config, context.StateStore, context.TimeProvider)
    {
        ArgumentNullException.ThrowIfNull(context);

        this._context = context;
        this._profile = CodexProfile.Default(homeDirectory ?? CodexProfile.HomeDirectory);
    }

    public override string Id => CodexId;

    public override string DisplayName => "Codex";

    /// <summary>
    /// Whether the extra Spark / code-review windows are read.
    ///
    /// Port of <c>Preferences.storedShowCodexExtraLimits()</c>
    /// (<c>Sources/Settings/Preferences.swift:699-703</c>): absent means on. A reader option rather
    /// than a settings field because core's <c>ReaderConfig</c> has no per-provider options bag;
    /// the gap is listed in the PR.
    /// </summary>
    public bool ShowExtraLimits { get; init; } = true;

    protected override async Task<ProviderSnapshot> ReadAsync(
        Freshness freshness,
        CancellationToken cancellationToken)
    {
        var now = this._context.TimeProvider.GetUtcNow();

        // Codex can rotate its token between polls; this app never refreshes or writes it
        // (CodexLocalProvider.swift:46-47).
        var credential = CodexCredentials.Load(this._profile.AuthPath, now);

        using var usageRequest = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        AddSharedHeaders(usageRequest, credential);

        using var usageResponse = await this._context.Http
            .SendAsync(usageRequest, RequestTimeout, cancellationToken)
            .ConfigureAwait(false);

        var status = (int)usageResponse.StatusCode;
        if (status is 401 or 403)
        {
            throw new ReaderException(ReaderError.NeedsAuth);
        }

        if (status == 429)
        {
            var delay = TimeSpan.FromSeconds(
                Math.Max(RateLimitFloorSeconds, this.RetryAfterSeconds(usageResponse, now) ?? 0));
            throw new ReaderException(ReaderError.RateLimited(delay));
        }

        if (status is < 200 or >= 300)
        {
            throw new ReaderException(ReaderError.BadResponse(status));
        }

        var body = await usageResponse.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        // Unused resets are a separate endpoint from the extra Spark / code-review windows. Start
        // that fetch before parsing extras so a slow or empty extras payload cannot skip the
        // credits row (CodexLocalProvider.swift:74-77).
        var resetCredits = this.FetchResetCreditsAsync(credential, cancellationToken);

        var windows = CodexUsage.Windows(body, now, this.ShowExtraLimits);
        var account = CodexCredentials.Account(this._profile.AuthPath);
        var plan = CodexUsage.Plan(body) ?? NonEmpty(account?.Plan);

        // core's ProviderSnapshot has no reset-credits field, so the parsed value is dropped here;
        // the fetch still runs concurrently, exactly like Codenotch's, so the read path — and its
        // failures being ignored — stays the same. See the PR for the gap.
        _ = await resetCredits.ConfigureAwait(false);

        return new ProviderSnapshot
        {
            Id = this.Id,
            DisplayName = this.DisplayName,
            Fidelity = Fidelity.Official,
            Status = ProviderStatus.Ok,
            Windows = windows,

            // Named, not positional (CodexLocalProvider.swift:97-99).
            HeadlineId = "primary",
            WeeklyId = "secondary",
            Plan = plan,

            // The email claim is personal data: it is used only as this label, never logged.
            AccountLabel = account?.Label,
        };
    }

    /// <summary>
    /// Unused rate-limit resets on this Codex account, listed by the same backend as usage. A
    /// failure of any kind reads as nil — an unfamiliar payload is not a reason to fail the usage
    /// fetch (CodexLocalProvider.swift:134-158).
    /// </summary>
    private async Task<CodexResetCredits?> FetchResetCreditsAsync(
        CodexCredential credential,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ResetCreditsUrl);
            AddSharedHeaders(request, credential);
            request.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");

            using var response = await this._context.Http
                .SendAsync(request, RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or >= 300)
            {
                return null;
            }

            var body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            return CodexUsage.ResetCredits(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller left; that is not the endpoint saying anything, and the reader base is
            // the one place cancellation is interpreted.
            throw;
        }
        catch (Exception)
        {
            // "Failures ignored" is deliberate: this fetch is additional detail, not the reading.
            return null;
        }
    }

    /// <summary>
    /// The server's own retry hint, in seconds, from a <c>Retry-After</c> header that is either a
    /// number of seconds or an HTTP date (CodexLocalProvider.swift:161-173, via
    /// <see cref="Backoff.RetryAfter"/>).
    /// </summary>
    private double? RetryAfterSeconds(System.Net.Http.HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.TryGetValues("Retry-After", out var values)
            ? values.FirstOrDefault()
            : null;
        return Backoff.RetryAfter(header, now)?.TotalSeconds;
    }

    private static void AddSharedHeaders(HttpRequestMessage request, CodexCredential credential)
    {
        // The account id is sent only as this header — never displayed or logged.
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential.AccessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache, no-store");
    }

    private static string? NonEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
