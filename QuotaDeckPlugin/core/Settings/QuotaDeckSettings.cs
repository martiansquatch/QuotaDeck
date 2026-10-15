namespace QuotaDeck.Core.Settings;

using System.Text.Json.Serialization;

/// <summary>
/// QuotaDeck's core settings: what the faces show, where the bands sit, and which providers exist.
///
/// The threshold defaults and clamping rules are a port of Codenotch
/// <c>Sources/Settings/Preferences.swift</c>: <c>watchLimit</c>'s and <c>criticalLimit</c>'s
/// <c>didSet</c> clamps (lines 368-382) and the load-time repair in the initialiser (lines 1004-1010,
/// where a stored pair that crossed over is fixed the same way).
/// </summary>
public class QuotaDeckSettings
{
    /// <summary>
    /// Deliberate QuotaDeck preference, and deliberately the opposite of Codenotch's
    /// <c>showsRemainingInNotch</c> default (false): a quota face is read to find out what is left.
    /// </summary>
    [JsonPropertyName("showRemaining")]
    public bool ShowRemaining { get; set; } = true;

    [JsonPropertyName("watchLimit")]
    public double WatchLimit { get; set; } = Model.Bands.DefaultWatchLimit;

    [JsonPropertyName("criticalLimit")]
    public double CriticalLimit { get; set; } = Model.Bands.DefaultCriticalLimit;

    /// <summary>Per-provider schedule and enablement, keyed by reader id.</summary>
    [JsonPropertyName("providers")]
    public Dictionary<string, ReaderConfig> Providers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The order the faces sit in: provider ids, first is leftmost.</summary>
    [JsonPropertyName("order")]
    public List<string> Order { get; set; } = [];

    /// <summary>
    /// The watch limit's own clamp: between 1% and just below the critical limit, so the two can
    /// never cross and the band between them is never empty.
    /// </summary>
    public static double ClampWatchLimit(double watchLimit, double criticalLimit) =>
        Math.Min(Math.Max(watchLimit, 0.01), criticalLimit - 0.01);

    /// <summary>The critical limit's own clamp: between just above the watch limit and 100%.</summary>
    public static double ClampCriticalLimit(double criticalLimit, double watchLimit) =>
        Math.Min(Math.Max(criticalLimit, watchLimit + 0.01), 1.0);

    /// <summary>
    /// The load-time repair. The property setters cannot do it here — an object initialiser runs no
    /// clamping code — so a stored pair that crossed over is fixed when the settings are read.
    /// A non-finite value would survive the comparisons and poison every band, so it is repaired too.
    /// </summary>
    public void Normalize()
    {
        this.Providers ??= new Dictionary<string, ReaderConfig>(StringComparer.Ordinal);
        this.Order ??= [];

        foreach (var key in this.Providers.Keys.ToList())
        {
            if (this.Providers[key] is null)
            {
                this.Providers[key] = new ReaderConfig();
            }
            else
            {
                this.Providers[key].Normalize();
            }
        }

        var storedCritical = double.IsFinite(this.CriticalLimit)
            ? this.CriticalLimit
            : Model.Bands.DefaultCriticalLimit;
        var storedWatch = double.IsFinite(this.WatchLimit)
            ? this.WatchLimit
            : Model.Bands.DefaultWatchLimit;

        // `didSet` does the clamping at runtime, and it does not run for these assignments, so the
        // pair is repaired here instead.
        var critical = Math.Min(Math.Max(storedCritical, 0.02), 1.0);
        this.CriticalLimit = critical;
        this.WatchLimit = Math.Min(Math.Max(storedWatch, 0.01), critical - 0.01);
    }

    /// <summary>The provider's config, creating and storing a default one on first use.</summary>
    public ReaderConfig ForProvider(string providerId)
    {
        if (!this.Providers.TryGetValue(providerId, out var config) || config is null)
        {
            config = new ReaderConfig();
            this.Providers[providerId] = config;
        }

        return config;
    }
}
