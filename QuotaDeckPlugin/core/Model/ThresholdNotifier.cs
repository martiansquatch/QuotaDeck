namespace QuotaDeck.Core.Model;

/// <summary>
/// Watches snapshots and reports the moment a provider's headline limit crosses 80% or reaches 100%.
///
/// Port of Codenotch <c>Sources/Model/ThresholdNotifier.swift</c> (<c>final class ThresholdNotifier</c>;
/// the UNUserNotificationCenter delivery at the bottom of that file is deliberately not ported —
/// delivery is injected here, so the keypad pulse and a macOS notification can both subscribe later).
///
/// Crossing, not level: a provider parked at 91% must not alert twice, so the highest threshold
/// currently crossed is remembered per provider. The memory clears when the reading drops back below
/// the first threshold — the window has rolled over, and the next climb is a new fact worth
/// announcing. The memory is per provider id, not per window.
/// </summary>
public sealed class ThresholdNotifier
{
    private readonly Dictionary<string, int> _crossed = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<string, bool> _isMuted;
    private readonly Action<ThresholdAlert> _deliver;

    /// <param name="isMuted">Provider id → true when its alerts are switched off.</param>
    /// <param name="deliver">Where a crossing goes. Never called while the notifier's lock is held.</param>
    public ThresholdNotifier(Func<string, bool>? isMuted = null, Action<ThresholdAlert>? deliver = null)
    {
        this._isMuted = isMuted ?? (_ => false);
        this._deliver = deliver ?? (_ => { });
    }

    public void Observe(IEnumerable<ProviderSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            this.Observe(snapshot);
        }
    }

    public void Observe(ProviderSnapshot snapshot)
    {
        List<ThresholdAlert>? alerts = null;
        lock (this._gate)
        {
            // An archived reading is not a baseline. At launch a plugin publishes what it remembered
            // from the last run, marked stale, and the first live fetch follows seconds later;
            // measured against the archive it read as a crossing, and rang on every start.
            // Forgetting the provider here makes that first live reading the one that only records.
            if (snapshot.Status.IsStale)
            {
                this._crossed.Remove(snapshot.Id);
                return;
            }

            if (snapshot.UsedFraction is not { } fraction)
            {
                return;
            }

            var percent = fraction * 100;
            // No rounding: 79.99 is level 0, not level 80.
            var level = percent >= 100 ? 100 : percent >= 80 ? 80 : 0;

            // The first reading only records, even when it is already past a threshold: a launch is
            // not a crossing.
            if (!this._crossed.TryGetValue(snapshot.Id, out var previous))
            {
                this._crossed[snapshot.Id] = level;
                return;
            }

            // Always written, muted or not, so unmuting cannot replay a crossing that happened in
            // the silence.
            this._crossed[snapshot.Id] = level;
            if (level <= previous || this._isMuted(snapshot.Id))
            {
                return;
            }

            if (snapshot.Headline is not { } headline)
            {
                return;
            }

            alerts = [];
            foreach (var threshold in new[] { 80, 100 })
            {
                if (threshold > previous && threshold <= level)
                {
                    alerts.Add(new ThresholdAlert(
                        threshold,
                        snapshot.Id,
                        snapshot.DisplayName,
                        headline.Label,
                        (int)Math.Round(percent, MidpointRounding.AwayFromZero),
                        headline.ResetsAt));
                }
            }
        }

        if (alerts is not null)
        {
            foreach (var alert in alerts)
            {
                this._deliver(alert);
            }
        }
    }
}
