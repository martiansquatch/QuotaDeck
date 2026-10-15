namespace QuotaDeck.Readers.Shared;

using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

/// <summary>
/// Read-only access to another app's SQLite store.
///
/// Port of Codenotch <c>Sources/Providers/SQLiteStore.swift</c> (<c>open(_:)</c> lines 21-33 and the
/// two <c>rows(...)</c> readers lines 38-69). OpenCode, Cursor and Devin all keep their state this
/// way and all run it in WAL mode, which makes opening it more delicate than it looks — that is why
/// two forms are tried, in order:
/// <list type="bullet">
/// <item><description><c>mode=ro</c> sees the write-ahead log, so a running app's most recent writes
/// are visible. But it needs the <c>-shm</c> sidecar to do it, and that file exists only while the
/// owning app is running: once it has quit and checkpointed, a read-only open can fail outright with
/// "unable to open database file".</description></item>
/// <item><description><c>immutable=1</c> tells SQLite to ignore the write-ahead log entirely — a
/// running app's most recent writes are invisible, and a rotated token looks current. It is only
/// tried after <c>mode=ro</c> has failed, which is exactly when there is no write-ahead log left to
/// miss and ignoring it costs nothing.</description></item>
/// </list>
/// On SQLite 3.22+ a <c>mode=ro</c> open no longer fails when the <c>-shm</c> is merely *absent* —
/// it rebuilds a wal-index in heap memory — so the fallback carries the case where the sidecar
/// exists but cannot be read or the database refuses a read-only open for another reason (permissions,
/// an interrupted writer), rather than the merely-quiet case.
///
/// The native SQLite library ships under <c>runtimes/&lt;rid&gt;/native/</c> in the NuGet layout;
/// in a host whose probing layout misses it, <see cref="Open"/> throws <see cref="DllNotFoundException"/>
/// (often wrapped in <see cref="TypeInitializationException"/>) — the caller decides whether that is
/// fatal or whether to fall back to its other credential sources. Everything SQL-level fails closed:
/// a missing file opens as null, and a statement that will not prepare or run answers an empty row
/// set rather than an exception, because a reader written for one schema applied to another must
/// read as "nothing" rather than as a crash.
/// </summary>
public static class SqliteReadOnly
{
    /// <summary>
    /// The connection, or null when the file is missing or neither open form works. Callers own the
    /// connection and must dispose it.
    /// </summary>
    public static SqliteConnection? Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var query in new[] { "mode=ro", "immutable=1" })
        {
            try
            {
                var connection = new SqliteConnection($"Data Source=file:{path}?{query};Mode=ReadOnly");
                connection.Open();
                return connection;
            }
            catch (SqliteException)
            {
                // This form is not available for this file; the next one may be. Neither failure is
                // an error by itself — the caller gets null when both are gone.
            }
        }

        return null;
    }

    /// <summary>Every column of every row, as text.</summary>
    public static IReadOnlyList<string[]> Rows(SqliteConnection? connection, string sql, int columns)
    {
        ArgumentNullException.ThrowIfNull(sql);

        if (connection is null || columns <= 0)
        {
            return [];
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();

            var rows = new List<string[]>();
            while (reader.Read())
            {
                var count = Math.Min(columns, reader.FieldCount);
                var row = new string[count];
                for (var index = 0; index < count; index++)
                {
                    row[index] = Text(reader, index);
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (SqliteException)
        {
            // A statement a schema cannot prepare answers an empty set, like the Swift helper: the
            // silent-zero bug it exists to prevent is a *reader* problem, not this one's.
            return [];
        }
    }

    /// <summary>First column of every row, as text. SQL NULL is skipped, an empty string is kept.</summary>
    public static IReadOnlyList<string> Column(SqliteConnection? connection, string sql) =>
        ColumnCore(connection, sql, bind: null);

    /// <summary>
    /// First column of every row, as text, with one value bound to the numbered parameter
    /// <c>?1</c> — the SQL must name it the same way (<c>... WHERE name = ?1</c>), because
    /// Microsoft.Data.Sqlite binds named parameters only; an unnamed <c>?</c> cannot receive a value.
    /// </summary>
    public static IReadOnlyList<string> Column(SqliteConnection? connection, string sql, string bind) =>
        ColumnCore(connection, sql, bind);

    private static IReadOnlyList<string> ColumnCore(SqliteConnection? connection, string sql, string? bind)
    {
        ArgumentNullException.ThrowIfNull(sql);

        if (connection is null)
        {
            return [];
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (bind is not null)
            {
                command.Parameters.AddWithValue("?1", bind);
            }

            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read())
            {
                // sqlite3_column_text answers nil for SQL NULL and the Swift reader drops the row.
                if (!reader.IsDBNull(0))
                {
                    values.Add(Text(reader, 0));
                }
            }

            return values;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>
    /// One value as text, the way <c>sqlite3_column_text</c> would hand it over: numbers in
    /// invariant form, blobs decoded as UTF-8, NULL already answered as the empty string.
    /// </summary>
    private static string Text(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return string.Empty;
        }

        return reader.GetValue(index) switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            { } value => value.ToString() ?? string.Empty,
        };
    }
}
