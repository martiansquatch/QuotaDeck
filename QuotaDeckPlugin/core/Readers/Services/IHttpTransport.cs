namespace QuotaDeck.Core.Readers.Services;

/// <summary>
/// How a reader talks to an HTTP endpoint.
///
/// Lives in core so readers can be tested against a scripted transport without an SDK or a socket;
/// the one real implementation (<c>HttpClientTransport</c>) lives in the readers project. The
/// per-call timeout is a parameter rather than an <c>HttpClient</c> property because different
/// endpoints get different budgets, and a timeout must read as
/// <see cref="ReaderException"/> with <see cref="ReaderError.TimedOut"/> — never as a cancellation —
/// so the reader base's cancellation guard cannot mistake a slow endpoint for a caller who left.
/// </summary>
public interface IHttpTransport
{
    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
