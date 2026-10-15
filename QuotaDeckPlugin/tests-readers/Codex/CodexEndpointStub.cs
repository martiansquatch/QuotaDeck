namespace QuotaDeck.Readers.Tests.Codex;

using System.Net;
using System.Text;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// The C# mirror of the stateless <c>CodexProfileEndpoint</c> URLProtocol stub in Codenotch
/// <c>Tests/CodexProfileTests.swift:243-272</c>: the answer is chosen by the
/// (<c>ChatGPT-Account-Id</c>, <c>Authorization</c>) pair — personal and work get their own
/// percentages, a limited token gets 429 with <c>Retry-After: 300</c>, anything else 401.
/// </summary>
internal sealed class CodexEndpointStub : IHttpTransport
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        this.Requests.Add(request);

        var account = request.Headers.TryGetValues("ChatGPT-Account-Id", out var accounts)
            ? accounts.FirstOrDefault()
            : null;
        var token = request.Headers.TryGetValues("Authorization", out var tokens)
            ? tokens.FirstOrDefault()
            : null;

        var (percent, status) = (account, token) switch
        {
            ("personal", "Bearer personal-token") => (10, HttpStatusCode.OK),
            ("work", "Bearer work-token") => (75, HttpStatusCode.OK),
            ("work", "Bearer rotated-token") => (80, HttpStatusCode.OK),
            ("work", "Bearer limited-token") => (0, HttpStatusCode.TooManyRequests),
            _ => (0, HttpStatusCode.Unauthorized),
        };

        var response = new HttpResponseMessage(status);
        if (status == HttpStatusCode.TooManyRequests)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", "300");
        }

        response.Content = new StringContent(
            $"{{\"rate_limit\":{{\"primary_window\":{{\"used_percent\":{percent},\"limit_window_seconds\":18000}}}}}}",
            Encoding.UTF8,
            "application/json");
        return Task.FromResult(response);
    }
}
