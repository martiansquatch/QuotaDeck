namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Settings;

/// <summary>
/// The half of a reader every provider shares — Codenotch <c>Sources/Model/UsageStore.swift</c>'s
/// fetch catch path with <c>degraded(provider:error:)</c>, <c>status(for:)</c> and
/// <c>placeholder(_:)</c>, plus the per-provider rate-limit bookkeeping of
/// <c>Sources/Providers/ClaudeOAuthProvider.swift</c>: a fetch never throws, a success is remembered
/// and clears the hold-off, and a read that outlives the deadline reports no response.
/// </summary>
public class UsageReaderBaseTests
{
    private readonly InMemoryReaderStateStore _store = new();
    private readonly TestClock _clock = new(TestData.Now);

    public static TheoryData<ReaderError, ProviderStatus> ReaderFailures => new()
    {
        { ReaderError.NeedsAuth, ProviderStatus.NeedsAuth },
        { ReaderError.AccessDenied, ProviderStatus.AccessDenied },
        { ReaderError.CredentialExpired, ProviderStatus.Stale(TestData.Now) },
        { ReaderError.SignedOutByOwner, ProviderStatus.SignedOutByOwner },
        { ReaderError.TimedOut, ProviderStatus.Error("no response") },
        { ReaderError.BadResponse(503), ProviderStatus.Error("HTTP 503") },
        { ReaderError.ApiError("overloaded"), ProviderStatus.Error("overloaded") },
        { ReaderError.RateLimited(), ProviderStatus.Stale(TestData.Now) },
        { ReaderError.NothingMetered("free plan"), ProviderStatus.Unsupported("free plan") },
    };

    [Theory]
    [MemberData(nameof(ReaderFailures))]
    public async Task AReaderExceptionComesBackAsItsStatusAndNeverThrows(ReaderError error, ProviderStatus expected)
    {
        var reader = this.Reader();
        reader.OnRead = (_, _) => throw new ReaderException(error);

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(expected, snapshot.Status);
        Assert.Equal("test", snapshot.Id);
        Assert.Equal("Test provider", snapshot.DisplayName);
        Assert.Empty(snapshot.Windows);
    }

