namespace QuotaDeck.Readers.Codex;

using System.Globalization;
using System.Text.Json;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;

/// <summary>
/// Codex's account-wide rate-limit windows, from the <c>/wham/usage</c> payload.
///
/// Port of Codenotch <c>Sources/Providers/CodexUsage.swift</c> —
/// <c>windows(from:now:includeExtras:)</c> (lines 274-333), <c>plan(from:)</c> (lines 336-338),
/// <c>resetCredits(from:)</c> (lines 364-386), <c>limitWindow(id:group:from:fallback:now:)</c>
/// (lines 397-415), <c>appendExtra(_:primaryID:secondaryID:group:now:to:)</c> (lines 417-429),
/// <c>appendUnique(id:window:group:fallback:now:to:)</c> (lines 431-443), <c>isSpark(_:)</c>
/// (lines 445-449), <c>label(windowSeconds:fallback:)</c> (lines 451-471) — and the tolerant
/// decoders <c>Response</c> (lines 91-125), <c>SpendControl</c>, <c>CreditLimit</c> (lines 127-149),
/// <c>RateLimit</c> (lines 169-185), <c>AdditionalRateLimit</c>/<c>FailableAdditionalRateLimit</c>
/// (lines 187-198), <c>Window</c> (lines 200-224) and <c>ResetCredit</c> (lines 246-272).
///
/// Every number is decoded per field: a bad value becomes nil and never discards its siblings.
/// </summary>
public static class CodexUsage
{
    private static readonly string[] Iso8601Formats =
    [
        // Seven F's is .NET's own tick precision; the endpoint's stamps use three.
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK",
    ];

    /// <summary>
    /// The windows the rings and the hover card read.
    ///
    /// The main pair comes first — primary then secondary — so <c>windows[0]</c> stays the account
    /// primary; Spark and code-review extras are appended after it and skipped when switched off.
    /// A payload with no windows at all falls back to the credit seat, and a payload with neither
    /// windows nor a credit percentage is <see cref="ReaderError.NothingMetered"/>, not a silent
    /// empty success. Text that is not a JSON object is <see cref="ReaderError.BadResponse"/> with
    /// status 0.
    /// </summary>
    public static List<LimitWindow> Windows(string json, DateTimeOffset now, bool includeExtras = true)
    {
        var root = CodexJson.ParseObject(json)
            ?? throw new ReaderException(ReaderError.BadResponse(0));

        var windows = new List<LimitWindow>();
        if (CodexJson.Object(root, "rate_limit") is { } rateLimit)
        {
            AppendMain(windows, "primary", CodexJson.Object(rateLimit, "primary_window"), "primary", now);
            AppendMain(windows, "secondary", CodexJson.Object(rateLimit, "secondary_window"), "secondary", now);
        }

        if (includeExtras)
        {
            foreach (var extra in CodexJson.Array(root, "additional_rate_limits"))
            {
                if (extra.ValueKind != JsonValueKind.Object || !IsSpark(extra))
                {
                    continue;
                }

                var extraRateLimit = CodexJson.Object(extra, "rate_limit");
                AppendUnique(windows, "spark", CodexJson.Object(extraRateLimit, "primary_window"), "Spark", "primary", now);
                AppendUnique(windows, "spark-secondary", CodexJson.Object(extraRateLimit, "secondary_window"), "Spark", "secondary", now);
            }

            var codeReview = CodexJson.Object(root, "code_review_rate_limit");
            AppendUnique(windows, "code-review", CodexJson.Object(codeReview, "primary_window"), "Code review", "primary", now);
            AppendUnique(windows, "code-review-secondary", CodexJson.Object(codeReview, "secondary_window"), "Code review", "secondary", now);
        }

        // No rolling windows at all: a credit-based seat (Business/Team). Its cap is the ring.
        if (windows.Count == 0
            && CodexJson.Object(CodexJson.Object(root, "spend_control"), "individual_limit") is { } credit
            && CodexJson.LooseNumber(credit, "used_percent") is { } percent)
        {
            windows.Add(new LimitWindow
            {
                Id = "credits",
                Label = "Credits",
                UsedFraction = Math.Min(Math.Max(percent / 100, 0), 1),
                Remaining = CodexJson.LooseNumber(credit, "limit") is { } limit
                    && CodexJson.LooseNumber(credit, "used") is { } spent
                        ? (int)Math.Round(limit - spent, MidpointRounding.AwayFromZero)
                        : null,
                Used = CodexJson.LooseNumber(credit, "used") is { } used
                    ? (int)Math.Round(used, MidpointRounding.AwayFromZero)
                    : null,
                ResetsAt = CodexJson.LooseNumber(credit, "reset_at") is { } resetAt
                    ? CodexJson.Timestamp(resetAt)
                    : null,
            });
        }

        return windows.Count > 0
            ? windows
            : throw new ReaderException(ReaderError.NothingMetered("Codex reported no usage windows"));
    }

    /// <summary>
    /// The account tier the usage payload names, when it names a non-empty one
    /// (CodexUsage.swift:336-338 and <c>nonEmptyPlan</c> in <c>UsageModel.swift:5-11</c>).
    /// </summary>
    public static string? Plan(string json)
    {
        if (CodexJson.ParseObject(json) is not { } root)
        {
            return null;
        }

        var plan = CodexJson.Text(root, "plan_type")?.Trim();
        return string.IsNullOrEmpty(plan) ? null : plan;
    }

