namespace QuotaDeck.Readers.Tests.Support;

using System.Net;
using System.Text;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// A scripted HTTP transport, the C# analogue of Codenotch's <c>URLProtocol</c> stubs: responses
/// are queued either against a URL or in order, a scripted response can carry headers (a
/// <c>Retry-After</c> among them), every request is recorded for assertions, and an empty queue
/// answers 500 — running dry is a test bug, and a 500 says so more clearly than a crash inside a
/// reader would.
/// </summary>
public sealed class FakeTransport : IHttpTransport
{
    private readonly Dictionary<string, Queue<HttpResponseMessage>> _byUrl = new(StringComparer.Ordinal);
    private readonly Queue<HttpResponseMessage> _inOrder = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<TimeSpan> Timeouts { get; } = [];

    /// <summary>
    /// Every request as it arrived, captured at send time — a reader disposing the message
    /// afterwards cannot take the evidence with it — with its headers exposed by name.
    /// </summary>
    public List<RecordedRequest> Recorded { get; } = [];

    /// <summary>Queue a response for the next request, whatever its URL.</summary>
    public void Respond(
        HttpStatusCode status,
        string body = "",
        string contentType = "application/json",
        params (string Name, string Value)[] headers) =>
        this._inOrder.Enqueue(Message(status, body, contentType, headers));

    /// <summary>Queue a response for a specific URL, used before the queue in order.</summary>
    public void Respond(
        string url,
        HttpStatusCode status,
        string body = "",
        string contentType = "application/json",
        params (string Name, string Value)[] headers)
    {
        if (!this._byUrl.TryGetValue(url, out var queue))
        {
            queue = new Queue<HttpResponseMessage>();
            this._byUrl[url] = queue;
        }

        queue.Enqueue(Message(status, body, contentType, headers));
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        this.Requests.Add(request);
        this.Timeouts.Add(timeout);
        this.Recorded.Add(RecordedRequest.From(request, timeout));

        if (request.RequestUri is { } uri
            && this._byUrl.TryGetValue(uri.ToString(), out var queue)
            && queue.Count > 0)
        {
            return Task.FromResult(queue.Dequeue());
        }

        return Task.FromResult(
            this._inOrder.Count > 0
                ? this._inOrder.Dequeue()
                : Message(HttpStatusCode.InternalServerError, string.Empty, "text/plain", []));
    }

    private static HttpResponseMessage Message(
        HttpStatusCode status,
        string body,
        string contentType,
        (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    /// <summary>One request as the fake saw it: method, URL, headers and the budget it was given.</summary>
    public sealed record RecordedRequest(
        string Method,
        string Url,
        IReadOnlyDictionary<string, string[]> Headers,
        TimeSpan Timeout)
    {
        /// <summary>The first value of a header, or nil when the header was not sent.</summary>
        public string? Header(string name) =>
            this.Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

        public static RecordedRequest From(HttpRequestMessage request, TimeSpan timeout)
        {
            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = values.ToArray();
            }

            if (request.Content is { } content)
            {
                foreach (var (name, values) in content.Headers)
                {
                    headers[name] = values.ToArray();
                }
            }

            return new RecordedRequest(
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                headers,
                timeout);
        }
    }
}
