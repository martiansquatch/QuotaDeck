namespace QuotaDeck.Core.Readers.Services;

/// <summary>One execution of a local process.</summary>
public sealed record ProcessRequest
{
    /// <summary>Absolute path, or a name resolved on PATH by the real runner.</summary>
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Overrides merged over the inherited environment. A null value removes the variable rather
    /// than setting it to the empty string — a distinction argv or a credential helper may care about.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Capture stderr into the result; otherwise it is discarded as the process writes it.</summary>
    public bool CaptureStandardError { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
