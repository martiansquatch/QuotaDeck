namespace QuotaDeck.Core.Settings;

/// <summary>
/// How a reader waits after a 429.
///
/// Codenotch's providers differ, and each difference is deliberate there:
/// <list type="bullet">
/// <item><description>
/// <see cref="Ladder"/> — the wait starts at a minute, doubles for each 429 in a row and stops at
/// fifteen, with the server's own hint honoured only as a floor. That is Claude
/// (<c>Sources/Providers/ClaudeOAuthProvider.swift</c>, <c>backoff(forAttempt:retryAfter:)</c>) and
/// OpenCode (<c>OpenCodeProvider.swift:206-210</c>).
/// </description></item>
/// <item><description>
/// <see cref="Flat"/> — a flat <c>max(60 s, hint)</c> on every 429, no doubling. That is Codex
/// (<c>Sources/Providers/CodexLocalProvider.swift</c>, <c>retryAfter(from:now:)</c> and the
/// sixty-second floor in the 429 branch), Devin (<c>DevinLocalProvider.swift:64</c>) and Muse
/// (<c>MuseLocalProvider.swift:96</c>).
/// </description></item>
/// </list>
/// Cursor has no backoff logic of its own.
/// </summary>
public enum RateLimitBackoff
{
    /// <summary>60 s doubling to a 900 s cap, the server's hint as a floor. The default.</summary>
    Ladder,

    /// <summary>A flat <c>max(60 s, hint)</c> on every 429.</summary>
    Flat,
}
