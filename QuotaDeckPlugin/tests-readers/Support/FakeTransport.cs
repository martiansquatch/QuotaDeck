namespace QuotaDeck.Readers.Tests.Support;

using System.Net;
using System.Text;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// A scripted HTTP transport, the C# analogue of Codenotch's <c>URLProtocol</c> stubs: responses
/// are queued either against a URL or in order, every request is recorded for assertions, and an
/// empty queue answers 500 — running dry is a test bug, and a 500 says so more clearly than a
/// crash inside a reader would.
/// </summary>
public sealed class FakeTransport : IHttpTransport
{
    private readonly Dictionary<string, Queue<HttpResponseMessage>> _byUrl = new(StringComparer.Ordinal);
    private readonly Queue<HttpResponseMessage> _inOrder = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<TimeSpan> Timeouts { get; } = [];

    /// <summary>Queue a response for the next request, whatever its URL.</summary>
    public void Respond(HttpStatusCode status, string body = "", string contentType = "application/json") =>
        this._inOrder.Enqueue(Message(status, body, contentType));

    /// <summary>Queue a response for a specific URL, used before the queue in order.</summary>
    public void Respond(string url, HttpStatusCode status, string body = "", string contentType = "application/json")
    {
        if (!this._byUrl.TryGetValue(url, out var queue))
        {
            queue = new Queue<HttpResponseMessage>();
            this._byUrl[url] = queue;
        }

        queue.Enqueue(Message(status, body, contentType));
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        this.Requests.Add(request);
        this.Timeouts.Add(timeout);

        if (request.RequestUri is { } uri
            && this._byUrl.TryGetValue(uri.ToString(), out var queue)
            && queue.Count > 0)
        {
            return Task.FromResult(queue.Dequeue());
        }

        return Task.FromResult(
            this._inOrder.Count > 0
                ? this._inOrder.Dequeue()
                : Message(HttpStatusCode.InternalServerError, string.Empty, "text/plain"));
    }

    private static HttpResponseMessage Message(HttpStatusCode status, string body, string contentType) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
}
