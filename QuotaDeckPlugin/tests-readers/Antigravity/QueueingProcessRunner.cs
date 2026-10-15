namespace QuotaDeck.Readers.Tests.Antigravity;

using QuotaDeck.Core.Readers.Services;

/// <summary>
/// A process runner whose answers queue per executable, so a test can script "the first lsof is
/// empty, the second has the ports" — the shared <c>FakeProcessRunner</c> answers the same script
/// every time, which is enough for one-shot commands but not for polling. Every request is recorded.
/// </summary>
internal sealed class QueueingProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, Queue<ProcessResult>> _answers = new(StringComparer.Ordinal);

    public List<ProcessRequest> Requests { get; } = [];

    public void Respond(string fileName, int exitCode, string stdout = "", string? stderr = null)
    {
        if (!this._answers.TryGetValue(fileName, out var queue))
        {
            queue = new Queue<ProcessResult>();
            this._answers[fileName] = queue;
        }

        queue.Enqueue(new ProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = stdout,
            StandardError = stderr,
        });
    }

    public int Count(string fileName) => this.Requests.Count(request => request.FileName == fileName);

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        this.Requests.Add(request);
        if (this._answers.TryGetValue(request.FileName, out var queue) && queue.Count > 0)
        {
            return Task.FromResult(queue.Dequeue());
        }

        return Task.FromResult(new ProcessResult
        {
            ExitCode = 127,
            StandardOutput = string.Empty,
            StandardError = null,
        });
    }
}
