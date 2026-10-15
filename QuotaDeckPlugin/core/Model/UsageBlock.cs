namespace QuotaDeck.Core.Model;

/// <summary>
/// A limit that has been <em>reached</em>, even where the headline still shows room.
///
/// Port of Codenotch <c>Sources/Model/UsageModel.swift</c> (<c>struct UsageBlock</c>, minus the
/// tooltip's date-formatting <c>summary</c>, which is a macOS calendar concern).
///
/// Vendors meter some capabilities separately from the plan's main allowance, so "84% left" and
/// "paused until 4:13 PM" are both true at once. A ring that only knows the headline reports the
/// first and hides the second, which is the reading that actually stops you working.
/// </summary>
public sealed record UsageBlock
{
    /// <summary>What is paused, in the vendor's own terms.</summary>
    public required string Reason { get; init; }

    /// <summary>When it lifts, where the vendor says.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>
    /// Set when the snapshot derived this block from a spent weekly allowance rather than a provider
    /// reporting a pause. The face treats both the same; the limit watcher tells them apart, because
    /// a spent week has its own alert and must not also fire the session one.
    /// </summary>
    public bool IsWeeklyExhaustion { get; init; }
}
