namespace QuotaDeck.Readers.Tests.Antigravity;

using System.Net;
using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// A scripted transport that snapshots each request's body before the caller disposes it — readers
/// dispose their requests after sending, which the shared <c>FakeTransport</c>'s recorded messages
/// cannot survive.
/// </summary>
internal sealed class RecordingTransport : IHttpTransport
{
    private readonly FakeTransport _inner = new();

    public List<RecordedRequest> Requests { get; } = [];

    public void Respond(HttpStatusCode status, string body = "", string contentType = "application/json") =>
        this._inner.Respond(status, body, contentType);

    public void Respond(string url, HttpStatusCode status, string body = "", string contentType = "application/json") =>
        this._inner.Respond(url, status, body, contentType);

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var response = await this._inner.SendAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        this.Requests.Add(new RecordedRequest(request, body, request.RequestUri?.ToString() ?? string.Empty));
        return response;
    }

    internal sealed record RecordedRequest(HttpRequestMessage Request, string? Body, string Url);
}