    /// <summary>The same mapping, reached by a failure thrown after the read had already started.</summary>
    [Fact]
    public async Task AFailureAfterTheReadStartsIsAlsoMapped()
    {
        var reader = this.Reader();
        reader.OnRead = async (_, _) =>
        {
            await Task.Yield();
            throw new ReaderException(ReaderError.TimedOut);
        };

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatus.Error("no response"), snapshot.Status);
    }

    [Fact]
    public async Task AnArbitraryExceptionBecomesAnError()
    {
        var reader = this.Reader();
        reader.OnRead = (_, _) => throw new InvalidOperationException("boom");

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatus.Error("boom"), snapshot.Status);
    }

    [Fact]
    public async Task ASuccessfulReadIsRememberedAndStamped()
    {
        var reader = this.Reader();
        reader.OnRead = (_, _) => Task.FromResult(TestData.SessionAndWeekly(0.42, 0.11));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(TestData.Now, snapshot.FetchedAt);

        var memory = this._store.Load("test");
        Assert.NotNull(memory);
        Assert.Equal(TestData.Now, memory!.FetchedAt);
        Assert.Equal(0.42, memory.Snapshot.UsedFraction!.Value, 10);
    }

    [Fact]
    public async Task AReadThatOutlivesTheDeadlineIsNoResponse()
    {
        var reader = this.Reader(deadline: TimeSpan.FromMilliseconds(30));
        var abandoned = new TaskCompletionSource<ProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.OnRead = (_, _) => abandoned.Task;

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatus.Error("no response"), snapshot.Status);
        Assert.Equal(1, reader.Calls);
        Assert.Empty(snapshot.Windows);

        // Release the abandoned read so it does not outlive the test.
        abandoned.SetResult(TestData.SessionAndWeekly(0.5, 0.5));
    }

    [Fact]
    public async Task ConsecutiveRateLimitsRaiseTheStoredBackoffAndASuccessClearsIt()
    {
        var reader = this.Reader();
        reader.OnRead = (_, _) => throw new ReaderException(ReaderError.RateLimited());

        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(60), this._store.LoadBackoffUntil("test"));

        this._clock.Now = TestData.Now.AddSeconds(61);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(61 + 120), this._store.LoadBackoffUntil("test"));

        // Past the second deadline, a success clears the hold-off and remembers the reading.
        this._clock.Now = TestData.Now.AddSeconds(61 + 121);
        reader.OnRead = (_, _) => Task.FromResult(TestData.SessionAndWeekly(0.2, 0.1));
        var snapshot = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
        Assert.Null(this._store.LoadBackoffUntil("test"));
        Assert.NotNull(this._store.Load("test"));

        // The ladder starts from the first rung again.
        reader.OnRead = (_, _) => throw new ReaderException(ReaderError.RateLimited());
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(this._clock.Now.AddSeconds(60), this._store.LoadBackoffUntil("test"));
    }

    /// <summary>
    /// The flat choice of Codex, Devin and Muse through the reader: a second 429 waits the same
    /// max(60 s, hint), never the ladder's second rung.
    /// </summary>
    [Fact]
    public async Task AFlatBackoffWaitsTheHintOnEveryRateLimitWithoutClimbing()
    {
        var reader = this.Reader(new ReaderConfig { RateLimitBackoff = RateLimitBackoff.Flat });
        reader.OnRead = (_, _) => throw new ReaderException(ReaderError.RateLimited(TimeSpan.FromSeconds(120)));

        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(120), this._store.LoadBackoffUntil("test"));

        this._clock.Now = TestData.Now.AddSeconds(121);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(121 + 120), this._store.LoadBackoffUntil("test"));

        // A success still clears it and starts the count over, exactly like the ladder.
        this._clock.Now = TestData.Now.AddSeconds(121 + 121);
        reader.OnRead = (_, _) => Task.FromResult(TestData.SessionAndWeekly(0.2, 0.1));
        await reader.FetchAsync(Freshness.Standard);
        Assert.Null(this._store.LoadBackoffUntil("test"));
    }

    [Fact]
    public async Task AFlatBackoffWithoutAHintWaitsTheMinuteOnEveryRateLimit()
    {
        var reader = this.Reader(new ReaderConfig { RateLimitBackoff = RateLimitBackoff.Flat });
        reader.OnRead = (_, _) => throw new ReaderException(ReaderError.RateLimited());

        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(60), this._store.LoadBackoffUntil("test"));

        this._clock.Now = TestData.Now.AddSeconds(61);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(61 + 60), this._store.LoadBackoffUntil("test"));
    }

    /// <summary>
    /// A flat wait never climbs, so it must not spend the ladder's rungs: a reader that later uses
    /// the ladder (a style switch here, the config's own reader in the field) starts at 60 s.
    /// </summary>
    [Fact]
    public async Task AFlatWaitDoesNotConsumeTheLaddersRungs()
    {
        var reader = this.Reader();
        reader.StyleOverride = RateLimitBackoff.Flat;
        reader.OnRead = (_, _) => throw new ReaderException(ReaderError.RateLimited());

        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(60), this._store.LoadBackoffUntil("test"));

        this._clock.Now = TestData.Now.AddSeconds(61);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(61 + 60), this._store.LoadBackoffUntil("test"));

        // The ladder's first rung is still 60 s, not 4 minutes.
        reader.StyleOverride = RateLimitBackoff.Ladder;
        this._clock.Now = TestData.Now.AddSeconds(61 + 61);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(61 + 61 + 60), this._store.LoadBackoffUntil("test"));
    }

    /// <summary>
    /// OpenCode's ladder caps the whole wait, hint included (OpenCodeProvider.swift:206-211):
    /// a one-hour hint is fifteen minutes, where the uncapped ladder honours it in full.
    /// </summary>
    [Fact]
    public async Task ACappedLadderBackoffCapsTheHintAtTheCeiling()
    {
        var reader = this.Reader(new ReaderConfig { RateLimitBackoff = RateLimitBackoff.LadderCapped });
        reader.OnRead = (_, _) => throw new ReaderException(ReaderError.RateLimited(TimeSpan.FromSeconds(3600)));

        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(900), this._store.LoadBackoffUntil("test"));

        // And it still climbs: the second 429 doubles (120 s), the hint still capped.
        this._clock.Now = TestData.Now.AddSeconds(901);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(901 + 900), this._store.LoadBackoffUntil("test"));
    }

    /// <summary>
    /// ReaderManaged skips the base's pre-read check entirely — the reader gets to try its other
    /// sources while the endpoint is held off — and a read that never consulted the held-off source
    /// replays the hold-off instead of reporting a new 429: no rung, no deadline write, and a dimmed
    /// reading keeps its own FetchedAt as the Stale since (ClaudeOAuthProvider.swift:303-309).
    /// </summary>
    [Fact]
    public async Task AReplayedHoldOffTakesNoRungAndKeepsTheOriginalSince()
    {
        var originalFetchedAt = TestData.Now.AddMinutes(-20);
        this._store.Seed("test", TestData.SessionAndWeekly(0.5, 0.4), originalFetchedAt);
        this._store.SaveBackoffUntil("test", TestData.Now.AddMinutes(30));
        var reader = this.Reader(holdOffScope: HoldOffScope.ReaderManaged);

        // No hint: were this treated as a new 429, the deadline would move to Now+60.
        reader.OnRead = (_, _) => throw new ReaderException(ReaderError.RateLimited());

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        // The read was called (that is the point of ReaderManaged) and the replay degraded the old
        // reading in place: the same number, the same since, the store untouched.
        Assert.Equal(1, reader.Calls);
        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Equal(originalFetchedAt, snapshot.Status.StaleSince);
        Assert.Equal(0.5, snapshot.UsedFraction!.Value, 10);
        Assert.Equal(originalFetchedAt, this._store.Load("test")!.FetchedAt);
        Assert.Equal(TestData.Now.AddMinutes(30), this._store.LoadBackoffUntil("test"));

        // A second replay sees exactly the same: no state from the first leaks into the next cycle.
        var second = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Stale, second.Status.Kind);
        Assert.Equal(originalFetchedAt, second.Status.StaleSince);
        Assert.Equal(originalFetchedAt, this._store.Load("test")!.FetchedAt);
        Assert.Equal(TestData.Now.AddMinutes(30), this._store.LoadBackoffUntil("test"));

        // And no rung was spent: a real 429 next is still the ladder's first.
        reader.OnRead = (_, _) =>
        {
            reader.MarkConsulted();
            throw new ReaderException(ReaderError.RateLimited());
        };
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(60), this._store.LoadBackoffUntil("test"));
    }

    /// <summary>
    /// A ReaderManaged read that DID consult the held-off source answers for it: a success resets
    /// the ladder and clears the deadline, exactly like a WholeRead reader's.
    /// </summary>
    [Fact]
    public async Task AConsultedSuccessResetsTheLadderAndClearsTheDeadline()
    {
        var reader = this.Reader(holdOffScope: HoldOffScope.ReaderManaged);
        reader.OnRead = (_, _) =>
        {
            reader.MarkConsulted();
            throw new ReaderException(ReaderError.RateLimited());
        };

        // Two real 429s climb the ladder: 60 s, then 120 s.
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(60), this._store.LoadBackoffUntil("test"));
        this._clock.Now = TestData.Now.AddSeconds(61);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(61 + 120), this._store.LoadBackoffUntil("test"));

        // A consulted success clears the deadline and resets the ladder. Still inside the hold-off,
        // so the read sees it for itself.
        this._clock.Now = TestData.Now.AddSeconds(61 + 61);
        bool? heldOffInRead = null;
        DateTimeOffset? deadlineInRead = null;
        reader.OnRead = (_, _) =>
        {
            heldOffInRead = reader.HeldOffNow();
            deadlineInRead = reader.HoldOffDeadline();
            reader.MarkConsulted();
            return Task.FromResult(TestData.SessionAndWeekly(0.4, 0.2));
        };
        var snapshot = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
        Assert.True(heldOffInRead);
        Assert.Equal(TestData.Now.AddSeconds(61 + 120), deadlineInRead);
        Assert.Null(this._store.LoadBackoffUntil("test"));

        // One more real 429 starts at the first rung again.
        reader.OnRead = (_, _) =>
        {
            reader.MarkConsulted();
            throw new ReaderException(ReaderError.RateLimited());
        };
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(61 + 61 + 60), this._store.LoadBackoffUntil("test"));
    }

    /// <summary>
    /// A reading that never asked the held-off source — a Desktop or CLI answer — is stored as
    /// last-good, but it says nothing about that source: the ladder and the deadline are unchanged,
    /// so the next real 429 is a rung further up, not the first.
    /// </summary>
    [Fact]
    public async Task AReturnedReadingWithoutAConsultedSourceKeepsTheLadderAndDeadline()
    {
        var reader = this.Reader(holdOffScope: HoldOffScope.ReaderManaged);
        reader.OnRead = (_, _) =>
        {
            reader.MarkConsulted();
            throw new ReaderException(ReaderError.RateLimited());
        };

        // Climb to the second rung so the counter's fate is observable.
        await reader.FetchAsync(Freshness.Standard);
        this._clock.Now = TestData.Now.AddSeconds(61);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(61 + 120), this._store.LoadBackoffUntil("test"));

        // The unmarked answer is a fresh reading and is remembered as such...
        this._clock.Now = TestData.Now.AddSeconds(61 + 121);
        reader.OnRead = (_, _) => Task.FromResult(TestData.SessionAndWeekly(0.3, 0.1));
        var snapshot = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
        Assert.Equal(this._clock.Now, this._store.Load("test")!.FetchedAt);

        // ...but it moved neither the ladder nor the deadline.
        Assert.Equal(TestData.Now.AddSeconds(61 + 120), this._store.LoadBackoffUntil("test"));
        reader.OnRead = (_, _) =>
        {
            reader.MarkConsulted();
            throw new ReaderException(ReaderError.RateLimited());
        };
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(this._clock.Now.AddSeconds(240), this._store.LoadBackoffUntil("test"));
    }

    /// <summary>A consulted 429 is the real thing: it takes the ladder's next rung.</summary>
    [Fact]
    public async Task AConsultedRateLimitTakesTheLadderRung()
    {
        var reader = this.Reader(holdOffScope: HoldOffScope.ReaderManaged);
        reader.OnRead = (_, _) =>
        {
            reader.MarkConsulted();
            throw new ReaderException(ReaderError.RateLimited());
        };

        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(60), this._store.LoadBackoffUntil("test"));

        // A replay in between spends nothing...
        this._clock.Now = TestData.Now.AddSeconds(61);
        reader.OnRead = (_, _) => throw new ReaderException(ReaderError.RateLimited());
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(60), this._store.LoadBackoffUntil("test"));

        // ...so the second real 429 is the second rung: 120 s.
        this._clock.Now = TestData.Now.AddSeconds(62);
        reader.OnRead = (_, _) =>
        {
            reader.MarkConsulted();
            throw new ReaderException(ReaderError.RateLimited());
        };
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(62 + 120), this._store.LoadBackoffUntil("test"));
    }

    [Fact]
    public async Task WhileHeldOffNoReadIsMadeAndTheReadingAges()
    {
        var reader = this.Reader();
        reader.OnRead = (_, _) => Task.FromResult(TestData.SessionAndWeekly(0.80, 0.40));
        var fresh = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(ProviderStatusKind.Ok, fresh.Status.Kind);

        // A refusal with a long hint: the hold-off outlives the freshness window.
        reader.OnRead = (_, _) => throw new ReaderException(ReaderError.RateLimited(TimeSpan.FromMinutes(30)));
        var refused = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(2, reader.Calls);

        // Recent, so still shown undimmed and with its own status.
        Assert.Equal(ProviderStatusKind.Ok, refused.Status.Kind);

        this._clock.Now = TestData.Now.AddMinutes(16);
        var heldOff = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(2, reader.Calls); // the endpoint was not asked again while the penalty stands
        Assert.Equal(ProviderStatusKind.Stale, heldOff.Status.Kind);
        Assert.Equal(TestData.Now, heldOff.Status.StaleSince);
        Assert.Equal(0.80, heldOff.UsedFraction!.Value, 10);
    }

    /// <summary>The spent week is attached at publication, not left to the drawing site: the
    /// remembered reading carries the block, the band is Exhausted, and the limit watcher reads the
    /// block as the weekly crossing it is.</summary>
    [Fact]
    public async Task ASpentWeekIsPublishedAsABlock()
    {
        var reader = this.Reader();
        reader.OnRead = (_, _) => Task.FromResult(TestData.SessionAndWeekly(0.30, 1.00));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.NotNull(snapshot.Block);
        Assert.True(snapshot.Block!.IsWeeklyExhaustion);
        Assert.Equal("Weekly limit reached", snapshot.Block.Reason);
        Assert.NotNull(this._store.Load("test")!.Snapshot.Block);
        Assert.Equal(UsageBand.Exhausted, FaceFigures.For(snapshot, showRemaining: false).Band);

        var alerts = new List<UsageAlertEvent>();
        var watcher = new UsageLimitWatcher(deliver: alerts.Add);
        watcher.Observe(TestData.SessionAndWeekly(0.30, 0.50));
        watcher.Observe(snapshot);
        Assert.Equal(new[] { UsageAlertKind.WeeklyLimitReached }, alerts.Select(alert => alert.Kind));
    }

    /// <summary>Cancellation is not a failure: the caller gets the last reading (or a placeholder
    /// repeating the last status) and the store is not written.</summary>
    [Fact]
    public async Task ACancelledFetchIsNotPublishedAsAFailure()
    {
        var reader = this.Reader();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        reader.OnRead = async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestData.SessionAndWeekly(0.5, 0.5);
        };

        var fetch = reader.FetchAsync(Freshness.Standard, cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        var snapshot = await fetch;

        Assert.NotEqual(ProviderStatusKind.Error, snapshot.Status.Kind);
        Assert.Empty(snapshot.Windows);
        Assert.Null(this._store.Load("test"));
        Assert.Null(this._store.LoadBackoffUntil("test"));
    }

    [Fact]
    public async Task ACancelledCallerDoesNotStartARead()
    {
        var reader = this.Reader();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var snapshot = await reader.FetchAsync(Freshness.Standard, cancellation.Token);

        Assert.Equal(0, reader.Calls);
        Assert.Null(this._store.Load("test"));

        // Swift's placeholder default: nothing has ever been read, so the face says so rather than
        // claiming Ok.
        Assert.Equal(ProviderStatus.Stale(DateTimeOffset.MinValue), snapshot.Status);
    }

    /// <summary>HttpClient reports its own timeout as a TaskCanceledException with the caller's
    /// token untouched. That is a hung endpoint, not a cancellation: it must read as no response
    /// and be recorded like any other failure.</summary>
    [Fact]
    public async Task AnHttpClientTimeoutIsNotACancellation()
    {
        var reader = this.Reader();
        reader.OnRead = (_, _) => Task.FromException<ProviderSnapshot>(
            new TaskCanceledException("The request timed out."));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatus.Error("no response"), snapshot.Status);

        // Recorded, not swallowed: the next caller to walk away repeats the failure's status
        // rather than a fresh-looking placeholder.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await reader.FetchAsync(Freshness.Standard, cancellation.Token);
        Assert.Equal(ProviderStatus.Error("no response"), cancelled.Status);
    }

    /// <summary>The hold-off and the failure's timestamp come from the moment the failure happened,
    /// like Swift's Date() in the catch — not from when the request started.</summary>
    [Fact]
    public async Task FailureTimesAreTakenWhenTheFailureHappens()
    {
        var reader = this.Reader();
        reader.OnRead = async (_, _) =>
        {
            await Task.Yield();
            this._clock.Now = this._clock.Now.AddSeconds(50);
            throw new ReaderException(ReaderError.RateLimited());
        };

        await reader.FetchAsync(Freshness.Standard);

        // Start + 50 s (when the 429 arrived) + the first rung of the ladder.
        Assert.Equal(TestData.Now.AddSeconds(110), this._store.LoadBackoffUntil("test"));
    }

    /// <summary>An older read must not hand its result to a caller once a newer one has replaced it:
    /// the caller gets the current reading, not the number it raced.</summary>
    [Fact]
    public async Task AnOutdatedReadingIsNotReturnedToItsCaller()
    {
        var reader = this.Reader();
        var slowRead = new TaskCompletionSource<ProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.OnRead = (freshness, _) => freshness == Freshness.FromSource
            ? Task.FromResult(TestData.SessionAndWeekly(0.60, 0.30))
            : slowRead.Task;

        var outdatedFetch = reader.FetchAsync(Freshness.Standard);
        Assert.Equal(1, reader.Calls);

        // A look is an explicit request of its own: it supersedes the scheduled read in flight.
        var newer = await reader.FetchAsync(Freshness.FromSource);
        Assert.Equal(2, reader.Calls);
        Assert.Equal(0.60, newer.UsedFraction!.Value, 10);

        slowRead.SetResult(TestData.SessionAndWeekly(0.40, 0.20));
        var outdated = await outdatedFetch;

        Assert.Equal(0.60, outdated.UsedFraction!.Value, 10);
        Assert.Equal(0.60, this._store.Load("test")!.Snapshot.UsedFraction!.Value, 10);
    }

    /// <summary>The generation check and the store write are one critical section, so a cycle that
    /// finishes second cannot overwrite the newer reading it was superseded by.</summary>
    [Fact]
    public async Task ANearSimultaneousOlderCycleDoesNotOverwriteTheNewerReading()
    {
        var reader = this.Reader();
        var slowRead = new TaskCompletionSource<ProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.OnRead = (freshness, _) => freshness == Freshness.FromSource
            ? Task.FromResult(TestData.SessionAndWeekly(0.60, 0.30))
            : slowRead.Task;

        var olderFetch = reader.FetchAsync(Freshness.Standard);

        // The newer cycle completes and publishes while the older read is still out.
        await reader.FetchAsync(Freshness.FromSource);
        Assert.Equal(0.60, this._store.Load("test")!.Snapshot.UsedFraction!.Value, 10);

        // The older read lands last. The store still holds the newer reading, and so its caller.
        slowRead.SetResult(TestData.SessionAndWeekly(0.40, 0.20));
        var older = await olderFetch;

        Assert.Equal(0.60, this._store.Load("test")!.Snapshot.UsedFraction!.Value, 10);
        Assert.Equal(0.60, older.UsedFraction!.Value, 10);
    }

    /// <summary>Concurrent scheduled fetches are one request: the second joins the read in flight
    /// and both callers get its answer.</summary>
    [Fact]
    public async Task ConcurrentFetchesShareOneRead()
    {
        var reader = this.Reader();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.OnRead = async (_, _) =>
        {
            await release.Task;
            return TestData.SessionAndWeekly(0.42, 0.11);
        };

        var first = reader.FetchAsync(Freshness.Standard);
        var second = reader.FetchAsync(Freshness.Standard);
        release.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, reader.Calls);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(0.42, results[0].UsedFraction!.Value, 10);
    }

    /// <summary>One coalesced 429 raises the ladder once: 60 s, not 60 s and then 120 s.</summary>
    [Fact]
    public async Task ConcurrentRateLimitedFetchesRecordOneBackoff()
    {
        var reader = this.Reader();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.OnRead = async (_, _) =>
        {
            await release.Task;
            throw new ReaderException(ReaderError.RateLimited());
        };

        var first = reader.FetchAsync(Freshness.Standard);
        var second = reader.FetchAsync(Freshness.Standard);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, reader.Calls);
        Assert.Equal(TestData.Now.AddSeconds(60), this._store.LoadBackoffUntil("test"));
    }

    /// <summary>The join rule at its edges: a Live or FromSource read in flight is shared by
    /// whoever arrives, and only a look supersedes a scheduled read (pinned by
    /// <see cref="AnOutdatedReadingIsNotReturnedToItsCaller"/>).</summary>
    [Fact]
    public async Task OnlyALookSupersedesAScheduledRead()
    {
        // A Live read in flight is shared with a scheduled caller.
        var liveFirst = this.Reader();
        var liveFirstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        liveFirst.OnRead = async (_, _) =>
        {
            await liveFirstRelease.Task;
            return TestData.SessionAndWeekly(0.42, 0.11);
        };
        var a = liveFirst.FetchAsync(Freshness.Live);
        var b = liveFirst.FetchAsync(Freshness.Standard);
        liveFirstRelease.SetResult();
        await Task.WhenAll(a, b);
        Assert.Equal(1, liveFirst.Calls);

        // A scheduled read in flight is shared with a Live caller.
        var scheduledFirst = this.Reader();
        var scheduledFirstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduledFirst.OnRead = async (_, _) =>
        {
            await scheduledFirstRelease.Task;
            return TestData.SessionAndWeekly(0.42, 0.11);
        };
        var c = scheduledFirst.FetchAsync(Freshness.Standard);
        var d = scheduledFirst.FetchAsync(Freshness.Live);
        scheduledFirstRelease.SetResult();
        await Task.WhenAll(c, d);
        Assert.Equal(1, scheduledFirst.Calls);

        // Two looks share one request too.
        var looks = this.Reader();
        var looksRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        looks.OnRead = async (_, _) =>
        {
            await looksRelease.Task;
            return TestData.SessionAndWeekly(0.42, 0.11);
        };
        var e = looks.FetchAsync(Freshness.FromSource);
        var f = looks.FetchAsync(Freshness.FromSource);
        looksRelease.SetResult();
        await Task.WhenAll(e, f);
        Assert.Equal(1, looks.Calls);
    }

    /// <summary>
    /// A real 429 from a cycle a look superseded mid-read still takes its rung and writes its
    /// deadline: the verdict is judged from the cycle that consulted the endpoint, not from the
    /// reader, so the newer cycle's fresh state cannot erase it.
    /// </summary>
    [Fact]
    public async Task ARealRateLimitFromASupersededCycleStillTakesItsRung()
    {
        var reader = this.Reader(holdOffScope: HoldOffScope.ReaderManaged);
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aMarked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseARateLimit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.OnRead = async (freshness, _) =>
        {
            if (freshness == Freshness.Standard)
            {
                aStarted.SetResult();
                await releaseA.Task;
                reader.MarkConsulted();
                aMarked.SetResult();
                await releaseARateLimit.Task;
                throw new ReaderException(ReaderError.RateLimited());
            }

            bStarted.SetResult();
            await releaseB.Task;
            return TestData.SessionAndWeekly(0.4, 0.2);
        };

        var fetchA = reader.FetchAsync(Freshness.Standard);
        await aStarted.Task;

        // A reaches its endpoint and marks; the look then starts and supersedes it while A is
        // still waiting for the answer.
        releaseA.SetResult();
        await aMarked.Task;
        var fetchB = reader.FetchAsync(Freshness.FromSource);
        await bStarted.Task;

        // A's endpoint answers 429 after the look began: A's own verdict still stands.
        releaseARateLimit.SetResult();
        var aSnapshot = await fetchA;
        Assert.Equal(ProviderStatusKind.Stale, aSnapshot.Status.Kind);
        Assert.Equal(TestData.Now.AddSeconds(60), this._store.LoadBackoffUntil("test"));

        releaseB.SetResult();
        var bSnapshot = await fetchB;
        Assert.Equal(ProviderStatusKind.Ok, bSnapshot.Status.Kind);
    }

    /// <summary>
    /// A look's unmarked reading — a Desktop or CLI answer — leaves the ladder and the deadline
    /// alone even while the older cycle has already marked its own endpoint visit: the newer
    /// cycle's success is judged from its own state, not from the other cycle's claim.
    /// </summary>
    [Fact]
    public async Task AnUnmarkedSupersedingAnswerLeavesTheLadderAndDeadlineAlone()
    {
        var reader = this.Reader(holdOffScope: HoldOffScope.ReaderManaged);

        // Two real 429s first, so the ladder is at its second rung and a deadline is stored.
        reader.OnRead = (_, _) =>
        {
            reader.MarkConsulted();
            throw new ReaderException(ReaderError.RateLimited());
        };
        await reader.FetchAsync(Freshness.Standard);
        await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(TestData.Now.AddSeconds(120), this._store.LoadBackoffUntil("test"));

        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aMarked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseARateLimit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.OnRead = async (freshness, _) =>
        {
            if (freshness == Freshness.Standard)
            {
                aStarted.SetResult();
                await releaseA.Task;
                reader.MarkConsulted();
                aMarked.SetResult();
                await releaseARateLimit.Task;
                throw new ReaderException(ReaderError.RateLimited());
            }

            bStarted.SetResult();
            await releaseB.Task;
            return TestData.SessionAndWeekly(0.4, 0.2);
        };

        var fetchA = reader.FetchAsync(Freshness.Standard);
        await aStarted.Task;
        var fetchB = reader.FetchAsync(Freshness.FromSource);
        await bStarted.Task;

        // A has marked; the look's unmarked answer lands now. It must not clear the deadline or
        // reset the ladder for a reading that never asked the endpoint.
        releaseA.SetResult();
        await aMarked.Task;
        releaseB.SetResult();
        var bSnapshot = await fetchB;
        Assert.Equal(ProviderStatusKind.Ok, bSnapshot.Status.Kind);
        Assert.Equal(TestData.Now.AddSeconds(120), this._store.LoadBackoffUntil("test"));

        // A's own 429 lands last and is the third rung: the counter the look left alone.
        releaseARateLimit.SetResult();
        var aSnapshot = await fetchA;
        Assert.Equal(0.4, aSnapshot.UsedFraction!.Value, 10);
        Assert.Equal(TestData.Now.AddSeconds(240), this._store.LoadBackoffUntil("test"));
    }

    /// <summary>The stamp is when the read came back, not when it was asked for.</summary>
    [Fact]
    public async Task FetchedAtIsStampedWhenTheReadReturns()
    {
        var reader = this.Reader();
        reader.OnRead = (_, _) =>
        {
            this._clock.Now = this._clock.Now.AddSeconds(50);
            return Task.FromResult(TestData.SessionAndWeekly(0.2, 0.1));
        };

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(TestData.Now.AddSeconds(50), snapshot.FetchedAt);
        Assert.Equal(TestData.Now.AddSeconds(50), this._store.Load("test")!.FetchedAt);
    }

    /// <summary>A switched-off provider says so on its face without dropping the reading the switch
    /// says nothing about.</summary>
    [Fact]
    public async Task ASwitchedOffProviderSaysSoWithoutForgettingHistory()
    {
        this._store.Seed("test", TestData.SessionAndWeekly(0.40, 0.20), TestData.Now.AddMinutes(-1));
        var reader = this.Reader(new ReaderConfig { Enabled = false });

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0, reader.Calls);
        Assert.Equal(ProviderStatus.Unsupported("Disabled in settings"), snapshot.Status);
        Assert.Empty(snapshot.Windows);
        Assert.NotNull(this._store.Load("test"));
    }

    private TestReader Reader(
        ReaderConfig? config = null,
        TimeSpan? deadline = null,
        HoldOffScope holdOffScope = HoldOffScope.WholeRead) =>
        new(config ?? new ReaderConfig(), this._store, this._clock)
        {
            FetchDeadline = deadline ?? TimeSpan.FromSeconds(RefreshSchedule.FetchDeadlineSeconds),
            Scope = holdOffScope,
        };

    private sealed class TestReader : UsageReaderBase
    {
        private readonly TimeProvider _time;

        public TestReader(ReaderConfig config, IReaderStateStore store, TimeProvider time)
            : base(config, store, time) => this._time = time;

        public override string Id => "test";

        public override string DisplayName => "Test provider";

        public Func<Freshness, CancellationToken, Task<ProviderSnapshot>> OnRead { get; set; } =
            (_, _) => Task.FromResult(TestData.SessionAndWeekly(0.5, 0.5));

        public int Calls { get; private set; }

        /// <summary>Settable so a test can opt into ReaderManaged, or move between backoff styles.</summary>
        public HoldOffScope Scope { get; set; } = HoldOffScope.WholeRead;

        /// <summary>Nil means the config's choice; set to pin one like a provider reader does.</summary>
        public RateLimitBackoff? StyleOverride { get; set; }

        /// <summary>What a ReaderManaged read would see for itself.</summary>
        public bool HeldOffNow() => this.IsHeldOff(this._time.GetUtcNow());

        /// <summary>The stored deadline, as a ReaderManaged read would read it.</summary>
        public DateTimeOffset? HoldOffDeadline() => this.LoadHoldOffUntil();

        /// <summary>What a ReaderManaged read calls before asking the held-off source.</summary>
        public void MarkConsulted() => this.MarkRateLimitedSourceConsulted();

        protected override HoldOffScope HoldOffScope => this.Scope;

        protected override RateLimitBackoff RateLimitBackoffStyle =>
            this.StyleOverride ?? base.RateLimitBackoffStyle;

        protected override Task<ProviderSnapshot> ReadAsync(Freshness freshness, CancellationToken cancellationToken)
        {
            this.Calls++;
            return this.OnRead(freshness, cancellationToken);
        }
    }

    /// <summary>A clock the test moves by hand. Timers still come from the system provider: the reader
    /// base starts one for its deadline, and that delay has to actually schedule.</summary>
    private sealed class TestClock : TimeProvider
    {
        public TestClock(DateTimeOffset now) => this.Now = now;

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => this.Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }
}
