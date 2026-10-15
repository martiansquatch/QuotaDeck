namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Core.Readers;
using QuotaDeck.Readers.Claude;

/// <summary>
/// `claude "/usage"` is asked before the endpoint, because Claude Code files a new keychain item on
/// every token rotation and a grant against the old one stops working about an hour later. These pin
/// what its output means, where the binary is found, and where it is run.
///
/// Ported from Codenotch <c>Tests/ClaudeUsageCLITests.swift</c>, with the fixture's `Asia/Jakarta`
/// swapped for `Europe/Berlin` and the tool-naming prose line dropped.
/// </summary>
public class ClaudeUsageCliTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);

    /// <summary>A real answer: the two limit lines, then Claude Code describing what drove them.</summary>
    private const string Live = """
    You are currently using your subscription to power your Claude Code usage

    Current session: 38% used · resets Sep 7 at 2:59pm (Europe/Berlin)
    Current week (all models): 4% used · resets Sep 14 at 5:59am (Europe/Berlin)

    What's contributing to your limits usage?
    Approximate, based on local sessions on this machine — does not include other devices.

    Last 24h · 268 requests · 3 sessions
      37% of your usage was at >150k context
    """;

    private static DateTimeOffset Utc(string text) => DateTimeOffset.Parse(
        text, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    [Fact]
    public void ANamedTierAboveTheWindowsIsRead()
    {
        const string Extra = """
        You are currently using your extra usage to power your Claude Code usage

        Current session: 10% used · resets Sep 7 at 2:59pm (Europe/Berlin)
        """;

        Assert.Equal("extra usage", ClaudeUsageCli.Plan(Extra));
        Assert.Null(ClaudeUsageCli.Plan(Live));
        Assert.Equal("Max 5x", ClaudeUsageCli.Plan("""
        You are currently using Max 5x

        Current session: 1% used · resets Sep 7 at 2:59pm
        """));
    }

    [Fact]
    public void ItReadsBothWindowsOffARealAnswer()
    {
        var windows = ClaudeUsageCli.Parse(Live, Now);

        Assert.Equal(["session", "weekly_all"], windows.Select(window => window.Id));
        Assert.Equal(["Current session", "All models"], windows.Select(window => window.Label));
    }

    [Fact]
    public void APercentageBecomesAFraction()
    {
        var windows = ClaudeUsageCli.Parse(Live, Now);

        Assert.Equal(0.38, windows[0].UsedFraction!.Value, 4);
        Assert.Equal(0.04, windows[1].UsedFraction!.Value, 4);
    }

    /// <summary>The prose underneath mentions percentages of its own; read as limits they would be nonsense.</summary>
    [Fact]
    public void TheProseUnderneathIsNotMistakenForALimit()
    {
        Assert.Equal(2, ClaudeUsageCli.Parse(Live, Now).Count);
    }

    /// <summary>Without the session window there is no headline; the caller falls back to the token path.</summary>
    [Fact]
    public void AnAnswerWithoutASessionIsRefused()
    {
        const string Text = "Current week (all models): 4% used · resets Sep 14 at 5:59am (Europe/Berlin)";

        var error = Assert.Throws<ReaderException>(() => ClaudeUsageCli.Parse(Text, Now));
        Assert.Equal(ReaderErrorKind.BadResponse, error.Error.Kind);
    }

    [Fact]
    public void UnrecognisedOutputIsRefusedRatherThanGuessedAt()
    {
        Assert.Throws<ReaderException>(() => ClaudeUsageCli.Parse("Please run /login first", Now));
    }

    /// <summary>The per-model weekly windows use the endpoint's own vocabulary, so one label table serves both sources.</summary>
    [Fact]
    public void PerModelWeeklyWindowsKeepTheEndpointsNames()
    {
        const string Text = """
        Current session: 10% used · resets Sep 7 at 2:59pm (Europe/Berlin)
        Current week (Opus): 12% used · resets Sep 14 at 5:59am (Europe/Berlin)
        """;

        var windows = ClaudeUsageCli.Parse(Text, Now);

        Assert.Equal(["session", "weekly_opus"], windows.Select(window => window.Id));
        Assert.Equal("Opus", windows[1].Label);
        Assert.Equal(ClaudeUsageResponse.LabelForKind("weekly_opus"), windows[1].Label);
    }

    /// <summary>A percentage that parsed is worth keeping even when the date beside it did not.</summary>
    [Fact]
    public void AWindowSurvivesAnUnreadableResetDate()
    {
        const string Text = "Current session: 38% used · resets whenever it feels like it";
        var window = Assert.Single(ClaudeUsageCli.Parse(Text, Now));

        Assert.Equal(0.38, window.UsedFraction!.Value, 4);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void TheZoneInBracketsIsHonoured()
    {
        var windows = ClaudeUsageCli.Parse(Live, Now);

        // 2:59pm in Berlin (UTC+2 in September) is 12:59 UTC.
        Assert.Equal(Utc("2026-09-07T12:59:00Z"), windows[0].ResetsAt);
        Assert.Equal(TimeSpan.FromHours(5), windows[0].Duration);
        Assert.Equal(Utc("2026-09-14T03:59:00Z"), windows[1].ResetsAt);
    }

    /// <summary>No year is printed, so the nearest candidate over last/this/next year is chosen.</summary>
    [Fact]
    public void AJanuaryResetReadInDecemberBelongsToNextYear()
    {
        var reset = ClaudeUsageCli.ResetDate("Jan 2 at 3:00am (UTC)", Utc("2026-12-31T12:00:00Z"));

        Assert.Equal(Utc("2027-01-02T03:00:00Z"), reset);
    }

    [Fact]
    public void ADecemberResetReadInJanuaryBelongsToLastYear()
    {
        var reset = ClaudeUsageCli.ResetDate("Dec 31 at 3:00am (UTC)", Utc("2027-01-02T12:00:00Z"));

        Assert.Equal(Utc("2026-12-31T03:00:00Z"), reset);
    }

    /// <summary>On the hour the minutes are dropped: `3pm`, not `3:00pm`.</summary>
    [Fact]
    public void ATimeOnTheHourHasNoMinutesToRead()
    {
        var reset = ClaudeUsageCli.ResetDate("Sep 7 at 3pm (Europe/Berlin)", Now);

        Assert.Equal(Utc("2026-09-07T13:00:00Z"), reset);
    }

    [Fact]
    public void AWindowResettingOnTheHourKeepsItsResetDate()
    {
        const string Text = "Current session: 72% used · resets Sep 7 at 3pm (Europe/Berlin)";
        var window = Assert.Single(ClaudeUsageCli.Parse(Text, Now));

        Assert.Equal(Utc("2026-09-07T13:00:00Z"), window.ResetsAt);
    }

    [Fact]
    public void MidnightIsReadAsMidnight()
    {
        var reset = ClaudeUsageCli.ResetDate("Sep 8 at 12am (Europe/Berlin)", Now);

        // Midnight in Berlin is 22:00 UTC the day before.
        Assert.Equal(Utc("2026-09-07T22:00:00Z"), reset);
    }

    /// <summary>`America/...` carries an "am" of its own, so the zone comes off before the am/pm fix.</summary>
    [Fact]
    public void AZoneNameContainingAMDoesNotCorruptTheTime()
    {
        var reset = ClaudeUsageCli.ResetDate("Sep 7 at 2:59pm (America/Panama)", Now);

        // 2:59pm in Panama (UTC-5) is 19:59 UTC.
        Assert.Equal(Utc("2026-09-07T19:59:00Z"), reset);
    }

    [Fact]
    public void AnUnknownZoneFallsBackToTheLocalOne()
    {
        // "Mars/Olympus" is not a zone; the local zone is whatever the test host runs, so the reset
        // is still a moment rather than nil.
        Assert.NotNull(ClaudeUsageCli.ResetDate("Sep 7 at 2:59pm (Mars/Olympus)", Now));
    }

    // MARK: - Finding the binary

    [Fact]
    public void ItFindsClaudeWhereTheInstallerPutsIt()
    {
        var files = new FakeClaudeFileSystem();
        var home = "/tmp/quotadeck-claude-home";
        files.AddExecutable(Path.Combine(home, ".local/bin/claude"));

        Assert.Equal(Path.Combine(home, ".local/bin/claude"), ClaudeUsageCli.Locate(home, files, home));
    }

    [Fact]
    public void NoBinaryMeansNoCli()
    {
        var files = new FakeClaudeFileSystem();
        var home = "/tmp/quotadeck-claude-home";

        Assert.Null(ClaudeUsageCli.Locate(home, files, home));
    }

    /// <summary>A file at the right path that cannot be run is not an installation.</summary>
    [Fact]
    public void ANonExecutableFileIsNotAnInstallation()
    {
        var files = new FakeClaudeFileSystem();
        var home = "/tmp/quotadeck-claude-home";
        files.Files[Path.Combine(home, ".local/bin/claude")] = [];

        Assert.Null(ClaudeUsageCli.Locate(home, files, home));
    }

    [Fact]
    public void ItFindsClaudeInstalledUnderNvm()
    {
        var files = new FakeClaudeFileSystem();
        var home = "/tmp/quotadeck-claude-home";
        files.AddExecutable(Path.Combine(home, ".nvm/versions/node/v22.22.3/bin/claude"));

        Assert.NotNull(ClaudeUsageCli.Locate(home, files, home));
    }

    /// <summary>Upgrading Node leaves every older tree behind; only the current one is certainly the install run.</summary>
    [Fact]
    public void TheNewestNodeVersionWins()
    {
        var files = new FakeClaudeFileSystem();
        var home = "/tmp/quotadeck-claude-home";
        files.AddExecutable(Path.Combine(home, ".nvm/versions/node/v20.20.2/bin/claude"));
        files.AddExecutable(Path.Combine(home, ".nvm/versions/node/v22.22.3/bin/claude"));

        var found = ClaudeUsageCli.Locate(home, files, home);

        Assert.Contains("v22.22.3", found);
    }

    [Theory]
    [InlineData(".volta/bin/claude")]
    [InlineData("Library/pnpm/claude")]
    [InlineData(".npm-global/bin/claude")]
    [InlineData(".bun/bin/claude")]
    [InlineData(".claude/local/claude")]
    public void ItFindsClaudeInEachInstallersOwnDirectory(string relative)
    {
        var files = new FakeClaudeFileSystem();
        var home = "/tmp/quotadeck-claude-home";
        files.AddExecutable(Path.Combine(home, relative));

        Assert.NotNull(ClaudeUsageCli.Locate(home, files, home));
    }

    /// <summary>The native installer still wins when both are present.</summary>
    [Fact]
    public void TheNativeInstallerOutranksANodeManager()
    {
        var files = new FakeClaudeFileSystem();
        var home = "/tmp/quotadeck-claude-home";
        files.AddExecutable(Path.Combine(home, ".nvm/versions/node/v22.22.3/bin/claude"));
        files.AddExecutable(Path.Combine(home, ".local/bin/claude"));

        Assert.Equal(Path.Combine(home, ".local/bin/claude"), ClaudeUsageCli.Locate(home, files, home));
    }

    [Fact]
    public void TheSystemPathsAreLookedAtLast()
    {
        var files = new FakeClaudeFileSystem();
        var home = "/tmp/quotadeck-claude-home";
        files.AddExecutable("/opt/homebrew/bin/claude");

        Assert.Equal("/opt/homebrew/bin/claude", ClaudeUsageCli.Locate(home, files, "/"));
    }

    // MARK: - Where it runs

    /// <summary>One directory, reused: a fresh one per call left a project folder behind every poll.</summary>
    [Fact]
    public void TheScratchDirectoryIsOneFixedPlace()
    {
        var files = new FakeClaudeFileSystem();
        var data = "/tmp/quotadeck-claude-data";

        var first = ClaudeUsageCli.ScratchDirectory(data, files);
        var second = ClaudeUsageCli.ScratchDirectory(data, files);

        Assert.Equal(first, second);
        Assert.Equal(Path.Combine(data, "usage-scratch"), first);
        Assert.True(files.IsDirectory(first));
    }

    [Fact]
    public void TheArgumentsAreTheOnesThatKeepClaudeCodeQuiet()
    {
        Assert.Contains("--print", ClaudeUsageCli.Arguments);
        Assert.Contains("--no-session-persistence", ClaudeUsageCli.Arguments);
        Assert.Contains("--strict-mcp-config", ClaudeUsageCli.Arguments);
        Assert.Equal("/usage", ClaudeUsageCli.Arguments[^1]);

        // `--mcp-config` beside `--strict-mcp-config` would eat `/usage` as a second config path.
        Assert.DoesNotContain("--mcp-config", ClaudeUsageCli.Arguments);
    }

    [Fact]
    public void RunningItUsesTheScratchDirectoryAndAPrintModeCommandLine()
    {
        var files = new FakeClaudeFileSystem();
        var process = new QuotaDeck.Readers.Tests.Support.FakeProcessRunner();
        var binary = "/opt/homebrew/bin/claude";
        process.Respond(binary, 0, "Current session: 1% used · resets Sep 7 at 3pm (UTC)\n");
        var cli = new ClaudeUsageCli(binary, "/tmp/quotadeck-claude-data", process, files);

        var reading = cli.ReadWithPlanAsync(
            ClaudeProfile.Default("/tmp/quotadeck-claude-home", files), Now, CancellationToken.None).GetAwaiter().GetResult();

        var request = Assert.Single(process.Requests);
        Assert.Equal(binary, request.FileName);
        Assert.Equal(ClaudeUsageCli.Arguments, request.Arguments);
        Assert.Equal("/tmp/quotadeck-claude-data/usage-scratch", request.WorkingDirectory);
        Assert.Equal("/tmp/quotadeck-claude-data/usage-scratch", request.Environment!["PWD"]);
        Assert.False(request.CaptureStandardError);
        Assert.Equal(TimeSpan.FromSeconds(20), request.Timeout);

        // The default profile must not carry CLAUDE_CONFIG_DIR: pointing it at `~/.claude` is not the
        // same as leaving it unset.
        Assert.False(request.Environment!.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Single(reading.Windows);
    }

    [Fact]
    public void ANamedProfileCarriesItsConfigDirectory()
    {
        var files = new FakeClaudeFileSystem();
        var process = new QuotaDeck.Readers.Tests.Support.FakeProcessRunner();
        var binary = "/opt/homebrew/bin/claude";
        process.Respond(binary, 0, "Current session: 1% used · resets Sep 7 at 3pm (UTC)\n");
        var cli = new ClaudeUsageCli(binary, "/tmp/quotadeck-claude-data", process, files);
        var profile = new ClaudeProfile("work", "/tmp/quotadeck-claude-home/.claude-work", files, "testaccount");

        _ = cli.ReadWithPlanAsync(profile, Now, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(
            "/tmp/quotadeck-claude-home/.claude-work",
            Assert.Single(process.Requests).Environment!["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void ANonZeroExitIsNeedsAuth()
    {
        var files = new FakeClaudeFileSystem();
        var process = new QuotaDeck.Readers.Tests.Support.FakeProcessRunner();
        var binary = "/opt/homebrew/bin/claude";
        process.Respond(binary, 1, string.Empty);
        var cli = new ClaudeUsageCli(binary, "/tmp/quotadeck-claude-data", process, files);

        var error = Assert.Throws<ReaderException>(() => cli
            .ReadWithPlanAsync(ClaudeProfile.Default("/tmp/quotadeck-claude-home", files), Now, CancellationToken.None)
            .GetAwaiter().GetResult());
        Assert.Equal(ReaderErrorKind.NeedsAuth, error.Error.Kind);
    }

    [Fact]
    public void AnEmptyAnswerIsBadResponse()
    {
        var files = new FakeClaudeFileSystem();
        var process = new QuotaDeck.Readers.Tests.Support.FakeProcessRunner();
        var binary = "/opt/homebrew/bin/claude";
        process.Respond(binary, 0, string.Empty);
        var cli = new ClaudeUsageCli(binary, "/tmp/quotadeck-claude-data", process, files);

        var error = Assert.Throws<ReaderException>(() => cli
            .ReadWithPlanAsync(ClaudeProfile.Default("/tmp/quotadeck-claude-home", files), Now, CancellationToken.None)
            .GetAwaiter().GetResult());
        Assert.Equal(ReaderErrorKind.BadResponse, error.Error.Kind);
    }
}
