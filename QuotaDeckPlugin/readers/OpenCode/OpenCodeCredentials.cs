namespace QuotaDeck.Readers.OpenCode;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuotaDeck.Readers.Shared;

/// <summary>
/// The credential OpenCode itself signed in with, borrowed — never written.
///
/// Port of Codenotch <c>Sources/Providers/OpenCodeCredentials.swift</c>: the three sources and their
/// order (<c>load(in:)</c> lines 63-70), the file origins (<c>loadGoKey(from:)</c> lines 78-81,
/// <c>loadOAuth(from:)</c> lines 86-92, <c>loadFromDatabase(_:)</c> lines 97-125), the value shapes
/// (<c>credential(from:source:)</c> lines 132-159) and the small helpers (lines 163-186).
///
/// OpenCode 1.18 moved its sign-in into SQLite, and with OpenCode 2.x on the machine the
/// <c>auth.json</c> this used to read may simply be gone. Three sources, in the order that keeps the
/// account steady, first hit wins, re-read on every fetch:
/// <list type="number">
/// <item><description><c>auth.json</c> → <c>opencode-go</c> (<c>{"type":"api","key":…}</c>), the Go
/// plan's own API key and what OpenCode wrote before 1.18;</description></item>
/// <item><description><c>opencode.db</c> → the <c>credential</c> table, where the sign-in lives now
/// — either that same Go key, or an <c>opencode</c> OAuth sign-in;</description></item>
/// <item><description><c>auth.json</c> → the <c>opencode</c> OAuth entry, only a fallback: OpenCode
/// stops updating the file once the database holds the sign-in.</description></item>
/// </list>
/// Which one is found decides the usage endpoint, because each route refuses the other's credential.
/// Only OpenCode's own two ids are ever claimed; another integration's row (<c>openrouter</c>,
/// <c>openai</c>, …) is that vendor's key, and reading it under OpenCode's name would meter the
/// wrong account.
/// </summary>
public static class OpenCodeCredentials
{
    /// <summary>
    /// Where the credential was read from, for a settings line. Never the token, and — faithfully
    /// to the Swift record — the same "OpenCode" for all three sources.
    /// </summary>
    public const string SourceName = "OpenCode";

    /// <summary>
    /// Where the "SQLite native library is missing" fallback gets said out loud — once per process,
    /// because every fetch would otherwise repeat it. Console.Error by default; the host may replace
    /// it. QuotaDeck core exposes no logging seam a readers-project class can reach (noted as a gap
    /// in the PR that added this file).
    /// </summary>
    public static Action<string>? Warning { get; set; } =
        message => Console.Error.WriteLine($"opencode: {message}");

    private static int _nativeLibraryWarningSent;

    /// <summary>
    /// One stored credential value, in any of the shapes OpenCode has written.
    /// </summary>
    public sealed record Credential(
        string Token,
        bool OAuth,
        Uri? Console,
        string? Org,
        DateTimeOffset? Expires,
        string Source);

