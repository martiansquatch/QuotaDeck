namespace QuotaDeck.Readers.Antigravity;

using System.Globalization;
using System.Text.Json;

/// <summary>
/// How much Antigravity has actually been used, counted from its own transcripts.
///
/// Port of Codenotch <c>Sources/Providers/AntigravityActivity.swift</c>: every
/// <c>~/.gemini/antigravity*/brain</c> root, every trajectory's
/// <c>.system_generated/logs/transcript.jsonl</c>, lines whose <c>source</c> is <c>MODEL</c> and
/// whose UTC <c>created_at</c> falls on today's *local* date. A count, never a percentage — there
/// is no published limit to divide by.
/// </summary>
public static class AntigravityActivity
{
    /// <summary>Every Antigravity install's transcripts, not just the first one found.</summary>
    public static IReadOnlyList<string> TranscriptRoots(string home)
    {
        var gemini = Path.Combine(home, ".gemini");
        if (!Directory.Exists(gemini))
        {
            return [];
        }

        return Directory.GetDirectories(gemini)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name.StartsWith("antigravity", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => Path.Combine(gemini, name, "brain"))
            .Where(Directory.Exists)
            .ToList();
    }

    public static AntigravityActivitySummary Read(IReadOnlyList<string> roots, DateTimeOffset now) =>
        roots.Aggregate(
            new AntigravityActivitySummary(0, null),
            (total, root) =>
            {
                var one = Read(root, now);
                return new AntigravityActivitySummary(
                    total.RequestsToday + one.RequestsToday,
                    new[] { total.LastRequest, one.LastRequest }.Max());
            });

    /// <summary>One install's activity. A missing or unreadable root is zero, never an error.</summary>
    public static AntigravityActivitySummary Read(string root, DateTimeOffset now)
    {
        if (!Directory.Exists(root))
        {
            return new AntigravityActivitySummary(0, null);
        }

        var today = 0;
        DateTimeOffset? latest = null;
        foreach (var trajectory in Directory.GetDirectories(root))
        {
            var transcript = Path.Combine(trajectory, ".system_generated", "logs", "transcript.jsonl");
            string text;
            try
            {
                text = File.ReadAllText(transcript);
            }
            catch
            {
                continue;
            }

            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var rootElement = document.RootElement;
                    var source = rootElement.TryGetProperty("source", out var sourceProperty)
                        ? sourceProperty.GetString()
                        : null;
                    var createdAt = rootElement.TryGetProperty("created_at", out var createdProperty)
                        ? createdProperty.GetString()
                        : null;
                    if (source != "MODEL" || ParseTimestamp(createdAt) is not { } at)
                    {
                        continue;
                    }

                    if (latest is null || at > latest)
                    {
                        latest = at;
                    }

                    // `created_at` is UTC; the count is of the *local* day.
                    if (at.ToLocalTime().Date == now.ToLocalTime().Date)
                    {
                        today++;
                    }
                }
                catch (JsonException)
                {
                    // A malformed line is skipped rather than fatal.
                }
            }
        }

        return new AntigravityActivitySummary(today, latest);
    }

    /// <summary>ISO 8601 with or without fractional seconds; both are UTC.</summary>
    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}

/// <summary>One day's counted activity, and when the newest request was seen.</summary>
public sealed record AntigravityActivitySummary(int RequestsToday, DateTimeOffset? LastRequest)
{
    /// <summary>
    /// What the window is called. A bare "0" reads as the app having found nothing, which is also
    /// what the wrong directory looked like, so a quiet day names the last time it was used.
    /// </summary>
    public string Label(DateTimeOffset now)
    {
        if (this.RequestsToday > 0 || this.LastRequest is not { } lastRequest)
        {
            return "Requests today · no limit published";
        }

        return $"Requests today · last used {LastUsed(lastRequest, now)}";
    }

    /// <summary>
    /// Counted in calendar days, like the count itself; inside a day it reads as elapsed time
    /// ("3 hr ago"), the phrase the session list uses.
    /// </summary>
    private static string LastUsed(DateTimeOffset date, DateTimeOffset now)
    {
        var days = (int)(now.ToLocalTime().Date - date.ToLocalTime().Date).TotalDays;
        return days switch
        {
            < 1 => Ago(date, now),
            1 => "yesterday",
            _ => $"{days} days ago",
        };
    }

    /// <summary>Port of <c>Sources/Model/ElapsedCopy.swift</c>'s <c>ago(since:now:)</c>.</summary>
    private static string Ago(DateTimeOffset since, DateTimeOffset now)
    {
        var seconds = Math.Max(0, (now - since).TotalSeconds);
        if (seconds < 45)
        {
            return "just now";
        }

        var minutes = (int)Math.Round(seconds / 60);
        if (minutes < 60)
        {
            return $"{Math.Max(1, minutes)} min ago";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} hr ago" : $"{hours} hr {rest} min ago";
    }
}
