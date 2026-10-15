namespace QuotaDeck.Core.Policy;

/// <summary>
/// How recent a reading has to be to answer a fetch.
///
/// Port of Codenotch <c>Sources/Providers/UsageProvider.swift</c> (<c>enum UsageFreshness</c>).
///
/// Several providers hold a cheaper source that is older than a live one. Reusing it is right while
/// nothing is being spent and wrong the moment somebody looks, so the caller says which of those is
/// happening and each reader decides what it can do about it.
/// </summary>
public enum Freshness
{
    /// <summary>
    /// Whatever the reader's ordinary sources allow, cache included. The schedule's default: a
    /// number nobody is watching and nothing is moving.
    /// </summary>
    Standard,

    /// <summary>
    /// Old enough to be interesting is not good enough. A reader honouring this drops a cached
    /// reading it would otherwise have served and asks a source that can answer for right now.
    /// </summary>
    Live,

    /// <summary>
    /// Nothing held, at any age: the source itself, and a request spent on it. What "refresh now"
    /// and a click on a face ask for. Strictly more expensive than <see cref="Live"/> and not
    /// strictly better — a provider that rate limits answers a request too many with a 429, and its
    /// back-off then holds an *older* number than the cache would have. So it is what somebody asks
    /// for, never what the schedule decides on its own.
    /// </summary>
    FromSource,
}
