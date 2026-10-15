namespace QuotaDeck.Core.Settings;

using System.Text.Json.Serialization;
using QuotaDeck.Core.Policy;

/// <summary>
/// One provider's slice of the schedule.
///
/// No direct Swift counterpart: Codenotch's tick intervals are global constants on <c>UsageStore</c>
/// (<c>Sources/Model/UsageStore.swift</c>, initialiser lines 225-250). QuotaDeck makes them
/// per-reader — one provider costs a request per call and another is a local file read — with the
/// Codenotch values as the defaults.
/// </summary>
public sealed class ReaderConfig
{
    /// <summary>A switched-off provider is not fetched and keeps no reading.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Poll spacing while work is running. Defaults to <see cref="RefreshSchedule.BusyIntervalSeconds"/>.</summary>
    [JsonPropertyName("busyIntervalSeconds")]
    public double BusyIntervalSeconds { get; set; } = RefreshSchedule.BusyIntervalSeconds;

    /// <summary>Poll spacing while nothing is running. Defaults to <see cref="RefreshSchedule.IdleIntervalSeconds"/>.</summary>
    [JsonPropertyName("idleIntervalSeconds")]
    public double IdleIntervalSeconds { get; set; } = RefreshSchedule.IdleIntervalSeconds;

    /// <summary>How old a kept reading may be before it is dimmed. Defaults to 15 minutes.</summary>
    [JsonPropertyName("staleAfterMinutes")]
    public double StaleAfterMinutes { get; set; } = RefreshSchedule.StaleAfterMinutes;

    public TimeSpan BusyInterval => TimeSpan.FromSeconds(this.BusyIntervalSeconds);

    public TimeSpan IdleInterval => TimeSpan.FromSeconds(this.IdleIntervalSeconds);

    public TimeSpan StaleAfter => TimeSpan.FromMinutes(this.StaleAfterMinutes);

    /// <summary>
    /// Repairs values a hand-edited config could carry. Not a Swift port: the intervals are
    /// QuotaDeck's own surface, and a zero or negative one would either busy-poll or never poll.
    /// </summary>
    public void Normalize()
    {
        this.BusyIntervalSeconds = Positive(this.BusyIntervalSeconds, RefreshSchedule.BusyIntervalSeconds);
        this.IdleIntervalSeconds = Positive(this.IdleIntervalSeconds, RefreshSchedule.IdleIntervalSeconds);
        this.StaleAfterMinutes = Positive(this.StaleAfterMinutes, RefreshSchedule.StaleAfterMinutes);
    }

    private static double Positive(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : fallback;
}
