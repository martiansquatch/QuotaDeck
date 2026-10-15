namespace QuotaDeck.Readers.Antigravity;

/// <summary>
/// One Google Antigravity configuration directory, and so one account.
///
/// Port of Codenotch <c>Sources/Providers/AntigravityProfile.swift</c> (identity, discovery,
/// <c>slug(fromDirectoryName:)</c>, <c>isProfileDirectory(_:)</c>). The default profile lives at
/// <c>~/.gemini/antigravity</c>; extras follow <c>~/.gemini/antigravity-&lt;slug&gt;</c>, ignoring the
/// internal tool flavours. The legacy Codenotch id for the default was <c>gemini</c>; QuotaDeck's
/// provider id is <c>antigravity</c>, and only the default profile is exposed by the module in this
/// phase.
/// </summary>
public sealed record AntigravityProfile(string? Slug, string ConfigDirectory)
{
    /// <summary>Every profile directory under <c>~/.gemini</c> starts with this.</summary>
    public const string DirectoryPrefix = "antigravity";

    /// <summary>Internal tool flavours that belong to the default installation, not an account.</summary>
    public static readonly IReadOnlySet<string> IgnoredSlugs = new HashSet<string>(StringComparer.Ordinal)
    {
        "ide", "cli", "backup", "api",
    };

    public string Id => Slug is null ? "antigravity" : $"{DirectoryPrefix}-{Slug}";

    public string DisplayName => Slug is null ? "Antigravity" : $"Antigravity ({Slug})";

    public string AuthPath => Path.Combine(this.ConfigDirectory, "oauth_creds.json");

    public string BrainDirectory => Path.Combine(this.ConfigDirectory, "brain");

    /// <summary><c>~/.gemini/antigravity</c>, whether or not it exists — the app has always read it.</summary>
    public static AntigravityProfile Default(string home) =>
        new(null, Path.Combine(home, ".gemini", DirectoryPrefix));

    /// <summary>
    /// The default profile followed by every <c>~/.gemini/antigravity-&lt;slug&gt;</c> that contains
    /// profile markers and valid credentials, slugs in alphabetical order.
    /// </summary>
    public static IReadOnlyList<AntigravityProfile> Discover(
        string home,
        Func<AntigravityProfile, bool> hasCredential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(hasCredential);

        var geminiDirectory = Path.Combine(home, ".gemini");
        var extras = new List<AntigravityProfile>();
        if (Directory.Exists(geminiDirectory))
        {
            foreach (var name in Directory.GetDirectories(geminiDirectory).Select(Path.GetFileName).OfType<string>())
            {
                if (SlugFromDirectoryName(name) is not { } slug)
                {
                    continue;
                }

                var directory = Path.Combine(geminiDirectory, name);
                if (!IsProfileDirectory(directory))
                {
                    continue;
                }

                var candidate = new AntigravityProfile(slug, directory);
                if (hasCredential(candidate))
                {
                    extras.Add(candidate);
                }
            }
        }

        extras.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Slug, right.Slug));
        var profiles = new List<AntigravityProfile> { Default(home) };
        profiles.AddRange(extras);
        return profiles;
    }

    /// <summary><c>antigravity-work</c> → <c>work</c>; anything else → null.</summary>
    public static string? SlugFromDirectoryName(string name)
    {
        var prefix = DirectoryPrefix + "-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var slug = name[prefix.Length..];
        return slug.Length == 0 || IgnoredSlugs.Contains(slug) ? null : slug;
    }

    private static readonly string[] Markers = ["oauth_creds.json", "credentials.json", "brain", "agent.db"];

    /// <summary>Markers identifying an Antigravity configuration/profile directory.</summary>
    public static bool IsProfileDirectory(string directory) =>
        Directory.Exists(directory)
        && Markers.Any(marker => File.Exists(Path.Combine(directory, marker)) || Directory.Exists(Path.Combine(directory, marker)));
}
