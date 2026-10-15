namespace QuotaDeck.Core.Faces;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;

/// <summary>
/// Band → colour, as plain bytes.
///
/// Port of Codenotch <c>Sources/DesignSystem/Palette.swift</c> (watch <c>F2FF00</c>, critical
/// <c>FF3F00</c>) and <c>UsageBand.color(accent:)</c>, with two QuotaDeck rules: an ample — or
/// absent — band draws the provider's own accent, and a stale or refused reading is drawn grey at
/// reduced alpha (Codenotch's ring does the same with <c>Palette.textSecondary</c> and a 0.45
/// opacity).
/// </summary>
public static class FaceColors
{
    public static readonly ProviderPalette Watch = new(0xF2, 0xFF, 0x00);

    public static readonly ProviderPalette Critical = new(0xFF, 0x3F, 0x00);

    public static readonly ProviderPalette Dimmed = new(0x80, 0x80, 0x80);

    /// <summary>The alpha a renderer draws a dimmed reading with (~38%), ring and text alike.</summary>
    public const byte DimmedAlpha = 0x60;

    /// <summary>
    /// The ring and text colour for a reading: the provider's accent for an ample (or absent) band,
    /// the watch and critical palette colours above it, and grey whenever the reading is dimmed —
    /// an old or refused number is quiet, not alarming.
    /// </summary>
    public static ProviderPalette Accent(UsageBand? band, ProviderPalette provider, bool dimmed = false)
    {
        if (dimmed)
        {
            return Dimmed;
        }

        return band switch
        {
            UsageBand.Watch => Watch,
            UsageBand.Critical or UsageBand.Exhausted => Critical,
            _ => provider,
        };
    }
}
