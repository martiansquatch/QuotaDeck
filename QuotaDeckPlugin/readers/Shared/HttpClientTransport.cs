namespace QuotaDeck.Readers.Shared;

using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// The one HTTP transport every reader shares.
///
/// A single static client: a per-reader client would mean a per-reader connection pool, and there
/// is no credential or proxy here that belongs to one provider. The client is deliberately plain —
/// no automatic redirects (a probe must see the real answer), no cookies, no default headers — and
/// its own <see cref="HttpClient.Timeout"/> is disabled because the per-call budget is the only
/// timer. A budget that runs out becomes <see cref="ReaderError.TimedOut"/> rather than an
/// <see cref="OperationCanceledException"/>: the reader base reads cancellation as a caller walking
/// away, and a slow endpoint is not that.
/// </summary>
public sealed class HttpClientTransport : IHttpTransport
{
    private static readonly HttpClient Client = CreateClient();

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
            return await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
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
