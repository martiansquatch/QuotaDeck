namespace QuotaDeck.Readers.Tests.Codex;

using System.Net;
using System.Text;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// A scripted transport that can attach response headers.
///
/// The shared <c>FakeTransport</c> has no header seam, and the tightening this reader is tested for
/// — the 429 mapping — is carried entirely in <c>Retry-After</c>. This stub stays inside the Codex
/// test folder so the shared support file is untouched.
/// </summary>
internal sealed class CodexScriptedTransport : IHttpTransport
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<TimeSpan> Timeouts { get; } = [];

    public void Respond(HttpStatusCode status, string body = "{}", params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        this._responses.Enqueue(response);
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        this.Requests.Add(request);
        this.Timeouts.Add(timeout);

        return Task.FromResult(this._responses.Count > 0
            ? this._responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
            });
    }
}
