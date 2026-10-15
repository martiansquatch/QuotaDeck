namespace QuotaDeck.Readers.Codex;

using QuotaDeck.Core.Readers;

/// <summary>
/// Codex's entry in the provider list.
///
/// Port of Codenotch <c>Sources/Providers/CodexLocalProvider.swift</c> (the <c>id</c>,
/// <c>displayName</c> and <c>glyph</c> surface, lines 5-43) plus the profile discovery of
/// <c>Sources/Providers/CodexProfile.swift</c> (<c>discover(home:fileManager:)</c>, lines 18-28).
///
/// This PR exposes the default profile only: the <c>~/.codex-&lt;slug&gt;</c> siblings are discovered
/// and logged, and become keys in a follow-up after Checkpoint 1. Profiles are strictly isolated —
/// there is never a credential fallback from one account to another. The modules' log sink is the
/// in-folder workaround for core having no logging interface; see the PR.
/// </summary>
public sealed class CodexModule : IProviderModule
{
    /// <summary>Codex's own mint green (#10A37F).</summary>
    private static readonly ProviderPalette MintGreen = new(0x10, 0xA3, 0x7F);

    private readonly object _logGate = new();
    private bool _loggedProfiles;

    public string ProviderId => "codex";

    public string DisplayName => "Codex";

    public ProviderPalette Palette => MintGreen;

    /// <summary>
    /// Where Codex's state lives; the real home is computed at runtime. Injectable so tests can
    /// point it at a temp directory.
    /// </summary>
    public string HomeDirectory { get; init; } = CodexProfile.HomeDirectory;

    /// <summary>Whether the extra Spark / code-review windows are read; see <see cref="CodexReader.ShowExtraLimits"/>.</summary>
    public bool ShowExtraLimits { get; init; } = true;

    /// <summary>Where the extra-profile note goes; defaults to <c>Trace.WriteLine</c>.</summary>
    public Action<string>? LogSink { get; init; }

    /// <summary>
    /// The default profile's reader. The other <c>.codex-&lt;slug&gt;</c> profiles are discovered and
    /// logged rather than handed out — one key per account is the follow-up after Checkpoint 1.
    /// </summary>
    public IUsageReader CreateReader(ReaderContext context)
    {
        this.LogExtraProfilesOnce();
        return new CodexReader(context, this.HomeDirectory) { ShowExtraLimits = this.ShowExtraLimits };
    }

    /// <summary>A cheap presence probe: Codex is here when the default profile's session file is.</summary>
    public bool IsPresent(ReaderContext context) =>
        File.Exists(CodexProfile.Default(this.HomeDirectory).AuthPath);

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

        var extraProfiles = CodexProfile
            .Discover(this.HomeDirectory)
            .Skip(1)
            .Select(profile => profile.Id)
            .ToList();
        if (extraProfiles.Count == 0)
        {
            return;
        }

        var message = $"Codex extra profiles found (not yet exposed as keys): {string.Join(", ", extraProfiles)}";
        var sink = this.LogSink ?? (text => System.Diagnostics.Trace.WriteLine(text, "QuotaDeck.Codex"));
        sink(message);
    }
}
