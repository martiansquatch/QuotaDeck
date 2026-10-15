namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Core.Readers.Services;

/// <summary>
/// A transport that hands back ready-made responses, for the things the shared scripted transport
/// cannot express: a `Retry-After` header, a status with a body and a header at once. Everything
/// else uses <see cref="QuotaDeck.Readers.Tests.Support.FakeTransport"/>.
/// </summary>
public sealed class RawTransport(params HttpResponseMessage[] responses) : IHttpTransport
{
    private readonly Queue<HttpResponseMessage> _queued = new(responses);

    public List<HttpRequestMessage> Requests { get; } = [];

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        this.Requests.Add(request);
        return Task.FromResult(
            this._queued.Count > 0
                ? this._queued.Dequeue()
                : new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
    }
}
