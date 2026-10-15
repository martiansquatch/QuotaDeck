namespace QuotaDeck.Core.Model;

/// <summary>
/// One alert-worthy crossing of a provider's limit.
///
/// Port of Codenotch <c>Sources/Model/ThresholdNotifier.swift</c> (<c>struct ThresholdAlert</c>,
/// minus the glyph: icons are the plugin's concern).
/// </summary>
public sealed record ThresholdAlert(
    int Threshold,
    string ProviderId,
    string ProviderName,
    string WindowLabel,
    int UsedPercent,
    DateTimeOffset? ResetsAt);
