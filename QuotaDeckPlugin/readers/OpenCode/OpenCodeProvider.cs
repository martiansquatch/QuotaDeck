namespace QuotaDeck.Readers.OpenCode;

using System.Net.Http;
using System.Text;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// Talks to the OpenCode Go usage endpoints with the credential OpenCode itself stores on sign-in.
///
/// Port of Codenotch <c>Sources/Providers/OpenCodeProvider.swift</c>: <c>endpoint(for:)</c> lines
/// 110-112, <c>fetch(_:)</c> lines 114-166, <c>signedIn(_:)</c> lines 175-201. Which credential the
/// account holds decides the endpoint, because each route refuses the other's: the Go API key is for
/// <c>zen/go</c>, while an OAuth sign-in is only accepted by the <c>inference/</c> route OpenCode
/// itself uses. Both answer 401 to the wrong one, and that 401 is also what a valid account with no
/// Go plan returns — so an OAuth 401 is checked against the console before it is called a sign-out.
///
/// The Swift provider also owns the rate-limit bookkeeping (<c>backoff(forAttempt:retryAfter:)</c>
/// lines 206-211 and the <c>consecutiveRateLimits</c> counter). In QuotaDeck the base owns it —
/// <see cref="UsageReaderBase"/> applies <see cref="Backoff.Compute"/> to whatever hint the error
/// carries and persists the deadline — so this class reports only the raw <c>Retry-After</c> and the
/// base computes the 60 s doubling. The one Swift behaviour that moves with it is the console probe
/// order: it runs before the token's own expiry hint, so an expired sign-in whose console still
/// answers 200 is "no Go plan", not "credential expired".
/// </summary>
public static class OpenCodeProvider
{
    private const double TimeoutSeconds = 15;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The route this credential is for. Not a preference: each refuses the other's credential, and
    /// refuses it with the same 401 a planless account gets — so the wrong pairing is invisible from
    /// the response alone.
    /// </summary>
    public static Uri Endpoint(OpenCodeCredentials.Credential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new Uri(credential.OAuth ? OpenCodeUsage.OAuthEndpoint : OpenCodeUsage.Endpoint);
    }

    /// <summary>
    /// The usage body as text, or the <see cref="ReaderException"/> the status means. A network
    /// error is not translated here: it surfaces as whatever <see cref="IHttpTransport"/> throws and
    /// the reader base maps it.
    /// </summary>
    public static async Task<string> FetchUsageAsync(
        IHttpTransport http,
        TimeProvider time,
        OpenCodeCredentials.Credential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(credential);

        using var request = Request(Endpoint(credential), credential);
        using var response = await http
            .SendAsync(request, TimeSpan.FromSeconds(TimeoutSeconds), cancellationToken)
            .ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (status == 401)
        {
            // Upstream serves a missing Go plan as 401 through the same branch as a bad credential
            // — its join finds no plan row either way. Only the console can tell the two apart, and
            // only for an OAuth sign-in: the Go key has no equivalent probe, so its 401 stays a
            // sign-out, as before.
            if (credential.OAuth && await SignedInAsync(http, credential, cancellationToken).ConfigureAwait(false))
            {
                throw new ReaderException(ReaderError.NothingMetered("No OpenCode Go subscription on this account"));
            }

            // The token's own expiry is only a hint, but it is the difference between "sign in
            // again" and "open OpenCode, it will renew this" — and the last reading is still true in
            // the second case.
            if (credential.Expires is { } expires && expires <= time.GetUtcNow())
            {
                throw new ReaderException(ReaderError.CredentialExpired);
            }

            throw new ReaderException(ReaderError.NeedsAuth);
        }

        // A valid key that is not entitled to Go: readable, but metering nothing — not an error, and
        // it must not be shown as one.
        if (status == 403)
        {
            throw new ReaderException(ReaderError.NothingMetered("No OpenCode Go subscription on this key"));
        }

        if (status == 429)
        {
            // The raw hint only; the base turns it into the 60 s-doubling schedule and persists it.
            throw new ReaderException(ReaderError.RateLimited(Backoff.RetryAfter(RetryAfterHeader(response), time.GetUtcNow())));
        }

        if (status is < 200 or >= 300)
        {
            throw new ReaderException(ReaderError.BadResponse(status));
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Swift String(data:encoding:.utf8) refuses the bytes; the same badResponse(0).
            throw new ReaderException(ReaderError.BadResponse(0));
        }
    }

    /// <summary>
    /// Is this OAuth sign-in still good? Asked only after the usage endpoint's 401, which cannot
    /// tell a bad token from an account without a Go plan.
    ///
    /// The console's <c>/api/user</c> answers 401 only to a bad token, so a 200 here means the
    /// sign-in is fine and the account simply has no Go plan. The Zen model list cannot be used for
    /// this: it answers 200 to anything, including a made-up token.
    /// </summary>
    private static async Task<bool> SignedInAsync(
        IHttpTransport http,
        OpenCodeCredentials.Credential credential,
        CancellationToken cancellationToken)
    {
        var console = credential.Console ?? new Uri(OpenCodeUsage.DefaultConsole);

        // appendingPathComponent then query = nil in Swift: the console's path plus /api/user, with
        // any query or fragment dropped.
        var basePath = console.GetLeftPart(UriPartial.Path).TrimEnd('/');
        using var request = Request(new Uri(basePath + "/api/user"), credential);

        try
        {
            using var response = await http
                .SendAsync(request, TimeSpan.FromSeconds(TimeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
            return response.StatusCode == System.Net.HttpStatusCode.OK;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller left; this is not a verdict about the credential.
            throw;
        }
        catch
        {
            // A probe that cannot be made is not a verdict. Claiming "signed out" here would replace
            // a planless account with a sign-in prompt, which is the worse of the two mistakes.
            return false;
        }
    }

    private static HttpRequestMessage Request(Uri url, OpenCodeCredentials.Credential credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential.Token}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        // Only an OAuth sign-in is issued against an org, and only the `inference/` routes want it.
        if (credential.Org is { } org)
        {
            request.Headers.TryAddWithoutValidation("x-opencode-org-id", org);
        }

        return request;
    }

    private static string? RetryAfterHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
}
