namespace QuotaDeck.Readers.Claude;

using System.Globalization;
using System.Text.Json;
using QuotaDeck.Core.Model;

// Port of Codenotch Sources/Providers/ClaudeOAuthProvider.swift, the decoding half:
// struct UsageResponse and its members (Limit, Scope, Window, Spend, ExtraUsage,
// limitWindows(readAt:), spendWindow(readAt:), utcMonth(containing:), duration(forKind:),
// label(forKind:), displayOrder(_:_:), headlineID(for:)) — lines 663-948.
//
// One decoder for the endpoint and Claude Desktop's cache, because they carry the *same* response:
// two decoders would be two chances for one of them to drift and silently start dropping windows.
//
// Reset credits (`cedar_ember`) are deliberately not read. Codenotch carries them in a field of its
// own snapshot; QuotaDeck.Core has no such field, so there is nowhere to put them, and half-reading
// the block would be worse than not reading it. The cache keys that carry the block are skipped
// whole — see ClaudeDesktopUsageCache.

/// <summary>The shape of <c>GET /api/oauth/usage</c>.</summary>
public sealed class ClaudeUsageResponse
{
    public const string SessionId = "session";

    public const string WeeklyAllId = "weekly_all";

    public const string SpendId = "spend";

    /// <summary>One entry of <c>limits[]</c>.</summary>
    public sealed record ClaudeLimit(string Kind, double Percent, DateTimeOffset? ResetsAt, ClaudeScope? Scope)
    {
        /// <summary>
        /// The window's own name: the model where the response names one, the kind's own wording
        /// otherwise. The model-specific weekly window comes back as <c>weekly_scoped</c> for
        /// *every* model, so the kind alone can only ever say "Scoped".
        /// </summary>
        public string WindowLabel
        {
            get
            {
                var named = Scope?.Model?.DisplayName?.Trim();
                return string.IsNullOrEmpty(named) ? LabelForKind(this.Kind) : named;
            }
        }
    }

    public sealed record ClaudeScope(ClaudeScopeModel? Model);

    public sealed record ClaudeScopeModel(string? DisplayName);

    /// <summary>One of the two named windows, for older responses without <c>limits</c>.</summary>
    public sealed record ClaudeWindow(double Utilization, DateTimeOffset? ResetsAt);

    /// <summary>A seat billed against credits rather than a plan.</summary>
    public sealed record ClaudeAmount(double? AmountMinor, string? Currency, int? Exponent)
    {
        /// <summary>Decimal places, so 20000 with exponent 2 is 200.00.</summary>
        public double? Value => this.AmountMinor is { } minor ? minor / Math.Pow(10, this.Exponent ?? 2) : null;
    }

    public sealed record ClaudeSpend(bool? Enabled, ClaudeAmount? Used, ClaudeAmount? Limit);

    /// <summary>Read for one thing only: whether the cap is a *monthly* one.</summary>
    public sealed record ClaudeExtraUsage(double? MonthlyLimit);

    public IReadOnlyList<ClaudeLimit>? Limits { get; private init; }

    public ClaudeWindow? FiveHour { get; private init; }

    public ClaudeWindow? SevenDay { get; private init; }

    public ClaudeSpend? Spend { get; private init; }

    public ClaudeExtraUsage? ExtraUsage { get; private init; }

