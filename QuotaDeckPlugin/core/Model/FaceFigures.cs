namespace QuotaDeck.Core.Model;

using QuotaDeck.Core.Settings;
using QuotaDeck.Core.Text;

/// <summary>
/// Everything one quota face shows for a snapshot, given the user's settings.
///
/// QuotaDeck-only helper (no single Swift counterpart): it gathers the rules a Codenotch surface
/// applies at the drawing site — <c>ProviderRing.swift</c>'s <c>band</c>/<c>displayFraction</c>/
/// <c>displayWeeklyFraction</c> (lines 63-122) and <c>ProviderSnapshot.headlineText(showingRemaining:)</c>
/// — so the renderer has one call and no arithmetic of its own.
///
/// Bands and thresholds are always judged by the used fraction, even when the figures are shown from
/// the remaining end. Ring sweeps are clamped to what can be drawn.
///
/// <para><see cref="Band"/> is null when there is no used fraction to judge — that is not "no
/// colour": the renderer must fall back to the provider's accent colour for the ring and the face.</para>
/// </summary>
public sealed record FaceFigures(string Headline, string? Secondary, double? RingSweep, UsageBand? Band)
{
    public static FaceFigures For(ProviderSnapshot snapshot, QuotaDeckSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        return For(snapshot, settings.ShowRemaining, settings.WatchLimit, settings.CriticalLimit);
    }

    public static FaceFigures For(
        ProviderSnapshot snapshot,
        bool showRemaining,
        double watchLimit = Bands.DefaultWatchLimit,
        double criticalLimit = Bands.DefaultCriticalLimit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var used = snapshot.UsedFraction;
        var headline = snapshot.HeadlineText(showRemaining);

        // The second figure is the weekly allowance's, from whichever end the face is reading — and
        // never the headline's own window, which is already the big number.
        string? secondary = null;
        if (snapshot.WeeklyWindow?.UsedFraction is { } weekly)
        {
            var figure = showRemaining ? Percent.Halves(weekly).Left : Percent.Text(weekly);
            secondary = "wk " + figure + "%";
        }

        var sweep = used is { } fraction
            ? Math.Clamp(Percent.Metered(fraction, showRemaining), 0, 1)
            : (double?)null;

        // A face with no denominator has no band to be wrong about; a block outranks everything,
        // since nothing on the account is usable while it lasts.
        var band = snapshot.Block is not null
            ? UsageBand.Exhausted
            : used is null
                ? (UsageBand?)null
                : snapshot.BandOverride ?? Bands.For(used.Value, watchLimit, criticalLimit);

        return new FaceFigures(headline, secondary, sweep, band);
    }
}