    /// <summary>
    /// The label for a window derived from the length Codex actually sent (CodexUsage.swift:458-471):
    /// a free plan has shown a 30-day primary window here, not the 5-hour one a paid plan reports.
    /// </summary>
    public static string Label(double windowSeconds, string fallback)
    {
        if (!(windowSeconds > 0))
        {
            return fallback == "primary" ? "Current session" : "Longer window";
        }

        var minutes = windowSeconds / 60;
        if (minutes < 60)
        {
            return $"{(int)minutes}m limit";
        }

        if (minutes < 60 * 24)
        {
            return $"{(int)(minutes / 60)}h limit";
        }

        var days = (int)Math.Round(minutes / (60 * 24), MidpointRounding.AwayFromZero);
        return days switch
        {
            7 => "Weekly limit",
            30 => "Monthly limit",
            _ => $"{days}d limit",
        };
    }

    /// <summary>
    /// Decodes the ChatGPT backend list of unused rate-limit resets (CodexUsage.swift:364-386).
    /// Throws only when the body is not JSON (or is a scalar, which Foundation's JSONSerialization
    /// also refuses), so an unfamiliar payload cannot fail the usage fetch; a JSON object or array
    /// that does not fit the shape reads as an empty list.
    /// </summary>
    public static CodexResetCredits ResetCredits(string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ReaderException(ReaderError.BadResponse(0));
        }

        if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            throw new ReaderException(ReaderError.BadResponse(0));
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            return new CodexResetCredits(0, []);
        }

        var credits = new List<CodexResetCredit>();
        foreach (var entry in CodexJson.Array(root, "credits"))
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !TryOptionalText(entry, "id", out var id)
                || !TryOptionalText(entry, "status", out var status))
            {
                // One unreadable credit must not discard the rest of a valid list
                // (FailableResetCredit, CodexUsage.swift:244-249).
                continue;
            }

            var expiresAt = CodexJson.Text(entry, "expires_at") is { } text ? ParseIso8601(text) : null;
            credits.Add(new CodexResetCredit(id, status, expiresAt));
        }

        var availableCount = root.TryGetProperty("available_count", out var count)
                             && count.ValueKind == JsonValueKind.Number
                             && count.TryGetInt32(out var value)
            ? value
            : (int?)null;

        return new CodexResetCredits(
            availableCount ?? credits.Count(credit => credit.Status == "available"),
            credits);
    }

    /// <summary>
    /// One main-pair window, appended whenever it carries a percentage
    /// (CodexUsage.swift:283-293 and <c>limitWindow</c>): a null <c>used_percent</c> on the 5 h
    /// window must not hide a good weekly one.
    /// </summary>
    private static void AppendMain(
        List<LimitWindow> windows,
        string id,
        JsonElement? window,
        string fallback,
        DateTimeOffset now)
    {
        if (window is { } value && ToLimitWindow(id, null, value, fallback, now) is { } item)
        {
            windows.Add(item);
        }
    }

    /// <summary>
    /// One extra window, appended under its namespaced id unless that id is already there —
    /// a payload that names Spark twice must not emit two 5 h rows with the same id
    /// (CodexUsage.swift:431-443).
    /// </summary>
    private static void AppendUnique(
        List<LimitWindow> windows,
        string id,
        JsonElement? window,
        string group,
        string fallback,
        DateTimeOffset now)
    {
        if (window is not { } value || windows.Any(existing => existing.Id == id))
        {
            return;
        }

        if (ToLimitWindow(id, group, value, fallback, now) is { } item)
        {
            windows.Add(item);
        }
    }

    private static LimitWindow? ToLimitWindow(
        string id,
        string? group,
        JsonElement window,
        string fallback,
        DateTimeOffset now)
    {
        if (CodexJson.Number(window, "used_percent") is not { } percent)
        {
            return null;
        }

        DateTimeOffset? resetsAt =
            CodexJson.Number(window, "reset_at") is { } resetAt ? CodexJson.Timestamp(resetAt)
            : CodexJson.Number(window, "reset_after_seconds") is { } after ? CodexJson.Offset(now, after)
            : null;

        var seconds = CodexJson.Number(window, "limit_window_seconds");
        return new LimitWindow
        {
            Id = id,
            Group = group,
            Label = Label(seconds ?? 0, fallback),
            UsedFraction = percent / 100,
            ResetsAt = resetsAt,
            Duration = CodexJson.FiniteDuration(seconds),
        };
    }

    /// <summary>
    /// Whether an extra names Spark in either field, case-insensitively — the additional limit can
    /// be named after the model ("GPT-5.3-Codex-Spark") rather than "Spark" (CodexUsage.swift:445-449).
    /// </summary>
    private static bool IsSpark(JsonElement extra) =>
        ContainsSpark(CodexJson.Text(extra, "limit_name"))
        || ContainsSpark(CodexJson.Text(extra, "metered_feature"));

    private static bool ContainsSpark(string? name) =>
        name is not null && name.Contains("spark", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>try container.decodeIfPresent(String.self, forKey:)</c> for one row
    /// (CodexUsage.swift:262-271): absent or null yields "", a wrong type fails the row.
    /// </summary>
    private static bool TryOptionalText(JsonElement entry, string name, out string value)
    {
        value = string.Empty;
        if (!entry.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>
    /// The backend mixes whole-second and fractional ISO-8601 stamps; each formatter rejects the
    /// other form (CodexUsage.swift:388-395).
    /// </summary>
    private static DateTimeOffset? ParseIso8601(string text)
    {
        foreach (var format in Iso8601Formats)
        {
            if (DateTimeOffset.TryParseExact(
                    text,
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
}
