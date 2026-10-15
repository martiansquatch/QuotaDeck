namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Core.Readers.Services;

/// <summary>
/// A process runner that can do things while the process "runs" — renewing a token by rewriting the
/// keychain, for instance, which is the one thing the real <c>claude</c> does that a test cannot
/// reach through a canned answer.
/// </summary>
public sealed class ScriptedProcessRunner : IProcessRunner
{
    private readonly List<ProcessRequest> _requests = [];

    public Func<ProcessRequest, ProcessResult>? OnRun { get; set; }

    public IReadOnlyList<ProcessRequest> Requests => this._requests;

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        this._requests.Add(request);
        return Task.FromResult(
            this.OnRun?.Invoke(request)
            ?? new ProcessResult { ExitCode = 1, StandardOutput = string.Empty });
    }
}
