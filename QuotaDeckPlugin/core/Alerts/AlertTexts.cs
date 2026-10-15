namespace QuotaDeck.Core.Alerts;

using System.Globalization;
using QuotaDeck.Core.Model;

/// <summary>What one notification says.</summary>
public sealed record AlertMessage(string Title, string Body);

/// <summary>
/// The notification copy, ported from Codenotch <c>Sources/Model/ThresholdNotifier.swift</c>
/// (<c>ThresholdAlerts.deliver</c>, lines 88-113, and <c>UsageAlertNotifications.deliver</c>,
/// lines 122-148): the window label is lowercased inside the sentence, a spent limit names its
/// reset when it knows one, and a reset says the limit is available again.
/// </summary>
public static class AlertTexts
{
    /// <summary>A headline crossing of the 80% or 100% threshold.</summary>
    public static AlertMessage ForThreshold(ThresholdAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var label = alert.WindowLabel.ToLowerInvariant();
        if (alert.Threshold >= 100)
        {
            return new AlertMessage(
                $"{alert.ProviderName} limit reached",
                alert.ResetsAt is { } resetsAt
                    ? $"Its {label} limit is spent — resets {ShortTime(resetsAt)}"
                    : $"Its {label} limit is spent.");
        }

        return new AlertMessage(
            $"{alert.ProviderName} is at {alert.UsedPercent}%",
            $"{alert.UsedPercent}% of its {label} limit used.");
    }

    /// <summary>A session or weekly limit reached, or a window reset.</summary>
    public static AlertMessage ForUsage(UsageAlertEvent usageEvent)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);

        var label = usageEvent.WindowLabel.ToLowerInvariant();
        if (usageEvent.Kind == UsageAlertKind.Reset)
        {
            return new AlertMessage(
                $"{usageEvent.ProviderName} has reset",
                $"Its {label} limit is available again.");
        }

        return new AlertMessage(
            $"{usageEvent.ProviderName} limit reached",
            usageEvent.ResetsAt is { } resetsAt
                ? $"Its {label} limit is spent — resets {ShortTime(resetsAt)}"
                : $"Its {label} limit is spent.");
    }

    /// <summary>
    /// A short local clock time — the .NET counterpart of Swift's
    /// <c>.formatted(date: .omitted, time: .shortened)</c>. A fixed twelve-hour pattern rather than
    /// the culture's own: the plugin's copy is English-only for now, and a time that changes shape
    /// with the machine's locale would change the notification's meaning.
    /// </summary>
    private static string ShortTime(DateTimeOffset at) =>
        at.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture);
}