    /// <summary>
    /// OpenCode's data directory: <c>$XDG_DATA_HOME/opencode</c> when set and non-empty, else
    /// <c>~/.local/share/opencode</c> — the same resolution OpenCode itself makes. Computed from the
    /// environment on every call, like the Swift computed property.
    /// </summary>
    public static string ResolveDataDirectory()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg))
        {
            return Path.Combine(xdg, "opencode");
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return Path.Combine(home, ".local", "share", "opencode");
    }

    /// <summary>
    /// Is OpenCode on this machine at all? A machine that never had it shows no cell, which is a
    /// different answer from one that has it and is signed out.
    /// </summary>
    public static bool Present(string? directory = null)
    {
        var dir = directory ?? ResolveDataDirectory();
        return File.Exists(Path.Combine(dir, "auth.json")) || File.Exists(Path.Combine(dir, "opencode.db"));
    }

    /// <summary>The three sources above, in order. Null means OpenCode is installed but has no sign-in we may claim.</summary>
    public static Credential? Load(string? directory = null)
    {
        var dir = directory ?? ResolveDataDirectory();
        var authPath = Path.Combine(dir, "auth.json");

        if (LoadGoKey(authPath) is { } go)
        {
            return go;
        }

        if (LoadFromDatabase(Path.Combine(dir, "opencode.db")) is { } stored)
        {
            return stored;
        }

        return LoadOAuth(authPath);
    }

    /// <summary>
    /// Source 1: the Go plan's own API key from <c>auth.json</c>. Kept as its own entry point
    /// because the tests pin it, and because "the key this account was given" is the one shape that
    /// has never moved.
    /// </summary>
    public static Credential? LoadGoKey(string authPath)
    {
        using var root = ReadJsonObject(authPath);
        if (root is null || !root.RootElement.TryGetProperty("opencode-go", out var entry))
        {
            return null;
        }

        return FromEntry(entry, SourceName);
    }

    /// <summary>
    /// Source 3: the OAuth sign-in mirrored into <c>auth.json</c>. A fallback only — OpenCode stops
    /// writing it once the database holds the sign-in, so this covers a machine whose
    /// <c>opencode.db</c> cannot be opened, and nothing else.
    /// </summary>
    public static Credential? LoadOAuth(string authPath)
    {
        using var root = ReadJsonObject(authPath);
        if (root is null || !root.RootElement.TryGetProperty("opencode", out var entry))
        {
            return null;
        }

        if (entry.ValueKind != JsonValueKind.Object || StringProperty(entry, "type") != "oauth")
        {
            return null;
        }

        return FromEntry(entry, SourceName);
    }

    /// <summary>
    /// Source 2. Read-only, and never <c>immutable</c> while OpenCode is running: that would ignore
    /// the write-ahead log and serve a token OpenCode has already rotated.
    /// </summary>
    public static Credential? LoadFromDatabase(string databasePath)
    {
        SqliteConnection? database;
        try
        {
            database = SqliteReadOnly.Open(databasePath);
        }
        catch (Exception exception) when (MissingNativeLibrary(exception))
        {
            // The plugin's flattened bin layout can leave the NuGet runtimes/ folder behind; the
            // native library then fails to load. That is not a credential answer — fall through to
            // the auth.json sources instead of failing the fetch, and say so somewhere visible.
            // Once per process: the condition is a property of the host, not of the fetch.
            if (Interlocked.Exchange(ref _nativeLibraryWarningSent, 1) == 0)
            {
                Warning?.Invoke("SQLite native library unavailable; reading credentials from auth.json only");
            }

            return null;
        }

        if (database is null)
        {
            return null;
        }

        using (database)
        {
            // Newest first, so a rotated sign-in beats the one it replaced. The active flag is
            // honoured but a row that omits it counts as active: OpenCode has shipped both.
            var rows = SqliteReadOnly.Rows(
                database,
                """
                SELECT integration_id, value FROM credential
                WHERE integration_id IN ('opencode-go', 'opencode')
                  AND COALESCE(active, 1) != 0
                ORDER BY time_updated DESC
                """,
                columns: 2);

            // Go key first, then the OAuth sign-in: same order as the file sources, so a machine
            // holding both reads the same account either way.
            foreach (var wanted in new[] { "opencode-go", "opencode" })
            {
                foreach (var row in rows)
                {
                    if (row.Length < 2 || row[0] != wanted)
                    {
                        continue;
                    }

                    JsonDocument value;
                    try
                    {
                        value = JsonDocument.Parse(row[1]);
                    }
                    catch (JsonException)
                    {
                        // A row whose value is not JSON is not a credential; keep looking.
                        continue;
                    }

                    using (value)
                    {
                        // JSONSerialization.jsonObject(with:) without .fragmentsAllowed refuses a
                        // bare string (or number), so Swift's database reader skips a row like
                        // `"sk-old"` and tries the next one. The bare-string shape survives only in
                        // auth.json, where the value arrives already parsed — see FromEntry. Arrays
                        // parse there but produce no credential either, so both are skipped here.
                        if (value.RootElement.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (FromEntry(value.RootElement, SourceName) is { } found)
                        {
                            return found;
                        }
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// One stored credential value, in any of the shapes OpenCode has written: a bare string,
    /// <c>{"type":"api","key":…}</c>, or
    /// <c>{"type":"oauth","access":…,"expires":…,"metadata":{"orgID","server"}}</c>.
    ///
    /// Port of the Swift <c>credential(from:source:)</c>, which both auth.json sources feed
    /// directly. Database values arrive through <see cref="LoadFromDatabase"/>, which rejects the
    /// fragment shapes <c>JSONSerialization</c> refuses before calling this.
    /// </summary>
    public static Credential? FromEntry(JsonElement entry, string source)
    {
        // A bare string is the key itself; an empty one is worse than a missing one and counts as
        // missing (the Swift nonEmpty helper drops it, then the object check finds no object).
        if (entry.ValueKind == JsonValueKind.String)
        {
            return NonEmpty(entry.GetString()) is { } bare
                ? new Credential(bare, OAuth: false, Console: null, Org: null, Expires: null, source)
                : null;
        }

        if (entry.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (StringProperty(entry, "type") == "oauth")
        {
            if (NonEmpty(StringProperty(entry, "access")) is not { } token)
            {
                return null;
            }

            var metadata = entry.TryGetProperty("metadata", out var value) && value.ValueKind == JsonValueKind.Object
                ? value
                : (JsonElement?)null;

            var server = metadata is { } m ? NonEmpty(StringProperty(m, "server")) : null;
            return new Credential(
                token,
                OAuth: true,
                // Swift URL(string:) also admits a relative string; a console URL that cannot be
                // reached as an absolute URI is treated as absent, and the default console is asked
                // instead — the probe result is the same either way (it fails, and the 401 stands).
                Console: server is not null && Uri.TryCreate(server, UriKind.Absolute, out var parsed) ? parsed : null,
                Org: metadata is { } md ? NonEmpty(StringProperty(md, "orgID")) : null,
                // `expires` is a number in auth.json and a string in opencode.db.
                Expires: entry.TryGetProperty("expires", out var expires) ? Milliseconds(expires) : null,
                source);
        }

        foreach (var name in new[] { "key", "apiKey", "api_key", "token", "accessToken" })
        {
            if (NonEmpty(StringProperty(entry, name)) is { } token)
            {
                return new Credential(token, OAuth: false, Console: null, Org: null, Expires: null, source);
            }
        }

        return null;
    }

    private static JsonDocument? ReadJsonObject(string path)
    {
        try
        {
            // The Swift reader hands the bytes to JSONSerialization and casts the root to a
            // dictionary; anything else — missing file, malformed JSON, a non-object root — reads
            // as "no credential", never as an error.
            var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Non-empty, and not trimmed: an empty key is worse than a missing one, it is a request that
    /// cannot succeed being sent all the same.
    /// </summary>
    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Epoch milliseconds, from the number auth.json writes or the string opencode.db writes, as the
    /// Swift <c>milliseconds(_:)</c> reads both. Non-positive and non-finite values are not a date.
    /// </summary>
    private static DateTimeOffset? Milliseconds(JsonElement value)
    {
        double? number = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var numeric) => numeric,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text) => text,
            _ => null,
        };

        return number is { } milliseconds && double.IsFinite(milliseconds) && milliseconds > 0
            ? FromEpochMilliseconds(milliseconds)
            : null;
    }

    /// <summary>
    /// Swift's <c>Date(timeIntervalSince1970:)</c> holds arbitrarily large intervals and simply
    /// compares as far-future; the field's only use is an expiry test, so an interval beyond
    /// <see cref="DateTimeOffset.MaxValue"/> reads as MaxValue — far-future — rather than throwing
    /// the way a bare <c>AddMilliseconds</c> would. (Missing stays missing: only non-finite and
    /// non-positive values are dropped.)
    /// </summary>
    private static DateTimeOffset FromEpochMilliseconds(double milliseconds)
    {
        try
        {
            return DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    /// <summary>
    /// A missing native SQLite library surfaces as <see cref="DllNotFoundException"/>, often wrapped
    /// in <see cref="TypeInitializationException"/>/<see cref="System.Reflection.TargetInvocationException"/>
    /// by the provider's one-time static initialiser; the whole chain is walked.
    /// </summary>
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
}
