namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Core.Settings;
using QuotaDeck.Readers.Claude;
using QuotaDeck.Readers.Shared;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// One reader, every fake it can be given, and the fixtures the tests share: a fixed clock inside
/// the fixture entries' own week, a home directory a test controls, and an account file for the
/// default profile. Nothing here touches the network, a process, the keychain or the real home.
/// </summary>
public sealed class ClaudeRig
{
    /// <summary>2026-09-09T16:20:00Z — eight minutes after the fixture entry's `Date:`.</summary>
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 9, 9, 16, 20, 0, TimeSpan.Zero));

    public FakeTransport Http { get; } = new();

    public FakeProcessRunner Process { get; } = new();

    public FakeKeychain Keychain { get; } = new();

    public FakeClaudeFileSystem Files { get; } = new();

    public InMemoryReaderStateStore Store { get; } = new();

    public ReaderConfig Config { get; } = new();

    public string Home { get; } = Path.Combine(Path.GetTempPath(), "quotadeck-claude-" + Guid.NewGuid().ToString("N"));

    public string DataDirectory => Path.Combine(this.Home, "quota-data");

    public string Account { get; } = "testaccount";

    /// <summary>The cache directory the fixtures' entries land in.</summary>
    public string CacheDirectory => ClaudeDesktopUsageCache.DefaultDirectory(this.Home);

    /// <summary>The service a profile's token is filed under.</summary>
    public string DefaultSuffixedService =>
        ClaudeProfile.DefaultKeychainService + "-" + ClaudeProfile.KeychainSuffixForPath(this.ConfigDirectory);

    public string ConfigDirectory => Path.Combine(this.Home, ".claude");

    /// <summary>The default profile, over the fake filesystem.</summary>
    public ClaudeProfile Profile => ClaudeProfile.Default(this.Home, this.Files, this.Account);

    /// <summary>A <c>.claude.json</c> for the default profile — beside <c>~/.claude</c>.</summary>
    public void AddAccountFile(
        string? address = "user@example.com",
        string? organization = DesktopEntry.Organization,
        string? organizationType = null,
        DateTimeOffset? modified = null)
    {
        var fields = new List<string>();
        if (address is not null)
        {
            fields.Add($"\"emailAddress\":\"{address}\"");
        }

        if (organization is not null)
        {
            fields.Add($"\"organizationUuid\":\"{organization}\"");
        }

        if (organizationType is not null)
        {
            fields.Add($"\"organizationType\":\"{organizationType}\"");
        }

        this.Files.AddFile(
            Path.Combine(this.Home, ".claude.json"),
            System.Text.Encoding.UTF8.GetBytes($"{{\"oauthAccount\":{{{string.Join(",", fields)}}}}}"),
            modified ?? new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// One Desktop cache entry. Dated by its file's modification time unless a test passes a
    /// `Date:` header: the timestamp a test can control is the one the reading is judged on, and a
    /// pinned header would quietly turn every age into the same eight minutes.
    /// </summary>
    public void AddDesktopEntry(
        byte[]? bytes = null,
        TimeSpan? age = null,
        string name = "entry_0",
        string? key = null,
        string? responseDate = null)
    {
        var modified = this.Clock.Now - (age ?? TimeSpan.Zero);
        this.Files.AddFile(
            Path.Combine(this.CacheDirectory, name),
            bytes ?? DesktopEntry.Build(key: key, responseDate: responseDate),
            modified);
    }

    /// <summary>A credential under the default profile's suffixed service.</summary>
    public FakeKeychain.FakeItem AddCredential(
        string accessToken = "token",
        DateTimeOffset? expiresAt = null,
        string? subscriptionType = "max",
        DateTimeOffset? modified = null,
        string? service = null) =>
        this.Keychain.AddCredential(
            service ?? this.DefaultSuffixedService,
            accessToken,
            expiresAt ?? this.Clock.Now.AddHours(8),
            modified ?? this.Clock.Now.AddMinutes(-1),
            subscriptionType,
            this.Account);

    public ClaudeReader Reader(Action<ClaudeReaderOptionsBuilder>? configure = null)
    {
        var builder = new ClaudeReaderOptionsBuilder
        {
            Context = new ReaderContext
            {
                Config = this.Config,
                StateStore = this.Store,
                TimeProvider = this.Clock,
                DataDirectory = this.DataDirectory,
                Http = this.Http,
                Process = this.Process,
            },
            HomeDirectory = this.Home,
            Keychain = this.Keychain,
            FileSystem = this.Files,
            Profile = this.Profile,

            // No CLI and no token refresher binary by default: these are tests, and finding the
            // machine's own Claude Code would make them depend on what is installed rather than on
            // the code under test.
            CliFactory = () => null,
            RefreshCliFactory = () => null,
        };
        configure?.Invoke(builder);
        return new ClaudeReader(builder.Build());
    }

    /// <summary>A mutable way to build the options record for one test.</summary>
    public sealed class ClaudeReaderOptionsBuilder
    {
        public ReaderContext? Context { get; set; }

        /// <summary>Replaces the scripted transport, for a response it cannot express.</summary>
        public IHttpTransport? Http { get; set; }

        public string? HomeDirectory { get; set; }

        public IKeychain? Keychain { get; set; }

        public IClaudeFileSystem? FileSystem { get; set; }

        public ClaudeProfile? Profile { get; set; }

        public Func<IClaudeUsageCli?>? CliFactory { get; set; }

        public Func<string?>? RefreshCliFactory { get; set; }

        public TimeSpan? DesktopFreshness { get; set; }

        public TimeSpan? LiveDesktopFreshness { get; set; }

        public TimeSpan? DesktopRescanInterval { get; set; }

        public TimeSpan? CliRefreshInterval { get; set; }

        public TimeSpan? LiveCliRefreshInterval { get; set; }

        public TimeSpan? MinEndpointInterval { get; set; }

        public TimeSpan? LiveMinEndpointInterval { get; set; }

        public ClaudeReaderOptions Build()
        {
            var context = this.Context ?? throw new InvalidOperationException("the rig's context is required");
            if (this.Http is { } http)
            {
                context = context with { Http = http };
            }

            return new ClaudeReaderOptions
            {
                Context = context,
                HomeDirectory = this.HomeDirectory,
                Keychain = this.Keychain,
                FileSystem = this.FileSystem,
                Profile = this.Profile,
                CliFactory = this.CliFactory,
                RefreshCliFactory = this.RefreshCliFactory,
                DesktopFreshness = this.DesktopFreshness,
                LiveDesktopFreshness = this.LiveDesktopFreshness,
                DesktopRescanInterval = this.DesktopRescanInterval,
                CliRefreshInterval = this.CliRefreshInterval,
                LiveCliRefreshInterval = this.LiveCliRefreshInterval,
                MinEndpointInterval = this.MinEndpointInterval,
                LiveMinEndpointInterval = this.LiveMinEndpointInterval,
            };
        }
    }
}
