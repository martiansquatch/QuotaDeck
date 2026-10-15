namespace QuotaDeck.Core.Settings;

using QuotaDeck.Core.Model;

/// <summary>
/// The rule that turns a remembered order into a real one.
///
/// Port of Codenotch <c>Sources/Model/ProviderOrder.swift</c> (<c>arrange(_:by:id:)</c>,
/// <c>remember(_:keeping:)</c>, <c>joiningConnected(_:in:isConnected:)</c>,
/// <c>byRemainingUsage(_:)</c>). The local-runtime cell helpers (<c>cells(from:keeping:)</c>) are left
/// out: QuotaDeck has no local runtimes.
///
/// The awkward part is not applying an order, it is that the set of providers is not fixed: an id
/// can appear on a machine that has never seen it and vanish from one that has. A stored order is a
/// preference to reconcile, never an authority.
/// </summary>
public static class ProviderOrder
{
    /// <summary>
    /// <paramref name="items"/> in the user's order, then everything the order has never seen, in
    /// the order it arrived in.
    ///
    /// Idempotent, which is what lets a caller re-apply it to an already-sorted list without the
    /// faces shuffling.
    /// </summary>
    public static List<T> Arrange<T>(IReadOnlyList<T> items, IReadOnlyList<string> order, Func<T, string> id)
    {
        if (order.Count == 0)
        {
            return [.. items];
        }

        var remaining = new List<T>(items);
        var arranged = new List<T>();
        foreach (var providerId in order)
        {
            // A stored id no provider claims is the ordinary case, not corruption: a profile that is
            // not on this machine today.
            var index = remaining.FindIndex(item => id(item) == providerId);
            if (index < 0)
            {
                continue;
            }

            arranged.Add(remaining[index]);
            remaining.RemoveAt(index);
        }

        // Appended rather than dropped: a provider added by a new version has to turn up somewhere,
        // and the end is the one position that is never a lie about what the user chose.
        arranged.AddRange(remaining);
        return arranged;
    }

    /// <summary>
    /// Where a provider goes when it is switched back on: after the ones already connected, rather
    /// than back to wherever it used to sit. Restoring its old place would make off/on a perfect
    /// round trip, but it lets a row that was not on screen outrank one the user deliberately
    /// dragged to the top while it was away.
    /// </summary>
    public static List<string> JoiningConnected(string id, IReadOnlyList<string> order, Func<string, bool> isConnected)
    {
        var rest = order.Where(entry => entry != id).ToList();

        // Nothing connected at all makes it the first, not the last.
        var insertAt = rest.FindLastIndex(entry => isConnected(entry));
        rest.Insert(insertAt < 0 ? 0 : insertAt + 1, id);
        return rest;
    }

    /// <summary>
    /// The order to remember, given the order now on screen. Ids that are remembered but not visible
    /// are kept, at the end: a profile whose directory is not on this machine today must not lose its
    /// place because an unrelated row moved.
    /// </summary>
    public static List<string> Remember(IReadOnlyList<string> visible, IReadOnlyList<string> remembered) =>
        [.. visible, .. remembered.Where(entry => !visible.Contains(entry))];

    /// <summary>
    /// Auto-order: most remaining first, by the window that leads each cell.
    ///
    /// Tiers, then remaining within them:
    /// — usable first, most remaining at the top;
    /// — shut with room next: a high main number that cannot be spent because the other window is
    ///   empty sinks below everything usable, but stays above what is fully empty;
    /// — fully empty after that — the headline itself spent;
    /// — unmeasurable last: no reading yet.
    ///
    /// Stable by arrival order, which is the manual order: ties, and the unmeasurable trailing group,
    /// keep the arrangement dragging made.
    /// </summary>
    public static List<ProviderSnapshot> ByRemainingUsage(IReadOnlyList<ProviderSnapshot> snapshots) =>
        [.. snapshots
            .Select((snapshot, index) => (snapshot, index, key: OrderKey(snapshot)))
            .OrderBy(entry => entry.key.Tier)
            .ThenByDescending(entry => entry.key.Remaining)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.snapshot)];

    /// <summary>
    /// (tier, headline-remaining): 0 usable, 1 shut with room, 2 fully empty, 3 nothing measurable.
    /// The other window is the weekly one where the provider has one apart from its headline.
    /// </summary>
    private static (int Tier, double Remaining) OrderKey(ProviderSnapshot snapshot)
    {
        if (snapshot.Headline?.UsedFraction is not { } used)
        {
            return (3, 0);
        }

        var remaining = 1 - used;
        if (used >= 1)
        {
            return (2, remaining);
        }

        if (snapshot.WeeklyWindow?.UsedFraction is { } other && other >= 1)
        {
            return (1, remaining);
        }

        return (0, remaining);
    }
}
