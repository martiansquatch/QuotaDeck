namespace QuotaDeck.Core.Model;

/// <summary>
/// Watches snapshots and detects when a provider's session or weekly limit reaches 100% (exhausted)
/// or becomes blocked.
///
/// Port of Codenotch <c>Sources/Model/UsageLimitWatcher.swift</c> (<c>final class UsageLimitWatcher</c>).
/// This is what will trigger the full-panel pulse.
///
/// The session (headline) and weekly latches are tracked separately, each seeded by its own first
/// observation — the store's first publication for a provider can be a placeholder with no window at
/// all, and the weekly window can appear a fetch after the session one. A stale snapshot wipes the
/// provider's state, so an archived reading is never a baseline.
/// </summary>
public sealed class UsageLimitWatcher
{
    private sealed class TrackedLimit
    {
        public bool Seeded { get; set; }

        public bool IsExhausted { get; set; }

        public DateTimeOffset? ResetsAt { get; set; }

        public double Fraction { get; set; }
    }

    private sealed class ProviderLimitState
    {
        public TrackedLimit Session { get; } = new();

        public TrackedLimit Weekly { get; } = new();
    }

    private readonly Dictionary<string, ProviderLimitState> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<string, bool> _isMuted;
    private readonly Action<UsageAlertEvent> _deliver;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="isMuted">Provider id → true when its alerts are switched off.</param>
    /// <param name="deliver">Where events go. Never called while the watcher's lock is held.</param>
    /// <param name="now">The clock the rollover rule is judged against.</param>
    public UsageLimitWatcher(
        Func<string, bool>? isMuted = null,
        Action<UsageAlertEvent>? deliver = null,
        Func<DateTimeOffset>? now = null)
    {
        this._isMuted = isMuted ?? (_ => false);
        this._deliver = deliver ?? (_ => { });
        this._now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The session (headline) window reached its limit.</summary>
    public event Action<UsageAlertEvent>? SessionLimitReached;

    /// <summary>The weekly (secondary) window reached its limit.</summary>
    public event Action<UsageAlertEvent>? WeeklyLimitReached;

    public void Observe(IEnumerable<ProviderSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            this.Observe(snapshot);
        }
    }

    public void Observe(ProviderSnapshot snapshot)
    {
        List<UsageAlertEvent>? events = null;
        lock (this._gate)
        {
            // Same rule as the reset watcher: an archived (stale) reading is not a baseline, so the
            // first live reading after one only records.
            if (snapshot.Status.IsStale)
            {
                this._states.Remove(snapshot.Id);
                return;
            }

            var state = this._states.TryGetValue(snapshot.Id, out var tracked)
                ? tracked
                : new ProviderLimitState();

            // 1. Session limit (headline window).
            if (snapshot.Headline is { } headline && snapshot.UsedFraction is { } fraction)
            {
                // A block the spent week attached is the weekly alert's news, not the session's:
                // counting it here would fire a session card claiming the session itself spent while
                // it still shows room.
                var blockedBeyondWeekly = snapshot.Block is { IsWeeklyExhaustion: false };
                var isExhausted = fraction >= 1.0 || blockedBeyondWeekly;
                var dateRolledOver = this.RolledOver(state.Session.ResetsAt, headline.ResetsAt);

                if (dateRolledOver || fraction < 0.95)
                {
                    state.Session.IsExhausted = false;
                }

                if (!state.Session.Seeded)
                {
                    state.Session.Seeded = true;
                    state.Session.IsExhausted = isExhausted;
                }
                else if (isExhausted && !state.Session.IsExhausted && !this._isMuted(snapshot.Id))
                {
                    state.Session.IsExhausted = true;
                    (events ??= []).Add(new UsageAlertEvent(
                        UsageAlertKind.SessionLimitReached,
                        snapshot.Id,
                        snapshot.DisplayName,
                        headline.Label,
                        state.Session.Fraction,
                        fraction,
                        headline.ResetsAt));
                }

                state.Session.Fraction = fraction;
                state.Session.ResetsAt = headline.ResetsAt;
            }

            // 2. Weekly limit (secondary window).
            if (snapshot.WeeklyWindow is { } weekly && snapshot.WeeklyFraction is { } weeklyFraction)
            {
                var isWeeklyExhausted = weeklyFraction >= 1.0;
                var dateRolledOver = this.RolledOver(state.Weekly.ResetsAt, weekly.ResetsAt);

                if (dateRolledOver || weeklyFraction < 0.95)
                {
                    state.Weekly.IsExhausted = false;
                }

                if (!state.Weekly.Seeded)
                {
                    state.Weekly.Seeded = true;
                    state.Weekly.IsExhausted = isWeeklyExhausted;
                }
                else if (isWeeklyExhausted && !state.Weekly.IsExhausted && !this._isMuted(snapshot.Id))
                {
                    state.Weekly.IsExhausted = true;
                    (events ??= []).Add(new UsageAlertEvent(
                        UsageAlertKind.WeeklyLimitReached,
                        snapshot.Id,
                        snapshot.DisplayName,
                        weekly.Label,
                        state.Weekly.Fraction,
                        weeklyFraction,
                        weekly.ResetsAt));
                }

                state.Weekly.Fraction = weeklyFraction;
                state.Weekly.ResetsAt = weekly.ResetsAt;
            }

            this._states[snapshot.Id] = state;
        }

        if (events is not null)
        {
            foreach (var usageEvent in events)
            {
                this.Raise(usageEvent);
            }
        }
    }

    private void Raise(UsageAlertEvent usageEvent)
    {
        switch (usageEvent.Kind)
        {
            case UsageAlertKind.SessionLimitReached:
                this.SessionLimitReached?.Invoke(usageEvent);
                break;
            case UsageAlertKind.WeeklyLimitReached:
                this.WeeklyLimitReached?.Invoke(usageEvent);
                break;
            default:
                break;
        }

        this._deliver(usageEvent);
    }

    /// <summary>
    /// A later reset timestamp alone is not a new window: APIs which report a relative countdown move
    /// that timestamp by a few seconds on every refresh, and re-arming on that announced "limit
    /// reached" again on every fetch while the limit stayed spent. The tracked window must have
    /// actually elapsed.
    /// </summary>
    private bool RolledOver(DateTimeOffset? previous, DateTimeOffset? current)
    {
        if (previous is not { } from || current is not { } to)
        {
            return false;
        }

        return from <= this._now() && to > from;
    }
}
