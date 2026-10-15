namespace QuotaDeck.Core.Settings;

/// <summary>
/// How the face's corner countdown counts out what is left.
///
/// <list type="bullet">
/// <item><description>
/// <see cref="Hours"/> — Codenotch's own rendering, e.g. "72h 00m"
/// (<c>Sources/Model/ResetCopy.swift</c>'s <c>countdown(to:now:locale:)</c>, which has no days
/// unit). The default, so a face reads exactly like the macOS app's.
/// </description></item>
/// <item><description>
/// <see cref="Days"/> — "3d 00h" once at least a day is left, "23h 59m" below it. A QuotaDeck
/// option for the keypad's corner, which is narrower than the notch row.
/// </description></item>
/// </list>
/// </summary>
public enum CountdownStyle
{
    /// <summary>"72h 00m" — Codenotch's own form.</summary>
    Hours,

    /// <summary>"3d 00h" at a day and beyond, the hours form below it.</summary>
    Days,
}
