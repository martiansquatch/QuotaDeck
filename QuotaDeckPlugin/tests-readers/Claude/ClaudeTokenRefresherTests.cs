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

    // MARK: - One launch, whatever the interleaving

    /// <summary>
    /// Two fetches finishing together must not launch two `claude -p` processes for one expiry. The
    /// winner blocks inside its process while the rest arrive, and every one of them is told to leave.
    /// </summary>
    [Fact]
    public async Task ConcurrentRenewalTriggersLaunchOneProcess()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var process = new ScriptedProcessRunner
        {
            OnRun = _ =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return new ProcessResult { ExitCode = 1, StandardOutput = string.Empty };
            },
        };
        var refresher = Refresher(process, () => In(60), () => In(8 * 3600));

        try
        {
            var winner = Task.Run(() => refresher.ConsiderRenewingAsync(Now));
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "the renewal never started");

            // Seven more triggers, arriving while the first is mid-process.
            var losers = Enumerable.Range(0, 7)
                .Select(_ => refresher.ConsiderRenewingAsync(Now))
                .ToList();
            await Task.WhenAll(losers);

            Assert.True(refresher.IsRunning, "the winner's claim was lost while it ran");
            Assert.Single(process.Requests);

            release.Set();
            await winner;
            Assert.Single(process.Requests);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// The claim itself, hammered: a check-then-set pair leaves a window in which two callers both
    /// see 0, and a single atomic exchange cannot.
    /// </summary>
    [Fact]
    public void TheOverlapGuardHasExactlyOneWinnerPerRound()
    {
        const int Threads = 8;
        for (var round = 0; round < 500; round++)
        {
            var flag = 0;
            var winners = 0;
            using var gate = new Barrier(Threads);
            var threads = Enumerable.Range(0, Threads)
                .Select(_ => new Thread(() =>
                {
                    gate.SignalAndWait();
                    if (ClaudeTokenRefresher.TryClaim(ref flag))
                    {
                        Interlocked.Increment(ref winners);
                    }
                }))
                .ToList();

            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            Assert.Equal(1, winners);
        }
    }

    // MARK: - Through the reader

    /// <summary>
    /// The reader consults the gate after every fetch — its only timer — and the renewal runs on its
    /// own task once the reading is built.
    /// </summary>
    [Fact]
    public async Task TheReaderRenewsATokenThatIsAboutToExpire()
    {
        var rig = new ClaudeRig();
        var item = rig.AddCredential(expiresAt: rig.Clock.Now.AddMinutes(1));
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

        // The first fetch learns the expiry, and its finally then hands the gate its turn.
        _ = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);
        Assert.True(await process.WaitForRequestsAsync(1), "the renewal never launched");

        var request = Assert.Single(process.Requests);
        Assert.Equal("/opt/homebrew/bin/claude", request.FileName);
        Assert.Equal(["-p", "--no-session-persistence", "--strict-mcp-config"], request.Arguments);
        Assert.Equal(rig.DataDirectory + "/usage-scratch", request.WorkingDirectory);
        Assert.Equal(rig.DataDirectory + "/usage-scratch", request.Environment!["PWD"]);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);

        // The renewal was judged by the expiry moving, so the rotated token is the one on the wire —
        // and the 8-hour expiry means nothing to renew on the next tick. The process request is
        // recorded before the item is rewritten, so this waits for the renewal's own re-read rather
        // than for the launch.
        for (var attempt = 0; attempt < 200 && rig.Keychain.ReadCount < 2; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(2, rig.Keychain.ReadCount);
        _ = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);
        Assert.Equal("Bearer renewed", rig.Http.Requests[^1].Headers.GetValues("Authorization").Single());
        Assert.Single(process.Requests);
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
        Assert.True(await process.WaitForRequestsAsync(1), "the renewal never launched");
        _ = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);
        _ = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);

        // The same expiry value is never attempted twice, so the later ticks add nothing.
        Assert.False(await process.WaitForRequestsAsync(2, TimeSpan.FromMilliseconds(500)));
        Assert.Single(process.Requests);
    }

    /// <summary>
    /// A renewal can take its whole process timeout (30 s), and a fetch's deadline is 60: consulting
    /// the gate before the reading would let a slow `claude` start decide whether a reading arrives.
    /// The renewal runs on its own task, after the snapshot is built.
    /// </summary>
    [Fact]
    public async Task ASlowRenewalDoesNotDelayTheFetch()
    {
        var rig = new ClaudeRig();
        rig.AddCredential(expiresAt: rig.Clock.Now.AddMinutes(1));
        rig.Http.Respond(System.Net.HttpStatusCode.OK, """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z"}]}""");
        rig.Http.Respond(System.Net.HttpStatusCode.OK, """{"limits":[{"kind":"session","percent":42,"resets_at":"2099-01-01T00:00:00Z"}]}""");

        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var process = new ScriptedProcessRunner
        {
            OnRun = _ =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(30));
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

        try
        {
            // The first fetch learns the expiry and then — after its reading is built — hands the
            // gate its turn.
            var first = await reader.FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource);
            Assert.Equal(QuotaDeck.Core.Model.ProviderStatusKind.Ok, first.Status.Kind);

            // The renewal is under way and blocked inside its process...
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "the renewal never started");

            // ...and the next fetch still comes back promptly, because the renewal is not on its path.
            var second = await reader
                .FetchAsync(QuotaDeck.Core.Policy.Freshness.FromSource)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(QuotaDeck.Core.Model.ProviderStatusKind.Ok, second.Status.Kind);
        }
        finally
        {
            release.Set();
        }
    }
}
