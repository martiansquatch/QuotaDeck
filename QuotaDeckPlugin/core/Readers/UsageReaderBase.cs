namespace QuotaDeck.Core.Readers;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Settings;

/// <summary>
/// The half of a reader that is the same for every provider: never throwing, remembering the last
/// good reading, holding off after a 429, stopping at the deadline, and coalescing concurrent
/// fetches onto one read.
///
/// Port of the store-side machinery in Codenotch <c>Sources/Model/UsageStore.swift</c> —
/// <c>fetch</c>'s catch block (lines 839-848) with <c>degraded(provider:error:)</c>,
/// <c>status(for:)</c>, <c>placeholder(_:)</c>, the <c>generations</c>/<c>acceptsResult</c>
/// bookkeeping and <c>publish</c>'s <c>blockingSpentWeek()</c> — plus the per-provider rate-limit
/// handling of <c>Sources/Providers/ClaudeOAuthProvider.swift</c> (<c>fetchFromKeychain(reusableFor:)</c>,
/// lines 303-353: the hold-off check — whole-read by default, or per-source for a
/// <see cref="HoldOffScope.ReaderManaged"/> reader — <c>consecutiveRateLimits</c>,
/// <c>backoff(forAttempt:retryAfter:)</c> and the back-off save, with <c>endpointFetch</c>'s
/// coalescing of concurrent calls) and the refresh deadline (lines 204-223 of the store).
/// Codenotch keeps the last good readings and the deadline in the store and the rate-limit counter
/// in the provider; the split is invisible from outside.
///
/// One coalescing difference is deliberate. Swift joins every overlap and supersedes only through
/// <c>cancelRefresh</c>; QuotaDeck has no cancellation entry point, so an explicit
/// <see cref="Freshness.FromSource"/> look supersedes a scheduled <see cref="Freshness.Standard"/>
/// read instead of riding it — a look must not be made to wait out a slow schedule — while every
/// other overlap joins whatever is in flight. See <see cref="FetchAsync"/>.
///
/// Telling apart the cases that reach the same status is the whole job: a 429 and an expired token
/// both age the reading, a refusal keeps it, a sign-out clears it, and a cancellation records
/// nothing at all. That is <see cref="Degradation"/>, applied here.
/// </summary>
public abstract class UsageReaderBase : IUsageReader
{
    private static readonly ProviderStatus DisabledStatus =
        ProviderStatus.Unsupported("Disabled in settings");

    private readonly IReaderStateStore _store;
    private readonly TimeProvider _time;
    private readonly object _fetchGate = new();

    /// <summary>
    /// The read cycle a concurrent call may join, with the freshness it was started under. A
    /// completed cycle is never joined again, so the next caller starts a fresh read rather than
    /// being handed a stale answer.
    /// </summary>
    private (Task<ProviderSnapshot> Task, Freshness Freshness)? _inFlight;

    private int _generation;
    private int _consecutiveRateLimits;

    /// <summary>
    /// Whether this cycle's read asked the source the hold-off covers — set by
    /// <see cref="MarkRateLimitedSourceConsulted"/> from inside <c>ReadAsync</c>, reset under the
    /// fetch gate when the cycle starts, and read under the same gate when the outcome is judged.
    /// A plain field, not an <c>AsyncLocal</c>: a read cycle runs its read exactly once, so one
    /// field is enough to tell a cache answer from an endpoint one.
    /// </summary>
    private bool _rateLimitedSourceConsulted;

    /// <summary>
    /// What the last published result said. A cancellation repeats this instead of inventing a
    /// failure for it; see <see cref="Cancelled"/>. The initial value is Swift's placeholder
    /// default, <c>.stale(since: .distantPast)</c>: a cancellation before any fetch has a truthful
    /// "nothing has been read" status rather than a false Ok.
    /// </summary>
    private volatile ProviderStatus _lastStatus = ProviderStatus.Stale(DateTimeOffset.MinValue);

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
    /// How this reader waits after a 429: <see cref="ReaderConfig.RateLimitBackoff"/> unless the
    /// provider's own Swift counterpart pins a different one — Codex waits flat
    /// (<c>CodexLocalProvider.swift</c>), Claude climbs the ladder — in which case the pinned
    /// choice wins.
    /// </summary>
    protected virtual RateLimitBackoff RateLimitBackoffStyle => this.Config.RateLimitBackoff;

