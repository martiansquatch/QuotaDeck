namespace QuotaDeck.Readers.Antigravity;

using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaDeck.Core.Model;

/// <summary>
/// Normalizes the quota envelopes shared by Antigravity's local language server and Cloud Code.
///
/// Port of Codenotch <c>Sources/Providers/AntigravityQuotaParser.swift</c>: envelope groups from the
/// first non-empty of <c>response.groups</c>, <c>summary.groups</c>, <c>groups</c>,
/// <c>quotaGroups</c>; else top-level <c>buckets</c> — legacy used/limit when any bucket has a
/// limit, per-model aggregation otherwise. Remaining fractions are validated into 0…1 and inverted
/// (the sources report what is *left*; the face shows what is spent). An unfamiliar shape yields
/// nothing, which routes the caller to its honest fallback rather than a ring built on a guess.
/// </summary>
public static class AntigravityQuotaParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<LimitWindow> Parse(string json, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(json);

        ResponseDto? response;
        try
        {
            response = JsonSerializer.Deserialize<ResponseDto>(json, Options);
        }
        catch (JsonException)
        {
            return [];
        }

        if (response is null)
        {
            return [];
        }

        var groups = response.Response?.Groups
            ?? response.Summary?.Groups
            ?? response.Groups
            ?? response.QuotaGroups
            ?? [];
        if (groups.Count > 0)
        {
            return GroupedWindows(groups);
        }

        var buckets = response.Buckets;
        if (buckets is null || buckets.Count == 0)
        {
            return [];
        }

        return buckets.Any(bucket => bucket.Limit is not null)
            ? LegacyWindows(buckets)
            : ModelWindows(buckets, now);
    }

    private static List<LimitWindow> GroupedWindows(List<GroupDto> groups)
    {
        var sorted = groups
            .Select((group, index) => (group, index))
            .OrderBy(entry => GroupRank(entry.group.DisplayName))
            .ThenBy(entry => entry.index);

        var windows = new List<LimitWindow>();
        foreach (var (group, _) in sorted)
        {
            windows.AddRange(BucketWindows(group));
        }

        return windows;
    }

    private static IEnumerable<LimitWindow> BucketWindows(GroupDto group)
    {
        var sorted = (group.Buckets ?? [])
            .Select((bucket, index) => (bucket, index))
            .OrderBy(entry => Cadence(entry.bucket))
            .ThenBy(entry => entry.index);

        foreach (var (bucket, _) in sorted)
        {
            if (bucket.Disabled == true)
            {
                continue;
            }

            var groupName = group.DisplayName;
            var id = Id(bucket, groupName);
            var reset = ParseTimestamp(bucket.ResetTime);
            var groupValue = string.IsNullOrEmpty(groupName) ? null : groupName;

            if (bucket.Fraction is { } remaining && remaining >= 0 && remaining <= 1)
            {
                yield return new LimitWindow
                {
                    Id = id,
                    Group = groupValue,
                    Label = Label(bucket, "Usage"),
                    UsedFraction = 1 - remaining,
                    ResetsAt = reset,
                    Duration = Duration(bucket),
                };

                continue;
            }

            if (bucket.Limit is { } limit && limit > 0
                && bucket.Used is { } used && used >= 0 && used <= limit * 1.5)
            {
                yield return new LimitWindow
                {
                    Id = id,
                    Group = groupValue,
                    Label = Label(bucket, id),
                    UsedFraction = used / limit,
                    ResetsAt = reset,
                    Duration = Duration(bucket),
                };
            }
        }
    }

    private static List<LimitWindow> LegacyWindows(List<BucketDto> buckets)
    {
        var windows = new List<LimitWindow>();
        foreach (var bucket in buckets)
        {
            if (bucket.Limit is not { } limit || limit <= 0
                || bucket.Used is not { } used || used < 0 || used > limit * 1.5)
            {
                continue;
            }

            var id = Id(bucket, null);
            windows.Add(new LimitWindow
            {
                Id = id,
                Label = Label(bucket, id),
                UsedFraction = used / limit,
                ResetsAt = ParseTimestamp(bucket.ResetTime),
                Duration = Duration(bucket),
            });
        }

        return windows;
    }

    private static List<LimitWindow> ModelWindows(List<BucketDto> buckets, DateTimeOffset now)
    {
        var geminiHourly = new List<Candidate>();
        var geminiWeekly = new List<Candidate>();
        var thirdPartyHourly = new List<Candidate>();
        var thirdPartyWeekly = new List<Candidate>();

        foreach (var bucket in buckets)
        {
            if (bucket.Fraction is not { } remaining || remaining < 0 || remaining > 1)
            {
                continue;
            }

            var model = Normalized(Id(bucket, null));
            if (model.Length == 0 || model.StartsWith("chat_", StringComparison.Ordinal))
            {
                continue;
            }

            var reset = ParseTimestamp(bucket.ResetTime);
            var resetIsWeekly = reset is { } resetDate && resetDate - now > TimeSpan.FromHours(24);
            var isWeekly = Cadence(bucket) == 1 || resetIsWeekly;
            var candidate = new Candidate(remaining, reset, isWeekly);

            if (model.Contains("gemini", StringComparison.Ordinal))
            {
                (isWeekly ? geminiWeekly : geminiHourly).Add(candidate);
            }
            else if (model.Contains("claude", StringComparison.Ordinal)
                     || model.Contains("gpt", StringComparison.Ordinal)
                     || model.Contains("openai", StringComparison.Ordinal))
            {
                (isWeekly ? thirdPartyWeekly : thirdPartyHourly).Add(candidate);
            }
        }

        var windows = new List<LimitWindow>();
        if (Aggregate(geminiHourly, "gemini-hourly", "Gemini Models", "5-hour Limit", weekly: false) is { } geminiH)
        {
            windows.Add(geminiH);
        }

        if (Aggregate(geminiWeekly, "gemini-weekly", "Gemini Models", "Weekly Limit", weekly: true) is { } geminiW)
        {
            windows.Add(geminiW);
        }

        if (Aggregate(thirdPartyHourly, "3p-hourly", "Claude and GPT models", "5-hour Limit", weekly: false) is { } thirdH)
        {
            windows.Add(thirdH);
        }

        if (Aggregate(thirdPartyWeekly, "3p-weekly", "Claude and GPT models", "Weekly Limit", weekly: true) is { } thirdW)
        {
            windows.Add(thirdW);
        }

        return windows;
    }

    private static LimitWindow? Aggregate(
        List<Candidate> candidates,
        string id,
        string group,
        string label,
        bool weekly)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var best = candidates.MinBy(candidate => candidate.Remaining)!;
        return new LimitWindow
        {
            Id = id,
            Group = group,
            Label = label,
            UsedFraction = 1 - best.Remaining,
            ResetsAt = best.Reset,
            Duration = weekly ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5),
        };
    }

    private static string Normalized(string value) =>
        value.Trim().ToLowerInvariant().Replace('_', '-');

    private static int GroupRank(string? value)
    {
        var normalized = Normalized(value ?? "");
        if (normalized.Contains("gemini", StringComparison.Ordinal))
        {
            return 0;
        }

        return normalized.Contains("claude", StringComparison.Ordinal)
               || normalized.Contains("gpt", StringComparison.Ordinal)
            ? 1
            : 2;
    }

    /// <summary>0 for a five-hour window, 1 for weekly, 2 for a cadence the envelope does not name.</summary>
    private static int Cadence(BucketDto bucket)
    {
        if (CadenceValue(bucket.Window) is { } window && IsWeeklyValue(window))
        {
            return 1;
        }

        if (CadenceValue(bucket.BucketId) is { } bucketId && IsWeeklyValue(bucketId))
        {
            return 1;
        }

        if (CadenceValue(bucket.DisplayName) is { } displayName && IsWeeklyValue(displayName))
        {
            return 1;
        }

        if (CadenceValue(bucket.Window) is { } windowSession && IsSessionValue(windowSession))
        {
            return 0;
        }

        if (CadenceValue(bucket.BucketId) is { } bucketSession && IsSessionValue(bucketSession))
        {
            return 0;
        }

        if (CadenceValue(bucket.DisplayName) is { } displaySession && IsSessionValue(displaySession))
        {
            return 0;
        }

        return 2;
    }

    private static string? CadenceValue(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var value = Normalized(raw);
        if (value.Length == 0)
        {
            return null;
        }

        return value.EndsWith(" limit", StringComparison.Ordinal)
            ? value[..^" limit".Length]
            : value;
    }

    private static bool IsWeeklyValue(string value) =>
        value == "weekly"
        || value.EndsWith("-weekly", StringComparison.Ordinal)
        || value.EndsWith(" weekly", StringComparison.Ordinal);

    private static bool IsSessionValue(string value) =>
        value is "session" or "5h" or "5-hour" or "five hour" or "five-hour" or "hourly"
        || value.EndsWith("-session", StringComparison.Ordinal)
        || value.EndsWith("-5h", StringComparison.Ordinal)
        || value.EndsWith("-5-hour", StringComparison.Ordinal)
        || value.EndsWith("-five-hour", StringComparison.Ordinal)
        || value.EndsWith("-hourly", StringComparison.Ordinal);

    private static TimeSpan? Duration(BucketDto bucket) => Cadence(bucket) switch
    {
        0 => TimeSpan.FromHours(5),
        1 => TimeSpan.FromDays(7),
        _ => null,
    };

    private static string Label(BucketDto bucket, string fallback)
    {
        var value = bucket.DisplayName ?? fallback;
        if (value.EndsWith(" Remaining", StringComparison.Ordinal))
        {
            value = value[..^" Remaining".Length];
        }

        return value == "Five Hour Limit" ? "5-hour Limit" : value;
    }

    private static string Id(BucketDto bucket, string? group)
    {
        foreach (var value in new[] { bucket.BucketId, bucket.ModelId, bucket.Name, group })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "quota";
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;

    private sealed record Candidate(double Remaining, DateTimeOffset? Reset, bool IsWeekly);

    private sealed class ResponseDto
    {
        public List<BucketDto>? Buckets { get; set; }

        public List<GroupDto>? Groups { get; set; }

        public List<GroupDto>? QuotaGroups { get; set; }

        public GroupBodyDto? Response { get; set; }

        public GroupBodyDto? Summary { get; set; }
    }

    private sealed class GroupBodyDto
    {
        public List<GroupDto>? Groups { get; set; }
    }

    private sealed class GroupDto
    {
        public string? DisplayName { get; set; }

        public List<BucketDto>? Buckets { get; set; }
    }

    private sealed class BucketDto
    {
        public string? ModelId { get; set; }

        public string? BucketId { get; set; }

        public string? Name { get; set; }

        public string? DisplayName { get; set; }

        public double? RemainingFraction { get; set; }

        public RemainingDto? Remaining { get; set; }

        public double? Used { get; set; }

        public double? Limit { get; set; }

        public string? ResetTime { get; set; }

        public string? Window { get; set; }

        public bool? Disabled { get; set; }

        public double? Fraction => this.RemainingFraction ?? this.Remaining?.Fraction;
    }

    private sealed class RemainingDto
    {
        public double? RemainingFraction { get; set; }

        [JsonPropertyName("case")]
        public string? OneofCase { get; set; }

        public double? Value { get; set; }

        public double? Fraction =>
            this.RemainingFraction
            ?? (this.OneofCase == "remainingFraction" ? this.Value : null);
    }
}
