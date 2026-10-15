namespace QuotaDeck.Core.Model;

/// <summary>
/// The colour a ring or bar takes at a given level of use.
///
/// Port of Codenotch <c>Sources/Model/UsageBand.swift</c> (<c>enum UsageBand</c>,
/// <c>band(for:watchLimit:criticalLimit:)</c>). Colours themselves are the plugin's concern; this is
/// only the vocabulary the faces and the alerts share.
/// </summary>
public enum UsageBand
{
    /// <summary>Under the watch limit.</summary>
    Ample,

    /// <summary>Getting close.</summary>
    Watch,

    /// <summary>Nearly out.</summary>
    Critical,

    /// <summary>Limit hit, waiting for the reset.</summary>
    Exhausted,
}

public static class Bands
{
    public const double DefaultWatchLimit = 0.50;
    public const double DefaultCriticalLimit = 0.70;

    /// <summary>
    /// The band a used fraction falls in. Always judged by what was spent, even when the face is set
    /// to show what is left — how bad a reading is is not a question about which end is displayed.
    /// </summary>
    public static UsageBand For(
        double usedFraction,
        double watchLimit = DefaultWatchLimit,
        double criticalLimit = DefaultCriticalLimit)
    {
        if (usedFraction < watchLimit)
        {
            return UsageBand.Ample;
        }

        if (usedFraction < criticalLimit)
        {
            return UsageBand.Watch;
        }

        return usedFraction < 1.0 ? UsageBand.Critical : UsageBand.Exhausted;
    }
}
