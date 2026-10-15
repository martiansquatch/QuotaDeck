namespace QuotaDeck.Core.Readers.Services;

/// <summary>What a finished (or killed) process left behind.</summary>
public sealed record ProcessResult
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    /// <summary>Set only when the request asked for stderr to be captured.</summary>
    public string? StandardError { get; init; }

    /// <summary>
    /// True when the process outlived its timeout and was killed. The exit code is whatever the
    /// kill produced; callers that care about the distinction check this rather than the code.
    /// </summary>
    public bool TimedOut { get; init; }
}
