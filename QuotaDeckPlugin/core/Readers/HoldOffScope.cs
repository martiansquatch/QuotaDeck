namespace QuotaDeck.Core.Readers;

/// <summary>
/// Whether a persisted 429 hold-off suppresses a reader's whole fetch, or only the steps the reader
/// applies it to itself.
///
/// Port of the difference between Codenotch's store and its providers: the deadline is a
/// per-provider <c>retryNoEarlierThan</c>, and how much it covers is the provider's own choice.
/// <c>ClaudeOAuthProvider</c> checks it inside <c>fetchFromKeychain(reusableFor:)</c> (lines
/// 303-309) — *after* the Desktop cache and CLI steps have run, so a held-off endpoint still lets a
/// cheaper local source answer — while Codex's <c>fetchSnapshot()</c> checks it before anything
/// else (lines 42-44).
/// </summary>
public enum HoldOffScope
{
    /// <summary>
    /// The base checks the deadline before <c>ReadAsync</c>: no source of the reader runs while the
    /// hold-off stands. The default.
    /// </summary>
    WholeRead,

    /// <summary>
    /// The base skips its own check and exposes <c>LoadHoldOffUntil()</c> and <c>IsHeldOff(now)</c>;
    /// the reader applies the deadline to the steps that need it — an endpoint fetch, say — and
    /// lets its other sources answer, calling <c>MarkRateLimitedSourceConsulted()</c> just before
    /// the held-off request so the base knows whether a returned reading or a replayed hold-off may
    /// move the ladder. The base records hold-offs from 429s and clears them on success exactly as
    /// it does for <see cref="WholeRead"/>.
    /// </summary>
    ReaderManaged,
}
