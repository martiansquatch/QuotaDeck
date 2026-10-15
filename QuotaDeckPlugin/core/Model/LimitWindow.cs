namespace QuotaDeck.Core.Model;

using System.Globalization;
using QuotaDeck.Core.Text;

/// <summary>
/// One metered window a provider exposes — Claude has two (the rolling session and the longer
/// all-models window), others have one.
///
/// Port of Codenotch <c>Sources/Model/UsageModel.swift</c> (<c>struct LimitWindow</c>: fields,
/// <c>isFiveHour</c>, <c>compact(_:)</c>, <c>summary(showingRemaining:)</c>).
/// </summary>
public sealed record LimitWindow
{
    public required string Id { get; init; }

    /// <summary>Set for a per-model or per-feature allowance rather than the account's own.</summary>
    public string? Group { get; init; }

    public required string Label { get; init; }

    /// <summary>
    /// 0…1+, where 1 means the limit is spent. Null when the provider reports what is left but never
    /// says what the limit was — a percentage would have to invent the denominator.
    /// </summary>
    public double? UsedFraction { get; init; }

    /// <summary>How many are left, when that is what the provider reports.</summary>
    public int? Remaining { get; init; }

    /// <summary>How many have been spent, when the provider counts up rather than down.</summary>
    public int? Used { get; init; }

    /// <summary>Optional provider-specific value for count-only rows.</summary>
    public string? Detail { get; init; }

    /// <summary>A count short enough to sit under a percentage, when the raw count is the wrong unit.</summary>
    public string? UsedText { get; init; }

    /// <summary>Nil when the provider does not say when the window rolls over.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Exact cycle length when known; optional to keep older archives readable.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>A band the provider insisted on, where its own reading would say something else.</summary>
    public UsageBand? BandOverride { get; init; }

    /// <summary>Structured money data for providers whose account is metered in money.</summary>
    public UsageMoneyBreakdown? Money { get; init; }

    /// <summary>Whether the headline should prefer <see cref="UsedText"/> over the percentage.</summary>
    public bool PrefersUsedText { get; init; }

    /// <summary>
    /// Whether this is a rolling five-hour window — the limit a coding session runs into first. Read
    /// from the length the provider reported rather than from an id, because every vendor names it
    /// differently. A minute's slack: some vendors send the window as a start and an end, and the
    /// difference is not always a whole number of seconds.
    /// </summary>
    public bool IsFiveHour =>
        this.Duration is { } duration && Math.Abs(duration.TotalSeconds - (5 * 3600)) < 60;

    /// <summary>
    /// A count short enough to sit inside a ring. Requests and credits are three or four digits and
    /// print verbatim; token counts run to seven, and "651061" under a ring is unreadable at that
    /// width. The threshold is 10 000 so no existing provider's number changes.
    /// </summary>
    public static string Compact(int count)
    {
        if (count < 10_000)
        {
            return count.ToString(CultureInfo.InvariantCulture);
        }

        if (count < 1_000_000)
        {
            return (count / 1_000).ToString(CultureInfo.InvariantCulture) + "k";
        }

        return (count / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>
    /// The line under the bar. Both ends stay on it either way; the setting only decides which end
    /// leads.
    /// </summary>
    public string Summary(bool showingRemaining = false)
    {
        if (this.UsedFraction is { } usedFraction)
        {
            var halves = Percent.Halves(usedFraction);
            return showingRemaining
                ? $"{halves.Left}% left · {halves.Used}% Used"
                : $"{halves.Used}% Used · {halves.Left}% left";
        }

        if (this.Remaining is { } remaining)
        {
            return remaining < 10_000
                ? $"{remaining} left"
                : $"{Compact(remaining)} left";
        }

        if (this.Used is { } used)
        {
            if (this.UsedText is { } text)
            {
                return text;
            }

            return used < 10_000 ? $"{used} used" : $"{Compact(used)} used";
        }

        return "No reading";
    }
}
