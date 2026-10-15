namespace QuotaDeck.Core.Model;

using QuotaDeck.Core.Text;

/// <summary>
/// A provider-reported money balance. The percentage is derived from these exact account amounts;
/// the amounts themselves come from the provider.
///
/// Port of Codenotch <c>Sources/Model/UsageModel.swift</c> (<c>struct UsageMoneyBreakdown</c>:
/// <c>funded</c>, <c>spentFraction</c>, <c>headlineText(showingRemaining:)</c>).
/// </summary>
public sealed record UsageMoneyBreakdown(string Currency, double Spent, double Remaining)
{
    /// <summary>What the account was funded with, as far as the two figures can tell.</summary>
    public double Funded => this.Spent + this.Remaining;

    /// <summary>Zero when nothing was funded — there is no share of zero to spend.</summary>
    public double SpentFraction =>
        this.Funded > 0 ? Math.Clamp(this.Spent / this.Funded, 0, 1) : 0;

    /// <summary>
    /// The headline: spent by default, what is left when the face is set that way. The left half
    /// derives from the <em>rounded</em> spent figure, the same rule as the window rows, so the two
    /// never disagree by a point.
    /// </summary>
    public string HeadlineText(bool showingRemaining) =>
        showingRemaining
            ? Percent.Halves(this.SpentFraction).Left + "% left"
            : Percent.Text(this.SpentFraction) + "% used";
}
