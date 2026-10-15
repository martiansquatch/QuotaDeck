namespace QuotaDeck.Readers.Shared;

using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// The one HTTP transport every reader shares.
///
/// A single shared client for the default constructor: a per-reader client would mean a
/// per-reader connection pool, and there is no credential or proxy here that belongs to one
/// provider. The client is deliberately plain — no automatic redirects (a probe must see the real
/// answer), no cookies, no default headers — and its own <see cref="HttpClient.Timeout"/> is
/// disabled because the per-call budget is the only timer. A budget that runs out becomes
/// <see cref="ReaderError.TimedOut"/> rather than an <see cref="OperationCanceledException"/>: the
/// reader base reads cancellation as a caller walking away, and a slow endpoint is not that.
///
/// The per-call budget covers the whole exchange, headers *and* body: the call is made with
/// <see cref="HttpCompletionOption.ResponseContentRead"/>, so a body that stalls past the budget
/// times out here rather than hanging a reader that has already been handed the response. Swift's
/// <c>URLRequest.timeoutInterval</c> is the same shape — it is a timer on the whole request — and
/// every reader's body is small JSON, so buffering it inside the budget costs nothing.
/// </summary>
public sealed class HttpClientTransport : IHttpTransport
{
    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient _client;

    public HttpClientTransport() => this._client = SharedClient;

    /// <summary>
    /// A transport over a caller-supplied handler, for tests that need to script what the network
    /// does — headers first, then a body that stalls. The caller's handler brings its own redirect
    /// and cookie behaviour: the production settings (<c>AllowAutoRedirect = false</c> and
    /// <c>UseCookies = false</c>, see <see cref="CreateClient"/>) are deliberately not applied
    /// here, so the seam cannot silently hide a handler that follows a redirect or sends cookies.
    /// The production constructor is the one above.
    /// </summary>
    public HttpClientTransport(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        this._client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        this._client.DefaultRequestHeaders.Clear();
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            return await this._client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReaderException(ReaderError.TimedOut);
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };

        var client = new HttpClient(handler)
        {
            // The per-call timeout governs; the client's own 100 s timer must never fire first.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.Clear();
        return client;
    }
}
