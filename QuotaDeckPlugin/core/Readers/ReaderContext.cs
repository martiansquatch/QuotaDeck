namespace QuotaDeck.Core.Readers;

using QuotaDeck.Core.Settings;

/// <summary>
/// Everything a module's reader needs, handed in rather than reached for.
///
/// A plain record instead of an <c>IServiceProvider</c>: the plugin has exactly these services, and
/// naming them keeps every reader testable with the fakes in the readers test project.
/// </summary>
public sealed record ReaderContext
{
    public required ReaderConfig Config { get; init; }

    public required IReaderStateStore StateStore { get; init; }

    public required TimeProvider TimeProvider { get; init; }

    /// <summary>Per-user data directory (config, state file, caches).</summary>
    public required string DataDirectory { get; init; }

    public required Services.IHttpTransport Http { get; init; }

    public required Services.IProcessRunner Process { get; init; }
}
