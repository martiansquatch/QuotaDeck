namespace QuotaDeck.Core.Policy;

using QuotaDeck.Core.Model;

/// <summary>
/// The tick loop's memory: which providers a fetch is owed for, and which looks are worth their own.
///
/// Port of the scheduling state Codenotch keeps inside <c>Sources/Model/UsageStore.swift</c>
/// (<c>lastAttempt</c>, <c>lastWorkFinished</c> / <c>onLookInterval</c>) with the decisions left to
/// <see cref="RefreshSchedule"/>. The plugin's tick calls <see cref="Due"/>; hovering a key or
/// opening its panel calls <see cref="Look"/>.
/// </summary>
public sealed class RefreshCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastLook = new(StringComparer.Ordinal);

    /// <summary>
    /// The ids whose schedule says a fetch is owed now — a window rollover first, otherwise the
    /// busy or idle interval. Every id returned has its attempt time moved to <paramref name="now"/>:
    /// an attempt means "the caller is starting the fetch now", the same rule as Codenotch's
    /// <c>refreshNow</c>.
    /// </summary>
    public IReadOnlyList<string> Due(
        IReadOnlyList<ProviderSnapshot> snapshots,
        DateTimeOffset now,
        bool busy = false)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        lock (this._gate)
        {
            var due = new List<string>();
            foreach (var snapshot in snapshots)
            {
                if (RefreshSchedule.ShouldRefresh(this.Attempt(snapshot.Id), now, busy, snapshot.Windows))
                {
                    due.Add(snapshot.Id);
                }
            }

            foreach (var id in due)
            {
                this._lastAttempt[id] = now;
            }

            return due;
        }
    }

    /// <summary>
    /// True when a look — the menu opening, a pointer landing on a ring — is owed a fetch of its own,
    /// and false inside the fifteen-second spacing. Attention is not a budget: four faces hovered in
    /// four seconds is four looks and one fetch.
    /// </summary>
    public bool Look(string providerId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        lock (this._gate)
        {
            if (this._lastLook.TryGetValue(providerId, out var last)
                && !RefreshSchedule.ShouldRefreshOnEvent((now - last).TotalSeconds))
            {
                return false;
            }

            this._lastLook[providerId] = now;
            return true;
        }
    }

    private DateTimeOffset? Attempt(string providerId) =>
        this._lastAttempt.TryGetValue(providerId, out var at) ? at : null;
}
