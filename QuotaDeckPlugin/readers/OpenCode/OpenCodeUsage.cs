namespace QuotaDeck.Readers.OpenCode;

using System.Globalization;
using System.Text.Json;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;

/// <summary>
/// Parses <c>GET https://opencode.ai/zen/go/v1/usage</c>.
///
/// Port of Codenotch <c>Sources/Providers/OpenCodeUsage.swift</c> (<c>windows(fromJSON:now:)</c>
/// lines 41-62, <c>date(from:)</c> lines 64-69, <c>duration(for:endingAt:)</c> lines 71-79). The
/// account-wide Go plan windows, the same figures the OpenCode dashboard shows — recorded live
/// (secret redacted):
///
/// <code>
/// {"usage":{
///   "rolling":{"status":"ok","percent":0,"resetsAt":"2026-09-06T12:31:06.611Z"},
///   "weekly": {"status":"ok","percent":0,"resetsAt":"2026-09-07T00:00:00.611Z"},
///   "monthly":{"status":"ok","percent":0,"resetsAt":"2026-10-03T13:09:45.611Z"}}}
/// </code>
///
/// <c>percent</c> is *used*, matching the dashboard's "X% used" — the ring needs no inversion.
/// <c>resetsAt</c> carries milliseconds, which the plain ISO8601 form refuses to read, so both
/// fractional and plain forms are tried. <c>status</c> is ignored: a window is either numeric or it
/// is not there. A window without a numeric percent is dropped rather than invented; with none left
/// at all that is a bad response, not zeros.
/// </summary>
public static class OpenCodeUsage
{
    /// <summary>The Go plan's API key. This is the route the key is for.</summary>
    public const string Endpoint = "https://opencode.ai/zen/go/v1/usage";

    /// <summary>
    /// The same figures for an OAuth sign-in — the <c>inference/</c> routes OpenCode itself uses
    /// once signed in. The pairing is not interchangeable: <c>zen/go</c> answers an OAuth token 401
    /// even on an account that has Go, and <c>inference/go</c> answers a Go key the same way. Both
    /// 401s are indistinguishable from "no Go plan", so sending the wrong one reads as a signed-out
    /// app rather than as a bug. See <see cref="OpenCodeProvider"/>.
    /// </summary>
    public const string OAuthEndpoint = "https://opencode.ai/inference/go/v1/usage";

    /// <summary>Where an OAuth sign-in's <c>metadata.server</c> points when it names none.</summary>
    public const string DefaultConsole = "https://opencode.ai/console";

    /// <summary>
    /// Window ids in headline order. The ring means the rolling window — the current one, the same
    /// subject Claude's session and Codex's primary are.
    /// </summary>
    private static readonly (string Id, string Label)[] WindowList =
    [
        ("rolling", "5h limit"),
        ("weekly", "Weekly limit"),
        ("monthly", "Monthly limit"),
    ];

    /// <summary>
    /// The windows a usage body carries, in the fixed order above, or
    /// <see cref="ReaderError.BadResponse"/> with status 0 when nothing readable remains — bad JSON,
    /// a missing <c>usage</c> object, or every window dropped for want of a percent.
    /// </summary>
    public static IReadOnlyList<LimitWindow> Windows(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw BadResponse();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                throw BadResponse();
            }

            var windows = new List<LimitWindow>();
            foreach (var (id, label) in WindowList)
            {
                if (!usage.TryGetProperty(id, out var entry)
                    || entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("percent", out var percent)
                    || percent.ValueKind != JsonValueKind.Number
                    || !percent.TryGetDouble(out var used))
                {
                    continue;
                }

                var resetsAt = entry.TryGetProperty("resetsAt", out var stamp) && stamp.ValueKind == JsonValueKind.String
                    ? ParseDate(stamp.GetString()!)
                    : null;

                windows.Add(new LimitWindow
                {
                    Id = id,
                    Label = label,
                    UsedFraction = used / 100,
                    ResetsAt = resetsAt,
                    Duration = Duration(id, resetsAt),
                });
            }

            if (windows.Count == 0)
            {
                throw BadResponse();
            }

            return windows;
        }
    }

    private static ReaderException BadResponse() => new(ReaderError.BadResponse(0));

    private static DateTimeOffset? ParseDate(string stamp)
    {
        // Fractional seconds first, then plain: the plain formatter refuses milliseconds, and
        // reading only it would silently lose every reset time.
        foreach (var format in new[] { "yyyy-MM-dd'T'HH:mm:ss'.'FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK" })
        {
            if (DateTimeOffset.TryParseExact(
                    stamp,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// The window's cycle length when known: fixed for rolling and weekly; for monthly, one UTC
    /// Gregorian month before the reset, the same arithmetic the Swift UTC calendar does.
    /// </summary>
    private static TimeSpan? Duration(string id, DateTimeOffset? resetsAt)
    {
        if (id == "rolling")
        {
            return TimeSpan.FromSeconds(5 * 3600);
        }

        if (id == "weekly")
        {
            return TimeSpan.FromSeconds(7 * 86400);
        }

        if (id != "monthly" || resetsAt is not { } reset)
        {
            return null;
        }

        var utc = reset.ToUniversalTime();
        return reset - utc.AddMonths(-1);
    }
}
