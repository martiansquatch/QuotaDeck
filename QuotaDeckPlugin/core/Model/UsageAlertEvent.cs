namespace QuotaDeck.Core.Model;

/// <summary>The kind of usage alert event.</summary>
public enum UsageAlertKind
{
    Reset,
    SessionLimitReached,
    WeeklyLimitReached,
}

/// <summary>
/// One alert event for a provider's usage window (reset or limit reached).
///
/// Port of Codenotch <c>Sources/Model/UsageResetWatcher.swift</c> (<c>struct UsageAlertEvent</c>,
/// minus the glyph and the notice overrides — both are presentation).
/// </summary>
public sealed record UsageAlertEvent(
    UsageAlertKind Kind,
    string ProviderId,
    string ProviderName,
    string WindowLabel,
    double PreviousFraction,
    double CurrentFraction,
    DateTimeOffset? ResetsAt);
