namespace QuotaDeck.Readers.Claude;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuotaDeck.Readers.Shared;

// Port of Codenotch Sources/Providers/ClaudeProfile.swift (struct ClaudeProfile: default(home:),
// discover(home:fileManager:hasCredential:), hasKeychainCredential(_:), slug(fromDirectoryName:),
// isProfileDirectory(_:fileManager:), id, displayName, accountLabel()/accountLabel(forAddress:),
// displayNames(for:), sessionsDirectory, projectsDirectory, accountFileURL, signedInAddress(),
// organizationID(), organizationPlan(), accountID(), keychainServices, keychainService,
// keychainSuffix(forPath:), sourceName, signInCommand, tilde(_:), displayPath) and the private
// AccountFileCache at its foot.

/// <summary>
/// One Claude Code configuration directory, and so one account.
///
/// Claude Code keeps everything for an account under a single directory: <c>~/.claude</c> by
/// default, or wherever <c>CLAUDE_CONFIG_DIR</c> points. People who keep a personal and a work login
/// apart do it by aliasing the second one to <c>~/.claude-work</c>, <c>~/.claude-client</c>, and so
/// on — each with its own token in the keychain and its own sessions folder. A profile is the
/// *convention* <c>~/.claude-&lt;slug&gt;</c>, not the environment variable: the plugin is launched
/// by the Logi Plugin Service, so a shell's alias never reaches it.
///
/// This PR reads the default profile only; discovery is ported and tested because the reader needs
/// its answer ("is this the only login?") and because multi-account keys are the next step.
/// </summary>
public sealed class ClaudeProfile
{
    /// <summary>The provider id the default profile has always had.</summary>
    public const string DefaultId = "claude";

    /// <summary>What every profile directory starts with.</summary>
    public const string DirectoryPrefix = ".claude";

    /// <summary>The service the default profile's token is filed under, unsuffixed.</summary>
    public const string DefaultKeychainService = "Claude Code-credentials";

    private static readonly string[] Markers =
        ["sessions", "projects", "settings.json", "history.jsonl", ".claude.json"];

    private readonly IClaudeFileSystem _fileSystem;