    /// <summary>
    /// Decodes one response. Throws <see cref="JsonException"/> for a document that is not the
    /// shape at all — the reading itself must decode — while optional enrichment (a malformed
    /// scope, spend block or extra-usage block) is tolerated and dropped.
    /// </summary>
    public static ClaudeUsageResponse Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("usage response is not an object");
        }

        return new ClaudeUsageResponse
        {
            Limits = RootLimits(root),
            FiveHour = RootWindow(root, "five_hour"),
            SevenDay = RootWindow(root, "seven_day"),
            Spend = Tolerant(root, "spend", SpendBlock),
            ExtraUsage = Tolerant(root, "extra_usage", ExtraUsageBlock),
        };
    }

    /// <summary>
    /// <c>limits</c> is the forward-compatible shape — it grows new kinds as Anthropic adds them —
    /// so it is preferred, with the two named windows merged in where it is missing one.
    /// </summary>
    public List<LimitWindow> LimitWindows(DateTimeOffset readAt)
    {
        var windows = new List<LimitWindow>();
        foreach (var limit in this.Limits ?? [])
        {
            if (limit.ResetsAt is not { } resetsAt)
            {
                // A window with no reset time is not one we can render a countdown for.
                continue;
            }

            windows.Add(new LimitWindow
            {
                Id = limit.Kind,
                Label = limit.WindowLabel,
                UsedFraction = limit.Percent / 100,
                ResetsAt = resetsAt,
                Duration = DurationForKind(limit.Kind),
            });
        }

        // The named windows are merged in rather than used only as a fallback. An entry is "present
        // only while the API reports it and its resets_at has not passed", so a window that has just
        // rolled over disappears from `limits` while `five_hour` still carries it — relying on the
        // array alone loses the session exactly when it resets, which is when someone is most likely
        // to be looking.
        this.Merge(windows, this.FiveHour, SessionId, "Current session");
        this.Merge(windows, this.SevenDay, WeeklyAllId, "All models");

        // An Enterprise seat reports *only* the balance: `limits` comes back empty and both named
        // windows are null, so without it such a seat has no reading at all. Several top-level
        // objects — `amber_ladder`, `nimbus_quill`, `tangelo` — carry `limit_dollars` and
        // `resets_at` and look exactly like windows, but they are internal codenames whose meaning
        // is not published and can change without notice, so nothing else in the response is read.
        if (this.SpendWindow(readAt) is { } balance)
        {
            windows.Add(balance);
        }

        windows.Sort(DisplayOrderCompare);
        return windows;
    }

    /// <summary>
    /// Which window the headline means. The session where there is one; a credit seat reports no
    /// session window at all, so there is nothing to promote *over*, and declaring one anyway would
    /// leave the ring showing a dash beside a card full of numbers.
    /// </summary>
    public static string HeadlineId(IReadOnlyList<LimitWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        if (windows.Any(window => window.Id == SessionId))
        {
            return SessionId;
        }

        return windows.FirstOrDefault(window => window.Money is not null)?.Id ?? SessionId;
    }

    /// <summary>
    /// The credit balance as a window, or nil where the seat has none.
    ///
    /// The share is the two amounts divided rather than <c>spend.percent</c>, which is rounded to
    /// whole percent: at $2.97 of $200 that field says 1 while the true figure is 1.485%. The month
    /// is the calendar month in UTC, worked out from <paramref name="readAt"/> — the month the
    /// reading was written in, not the one it happens to be decoded in.
    /// </summary>
    public LimitWindow? SpendWindow(DateTimeOffset readAt)
    {
        if (this.Spend is not { } spend || spend.Enabled == false
            || spend.Used?.Value is not { } used
            || spend.Limit?.Value is not { } limit
            || limit <= 0)
        {
            return null;
        }

        (DateTimeOffset Start, DateTimeOffset End)? month =
            this.ExtraUsage?.MonthlyLimit is not null ? UtcMonth(readAt) : null;

        return new LimitWindow
        {
            Id = SpendId,
            Label = "Spend limit",
            UsedFraction = used / limit,
            Money = new UsageMoneyBreakdown(
                spend.Limit?.Currency ?? spend.Used?.Currency ?? "USD",
                used,
                Math.Max(limit - used, 0)),
            ResetsAt = month?.End,
            Duration = month is { } range ? range.End - range.Start : null,
        };
    }

    /// <summary>The calendar month in UTC that contains a moment.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) UtcMonth(DateTimeOffset date)
    {
        var start = new DateTimeOffset(date.Year, date.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (start, start.AddMonths(1));
    }

    /// <summary>The exact cycle length of a window kind, where the kind names one.</summary>
    public static TimeSpan? DurationForKind(string kind) => kind switch
    {
        SessionId => TimeSpan.FromHours(5),
        _ when kind.StartsWith("weekly_", StringComparison.Ordinal) => TimeSpan.FromDays(7),
        _ => null,
    };

    /// <summary>The frame's wording, for the kinds it drew.</summary>
    public static string LabelForKind(string kind) => kind switch
    {
        SessionId => "Current session",
        WeeklyAllId => "All models",
        "weekly_opus" => "Opus",
        "weekly_sonnet" => "Sonnet",

        // Only reached when the response names no model for the window, which is the one case where
        // there is nothing better to call it.
        "weekly_scoped" or "scoped" => "Scoped",
        _ => Capitalize(kind.Replace("weekly_", string.Empty, StringComparison.Ordinal).Replace('_', ' ')),
    };

    /// <summary>Session first, then the weekly windows, then anything else, and a balance last.</summary>
    public static Comparison<LimitWindow> DisplayOrderCompare => (left, right) =>
    {
        var (leftRank, rightRank) = (Rank(left.Id), Rank(right.Id));
        return leftRank == rightRank ? string.CompareOrdinal(left.Id, right.Id) : leftRank.CompareTo(rightRank);
    };

    /// <summary>
    /// The plan the way Claude names it: the credential says <c>enterprise</c>, the product says
    /// "Enterprise". Anything that is not one bare lowercase word — the CLI's "extra usage", say —
    /// is shown exactly as it came.
    /// </summary>
    public static string? PlanName(string? raw)
    {
        if (ClaudeProfile.NonEmptyPlan(raw) is not { } plan)
        {
            return null;
        }

        return plan.All(char.IsLower) ? char.ToUpperInvariant(plan[0]) + plan[1..] : plan;
    }

    private static int Rank(string id) => id switch
    {
        SessionId => 0,
        WeeklyAllId => 1,

        // Last: a balance is not one of the plan's periods, and on a seat that has both it is the
        // odd one out rather than another window.
        SpendId => 3,
        _ => 2,
    };

    private static string Capitalize(string value)
    {
        var words = value.Split(' ');
        for (var index = 0; index < words.Length; index++)
        {
            if (words[index].Length > 0)
            {
                words[index] = char.ToUpperInvariant(words[index][0]) + words[index][1..].ToLowerInvariant();
            }
        }

        return string.Join(' ', words);
    }

    private void Merge(List<LimitWindow> windows, ClaudeWindow? window, string id, string label)
    {
        if (window?.ResetsAt is not { } resetsAt || windows.Any(existing => existing.Id == id))
        {
            return;
        }

        windows.Add(new LimitWindow
        {
            Id = id,
            Label = label,
            UsedFraction = window.Utilization / 100,
            ResetsAt = resetsAt,
            Duration = DurationForKind(id),
        });
    }

    private static IReadOnlyList<ClaudeLimit>? RootLimits(JsonElement root)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (limits.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("limits is not an array");
        }

        var entries = new List<ClaudeLimit>();
        foreach (var element in limits.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("a limits entry is not an object");
            }

            // Everything above is the reading itself and must decode; the scope is only a nicer name
            // for it, so a shape change there falls back to the kind's wording instead of costing
            // the whole response.
            entries.Add(new ClaudeLimit(
                RequiredString(element, "kind"),
                RequiredNumber(element, "percent"),
                OptionalDate(element, "resets_at"),
                Tolerant(element, "scope", ScopeBlock)));
        }

        return entries;
    }

    private static ClaudeWindow? RootWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (window.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{name} is not an object");
        }

        return new ClaudeWindow(RequiredNumber(window, "utilization"), OptionalDate(window, "resets_at"));
    }

    private static ClaudeScope? ScopeBlock(JsonElement scope)
    {
        // A scope that is not an object at all (the string "fable", say) names nothing.
        if (!scope.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object)
        {
            return new ClaudeScope(null);
        }

        return new ClaudeScope(new ClaudeScopeModel(OptionalString(model, "display_name")));
    }

    private static ClaudeSpend? SpendBlock(JsonElement spend)
    {
        if (spend.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("spend is not an object");
        }

        return new ClaudeSpend(
            OptionalBool(spend, "enabled"),
            AmountBlock(spend, "used"),
            AmountBlock(spend, "limit"));
    }

    private static ClaudeAmount? AmountBlock(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var amount) || amount.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ClaudeAmount(
            OptionalNumber(amount, "amount_minor"),
            OptionalString(amount, "currency"),
            OptionalInt(amount, "exponent"));
    }

    private static ClaudeExtraUsage? ExtraUsageBlock(JsonElement extraUsage)
    {
        if (extraUsage.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("extra_usage is not an object");
        }

        return new ClaudeExtraUsage(OptionalNumber(extraUsage, "monthly_limit"));
    }

    /// <summary>
    /// An optional block that must never cost the usage reading: a malformed non-null one becomes
    /// nil rather than failing the decode of the windows beside it.
    /// </summary>
    private static T? Tolerant<T>(JsonElement parent, string name, Func<JsonElement, T?> decode)
        where T : class
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return decode(value);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new JsonException($"{name} is missing");

    private static double RequiredNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : throw new JsonException($"{name} is missing");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? OptionalNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static int? OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? OptionalBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset? OptionalDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || !TryDate(value.GetString()!, out var date))
        {
            // A present date that does not parse costs the response, exactly as it does in the
            // Swift decoder: the entry is half a reading, not a reading with a missing half.
            throw new JsonException($"{name} is not a date");
        }

        return date;
    }

    /// <summary>
    /// ISO 8601 with the offset the endpoint always sends — with or without fractional seconds,
    /// which <c>.iso8601</c> alone will not parse.
    /// </summary>
    public static bool TryDate(string text, out DateTimeOffset date) =>
        DateTimeOffset.TryParseExact(
            text,
            [
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
                "yyyy-MM-dd'T'HH:mm:sszzz",
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
            ],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out date);
}
