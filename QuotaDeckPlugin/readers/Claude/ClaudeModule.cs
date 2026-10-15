namespace QuotaDeck.Readers.Claude;

using QuotaDeck.Core.Readers;
using QuotaDeck.Readers.Shared;

// Port of Codenotch Sources/Providers/ClaudeProfile.swift's discovery and identity, as a provider
// module: one Claude ring for the default login (`~/.claude`), with the id, name and colour the
// plugin's face draws. Codenotch builds one provider per discovered profile; the module system here
// registers one class per provider, so the extra logins are found and named in a log line but not
// drawn yet — see the PR notes.

/// <summary>The Claude provider: who it is, how to draw it, how to read it, and whether it is here.</summary>
public sealed class ClaudeModule : IProviderModule
{
    /// <summary>Claude's own orange, as the three channels the face renderer wants.</summary>
    public static readonly ProviderPalette PaletteColor = new(0xD9, 0x77, 0x57);

    /// <summary>Nil in production: the home directory is computed at runtime, never spelled out here.</summary>
    private readonly string? _home;

    public ClaudeModule()
    {
    }

    /// <summary>A module reading a home directory a test controls.</summary>
    internal ClaudeModule(string homeDirectory) => this._home = homeDirectory;

    public string ProviderId => ClaudeReader.ProviderId;

    public string DisplayName => "Claude";

    public ProviderPalette Palette => PaletteColor;

    public IUsageReader CreateReader(ReaderContext context) =>
        new ClaudeReader(new ClaudeReaderOptions
        {
            Context = context,
            HomeDirectory = this._home,
        });

    /// <summary>
    /// A <c>~/.claude</c> directory or a keychain item, and nothing that could raise a prompt:
    /// enumeration reads attributes only, so this stays cheap and safe to call on every render.
    /// </summary>
    public bool IsPresent(ReaderContext context)
    {
        var home = this._home ?? ClaudeProfile.HomeDirectory;
        var fileSystem = new SystemClaudeFileSystem();
        if (fileSystem.IsDirectory(Path.Combine(home, ClaudeProfile.DirectoryPrefix)))
        {
            return true;
        }

        var profile = ClaudeProfile.Default(home, fileSystem);
        return KeychainQuery.Newest(new MacKeychain(), profile.KeychainServices, profile.Account) is not null;
    }
}
