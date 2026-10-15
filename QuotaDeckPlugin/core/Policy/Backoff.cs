namespace QuotaDeck.Core.Policy;

using System.Globalization;

/// <summary>
/// How long to wait after a 429, and when a hold-off is still in force.
///
/// Port of Codenotch <c>Sources/Providers/ClaudeOAuthProvider.swift</c>:
/// <c>backoff(forAttempt:retryAfter:)</c> (lines 569-582), <c>retryAfter(from:)</c> (lines 584-599)
/// and <c>shouldHoldOff(until:slack:now:)</c> (lines 175-192), all three of which are pure there so
/// the resonance they exist to break can be tested without a timer and a live endpoint.
///
/// The server's own hint is honoured only as a *floor-raiser*: it answers <c>Retry-After: 0</c>, and
/// obeying that literally means retrying immediately, which is what keeps you rate limited. So the
/// wait starts at a minute and doubles for each 429 in a row. Only our schedule is capped, never the
/// server's hint.
/// </summary>
public static class Backoff
{
    public const double FloorSeconds = 60;

    public const double CeilingSeconds = 15 * 60;

    /// <summary>
    /// A deadline that expires a moment after a poll would still wait out the next poll, silently
    /// turning a 60 s penalty into 120 s. One second of slack makes that collision impossible.
    /// </summary>
    public const double HoldOffSlackSeconds = 1;

    /// <summary>
    /// <c>max(min(900, 60 * 2^min(attempt, 4)), retryAfter ?? 0)</c> seconds.
    /// </summary>
    /// <param name="attempt">How many 429s have already happened in a row; 0 for the first one.</param>
    /// <param name="retryAfter">The server's own hint, which can only raise the floor.</param>
    public static TimeSpan Compute(int attempt, TimeSpan? retryAfter = null)
    {
        var doubled = Math.Min(CeilingSeconds, FloorSeconds * Math.Pow(2, Math.Min(Math.Max(attempt, 0), 4)));
        var hint = retryAfter?.TotalSeconds ?? 0;
        return TimeSpan.FromSeconds(Math.Max(doubled, hint));
    }

    /// <summary>True while the deadline is still far enough away to be worth respecting.</summary>
    public static bool ShouldHoldOff(
        DateTimeOffset? until,
        DateTimeOffset now,
        TimeSpan? slack = null)
    {
        if (until is not { } deadline)
        {
            return false;
        }

        var slackSeconds = slack?.TotalSeconds ?? HoldOffSlackSeconds;
        return (deadline - now).TotalSeconds > slackSeconds;
    }

    /// <summary>
    /// <c>Retry-After</c> is either a number of seconds or an HTTP date (RFC 1123). Nil when the
    /// header is missing or does not parse; a past date never yields a negative delay.
    /// </summary>
    public static TimeSpan? RetryAfter(string? header, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var trimmed = header.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        if (DateTimeOffset.TryParseExact(
                trimmed,
                "r",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date))
        {
            return TimeSpan.FromSeconds(Math.Max(0, (date - now).TotalSeconds));
        }

        return null;
    }
}
