namespace QuotaDeck.Readers.Antigravity;

using QuotaDeck.Core.Readers;

/// <summary>
/// Antigravity's provider module: id <c>antigravity</c>, display name "Antigravity", the Google
/// blue as its accent.
///
/// Only the default profile is exposed in this phase: the <c>~/.gemini/antigravity-&lt;slug&gt;</c>
/// siblings are discovered and logged, and become keys in a follow-up. The module's log sink is the
/// in-folder workaround for core having no logging interface (the same pattern the Codex reader
/// uses); see the PR.
/// </summary>
public sealed class AntigravityModule : IProviderModule
{
    /// <summary>Antigravity's own Google blue (#4285F4).</summary>
    private static readonly ProviderPalette GoogleBlue = new(0x42, 0x85, 0xF4);

    private readonly object _logGate = new();
    private bool _loggedProfiles;

    /// <summary>
    /// Where <c>.gemini</c> lives; the real home is computed at runtime. Injectable so tests can
    /// point it at a temp directory.
    /// </summary>
    public string HomeDirectory { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where the extra-profile note goes; defaults to <c>Trace.WriteLine</c>.</summary>
    public Action<string>? LogSink { get; init; }

    public string ProviderId => "antigravity";

    public string DisplayName => "Antigravity";

    public ProviderPalette Palette => GoogleBlue;

    public IUsageReader CreateReader(ReaderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        this.LogExtraProfilesOnce();
        var options = AntigravityOptions.FromContext(context) with
        {
            Profile = AntigravityProfile.Default(this.HomeDirectory),
            HomeDirectory = this.HomeDirectory,
        };
        return new AntigravityReader(options, context.Config, context.StateStore, context.TimeProvider);
    }

    /// <summary>
    /// The cheap presence probe: the IDE bundle under an Applications directory, the default
    /// configuration directory, or <c>agy</c> on PATH.
    /// </summary>
    public bool IsPresent(ReaderContext context) =>
        IsPresent(
            this.HomeDirectory,
            ["/Applications", Path.Combine(this.HomeDirectory, "Applications")],
            Environment.GetEnvironmentVariable("PATH"));

    /// <summary>Pure, so the rule is testable with temp directories and a made-up PATH.</summary>
    public static bool IsPresent(string home, IReadOnlyList<string> applications, string? path)
    {
        ArgumentNullException.ThrowIfNull(home);

        if (Directory.Exists(Path.Combine(home, ".gemini", "antigravity")))
        {
            return true;
        }

        foreach (var directory in applications)
        {
            if (HasAppBundle(directory))
            {
                return true;
            }
        }

        if (path is not null)
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(Path.Combine(directory, "agy")))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasAppBundle(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                && Directory.EnumerateDirectories(directory, "*.app")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Any(name => string.Equals(name, "antigravity", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private void LogExtraProfilesOnce()
    {
        lock (this._logGate)
        {
            if (this._loggedProfiles)
            {
                return;
            }

            this._loggedProfiles = true;
        }

        var extraProfiles = AntigravityProfile
            .Discover(this.HomeDirectory, AntigravityCredentialStore.HasCredential)
            .Skip(1)
            .Select(profile => profile.Id)
            .ToList();
        if (extraProfiles.Count == 0)
        {
            return;
        }

        var message = $"Antigravity extra profiles found (not yet exposed as keys): {string.Join(", ", extraProfiles)}";
        var sink = this.LogSink ?? (text => System.Diagnostics.Trace.WriteLine(text, "QuotaDeck.Antigravity"));
        sink(message);
    }
}
