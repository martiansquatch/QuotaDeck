namespace QuotaDeck.Readers.Codex;

/// <summary>
/// One Codex account's local state directory: the default <c>~/.codex</c> and the
/// <c>~/.codex-&lt;slug&gt;</c> siblings a person keeps for other accounts.
///
/// Port of Codenotch <c>Sources/Providers/CodexProfile.swift</c> —
/// <c>default(home:)</c> (lines 14-16), <c>discover(home:fileManager:)</c> (lines 18-28),
/// <c>slug(fromDirectoryName:)</c> (lines 30-35), <c>isProfileDirectory(_:fileManager:)</c>
/// (lines 37-47), <c>id</c> (line 50), <c>displayName</c> (line 56), <c>authURL</c>,
/// <c>stateURL</c>, <c>desktopStoreURL</c> (lines 65-67), <c>isCodex(providerID:)</c>
/// (lines 53-55) and <c>slug(fromProviderID:)</c> (lines 58-63).
///
/// CODEX_HOME is deliberately not read: a Finder-launched app does not inherit a shell's
/// environment, so the directory convention is discovered from the home directory instead. The
/// path is computed at runtime from the user's home — never stored as an absolute path.
/// </summary>
public sealed record CodexProfile
{
    /// <summary>Preserved so existing readings and preferences survive (CodexProfile.swift:6).</summary>
    public const string DefaultId = "codex";

    public const string DirectoryPrefix = ".codex";

    /// <summary>
    /// The marker entries that make a directory a Codex profile. A signed-out profile can still
    /// have settings or sessions, and its row is kept so it can explain how to sign back in.
    /// </summary>
    private static readonly string[] Markers =
    [
        "auth.json",
        "config.toml",
        "sessions",
        "history.jsonl",
        "state_5.sqlite",
        "sqlite/codex-dev.db",
    ];

    private CodexProfile(string? slug, string configDirectory)
    {
        this.Slug = slug;
        this.ConfigDirectory = configDirectory;
    }

    /// <summary>Nil for the default profile (CodexProfile.swift:9).</summary>
    public string? Slug { get; }

    public string ConfigDirectory { get; }

    /// <summary>The user's home directory, computed at runtime.</summary>
    public static string HomeDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home)
                ? Environment.GetEnvironmentVariable("HOME") ?? string.Empty
                : home;
        }
    }

    /// <summary>The default profile's id, or <c>codex-&lt;slug&gt;</c> (CodexProfile.swift:50).</summary>
    public string Id => this.Slug is null ? DefaultId : $"{DefaultId}-{this.Slug}";

    /// <summary>"Codex", or "Codex (&lt;slug&gt;)" (CodexProfile.swift:56).</summary>
    public string DisplayName => this.Slug is null ? "Codex" : $"Codex ({this.Slug})";

    public string AuthPath => Path.Combine(this.ConfigDirectory, "auth.json");

    public string StatePath => Path.Combine(this.ConfigDirectory, "state_5.sqlite");

    public string DesktopStorePath => Path.Combine(this.ConfigDirectory, "sqlite", "codex-dev.db");

    /// <summary>The default profile under one home (CodexProfile.swift:14-16).</summary>
    public static CodexProfile Default(string homeDirectory) =>
        new(slug: null, Path.Combine(homeDirectory, DirectoryPrefix));

    /// <summary>
    /// The default profile followed by every <c>.codex-&lt;slug&gt;</c> sibling that is a profile
    /// directory, sorted by slug (CodexProfile.swift:18-28). A home that cannot be listed still
    /// yields the default.
    /// </summary>
    public static IReadOnlyList<CodexProfile> Discover(string homeDirectory)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(homeDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            entries = [];
        }

        var extras = new List<CodexProfile>();
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (SlugFromDirectoryName(name) is not { } slug)
            {
                continue;
            }

            var directory = Path.Combine(homeDirectory, name);
            if (IsProfileDirectory(directory))
            {
                extras.Add(new CodexProfile(slug, directory));
            }
        }

        extras.Sort((left, right) => string.CompareOrdinal(left.Slug, right.Slug));

        var profiles = new List<CodexProfile> { Default(homeDirectory) };
        profiles.AddRange(extras);
        return profiles;
    }

    /// <summary>The slug a <c>.codex-&lt;slug&gt;</c> directory name names; nil for anything else.</summary>
    public static string? SlugFromDirectoryName(string name)
    {
        var prefix = DirectoryPrefix + "-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var slug = name[prefix.Length..];
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>Whether a directory carries any of the profile markers.</summary>
    public static bool IsProfileDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        foreach (var marker in Markers)
        {
            var path = Path.Combine(directory, marker);
            if (File.Exists(path) || Directory.Exists(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a provider id names a Codex profile, default or otherwise.</summary>
    public static bool IsCodex(string providerId) =>
        providerId == DefaultId || providerId.StartsWith(DefaultId + "-", StringComparison.Ordinal);

    /// <summary>The slug a <c>codex-&lt;slug&gt;</c> provider id names; nil for the default id.</summary>
    public static string? SlugFromProviderId(string providerId)
    {
        var prefix = DefaultId + "-";
        if (!providerId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var slug = providerId[prefix.Length..];
        return slug.Length == 0 ? null : slug;
    }
}
