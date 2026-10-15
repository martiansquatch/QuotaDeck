namespace QuotaDeck.Readers.Tests.Claude;

using System.Net;
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

        // Each step is probed on its own first, so a failure names itself and the PR can say which
        // source answered. The two slow probes run before the cache probe: Claude Desktop rewrites
        // its entry while a `claude` subprocess starts, and the desktop answer is the one that has
        // to be current for the comparison below to mean anything.
        var cli = await this.ProbeCliAsync(home, profile, fileSystem, process);
        var endpoint = await this.ProbeEndpointAsync(profile, keychain, process);
        var desktop = await this.ProbeDesktopAsync(home, profile, fileSystem);
        this._output.WriteLine($"live: cli step: {cli}");
        this._output.WriteLine($"live: endpoint step: {endpoint}");
        this._output.WriteLine($"live: desktop step: {desktop}");

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
        var ids = string.Join(", ", snapshot.Windows.Select(window => window.Id));
        this._output.WriteLine($"live: reader status {snapshot.Status.Kind}, windows [{ids}]");

        var answered = new[] { desktop, cli, endpoint }
            .FirstOrDefault(step => step.Contains($"[{ids}]", StringComparison.Ordinal));
        this._output.WriteLine($"live: reader answered from: {answered ?? "a source that moved between probes"}");

        Assert.Contains(snapshot.Windows, window => window.Id == "session");
    }

    private async Task<string> ProbeDesktopAsync(string home, ClaudeProfile profile, IClaudeFileSystem fileSystem)
    {
        try
        {
            if (profile.OrganizationId() is not { } organization)
            {
                return "none (no organization recorded)";
            }

            var now = DateTimeOffset.UtcNow;
            var reading = new ClaudeDesktopUsageCache(ClaudeDesktopUsageCache.DefaultDirectory(home), fileSystem)
                .Read(organization, now);
            if (reading is not { Windows.Count: > 0 })
            {
                return "none (no usable entry)";
            }

            // Age and staleness only: no percentage, no reset time, no account.
            var age = (int)(now - reading.CapturedAt).TotalMinutes;
            var expired = ClaudeReader.HasExpiredWindow(reading.Windows, now) ? "expired-window" : "live-windows";
            return $"desktop [{string.Join(", ", reading.Windows.Select(window => window.Id))}] "
                + $"age {age}m, {expired}";
        }
        catch (Exception exception)
        {
            return $"none ({exception.GetType().Name})";
        }
    }

    private async Task<string> ProbeCliAsync(
        string home, ClaudeProfile profile, IClaudeFileSystem fileSystem, IProcessRunner process)
    {
        try
        {
            if (ClaudeUsageCli.Locate(home, fileSystem) is not { } binary)
            {
                return "none (no binary found)";
            }

            var cli = new ClaudeUsageCli(
                binary, Path.Combine(Path.GetTempPath(), "quotadeck-claude-live-scratch"), process, fileSystem);
            var reading = await cli.ReadWithPlanAsync(profile, DateTimeOffset.UtcNow, CancellationToken.None);
            return reading.Windows.Count > 0
                ? $"cli [{string.Join(", ", reading.Windows.Select(window => window.Id))}]"
                : "none (no windows)";
        }
        catch (Exception exception)
        {
            return $"none ({exception.GetType().Name})";
        }
    }

    private async Task<string> ProbeEndpointAsync(
        ClaudeProfile profile, IKeychain keychain, IProcessRunner process)
    {
        try
        {
            var credentials = await new ClaudeKeychain(
                profile.KeychainServices, profile.Account, keychain, process, TimeProvider.System).LoadAsync();
            if (credentials.AccessToken.Length == 0)
            {
                return "none (empty token)";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ClaudeReader.EndpointUrl));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credentials.AccessToken);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            using var response = await new HttpClientTransport().SendAsync(
                request, TimeSpan.FromSeconds(15), CancellationToken.None);
            if ((int)response.StatusCode != 200)
            {
                return $"none (HTTP {(int)response.StatusCode})";
            }

            var windows = ClaudeUsageResponse
                .Decode(await response.Content.ReadAsStringAsync(CancellationToken.None))
                .LimitWindows(DateTimeOffset.UtcNow);
            return windows.Count > 0
                ? $"endpoint [{string.Join(", ", windows.Select(window => window.Id))}]"
                : "none (no windows)";
        }
        catch (Exception exception)
        {
            return $"none ({exception.GetType().Name})";
        }
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
