namespace QuotaDeck.Core.Text;

using System.Globalization;

/// <summary>
/// The time left before a reset, as short as a face needs it: "2h 05m", "47m", "09s".
///
/// Port of Codenotch <c>Sources/Model/ResetCopy.swift</c> (<c>countdown(to:now:locale:)</c> and
/// <c>nextCountdownChange(to:now:)</c>). The longer "Resets in …" sentence of
/// <c>ResetCopy.text(for:…)</c> is deliberately not ported: QuotaDeck's faces want the countdown
/// only, and the absolute date/time formatter it falls back to is a macOS calendar concern.
///
/// Truncated, never rounded up: this is read against a clock, so it may never claim more time than
/// there is — "09s" is always at least nine seconds, and "1h 00m" is gone the moment the hour is.
/// Seconds and minutes both carry two digits beside what follows them, so the width holds as they
/// tick over.
/// </summary>
public static class ResetCopy
{
    /// <summary>Nil once the reset has passed — a window that is over has no time left to show.</summary>
    public static string? Countdown(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var seconds = (resetsAt - now).TotalSeconds;
        if (seconds <= 0)
        {
            return null;
        }

        var minutes = (int)(seconds / 60);
        if (minutes < 1)
        {
            // The last minute counts in seconds: "<1m" was true for fifty-nine of them and said
            // nothing about which — and it is the minute somebody is actually watching for.
            return ((int)seconds).ToString("00", CultureInfo.InvariantCulture) + "s";
        }

        if (minutes < 60)
        {
            return minutes.ToString(CultureInfo.InvariantCulture) + "m";
        }

        // There is no days unit: a three-day window reads "72h 00m".
        return (minutes / 60).ToString(CultureInfo.InvariantCulture)
            + "h "
            + (minutes % 60).ToString("00", CultureInfo.InvariantCulture)
            + "m";
    }

    /// <summary>
    /// When the countdown next reads differently — the next whole second of time left inside the
    /// last minute, the next whole minute above it, or the reset itself. Nil once it has passed.
    ///
    /// A ticker can wake itself on this and nothing else, so it is the only thing deciding how
    /// often a countdown is redrawn: once a minute for four hours and fifty-nine minutes of a
    /// five-hour window, then once a second for the last sixty.
    /// </summary>
    public static DateTimeOffset? NextCountdownChange(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var seconds = (resetsAt - now).TotalSeconds;
        if (seconds <= 0)
        {
            return null;
        }

        var step = seconds <= 60 ? 1.0 : 60.0;
        return resetsAt - TimeSpan.FromSeconds(Math.Floor(seconds / step) * step);
    }
}
