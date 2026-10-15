namespace QuotaDeck.Readers.Antigravity;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// Everything <see cref="AntigravityReader"/> needs, handed in rather than reached for.
///
/// The headline preferences are QuotaDeck reader options (Codenotch stores them in Preferences):
/// which model's windows lead (<c>gemini</c> by default, <c>3p</c> for the Claude/GPT group) and
/// whether a cadence is preferred (none by default). The remaining members are test seams: a home
/// directory that is not this Mac's, a scripted loopback transport, and the poll shape of the
/// started server.
/// </summary>
public sealed record AntigravityOptions
{
    public required AntigravityProfile Profile { get; init; }

    public required string HomeDirectory { get; init; }

    public required string DataDirectory { get; init; }

    public required IHttpTransport Http { get; init; }

    public required IProcessRunner Process { get; init; }

    public required TimeProvider TimeProvider { get; init; }

    /// <summary>The transport for the loopback language server; a real one is created when unset.</summary>
    public IHttpTransport? Loopback { get; init; }

    /// <summary>Which model's windows lead: "gemini" (default) or "3p".</summary>
    public string HeadlineModel { get; init; } = "gemini";

    /// <summary>Which cadence is preferred: "automatic" (default), "five-hour" or "weekly".</summary>
    public string HeadlineLimit { get; init; } = "automatic";

    /// <summary>
    /// Replaces the local language server for a test, exactly like Codenotch's
    /// <c>localQuotaOverride</c>. Production leaves this null.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<LimitWindow>?>>? LocalQuotaOverride { get; init; }

    /// <summary>How many <c>lsof</c> polls a freshly started server gets; 40 × 250 ms in production.</summary>
    public int ServerPollAttempts { get; init; } = 40;

    public TimeSpan ServerPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public static AntigravityOptions FromContext(ReaderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new AntigravityOptions
        {
            Profile = AntigravityProfile.Default(home),
            HomeDirectory = home,
            DataDirectory = context.DataDirectory,
            Http = context.Http,
            Process = context.Process,
            TimeProvider = context.TimeProvider,
        };
    }
}
