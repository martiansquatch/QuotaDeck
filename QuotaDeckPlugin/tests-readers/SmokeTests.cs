namespace QuotaDeck.Readers.Tests;

using System.Net;
using QuotaDeck.Readers.Tests.Support;

/// <summary>The readers test project builds and its scripted transport behaves as the readers expect.
/// </summary>
public class SmokeTests
{
    [Fact]
    public async Task ScriptedResponsesAreServedInOrderAndAnEmptyQueueAnswers500()
    {
        var transport = new FakeTransport();
        transport.Respond(HttpStatusCode.OK, "{\"ok\":true}");

        using var first = await transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/one"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        using var second = await transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/two"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, second.StatusCode);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), transport.Timeouts[0]);
    }

    [Fact]
    public async Task AUrlKeyedResponseIsUsedBeforeTheInOrderQueue()
    {
        var transport = new FakeTransport();
        transport.Respond(HttpStatusCode.TooManyRequests, "{}");
        transport.Respond("https://example.invalid/usage", HttpStatusCode.OK, "{\"windows\":[]}");

        using var keyed = await transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/usage"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        using var queued = await transport.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/other"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, queued.StatusCode);
    }
}
