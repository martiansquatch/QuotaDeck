namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Readers.Claude;

/// <summary>
/// The gate, the cooldown and the failure path around renewing the token.
///
/// Codenotch runs the refresher on a one-minute timer; QuotaDeck has no timer of its own, so the
/// reader consults the same pure gate at the top of every fetch. Ported from
/// <c>Tests/ClaudeTokenRefresherTests.swift</c>.
/// </summary>
public class ClaudeTokenRefresherTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 16, 20, 0, TimeSpan.Zero);

    private static DateTimeOffset In(int seconds) => Now.AddSeconds(seconds);

    // MARK: - The gate

    /// <summary>Under four minutes, and only then — the margin has to stay below Claude Code's own five.</summary>
    [Theory]
    [InlineData(8 * 60, false)]
    [InlineData(5 * 60, false)]
    [InlineData(3 * 60, true)]
    [InlineData(-60, true)]
    public void ItOnlyRunsInsideTheMargin(int remaining, bool expected)
    {
        Assert.Equal(
            expected,
            ClaudeTokenRefresher.ShouldRenew(
                In(remaining), Now, TimeSpan.FromMinutes(4), null, null, TimeSpan.FromMinutes(10)));
    }

    /// <summary>Never on a guess: no reading yet means no idea how long is left.</summary>
    [Fact]
    public void ItNeverRunsWithoutAnExpiry()
    {
        Assert.False(ClaudeTokenRefresher.ShouldRenew(
            null, Now, TimeSpan.FromMinutes(4), null, null, TimeSpan.FromMinutes(10)));
    }

    /// <summary>One attempt per token, ever: a launch that failed leaves the same value here next tick.</summary>
    [Fact]
    public void ATokenGetsOneAttempt()
    {
        var expiry = In(60);

        Assert.False(ClaudeTokenRefresher.ShouldRenew(
            expiry, Now, TimeSpan.FromMinutes(4), expiry, null, TimeSpan.FromMinutes(10)));

        // A different token is a different question.
        Assert.True(ClaudeTokenRefresher.ShouldRenew(
            In(120), Now, TimeSpan.FromMinutes(4), expiry, null, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void TheCooldownHoldsOffASecondLaunch()
    {
        Assert.False(ClaudeTokenRefresher.ShouldRenew(
            In(60), Now, TimeSpan.FromMinutes(4), null, Now.AddSeconds(-60), TimeSpan.FromMinutes(10)));
        Assert.True(ClaudeTokenRefresher.ShouldRenew(
            In(60), Now, TimeSpan.FromMinutes(4), null, Now.AddSeconds(-900), TimeSpan.FromMinutes(10)));
    }

    // MARK: - Running it

    private static ClaudeTokenRefresher Refresher(
        ScriptedProcessRunner process,
        Func<DateTimeOffset?> expiry,
        Func<DateTimeOffset?> after,
        string? cli = "/opt/homebrew/bin/claude") =>
        new(
            expiry: expiry,
            reload: _ => Task.FromResult(after()),
            cli: () => cli,
            process: process,
            scratchDirectory: () => "/tmp/quotadeck-claude-data/usage-scratch");

    [Fact]
    public async Task ItRenewsWhenTheExpiryMoves()
    {
        var process = new ScriptedProcessRunner();
        var refresher = Refresher(process, () => In(60), () => In(8 * 3600));

        await refresher.ConsiderRenewingAsync(Now);

        Assert.Equal(ClaudeRenewalState.Refreshed, refresher.Outcome.State);
        Assert.Equal(In(8 * 3600), refresher.Outcome.Until);
        Assert.Single(process.Requests);
    }

    /// <summary>Judged on the outcome, never on the exit status: refusing an empty prompt is non-zero and a renewal.</summary>
    [Fact]
    public async Task ANonZeroExitIsNotAFailureIfTheExpiryMoved()
    {
        var process = new ScriptedProcessRunner
        {
            OnRun = _ => new ProcessResult { ExitCode = 1, StandardOutput = "Input must be provided" },
        };
        var refresher = Refresher(process, () => In(60), () => In(8 * 3600));

        await refresher.ConsiderRenewingAsync(Now);

        Assert.Equal(ClaudeRenewalState.Refreshed, refresher.Outcome.State);
    }

    /// <summary>A launch that did not move the expiry stops, rather than looping every tick.</summary>
    [Fact]
    public async Task AFailedRenewalDoesNotTryTheSameTokenAgain()
    {
        var process = new ScriptedProcessRunner();
        var refresher = Refresher(process, () => In(60), () => In(60));

        await refresher.ConsiderRenewingAsync(Now);
        Assert.Equal(ClaudeRenewalState.Failed, refresher.Outcome.State);

        await refresher.ConsiderRenewingAsync(Now.AddMinutes(1));

        Assert.Single(process.Requests);
    }

    [Fact]
    public async Task NoClaudeCommandIsARenewalThatCannotRun()
    {
        var process = new ScriptedProcessRunner();
        var refresher = Refresher(process, () => In(60), () => null, cli: null);

        await refresher.ConsiderRenewingAsync(Now);

        Assert.Equal(ClaudeRenewalState.Failed, refresher.Outcome.State);
        Assert.Contains("isn't installed", refresher.Outcome.Message);
        Assert.Empty(process.Requests);
    }

    [Fact]
    public async Task NothingHappensFarFromExpiry()
    {
        var process = new ScriptedProcessRunner();
        var refresher = Refresher(process, () => In(8 * 3600), () => In(16 * 3600));

        await refresher.ConsiderRenewingAsync(Now);

        Assert.Equal(ClaudeRenewalState.Idle, refresher.Outcome.State);
        Assert.Empty(process.Requests);
    }

    // MARK: - Through the reader

    /// <summary>The reader consults the gate at the top of every fetch, which is its only timer.</summary>
    [Fact]
    public async Task TheReaderRenewsATokenThatIsAboutToExpire()
    {
        var rig = new ClaudeRig();
        var item = rig.AddCredential(expiresAt: rig.Clock.Now.AddMinutes(1));
        rig.Http.Respond(System.Net.HttpStatusCode.OK, """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z"}]}""");
        rig.Http.Respond(System.Net.HttpStatusCode.OK, """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z"}]}""");
        rig.Http.Respond(System.Net.HttpStatusCode.OK, """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z"}]}""");

        // The renewal Claude Code performs: the keychain item is rewritten with a later expiry.
        var process = new ScriptedProcessRunner
        {
            OnRun = _ =>
            {
                item.Modified = rig.Clock.Now;
                item.Data = FakeKeychain.Credential("renewed", rig.Clock.Now.AddHours(8));
                return new ProcessResult { ExitCode = 1, StandardOutput = string.Empty };
            },
        };

        var reader = rig.Reader(options =>
        {
            options.Context = options.Context! with { Process = process };
            options.RefreshCliFactory = () => "/opt/homebrew/bin/claude";
            options.MinEndpointInterval = TimeSpan.Zero;
            options.LiveMinEndpointInterval = TimeSpan.Zero;
        });

        // Nothing is known about the expiry yet, so the first tick cannot launch anything.
        _ = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);
        Assert.Empty(process.Requests);

        _ = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);

        var request = Assert.Single(process.Requests);
        Assert.Equal("/opt/homebrew/bin/claude", request.FileName);
        Assert.Equal(["-p", "--no-session-persistence", "--strict-mcp-config"], request.Arguments);
        Assert.Equal(rig.DataDirectory + "/usage-scratch", request.WorkingDirectory);
        Assert.Equal(rig.DataDirectory + "/usage-scratch", request.Environment!["PWD"]);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);

        // The renewal was judged by the expiry moving, so the rotated token is the one on the wire.
        _ = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);
        Assert.Equal("Bearer renewed", rig.Http.Requests[^1].Headers.GetValues("Authorization").Single());
    }

    /// <summary>A renewal whose expiry did not move is not retried on the next tick.</summary>
    [Fact]
    public async Task TheReaderDoesNotRelaunchForATokenThatDidNotRenew()
    {
        var rig = new ClaudeRig();
        rig.AddCredential(expiresAt: rig.Clock.Now.AddMinutes(1));
        rig.Http.Respond(System.Net.HttpStatusCode.OK, """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z"}]}""");
        rig.Http.Respond(System.Net.HttpStatusCode.OK, """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z"}]}""");
        rig.Http.Respond(System.Net.HttpStatusCode.OK, """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z"}]}""");
        var process = new ScriptedProcessRunner();
        var reader = rig.Reader(options =>
        {
            options.Context = options.Context! with { Process = process };
            options.RefreshCliFactory = () => "/opt/homebrew/bin/claude";
        });

        _ = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);
        _ = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);
        _ = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);

        Assert.Single(process.Requests);
    }
}