    /// <summary>
    /// Whether the persisted 429 hold-off suppresses this reader's whole read or only the steps the
    /// reader applies it to; see <see cref="HoldOffScope"/>. Defaults to
    /// <see cref="HoldOffScope.WholeRead"/>.
    /// </summary>
    protected virtual HoldOffScope HoldOffScope => HoldOffScope.WholeRead;

    /// <summary>
    /// When the stored 429 deadline expires, if one is recorded. For a
    /// <see cref="HoldOffScope.ReaderManaged"/> reader to consult on the steps that are held off;
    /// the base still owns the deadline — it writes it on a 429 that consulted the source and
    /// clears it on a consulted success. A method, not a property, because it reads the store.
    /// </summary>
    protected DateTimeOffset? LoadHoldOffUntil() => this._store.LoadBackoffUntil(this.Id);

    /// <summary>
    /// Whether the stored deadline is still in force at <paramref name="now"/>, with the usual
    /// one-second slack.
    /// </summary>
    protected bool IsHeldOff(DateTimeOffset now) =>
        this.LoadHoldOffUntil() is { } until && Backoff.ShouldHoldOff(until, now);

    /// <summary>
    /// Says that this read actually asked the source the hold-off covers; a
    /// <see cref="HoldOffScope.ReaderManaged"/> reader calls it immediately before that request.
    /// The base cannot tell a Desktop-cache answer from an endpoint one, so only a read that
    /// consulted the source may reset the ladder and clear the deadline on success, or take a rung
    /// and rewrite the deadline on a 429. Reset at the start of every read cycle.
    /// </summary>
    protected void MarkRateLimitedSourceConsulted() => this._rateLimitedSourceConsulted = true;

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
                // A switched-off provider is not fetched and keeps no reading. The status is built
                // directly, not through Degraded: saying "disabled" must not drop the remembered
                // reading, which the switch says nothing about.
                return Degradation.Placeholder(this.Id, this.DisplayName, DisabledStatus);
            }

            // A cancellation is not news about the provider. It records nothing, spends nothing,
            // and reports what was last published rather than inventing an error for the caller —
            // the same outcome as Swift's `acceptsResult` dropping a cancelled pass.
            if (cancellationToken.IsCancellationRequested)
            {
                return this.Cancelled(this._time.GetUtcNow());
            }

            var now = this._time.GetUtcNow();

            if (this.HoldOffScope == HoldOffScope.WholeRead
                && this._store.LoadBackoffUntil(this.Id) is { } until
                && Backoff.ShouldHoldOff(until, now))
            {
                // Asked to slow down and still slowing down: this is not a new 429, so the penalty is
                // neither counted nor extended. It reads as staleness, like any other hold-off. A
                // ReaderManaged reader skips this check and applies the deadline itself, per source.
                return this.Degraded(
                    Degradation.StatusFor(ReaderError.RateLimited(until - now), now), now);
            }

            Task<ProviderSnapshot> cycle;
            lock (this._fetchGate)
            {
                // Swift joins every overlap; the one deliberate difference here is that a FromSource
                // look does not ride a scheduled Standard read — it asks for a request of its own
                // and supersedes it, because there is no cancelRefresh here to reach for and a look
                // must not be made to wait out a slow schedule. Everything else joins: a Live or
                // FromSource read in flight is shared by whoever arrives.
                if (this._inFlight is { } inFlight
                    && !inFlight.Task.IsCompleted
                    && !(inFlight.Freshness == Freshness.Standard && freshness == Freshness.FromSource))
                {
                    cycle = inFlight.Task;
                }
                else
                {
                    // The generation moves under the gate, and Success re-checks it under the same
                    // gate before saving, so two cycles finishing together cannot overwrite the
                    // newer reading with the older one. The consulted flag belongs to this cycle:
                    // cleared here, set by the read if it asks the held-off source, and read once
                    // more under this gate when the outcome is judged.
                    this._rateLimitedSourceConsulted = false;
                    var generation = ++this._generation;
                    cycle = this.RunReadCycle(generation, freshness, cancellationToken);
                    this._inFlight = (cycle, freshness);
                }
            }

            var result = await cycle.ConfigureAwait(false);

            // A caller cancelled while it waited must not take the cycle's outcome — even a
            // successful one — as its own.
            return cancellationToken.IsCancellationRequested
                ? this.Cancelled(this._time.GetUtcNow())
                : result;
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

    private async Task<ProviderSnapshot> RunReadCycle(
        int generation,
        Freshness freshness,
        CancellationToken cancellationToken)
    {
        Task<ProviderSnapshot> read;
        try
        {
            read = this.ReadAsync(freshness, cancellationToken);
        }
        catch (Exception exception)
        {
            var caughtAt = this._time.GetUtcNow();
            return cancellationToken.IsCancellationRequested
                ? this.Cancelled(caughtAt)
                : this.Failed(exception, caughtAt);
        }

        var deadline = Task.Delay(this.FetchDeadline, this._time, cancellationToken);
        var winner = await Task.WhenAny(read, deadline).ConfigureAwait(false);
        if (winner != read)
        {
            var caughtAt = this._time.GetUtcNow();
            if (cancellationToken.IsCancellationRequested)
            {
                // Cancelled, not late: nothing is recorded, and no late-remembering is attached
                // to a read nobody is waiting for any more.
                return this.Cancelled(caughtAt);
            }

            // The abandoned read reports nowhere, but its answer may still be the freshest one
            // this reader has if nothing newer has started.
            _ = this.RememberLate(read, generation);
            return this.OnFailure(ReaderError.TimedOut, caughtAt);
        }

        try
        {
            var fresh = await read.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                // The answer raced the cancellation. Swift refuses a result the caller has walked
                // away from; it is not stored and not published.
                return this.Cancelled(this._time.GetUtcNow());
            }

            return this.Success(fresh, generation, this._time.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The read honoured the caller's token: a cancellation, not a failure, and not
            // published as one.
            return this.Cancelled(this._time.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            // HttpClient reports its own timeout as TaskCanceledException while the caller's token
            // is untouched. That is a hung endpoint, not a cancellation: it reads as no response
            // and is recorded like any other failure — Swift maps the URLSession timeout the same
            // way. The guard above is what keeps this case out of the cancellation path.
            return this.OnFailure(ReaderError.TimedOut, this._time.GetUtcNow());
        }
        catch (Exception exception)
        {
            var caughtAt = this._time.GetUtcNow();
            return cancellationToken.IsCancellationRequested
                ? this.Cancelled(caughtAt)
                : this.Failed(exception, caughtAt);
        }
    }

    /// <summary>
    /// Publishes a fresh reading, or hands back the current one when a newer generation has taken
    /// over. The generation check and the store write share the fetch gate with the generation
    /// increment, so two cycles finishing together cannot both pass the check and let the older
    /// save land after the newer one.
    /// </summary>
    private ProviderSnapshot Success(ProviderSnapshot fresh, int generation, DateTimeOffset returnedAt)
    {
        // The spent-week block is applied at publication, like the store's `blockingSpentWeek()`: a
        // weekly allowance at its limit shuts the headline with it, whatever the session reading
        // says. The blocked shape is what is remembered, so a later failure re-shows it as it was.
        var published = (fresh with { FetchedAt = returnedAt }).BlockingSpentWeek();

        lock (this._fetchGate)
        {
            if (this._generation != generation)
            {
                // A newer generation has started — and possibly completed — in the meantime. Its
                // reading is the current one; handing this one to the caller would be returning a
                // number the reader itself has already replaced.
                return this._store.Load(this.Id)?.Snapshot
                    ?? this.Degraded(this._lastStatus, returnedAt);
            }

            // A ReaderManaged read that never asked the rate-limited source — a Desktop or CLI
            // answer, say — is still a fresh reading, so it is stored; but it says nothing about
            // that source, so the ladder and the deadline are left exactly as they were. A
            // WholeRead read is every source by definition.
            if (this.HoldOffScope == HoldOffScope.WholeRead || this._rateLimitedSourceConsulted)
            {
                Interlocked.Exchange(ref this._consecutiveRateLimits, 0);
                this._store.SaveBackoffUntil(this.Id, null);
            }

            this._store.Save(this.Id, new ReaderMemory(published, returnedAt));
            this._lastStatus = published.Status;
            return published;
        }
    }

    private async Task RememberLate(Task<ProviderSnapshot> read, int generation)
    {
        try
        {
            var fresh = await read.ConfigureAwait(false);

            // Stamped when it actually returned, like any other success. Success itself decides
            // whether this generation is still the current one.
            this.Success(fresh, generation, this._time.GetUtcNow());
        }
        catch
        {
            // An abandoned read's failure is not news: this fetch already reported TimedOut.
        }
    }

    /// <summary>
    /// What a failed read becomes. A <see cref="ReaderException"/> is the reader's own report and
    /// goes through <see cref="OnFailure"/>, which is where a 429's hold-off is recorded; anything
    /// else is a bug being reported and reads as its message.
    /// </summary>
    private ProviderSnapshot Failed(Exception exception, DateTimeOffset now) =>
        exception is ReaderException readerException
            ? this.OnFailure(readerException.Error, now)
            : this.Degraded(Degradation.StatusFor(exception, now), now);

    private ProviderSnapshot OnFailure(ReaderError error, DateTimeOffset now)
    {
        if (error.Kind == ReaderErrorKind.RateLimited)
        {
            if (this.HoldOffScope == HoldOffScope.ReaderManaged && !this._rateLimitedSourceConsulted)
            {
                // A replay, not a new 429: the reader is honouring a hold-off it already has.
                // Taking a rung or writing the deadline again would ratchet both on every cached
                // cycle; instead the reading degrades through the normal path — nothing is saved,
                // and a dimmed reading keeps its own FetchedAt as the Stale since.
                return this.Degraded(ProviderStatus.Stale(now), now);
            }

            // The server's hint is honoured only as a floor-raiser: it answers `Retry-After: 0`, and
            // obeying that literally is what keeps you rate limited. Which wait follows the hint
            // depends on the provider's own counterpart: the ladder starts at a minute and doubles
            // for each 429 in a row, stopping at fifteen (Claude); OpenCode's ladder is the same but
            // caps the whole wait, hint included; Codex, Devin and Muse wait a flat max(60, hint)
            // every time. The deadline outlives the process either way. Only the climbing styles
            // consume a rung — a flat wait must not leave the ladder higher for a later climb.
            var wait = this.RateLimitBackoffStyle switch
            {
                RateLimitBackoff.Flat => Backoff.Flat(error.RetryAfter),
                RateLimitBackoff.LadderCapped => Backoff.LadderCapped(this.NextLadderRung(), error.RetryAfter),
                _ => Backoff.Compute(this.NextLadderRung(), error.RetryAfter),
            };
            this._store.SaveBackoffUntil(this.Id, now + wait);
        }

        return this.Degraded(Degradation.StatusFor(error, now), now);
    }

    /// <summary>
    /// The rung this 429 takes, raised atomically: two cycles (a scheduled read and a superseding
    /// look) may fail here at once, and each must take its own rung of the ladder.
    /// </summary>
    private int NextLadderRung() => Interlocked.Increment(ref this._consecutiveRateLimits) - 1;

    /// <summary>
    /// What a cancelled caller sees: what was last published, aged but never turned into a failure —
    /// a cancellation carries no information about the provider, and an error for it would put a
    /// wrong "no response" on the face of a reader that is working.
    /// </summary>
    private ProviderSnapshot Cancelled(DateTimeOffset now) => this.Degraded(this._lastStatus, now);

    /// <summary>
    /// What a failure shows: the last good reading aged or kept, or a placeholder carrying the status.
    /// A status that makes the remembered reading untrue — signed out, or a plan that meters nothing —
    /// drops it rather than dimming it.
    /// </summary>
    private ProviderSnapshot Degraded(ProviderStatus status, DateTimeOffset now)
    {
        this._lastStatus = status;
        var memory = this._store.Load(this.Id);
        if (Degradation.SupersedesHistory(status))
        {
            this._store.Forget(this.Id);
            memory = null;
        }

        return Degradation.Degrade(this.Id, this.DisplayName, memory, status, now, this.StaleAfter);
    }
}
