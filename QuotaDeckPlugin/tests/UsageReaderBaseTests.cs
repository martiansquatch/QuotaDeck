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
        Assert.NotEqual(ProviderStatusKind.Error, snapshot.Status.Kind);
        Assert.Null(this._store.Load("test"));
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

    private TestReader Reader(ReaderConfig? config = null, TimeSpan? deadline = null) =>
        new(config ?? new ReaderConfig(), this._store, this._clock)
        {
            FetchDeadline = deadline ?? TimeSpan.FromSeconds(RefreshSchedule.FetchDeadlineSeconds),
        };

    private sealed class TestReader : UsageReaderBase
    {
        public TestReader(ReaderConfig config, IReaderStateStore store, TimeProvider time)
            : base(config, store, time)
        {
        }

        public override string Id => "test";

        public override string DisplayName => "Test provider";

        public Func<Freshness, CancellationToken, Task<ProviderSnapshot>> OnRead { get; set; } =
            (_, _) => Task.FromResult(TestData.SessionAndWeekly(0.5, 0.5));

        public int Calls { get; private set; }

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
