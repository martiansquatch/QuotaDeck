namespace QuotaDeck.Core.Readers;

/// <summary>
/// The provider's accent colour, as the three channels a renderer needs.
///
/// Deliberately not an SDK colour type: core has no Logi reference, and a palette is data a test
/// can compare. The plugin converts it to its own colour type at the drawing site.
/// </summary>
public sealed record ProviderPalette(byte Red, byte Green, byte Blue);
