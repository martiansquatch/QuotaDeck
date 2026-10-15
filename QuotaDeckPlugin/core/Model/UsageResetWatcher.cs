namespace QuotaDeck.Core.Model;

/// <summary>
/// Watches snapshots and detects when a provider's headline window rolls over or its usage drops
/// back down to reset levels.
///
/// Port of Codenotch <c>Sources/Model/UsageResetWatcher.swift</c> (<c>final class UsageResetWatcher</c>).
/// Headline window only.
/// </summary>
public sealed class UsageResetWatcher
{
    private sealed class TrackedState
    {
        public double Fraction { get; set; }

        public DateTimeOffset? ResetsAt { get; set; }

        public double PeakFraction { get; set; }

        public DateTimeOffset? LastAlertedResetDate { get; set; }
    }

    private readonly Dictionary<string, TrackedState> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<string, bool> _isMuted;
    private readonly Action<UsageAlertEvent> _deliver;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="isMuted">Provider id → true when its alerts are switched off.</param>
    /// <param name="deliver">Where events go. Never called while the watcher's lock is held.</param>
    /// <param name="now">The clock the elapsed-window rule is judged against.</param>
    public UsageResetWatcher(
        Func<string, bool>? isMuted = null,
        Action<UsageAlertEvent>? deliver = null,
        Func<DateTimeOffset>? now = null)
    {
        this._isMuted = isMuted ?? (_ => false);
        this._deliver = deliver ?? (_ => { });
        this._now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>A headline window reset: its deadline elapsed, or its usage dropped significantly.</summary>
    public event Action<UsageAlertEvent>? Reset;

    public void Observe(IEnumerable<ProviderSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            this.Observe(snapshot);
        }
    }

    public void Observe(ProviderSnapshot snapshot)
    {
        UsageAlertEvent? raised = null;
        lock (this._gate)
        {
            // An archived reading is not a baseline: at launch the last run's snapshot is published
            // marked stale, and the live fetch that follows carries a newer reset date or a lower
            // fraction, both of which read as a reset. Forgetting the provider here makes the first
            // live reading the one that only records.
            if (snapshot.Status.IsStale)
            {
                this._states.Remove(snapshot.Id);
                return;
            }

            if (snapshot.Headline is not { } headline || snapshot.UsedFraction is not { } fraction)
            {
                return;
            }

            if (!this._states.TryGetValue(snapshot.Id, out var previous))
            {
                this._states[snapshot.Id] = new TrackedState
                {
                    Fraction = fraction,
                    ResetsAt = headline.ResetsAt,
                    PeakFraction = fraction,
                    LastAlertedResetDate = headline.ResetsAt,
                };
                return;
            }

            // A later reset timestamp alone is not evidence of a reset: APIs which report a relative
            // countdown can move that timestamp by a few seconds on every refresh. The previous
            // window must have actually elapsed before its replacement can announce a reset.
            var previousWindowElapsed = previous.ResetsAt is { } previousResets && previousResets <= this._now();
            var isDateRolled =
                previousWindowElapsed
                && headline.ResetsAt is { } currentResets
                && previous.ResetsAt is { } previousDeadline
                && currentResets > previousDeadline
                && (previous.LastAlertedResetDate is not { } lastAlerted || currentResets > lastAlerted);

            var droppedSignificantly =
                fraction < previous.Fraction
                && (previous.Fraction - fraction >= 0.20
                    || (previous.PeakFraction >= 0.30 && fraction <= 0.10));

            var hadSignificantUsage = previous.PeakFraction >= 0.15;

            // A percentage-only fallback, for providers that give no reset timestamp at all, and for
            // the reading after a deadline has passed whose new window comes back without one. Before
            // a known deadline, a lower reading is a correction or fluctuation, not a reset.
            var canInferResetFromDrop =
                droppedSignificantly && (previous.ResetsAt is null || previousWindowElapsed);

            if ((isDateRolled || canInferResetFromDrop) && hadSignificantUsage && !this._isMuted(snapshot.Id))
            {
                raised = new UsageAlertEvent(
                    UsageAlertKind.Reset,
                    snapshot.Id,
                    snapshot.DisplayName,
                    headline.Label,
                    previous.Fraction,
                    fraction,
                    headline.ResetsAt);

                previous.Fraction = fraction;
                previous.PeakFraction = fraction;
                previous.ResetsAt = headline.ResetsAt;
                previous.LastAlertedResetDate = headline.ResetsAt;
            }
            else
            {
                previous.Fraction = fraction;
                previous.PeakFraction = Math.Max(previous.PeakFraction, fraction);
                previous.ResetsAt = headline.ResetsAt;
            }

            this._states[snapshot.Id] = previous;
        }

        if (raised is not null)
        {
            this.Reset?.Invoke(raised);
            this._deliver(raised);
        }
    }
}