    public ClaudeProfile(string? slug, string configDirectory, IClaudeFileSystem fileSystem, string account)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        this.Slug = slug;
        this.ConfigDirectory = configDirectory.TrimEnd('/');
        this._fileSystem = fileSystem;
        this.Account = account;
    }

    /// <summary>Nil for <c>~/.claude</c>; the part after <c>.claude-</c> otherwise.</summary>
    public string? Slug { get; }

    public string ConfigDirectory { get; }

    /// <summary>
    /// The account this login's keychain items are filed under — this user's. Used by the rescue
    /// that reads an item by name through <c>/usr/bin/security</c>, not by the enumeration.
    /// </summary>
    public string Account { get; }

    /// <summary><c>claude</c> for the default, <c>claude-&lt;slug&gt;</c> for the rest.</summary>
    public string Id => this.Slug is null ? DefaultId : $"{DefaultId}-{this.Slug}";

    /// <summary><c>~/.claude</c>, whether or not it exists.</summary>
    public static ClaudeProfile Default(
        string homeDirectory,
        IClaudeFileSystem? fileSystem = null,
        string? account = null) =>
        new(
            null,
            Path.Combine(homeDirectory, DirectoryPrefix),
            fileSystem ?? new SystemClaudeFileSystem(),
            account ?? Environment.UserName);

    /// <summary>The home directory the real app reads, computed at runtime and never spelled out.</summary>
    public static string HomeDirectory =>
        Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The default profile followed by every <c>~/.claude-&lt;slug&gt;</c> that Claude Code has
    /// actually used, slugs in alphabetical order so the rings never swap places between launches.
    ///
    /// "Actually used" is judged twice over: the files Claude Code writes on its first run rule out
    /// an empty directory or a stray one someone made by hand, and a token filed under the
    /// directory's own service name rules out everything else that has learned to live at
    /// <c>~/.claude-&lt;slug&gt;</c>. <c>claude-mem</c> keeps its state in <c>~/.claude-mem</c> and
    /// writes every one of the first-run names, so filenames alone cannot tell it from an account;
    /// the credential can.
    /// </summary>
    public static IReadOnlyList<ClaudeProfile> Discover(
        string homeDirectory,
        IClaudeFileSystem fileSystem,
        IKeychain keychain,
        string? account = null)
    {
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(keychain);

        var resolvedAccount = account ?? Environment.UserName;
        var extras = new List<ClaudeProfile>();
        foreach (var name in fileSystem.ListNames(homeDirectory))
        {
            if (SlugFromDirectoryName(name) is not { } slug)
            {
                continue;
            }

            var directory = Path.Combine(homeDirectory, name);
            if (!IsProfileDirectory(directory, fileSystem))
            {
                continue;
            }

            var candidate = new ClaudeProfile(slug, directory, fileSystem, resolvedAccount);
            if (!HasKeychainCredential(candidate, keychain))
            {
                continue;
            }

            extras.Add(candidate);
        }

        extras.Sort((left, right) => string.CompareOrdinal(left.Slug, right.Slug));
        var profiles = new List<ClaudeProfile> { Default(homeDirectory, fileSystem, resolvedAccount) };
        profiles.AddRange(extras);
        return profiles;
    }

    /// <summary>
    /// Whether Claude Code has ever filed a token for this profile's directory.
    ///
    /// The account is not part of the query, the same as Codenotch's
    /// <c>KeychainItem.newest(services:)</c>: the item is asked for by service, and the account is
    /// only ever *used* on the rescue that reads it by name. Deliberately not a check on whether the
    /// token is valid — an expired one still means the account exists and the ring is worth drawing.
    /// </summary>
    public static bool HasKeychainCredential(ClaudeProfile profile, IKeychain keychain) =>
        KeychainQuery.Newest(keychain, profile.KeychainServices) is not null;

    /// <summary><c>.claude-work</c> → <c>work</c>; anything else → nil.</summary>
    public static string? SlugFromDirectoryName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var prefix = DirectoryPrefix + "-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var slug = name[prefix.Length..];
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>Any of the files Claude Code creates the first time it runs against a directory.</summary>
    public static bool IsProfileDirectory(string path, IClaudeFileSystem fileSystem) =>
        fileSystem.IsDirectory(path)
        && Markers.Any(marker => fileSystem.FileExists(Path.Combine(path, marker)));

    /// <summary>Whether a provider id names a Claude profile, default or otherwise.</summary>
    public static bool IsClaude(string providerId) =>
        providerId == DefaultId || providerId.StartsWith(DefaultId + "-", StringComparison.Ordinal);

    /// <summary>The slug back out of a provider id.</summary>
    public static string? SlugFromProviderId(string providerId)
    {
        if (!providerId.StartsWith(DefaultId + "-", StringComparison.Ordinal))
        {
            return null;
        }

        var slug = providerId[(DefaultId.Length + 1)..];
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>
    /// Claude Code's rule for a token filed by a directory: the first eight hex digits of the
    /// SHA-256 of the directory's absolute path, no trailing slash.
    /// </summary>
    public static string KeychainSuffixForPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexStringLower(digest)[..8];
    }

    /// <summary>The directory as a person would type it.</summary>
    public string DisplayPath => Tilde(this.ConfigDirectory);

    public static string Tilde(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var home = HomeDirectory;
        return path.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }

    /// <summary>
    /// Every keychain service this profile's token might be filed under, in the order to prefer
    /// them — newest wins across the lot at read time.
    ///
    /// The subtlety is *when* Claude Code applies the suffix to the default directory: it suffixes
    /// whenever <c>CLAUDE_CONFIG_DIR</c> is set in the shell it runs from, and a shell that exports
    /// the variable exports it even when it points at the default <c>~/.claude</c> — so the default
    /// profile's live token can sit under <c>Claude Code-credentials-&lt;hash&gt;</c> rather than
    /// the bare name. So the default offers both, suffixed first; a named profile is only ever
    /// written suffixed.
    /// </summary>
    public IReadOnlyList<string> KeychainServices =>
        this.Slug is null
            ? [$"{DefaultKeychainService}-{KeychainSuffixForPath(this.ConfigDirectory)}", DefaultKeychainService]
            : [$"{DefaultKeychainService}-{KeychainSuffixForPath(this.ConfigDirectory)}"];

    /// <summary>The primary service — the first candidate.</summary>
    public string KeychainService => this.KeychainServices[0];

    public string SessionsDirectory => Path.Combine(this.ConfigDirectory, "sessions");

    public string ProjectsDirectory => Path.Combine(this.ConfigDirectory, "projects");

    /// <summary>
    /// Claude Code's own settings file, which carries the signed-in address.
    ///
    /// The default profile keeps it *beside* the directory, at <c>~/.claude.json</c>; a profile
    /// reached through <c>CLAUDE_CONFIG_DIR</c> keeps it *inside* its own directory. Reading the
    /// wrong one shows the personal account against the work ring.
    /// </summary>
    public string AccountFileUrl =>
        this.Slug is null
            ? Path.Combine(Path.GetDirectoryName(this.ConfigDirectory) ?? string.Empty, ".claude.json")
            : Path.Combine(this.ConfigDirectory, ".claude.json");

    /// <summary>
    /// <c>Claude Gmail</c>, <c>Claude Acme</c> — the account's own name where Claude Code records
    /// one — and <c>Claude</c> or <c>Claude (work)</c> where it does not.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (this.AccountLabel() is not { } label)
            {
                return this.Slug is null ? "Claude" : $"Claude ({this.Slug})";
            }

            return $"Claude {label}";
        }
    }

    /// <summary>The one word that names this account: <c>Gmail</c>, <c>Hotmail</c>, <c>Acme</c>.</summary>
    public string? AccountLabel() => AccountLabelForAddress(this.SignedInAddress());

    /// <summary>
    /// The domain's first label, capitalised — the account's own name where it has one.
    ///
    /// Kept static and separate from the file it comes from so the rule can be tested without a
    /// <c>.claude.json</c> on disk.
    /// </summary>
    public static string? AccountLabelForAddress(string? address)
    {
        if (address is null || string.IsNullOrEmpty(address))
        {
            return null;
        }

        var at = address.LastIndexOf('@');
        if (at < 0 || at == address.Length - 1)
        {
            return null;
        }

        var domain = address[(at + 1)..];

        // Empty pieces kept, so a domain that starts with a dot is malformed rather than silently
        // read as the piece after it.
        var first = domain.Split('.', StringSplitOptions.None)[0];
        if (first.Length == 0 || !first.Any(char.IsLetter))
        {
            return null;
        }

        return char.ToUpperInvariant(first[0]) + first[1..];
    }

    /// <summary>
    /// A name per profile id, with the whole address standing in wherever two accounts would
    /// otherwise be called the same thing. Only the profiles that actually collide pay the longer
    /// name.
    /// </summary>
    public static Dictionary<string, string> DisplayNames(IReadOnlyList<ClaudeProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var sharing = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            sharing[profile.DisplayName] = sharing.GetValueOrDefault(profile.DisplayName) + 1;
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            var name = profile.DisplayName;
            if (sharing[name] > 1 && profile.SignedInAddress() is { } address)
            {
                names[profile.Id] = $"Claude {address}";
            }
            else
            {
                names[profile.Id] = name;
            }
        }

        return names;
    }

    /// <summary>Which tool the credential is borrowed from, said so two Claude rows can be told apart.</summary>
    public string SourceName => this.Slug is null ? "Claude Code" : $"Claude Code in {this.DisplayPath}";

    /// <summary>The command that signs this profile in. Quoted, because this is *run*, not printed.</summary>
    public string SignInCommand
    {
        get
        {
            if (this.Slug is null)
            {
                return "claude";
            }

            var path = "'" + this.ConfigDirectory.Replace("'", "'\"'\"'") + "'";
            return $"CLAUDE_CONFIG_DIR={path} claude";
        }
    }

    /// <summary>Who is signed in, read from that file.</summary>
    public string? SignedInAddress() =>
        AccountFileCache.Account(this.AccountFileUrl, this._fileSystem) is { EmailAddress.Length: > 0 } account
            ? account.EmailAddress
            : null;

    /// <summary>
    /// Which Anthropic organization this profile's account belongs to — the one fact that can tie a
    /// Claude Desktop cache entry to a Claude Code profile.
    /// </summary>
    public string? OrganizationId() =>
        AccountFileCache.Account(this.AccountFileUrl, this._fileSystem) is { OrganizationUuid.Length: > 0 } account
            ? account.OrganizationUuid
            : null;

    /// <summary>
    /// The kind of organization this profile's account belongs to, as Claude Code recorded it —
    /// <c>claude_enterprise</c> comes back as <c>enterprise</c>. For a reading taken from Claude
    /// Desktop's cache, which carries no plan of its own.
    /// </summary>
    public string? OrganizationPlan()
    {
        if (AccountFileCache.Account(this.AccountFileUrl, this._fileSystem) is not { OrganizationType: { } type }
            || NonEmptyPlan(type) is not { } plan)
        {
            return null;
        }

        const string prefix = "claude_";
        return plan.StartsWith(prefix, StringComparison.Ordinal)
            ? NonEmptyPlan(plan[prefix.Length..])
            : plan;
    }

    /// <summary>Which Anthropic *account* this profile is signed in to.</summary>
    public string? AccountId() =>
        AccountFileCache.Account(this.AccountFileUrl, this._fileSystem) is { AccountUuid.Length: > 0 } account
            ? account.AccountUuid
            : null;

    /// <summary>Nil when this would be an empty plan line on the card.</summary>
    internal static string? NonEmptyPlan(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>The part of Claude Code's own record of the account that is read here.</summary>
    public sealed record ClaudeAccount(
        string? EmailAddress,
        string? OrganizationUuid,
        string? AccountUuid,
        string? OrganizationType);

    /// <summary>
    /// One decode of a profile's <c>.claude.json</c>, held until the file changes.
    ///
    /// Worth a cache because that file is not a small one: Claude Code keeps per-project prompt
    /// history in it, so on a machine with a long history it runs to tens or hundreds of megabytes —
    /// and this is on the polling path. The gate is modification date and size, resting on the same
    /// fact the credential cache states outright: re-reading an unchanged item cannot produce a
    /// different answer. A nil is cached too, and deliberately: failing to find an account is the
    /// case that costs a full decode to learn nothing, and nothing about it can change until the
    /// file does.
    /// </summary>
    private static class AccountFileCache
    {
        private static readonly object Gate = new();

        private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

        private sealed record Entry(DateTimeOffset Modified, long Size, ClaudeAccount? Account);

        public static ClaudeAccount? Account(string path, IClaudeFileSystem fileSystem)
        {
            if (fileSystem.Stamp(path) is not { } stamp)
            {
                // No file at all: signed out, or never signed in. Forget whatever was held rather
                // than going on answering from a file that is gone.
                lock (Gate)
                {
                    Entries.Remove(path);
                }

                return null;
            }

            lock (Gate)
            {
                if (Entries.TryGetValue(path, out var held)
                    && held.Modified == stamp.Modified
                    && held.Size == stamp.Size)
                {
                    return held.Account;
                }
            }

            // Decoded outside the lock: it is the slow call in here, and holding the lock across it
            // would queue every other profile behind whichever one reached it first.
            var account = Decode(fileSystem.Read(path, int.MaxValue));

            lock (Gate)
            {
                Entries[path] = new Entry(stamp.Modified, stamp.Size, account);
            }

            return account;
        }

        private static ClaudeAccount? Decode(byte[] data)
        {
            if (data.Length == 0)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(data);
                if (!document.RootElement.TryGetProperty("oauthAccount", out var account)
                    || account.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                return new ClaudeAccount(
                    String(account, "emailAddress"),
                    String(account, "organizationUuid"),
                    String(account, "accountUuid"),
                    String(account, "organizationType"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? String(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
