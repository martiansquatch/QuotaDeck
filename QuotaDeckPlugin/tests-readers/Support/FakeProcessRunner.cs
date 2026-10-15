namespace QuotaDeck.Readers.Tests.Support;

using QuotaDeck.Core.Readers.Services;

/// <summary>
/// A scripted process runner: answers are matched by executable name (and optionally arguments),
/// every request is recorded, and anything unmatched answers exit code 127 — "command not found" —
/// which is what a reader should be tested against for absence.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<ScriptedAnswer> _answers = [];

    public List<ProcessRequest> Requests { get; } = [];

    public void Respond(string fileName, int exitCode, string stdout = "", string? stderr = null, string[]? arguments = null) =>
        this._answers.Add(new ScriptedAnswer(fileName, arguments ?? [], new ProcessResult
        {
            ExitCode = exitCode,
            StandardOutput = stdout,
            StandardError = stderr,
        }));

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        this.Requests.Add(request);

        foreach (var answer in this._answers)
        {
            if (answer.FileName == request.FileName
                && (answer.Arguments.Length == 0 || answer.Arguments.SequenceEqual(request.Arguments)))
            {
                return Task.FromResult(answer.Result);
            }
        }

        return Task.FromResult(new ProcessResult
        {
            ExitCode = 127,
            StandardOutput = string.Empty,
            StandardError = null,
        });
    }

    private sealed record ScriptedAnswer(string FileName, string[] Arguments, ProcessResult Result);
}
