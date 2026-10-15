namespace QuotaDeck.Readers.Antigravity;

using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// The transport for the language server: HTTPS on loopback with a self-signed certificate no
/// authority vouches for.
///
/// Port of Codenotch <c>Sources/Providers/LocalhostTrust.swift</c> (lines 10-23): the exception is
/// scoped to <c>127.0.0.1</c>, <c>localhost</c> and <c>::1</c> only, so nothing reached over a
/// network is ever trusted by this client. Injectable, so tests use the scripted transport instead.
/// </summary>
public sealed class LoopbackHttpTransport : IHttpTransport
{
    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// Whether this request's host is one whose certificate is accepted. The callback itself, so a
    /// test can pin the scoping without a socket.
    /// </summary>
    public static bool TrustsServerCertificate(HttpRequestMessage request) =>
        request?.RequestUri is { } uri && IsLoopbackHost(uri.Host);

    public static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

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
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                TrustsServerCertificate(request),
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.Clear();
        return client;
    }
}
