namespace QuotaDeck.Core.Faces;

using QuotaDeck.Core.Model;

/// <summary>
/// Everything one provider key needs, derived from a snapshot and the settings.
///
/// A plain record rather than an SDK type: core has no Logi reference, so the composer is a pure
/// function the renderer (and the tests) can consume without a keypad present.
/// </summary>
/// <param name="Headline">The big figure/word inside the ring; null only when there is no snapshot at all.</param>
/// <param name="Secondary">The small figure under the headline, e.g. "wk 59%"; null when the provider has no second window.</param>
/// <param name="Corner">The top-band countdown, "↻ 2h 05m"; null when the window has no reset date or it has passed.</param>
/// <param name="Label">The provider's display name, for the bottom band.</param>
/// <param name="Band">How bad the reading is; null when there is nothing to judge. A null band must fall back to the provider's accent colour.</param>
/// <param name="Dimmed">The reading is old or was refused: draw it quieter, never blank it.</param>
/// <param name="StatusText">The words for the no-reading case, e.g. "sign in"; null while any window exists.</param>
/// <param name="Blocked">A declared block or a spent window: the account cannot be used right now.</param>
public sealed record FaceModel(
    string? Headline,
    string? Secondary,
    string? Corner,
    string Label,
    UsageBand? Band,
    bool Dimmed,
    string? StatusText,
    bool Blocked);
