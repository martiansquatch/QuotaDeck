namespace QuotaDeck.Core.Text;

using System.Globalization;

/// <summary>
/// How percentages read.
///
/// Port of Codenotch <c>Sources/Model/UsageModel.swift</c> (<c>enum Percent</c>: <c>halves(for:)</c>,
/// <c>text(for:)</c>, <c>whole(for:)</c>, <c>metered(_:showingRemaining:)</c>, <c>small(_:)</c>).
///
/// Whole percents above one — "12%", "104%" — because decimals there are noise. Below one, whole
/// percents collapse a real reading into "0%", the one number that looks most like "nothing used",
/// so both halves gain a tenth of a percent and still add up: 0.3% used is 99.7% left. A tenth of
/// nothing says so rather than pretending to be zero.
///
/// Rounding is half away from zero throughout (Swift's <c>rounded()</c>), and every number is
/// formatted with the invariant culture: the decimal point is not up to the system settings, any
/// more than "%" is.
/// </summary>
public static class Percent
{
    /// <summary>
    /// The two halves of a used-fraction, as display text.
    ///
    /// The left half derives from the <em>rounded</em> used half, not from the raw value — 9.5% used
    /// is "10% Used · 90% left", because that is how the dashboard the user is comparing against
    /// does the maths.
    /// </summary>
    public static (string Used, string Left) Halves(double fraction)
    {
        var value = fraction * 100;
        var fractional = (value > 0 && value < 1) || (value > 99 && value < 100);
        if (!fractional)
        {
            var used = Round(value);
            return (Invariant(used), Invariant(Math.Max(0, 100 - used)));
        }

        var left = Math.Max(0, 100 - value);
        // "<0.1" has no number to subtract from a hundred, so the far half makes the same claim
        // from its own end: ">99.9".
        return (Small(value), left > 99.9 ? ">99.9" : Small(left));
    }

    /// <summary>One percentage, as display text — the ring's label.</summary>
    public static string Text(double fraction)
    {
        var value = fraction * 100;
        return value > 0 && value < 1 ? Small(value) : Invariant(Round(value));
    }

    /// <summary>
    /// One percentage with no decimals, for surfaces where a tenth is noise at a glance. Rounding
    /// never lands on the two figures that would say something else happened: "0" when a little has
    /// been used, or "100" while there is still room.
    /// </summary>
    public static string Whole(double fraction)
    {
        var value = Math.Max(0, fraction * 100);
        if (value > 0 && value < 1)
        {
            return "<1";
        }

        if (value > 99 && value < 100)
        {
            return "99";
        }

        return Invariant(Round(value));
    }

    /// <summary>
    /// The share of the allowance the meters draw: spent by default, what is left when the face is
    /// set that way. The one rule behind the ring's sweep and every flipped figure — callers clamp
    /// to what they draw, so an overspent limit reads empty rather than negative. Bands and alerts
    /// never come through here: how bad a reading is, and whether the limit is spent, are questions
    /// about what was spent, whichever end the meters draw from.
    /// </summary>
    public static double Metered(double fraction, bool showingRemaining) =>
        showingRemaining ? 1 - fraction : fraction;

    /// <summary>One decimal place, with "&lt;0.1" and "&gt;99.9" standing in at the ends.</summary>
    private static string Small(double value)
    {
        if (value <= 0)
        {
            return "0";
        }

        var tenths = Math.Round(value * 10, MidpointRounding.AwayFromZero) / 10;
        if (tenths < 0.1)
        {
            return "<0.1";
        }

        if (tenths > 99.9)
        {
            return ">99.9";
        }

        return tenths.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Whole percents, with zero stripped of its sign: "(-0.001) * 100" rounds to negative zero,
    /// and .NET prints negative zero as "-0". A zero fraction has no direction, and "-0%" is not a
    /// percentage anyone reads.
    /// </summary>
    private static string Invariant(double value) =>
        (value == 0 ? 0 : value).ToString("0", CultureInfo.InvariantCulture);
}
