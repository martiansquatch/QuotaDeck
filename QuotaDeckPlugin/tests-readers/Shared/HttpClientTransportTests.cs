namespace QuotaDeck.Readers.Tests.Shared;

using System.Net;
using System.Text;
using QuotaDeck.Core.Readers;
using QuotaDeck.Readers.Shared;

/// <summary>
/// The per-call budget of the one real transport: it covers the whole exchange — headers and body —
/// so a body that stalls past the budget reads as <c>TimedOut</c> instead of hanging a reader that
/// was already handed the response, matching Swift's <c>URLRequest.timeoutInterval</c>.
/// </summary>
public class HttpClientTransportTests
{
    [Fact(Timeout = 15_000)]
    public async Task ASlowBodyTimesOutTheWholeExchange()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler((_, token) =>
        {
            reached.SetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StallingContent(token),
            });
        });
        var transport = new HttpClientTransport(handler);

        var exception = await Assert.ThrowsAsync<ReaderException>(() => transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/slow"),
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None));

        Assert.Equal(ReaderErrorKind.TimedOut, exception.Error.Kind);
        await reached.Task; // the headers had arrived; it is the body that ran out the budget
    }

    [Fact(Timeout = 15_000)]
    public async Task AStalledExchangeStillTimesOut()
    {
        var handler = new ScriptedHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var transport = new HttpClientTransport(handler);

        var exception = await Assert.ThrowsAsync<ReaderException>(() => transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/hang"),
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None));

        Assert.Equal(ReaderErrorKind.TimedOut, exception.Error.Kind);
    }

    [Fact]
    public async Task ABodyInsideTheBudgetComesBackReadable()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
        }));
        var transport = new HttpClientTransport(handler);

        using var response = await transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/fast"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"ok\":true}", await response.Content.ReadAsStringAsync());
    }

    /// <summary>A caller who walks away stays a cancellation: the timeout mapping must not claim it.</summary>
    [Fact]
    public async Task ACallersCancellationIsNotATimeout()
    {
        var handler = new ScriptedHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var transport = new HttpClientTransport(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/cancelled"),
            TimeSpan.FromSeconds(30),
            cancellation.Token));
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    /// <summary>Headers arrive; the body waits for the request to be abandoned — a slow network
    /// body, the case the budget has to cover.</summary>
    private sealed class StallingContent(CancellationToken requestToken) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan, requestToken);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
