namespace QuotaDeck.Core.Policy;

using QuotaDeck.Core.Model;

/// <summary>
/// When a tick is worth a request at all.
///
/// Port of Codenotch <c>Sources/Model/UsageStore.swift</c>: the schedule constants from the store's
/// initialiser (lines 225-250: tick 15 s, busy 30 s, idle 5 min, staleAfter 15 min, refreshDeadline
/// 60 s, onLookInterval 15 s) and the pure functions <c>hasWindowRolledOver(in:since:at:)</c> and
/// <c>shouldRefresh(isBusy:sinceLastAttempt:idleInterval:busyInterval:resetDue:)</c> plus
/// <c>shouldRefreshOnEvent(sinceLastRefresh:spacing:)</c>.
///
/// Codenotch's schedule is global; here the busy and idle intervals come from the provider's own
/// <c>ReaderConfig</c>, and these constants are only the defaults.
/// </summary>
public static class RefreshSchedule
{
    /// <summary>How often the scheduler wakes. Fine enough to express the busy interval.</summary>
    public const double TickSeconds = 15;

    public const double BusyIntervalSeconds = 30;

    public const double IdleIntervalSeconds = 5 * 60;

    public const double StaleAfterMinutes = 15;

    /// <summary>How long one fetch gets before the caller stops waiting for it.</summary>
    public const double FetchDeadlineSeconds = 60;

    /// <summary>How far apart two "looks" are spaced — a key press counts as a look.</summary>
    public const double OnLookSpacingSeconds = 15;

    /// <summary>
    /// True when a window's <c>ResetsAt</c> fell between the last attempt and now.
    ///
    /// That boundary is the one moment the numbers are certain to have moved, and it is the moment a
    /// reset alert is owed — waiting out the idle interval there is what made the alert arrive
    /// minutes late. It fires once per rollover: the refresh it asks for puts the last attempt past
    /// the boundary, so the next tick no longer sees it.
    /// </summary>
    public static bool HasWindowRolledOver(
        IEnumerable<LimitWindow> windows,
        DateTimeOffset? since,
        DateTimeOffset now)
    {
        if (since is not { } last)
        {
            return false;
        }

        foreach (var window in windows)
        {
            if (window.ResetsAt is { } resetsAt && resetsAt > last && resetsAt <= now)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Poll at the busy rate while something is running, at once when a window has just rolled over,
    /// and otherwise wait out the idle interval. A first run has never attempted anything and must
    /// not be held back.
    /// </summary>
    public static bool ShouldRefresh(
        DateTimeOffset? lastAttempt,
        DateTimeOffset now,
        bool busy,
        IEnumerable<LimitWindow> windows,
        double busyIntervalSeconds = BusyIntervalSeconds,
        double idleIntervalSeconds = IdleIntervalSeconds)
    {
        if (HasWindowRolledOver(windows, lastAttempt, now))
        {
            return true;
        }

        if (lastAttempt is not { } last)
        {
            return true;
        }

        var waited = (now - last).TotalSeconds;
        return busy ? waited >= busyIntervalSeconds : waited >= idleIntervalSeconds;
    }

    /// <summary>
    /// Whether an event outside the schedule — a look, a run that just finished — is owed a fetch of
    /// its own, or falls inside one already taken. Attention is not a budget: four faces hovered in
    /// four seconds is four looks and one fetch.
    /// </summary>
    public static bool ShouldRefreshOnEvent(
        double sinceLastRefreshSeconds,
        double spacingSeconds = OnLookSpacingSeconds) =>
        sinceLastRefreshSeconds >= spacingSeconds;
}
