namespace QuotaDeck.Core.Readers;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Settings;

/// <summary>
/// The half of a reader that is the same for every provider: never throwing, remembering the last
/// good reading, holding off after a 429, and stopping at the deadline.
///
/// Port of the store-side machinery in Codenotch <c>Sources/Model/UsageStore.swift</c> —
/// <c>fetch</c>'s catch block (lines 839-848) with <c>degraded(provider:error:)</c>,
/// <c>status(for:)</c> and <c>placeholder(_:)</c> — plus the per-provider rate-limit handling of
/// <c>Sources/Providers/ClaudeOAuthProvider.swift</c> (<c>fetchFromKeychain(reusableFor:)</c>, lines
/// 303-353: the hold-off check, <c>consecutiveRateLimits</c>, <c>backoff(forAttempt:retryAfter:)</c>
/// and the back-off save) and the refresh deadline (lines 204-223 of the store). Codenotch keeps the
/// last good readings and the deadline in the store and the rate-limit counter in the provider; the
/// split is invisible from outside.
///
/// Telling apart the cases that reach the same status is the whole job: a 429 and an expired token
/// both age the reading, a refusal keeps it, and a sign-out clears it. That is
/// <see cref="Degradation"/>, applied here.
/// </summary>
public abstract class UsageReaderBase : IUsageReader
{
    private readonly IReaderStateStore _store;
    private readonly TimeProvider _time;
    private int _generation;
    private int _consecutiveRateLimits;

    protected UsageReaderBase(
        ReaderConfig config,
        IReaderStateStore stateStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stateStore);

        this.Config = config;
        this._store = stateStore;
        this._time = timeProvider ?? TimeProvider.System;
    }

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    /// <summary>This reader's schedule and enablement.</summary>
    protected ReaderConfig Config { get; }

    /// <summary>
    /// How long one fetch gets before this class stops waiting for it.
    ///
    /// Not a cancellation, and it cannot be one: at the bottom of a credential read there may be a
    /// synchronous platform call that blocks its thread until an authorization prompt is answered, and
    /// a Task cancellation sets a flag without reaching into a blocked call. What the deadline buys is
    /// that the caller stops waiting, so a wedged read costs one tick rather than the rest of the day.
    /// The abandoned read keeps running; its answer is remembered only if no newer fetch has started,
    /// the same rule as the store's `generations` bookkeeping.
    /// </summary>
    public TimeSpan FetchDeadline { get; init; } = TimeSpan.FromSeconds(RefreshSchedule.FetchDeadlineSeconds);

    /// <summary>How much memory after the last successful read is still worth showing undimmed.</summary>
    protected TimeSpan StaleAfter => this.Config.StaleAfter;

    public async Task<ProviderSnapshot> FetchAsync(Freshness freshness, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!this.Config.Enabled)
            {
                // A switched-off provider is not fetched and keeps no reading. Codenotch's store
                // filters it out of the published set entirely; with a snapshot owed here, the honest
                // answer is the placeholder it would draw.
                return Degradation.Placeholder(
                    this.Id, this.DisplayName, ProviderStatus.Stale(DateTimeOffset.MinValue));
            }

            var now = this._time.GetUtcNow();

            if (this._store.LoadBackoffUntil(this.Id) is { } until && Backoff.ShouldHoldOff(until, now))
            {
                // Asked to slow down and still slowing down: this is not a new 429, so the penalty is
                // neither counted nor extended. It reads as staleness, like any other hold-off.
                return this.Degraded(
                    Degradation.StatusFor(ReaderError.RateLimited(until - now), now), now);
            }

            var generation = Interlocked.Increment(ref this._generation);

            Task<ProviderSnapshot> read;
            try
            {
                read = this.ReadAsync(freshness, cancellationToken);
            }
            catch (Exception exception)
            {
                return this.Degraded(Degradation.StatusFor(exception, now), now);
            }

            var deadline = Task.Delay(this.FetchDeadline, this._time, cancellationToken);
            var winner = await Task.WhenAny(read, deadline).ConfigureAwait(false);
            if (winner != read)
            {
                // The abandoned read reports nowhere, but its answer may still be the freshest one
                // this reader has if nothing newer has started.
                _ = this.RememberLate(read, generation, now);
                return this.OnFailure(ReaderError.TimedOut, now);
            }

            try
            {
                var fresh = await read.ConfigureAwait(false);
                return this.Success(fresh, generation, now);
            }
            catch (Exception exception)
            {
                return this.Degraded(Degradation.StatusFor(exception, now), now);
            }
        }
        catch (Exception exception)
        {
            // Belt and braces: nothing above should reach this, but the contract is that a fetch
            // never throws, and a fetch that did would take the scheduler's tick with it.
            return this.Degraded(Degradation.StatusFor(exception, this._time.GetUtcNow()), this._time.GetUtcNow());
        }
    }

    /// <summary>
    /// The provider's own read. May throw <see cref="ReaderException"/>; any other exception is
    /// carried as an error named by its message.
    /// </summary>
    protected abstract Task<ProviderSnapshot> ReadAsync(Freshness freshness, CancellationToken cancellationToken);

    private ProviderSnapshot Success(ProviderSnapshot fresh, int generation, DateTimeOffset now)
    {
        var stamped = fresh with { FetchedAt = now };
        if (Volatile.Read(ref this._generation) == generation)
        {
            this._consecutiveRateLimits = 0;
            this._store.SaveBackoffUntil(this.Id, null);
            this._store.Save(this.Id, new ReaderMemory(stamped, now));
        }

        return stamped;
    }

    private async Task RememberLate(Task<ProviderSnapshot> read, int generation, DateTimeOffset now)
    {
        try
        {
            var fresh = await read.ConfigureAwait(false);
            if (Volatile.Read(ref this._generation) == generation)
            {
                this.Success(fresh, generation, now);
            }
        }
        catch
        {
            // An abandoned read's failure is not news: this fetch already reported TimedOut.
        }
    }

    private ProviderSnapshot OnFailure(ReaderError error, DateTimeOffset now)
    {
        if (error.Kind == ReaderErrorKind.RateLimited)
        {
            // The server's hint is honoured only as a floor-raiser: it answers `Retry-After: 0`, and
            // obeying that literally is what keeps you rate limited. The wait starts at a minute and
            // doubles for each 429 in a row, and the deadline outlives the process.
            var wait = Backoff.Compute(this._consecutiveRateLimits, error.RetryAfter);
            this._consecutiveRateLimits++;
            this._store.SaveBackoffUntil(this.Id, now + wait);
        }

        return this.Degraded(Degradation.StatusFor(error, now), now);
    }

    /// <summary>
    /// What a failure shows: the last good reading aged or kept, or a placeholder carrying the status.
    /// A status that makes the remembered reading untrue — signed out, or a plan that meters nothing —
    /// drops it rather than dimming it.
    /// </summary>
    private ProviderSnapshot Degraded(ProviderStatus status, DateTimeOffset now)
    {
        var memory = this._store.Load(this.Id);
        if (Degradation.SupersedesHistory(status))
        {
            this._store.Forget(this.Id);
            memory = null;
        }

        return Degradation.Degrade(this.Id, this.DisplayName, memory, status, now, this.StaleAfter);
    }
}
