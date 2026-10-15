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

    [Fact]
    public async Task ASwitchedOffProviderIsNotRead()
    {
        var reader = this.Reader(new ReaderConfig { Enabled = false });

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(0, reader.Calls);
        Assert.Empty(snapshot.Windows);
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
