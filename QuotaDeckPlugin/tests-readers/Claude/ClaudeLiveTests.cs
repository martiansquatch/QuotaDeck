namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Readers.Claude;
using QuotaDeck.Readers.Shared;
using QuotaDeck.Readers.Tests.Support;
using Xunit.Abstractions;

/// <summary>
/// The real chain on the machine this runs on: Claude Desktop's cache, then <c>claude /usage</c>,
/// then the keychain and the endpoint, with the real transport, the real process runner and the real
/// keychain. Skipped unless <c>QUOTADECK_LIVE_CLAUDE=1</c>, because it depends on what is signed in
/// here — which is exactly what it is there to check once.
///
/// It asserts one thing only: a reading came back with a <c>session</c> window. What it *reports* is
/// which step answered and which window ids arrived — never percentages, dates, addresses or account
/// ids.
/// </summary>
public sealed class ClaudeLiveTests
{
    private readonly ITestOutputHelper _output;

    public ClaudeLiveTests(ITestOutputHelper output) => this._output = output;

    [LiveFact]
    public async Task TheRealChainAnswersWithASessionWindow()
    {
        var home = ClaudeProfile.HomeDirectory;
        var fileSystem = new SystemClaudeFileSystem();
        var keychain = new MacKeychain();
        var process = new QuotaDeck.Readers.Shared.ProcessRunner();
        var profile = ClaudeProfile.Default(home, fileSystem);

        var step = await this.WhichStepAnswersAsync(home, profile, fileSystem, keychain, process);
        this._output.WriteLine($"live: step that answered: {step}");

        var reader = new ClaudeReader(new ClaudeReaderOptions
        {
            Context = new ReaderContext
            {
                Config = new QuotaDeck.Core.Settings.ReaderConfig(),
                StateStore = new InMemoryReaderStateStore(),
                TimeProvider = TimeProvider.System,
                DataDirectory = Path.Combine(Path.GetTempPath(), "quotadeck-claude-live-" + Guid.NewGuid().ToString("N")),
                Http = new HttpClientTransport(),
                Process = process,
            },
            HomeDirectory = home,
            Keychain = keychain,
            FileSystem = fileSystem,
        });

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        this._output.WriteLine(
            $"live: reader status {snapshot.Status.Kind}, windows [{string.Join(", ", snapshot.Windows.Select(window => window.Id))}]");

        Assert.Contains(snapshot.Windows, window => window.Id == "session");
    }

    /// <summary>
    /// The three steps, in the reader's own order, each answering "could this source carry a
    /// reading right now?" — the report the PR asks for, and the reason a failure here names itself.
    /// </summary>
    private async Task<string> WhichStepAnswersAsync(
        string home,
        ClaudeProfile profile,
        IClaudeFileSystem fileSystem,
        IKeychain keychain,
        IProcessRunner process)
    {
        // Step A: Claude Desktop's cache.
        try
        {
            if (profile.OrganizationId() is { } organization)
            {
                var cache = new ClaudeDesktopUsageCache(
                    ClaudeDesktopUsageCache.DefaultDirectory(home), fileSystem);
                var reading = cache.Read(organization, DateTimeOffset.UtcNow);
                if (reading is { Windows.Count: > 0 })
                {
                    return $"desktop ({string.Join(", ", reading.Windows.Select(window => window.Id))})";
                }
            }
        }
        catch (Exception exception)
        {
            this._output.WriteLine($"live: desktop step failed: {exception.GetType().Name}");
        }

        // Step B: `claude /usage`.
        try
        {
            if (ClaudeUsageCli.Locate(home, fileSystem) is { } binary)
            {
                var cli = new ClaudeUsageCli(
                    binary, Path.Combine(Path.GetTempPath(), "quotadeck-claude-live-scratch"), process, fileSystem);
                var reading = await cli.ReadWithPlanAsync(profile, DateTimeOffset.UtcNow, CancellationToken.None);
                if (reading.Windows.Count > 0)
                {
                    return $"cli ({string.Join(", ", reading.Windows.Select(window => window.Id))})";
                }
            }
        }
        catch (Exception exception)
        {
            this._output.WriteLine($"live: cli step failed: {exception.GetType().Name}");
        }

        // Step C: the keychain and the endpoint.
        try
        {
            var credentials = await new ClaudeKeychain(
                profile.KeychainServices, profile.Account, keychain, process, TimeProvider.System)
                .LoadAsync();
            if (credentials.AccessToken.Length > 0)
            {
                return "endpoint (a token was readable; the request itself is the reader's to make)";
            }
        }
        catch (Exception exception)
        {
            this._output.WriteLine($"live: credential step failed: {exception.GetType().Name}/{exception.Message}");
        }

        return "none";
    }
}

/// <summary>A fact that only runs when the machine is asked for a live check.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("QUOTADECK_LIVE_CLAUDE") != "1")
        {
            this.Skip = "live test: set QUOTADECK_LIVE_CLAUDE=1 to run against the real chain";
        }
    }
}
