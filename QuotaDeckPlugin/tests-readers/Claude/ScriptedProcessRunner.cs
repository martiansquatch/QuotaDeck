namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Core.Readers.Services;

/// <summary>
/// A process runner that can do things while the process "runs" — renewing a token by rewriting the
/// keychain, for instance, which is the one thing the real <c>claude</c> does that a test cannot
/// reach through a canned answer.
/// </summary>
public sealed class ScriptedProcessRunner : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<ProcessRequest> _requests = [];

    public Func<ProcessRequest, ProcessResult>? OnRun { get; set; }

    /// <summary>
    /// A snapshot, so a test can read it while the reader's background renewal is still recording —
    /// the refresher runs on its own task, which is the whole point of it.
    /// </summary>
    public IReadOnlyList<ProcessRequest> Requests
    {
        get
        {
            lock (this._gate)
            {
                return this._requests.ToList();
            }
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> runs have arrived, or the wait runs out.</summary>
    public async Task<bool> WaitForRequestsAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (this.Requests.Count >= count)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return this.Requests.Count >= count;
    }

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        lock (this._gate)
        {
            this._requests.Add(request);
        }

        return Task.FromResult(
            this.OnRun?.Invoke(request)
            ?? new ProcessResult { ExitCode = 1, StandardOutput = string.Empty });
    }
}
