namespace QuotaDeck.Readers.Antigravity;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuotaDeck.Core.Model;
using QuotaDeck.Readers.Shared;

/// <summary>
/// The OMP agent database: the account's token and, when it has one, its usage history.
///
/// Port of Codenotch <c>Sources/Providers/AntigravityCredentials.swift</c>'s
/// <c>readOMPCredentials(at:)</c> and <c>AntigravityProvider.swift</c>'s
/// <c>ompUsageWindows(forEmail:)</c> (lines 330-425): newest 10 rows for
/// <c>provider = 'google-antigravity'</c>, one slot each for the Gemini and third-party hourly and
/// weekly windows, newest row per slot, missing slots as 0.0 — always all four, because the
/// standard four-window shape is what the face and the headline resolver expect.
///
/// The database is opened through the shared <see cref="SqliteReadOnly"/>, which is the one that
/// reads a WAL store without littering sidecar files into another tool's state directory. One gap
/// in that helper is filled here: its <c>Rows</c> takes no parameter, and the email filter must be
/// bound rather than spliced into the SQL, so the filtered form runs the same kind of command on
/// the connection the helper opened.
/// </summary>
public static class AntigravityOmp
{
    private const string UsageSql =
        "SELECT limit_id, label, window_label, used_fraction, resets_at FROM usage_history " +
        "WHERE provider = 'google-antigravity' ORDER BY recorded_at DESC, id DESC LIMIT 10;";

    private const string UsageByEmailSql =
        "SELECT limit_id, label, window_label, used_fraction, resets_at FROM usage_history " +
        "WHERE provider = 'google-antigravity' AND email = ?1 ORDER BY recorded_at DESC, id DESC LIMIT 10;";

    public static string DatabasePath(string home) => Path.Combine(home, ".omp", "agent", "agent.db");

    public static AntigravityCredentials? Credentials(string home) =>
        CredentialsFromDatabase(DatabasePath(home));

    public static AntigravityCredentials? CredentialsFromDatabase(string path)
    {
        using var connection = Open(path);
        var rows = SqliteReadOnly.Rows(
            connection,
            "SELECT data FROM auth_credentials WHERE provider = 'google-antigravity' ORDER BY updated_at DESC LIMIT 1;",
            columns: 1);
        if (rows.Count == 0 || rows[0][0] is not { Length: > 0 } data)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var access = root.TryGetProperty("access", out var accessProperty) ? accessProperty.GetString() : null;
            if (string.IsNullOrEmpty(access))
            {
                return null;
            }

            var expires = root.TryGetProperty("expires", out var expiry) && expiry.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)expiry.GetDouble())
                : DateTimeOffset.MaxValue;
            var projectId = root.TryGetProperty("projectId", out var project) ? project.GetString() : null;
            var email = root.TryGetProperty("email", out var mail) ? mail.GetString() : null;
            return new AntigravityCredentials(access, expires, "consumer", projectId, email);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<LimitWindow> UsageWindows(string home, string? email)
    {
        using var connection = Open(DatabasePath(home));
        if (connection is null)
        {
            return [];
        }

        var target = email;
        if (string.IsNullOrEmpty(target))
        {
            var latest = SqliteReadOnly.Column(
                connection,
                "SELECT email FROM usage_history WHERE provider = 'google-antigravity' AND email IS NOT NULL AND email != '' ORDER BY recorded_at DESC, id DESC LIMIT 1;");
            target = latest.Count > 0 ? latest[0] : null;
        }

        var withEmail = !string.IsNullOrEmpty(target);
        var rows = ReadUsageRows(connection, withEmail ? target : null);

        var newestBySlot = new Dictionary<string, (string Group, string Label, double Used, DateTimeOffset? Resets)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var limitId = row[0] ?? "";
            var label = row[1] ?? "";
            var windowLabel = (row[2] ?? "").ToLowerInvariant();
            var used = ParseNumber(row[3]) ?? 0;
            var resets = ParseNumber(row[4]) is { } milliseconds && milliseconds > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds)
                : (DateTimeOffset?)null;

            var isGemini = limitId.Contains(":google:", StringComparison.Ordinal)
                || label.Contains("Google", StringComparison.Ordinal);
            var isWeekly = windowLabel.Contains("weekly", StringComparison.Ordinal);
            var slot = isGemini
                ? isWeekly ? "gemini-weekly" : "gemini-hourly"
                : isWeekly ? "3p-weekly" : "3p-hourly";

            if (!newestBySlot.ContainsKey(slot))
            {
                newestBySlot[slot] = (
                    isGemini ? "Gemini Models" : "Claude and GPT models",
                    isWeekly ? "Weekly Limit" : "5-hour Limit",
                    used,
                    resets);
            }
        }

        var slots = new (string Id, string Group, string Label, bool Weekly)[]
        {
            ("gemini-hourly", "Gemini Models", "5-hour Limit", false),
            ("gemini-weekly", "Gemini Models", "Weekly Limit", true),
            ("3p-hourly", "Claude and GPT models", "5-hour Limit", false),
            ("3p-weekly", "Claude and GPT models", "Weekly Limit", true),
        };

        var windows = new List<LimitWindow>(slots.Length);
        foreach (var slot in slots)
        {
            var found = newestBySlot.TryGetValue(slot.Id, out var item);
            windows.Add(new LimitWindow
            {
                Id = slot.Id,
                Group = slot.Group,
                Label = slot.Label,
                UsedFraction = found ? item.Used : 0.0,
                ResetsAt = found ? item.Resets : null,
                Duration = slot.Weekly ? TimeSpan.FromDays(7) : null,
            });
        }

        return windows;
    }

    /// <summary>
    /// The shared opener, with the host-layout failure it documents turned into "unreadable": the
    /// native library can fail to load from a plugin host whose probing layout misses it, and an
    /// unreadable store is a state this reader already handles — not a failed fetch.
    /// </summary>
    private static SqliteConnection? Open(string path)
    {
        try
        {
            return SqliteReadOnly.Open(path);
        }
        catch (Exception exception) when (MissingNativeLibrary(exception))
        {
            return null;
        }
    }

    private static bool MissingNativeLibrary(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The usage query, with the email bound to <c>?1</c> when one is in hand. The shared
    /// <c>Rows</c> takes no parameter and the email must not be spliced into the SQL, so this one
    /// statement is run on the connection the shared helper opened; values come back as text in the
    /// same shape (SQL NULL as null).
    /// </summary>
    private static IReadOnlyList<string?[]> ReadUsageRows(SqliteConnection connection, string? email)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = email is null ? UsageSql : UsageByEmailSql;
            if (email is not null)
            {
                command.Parameters.AddWithValue("?1", email);
            }

            using var reader = command.ExecuteReader();

            var rows = new List<string?[]>();
            while (reader.Read())
            {
                var row = new string?[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    row[index] = reader.IsDBNull(index)
                        ? null
                        : reader.GetValue(index) switch
                        {
                            string text => text,
                            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                            { } value => value.ToString(),
                        };
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (SqliteException)
        {
            // A statement a schema cannot prepare answers an empty set, like the shared helper.
            return [];
        }
    }

    private static double? ParseNumber(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}
