namespace QuotaDeck.Readers.Shared;

using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

/// <summary>
/// Read-only access to another app's SQLite store.
///
/// Port of Codenotch <c>Sources/Providers/SQLiteStore.swift</c> (<c>open(_:)</c> lines 21-33 and the
/// two <c>rows(...)</c> readers lines 38-69). OpenCode, Cursor and Devin all keep their state this
/// way and all run it in WAL mode, which makes opening it more delicate than it looks — and makes a
/// naive read-only open a writer into the owner's directory:
/// <list type="bullet">
/// <item><description><c>mode=ro</c> alone still creates the <c>-shm</c> and <c>-wal</c> sidecars on
/// its first statement when the store has none, so a pure read leaves two new files in another
/// tool's state directory. Codenotch's identical URI does the same; QuotaDeck's rule is "never
/// write under another tool's state".</description></item>
/// <item><description><c>mode=ro&amp;readonly_shm=1</c> sees the write-ahead log — a running app's
/// most recent writes — while refusing to create the <c>-shm</c> sidecar. But its first statement
/// still creates a zero-length <c>-wal</c> when the store has none, *before* it fails with
/// "unable to open database file". So this form is attempted only while both sidecars already exist
/// on disk (a running writer, or a crash before cleanup): then there is nothing to create and a log
/// worth reading. With them absent the store is either idle — its main file is the whole truth — or
/// unreadable, and the attempt is skipped rather than allowed to leave litter.</description></item>
/// <item><description><c>immutable=1</c> tells SQLite to ignore the write-ahead log entirely, which
/// is correct exactly when there is none to miss: the idle store's checkpointed state, and the
/// fallback when the WAL-seeing form cannot serve rows (an unreadable sidecar, say). A running app's
/// most recent writes are invisible through it, so it is never the first choice.</description></item>
/// </list>
///
/// Failure timing is why <see cref="Open"/> probes before handing a connection out: on this SQLite a
/// read-only WAL open can return success and only report SQLITE_CANTOPEN on the first *statement*.
/// The Swift helper checks the open result alone, which serves an unusable handle to its caller and
/// lets the empty row set below turn it into a silent zero.
///
/// <c>Pooling=False</c> is deliberate: Microsoft.Data.Sqlite pools connections by default, so a
/// disposed connection would keep its native handle (and its shared-memory mapping) alive in this
/// process. These reads are minutes apart, and a borrow that truly ends is this helper's contract —
/// the Swift version closes, and so does this one. <c>Mode=ReadOnly</c> alongside the URI's
/// <c>mode=ro</c> states the same thing twice for the two parsers involved: SQLite's URI handling
/// and Microsoft.Data.Sqlite's connection-string handling.
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
    /// The connection, or null when the file is missing or no open form works. Callers own the
    /// connection and must dispose it; opening it never creates or modifies a file next to the store.
    /// </summary>
    public static SqliteConnection? Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!File.Exists(path))
        {
            return null;
        }

        var forms = new List<string>(2);
        if (File.Exists(path + "-wal") && File.Exists(path + "-shm"))
        {
            forms.Add("mode=ro&readonly_shm=1");
        }

        forms.Add("immutable=1");

        foreach (var query in forms)
        {
            SqliteConnection? connection = null;
            try
            {
                connection = new SqliteConnection($"Data Source=file:{path}?{query};Mode=ReadOnly;Pooling=False");
                connection.Open();

                // A read-only WAL open can succeed and only fail on the *first statement* — the open
                // call does not touch the write-ahead log, the query does, and SQLITE_CANTOPEN comes
                // out of step(), not open_v2(). Probing here is what makes "WAL-seeing form first,
                // immutable second" true: a form that cannot serve rows is rejected before it is
                // handed out, instead of surfacing as an empty row set.
                using var probe = connection.CreateCommand();
                probe.CommandText = "SELECT count(*) FROM sqlite_master";
                probe.ExecuteScalar();

                return connection;
            }
            catch (SqliteException)
            {
                // This form is not available for this file; the next one may be. Neither failure is
                // an error by itself — the caller gets null when none works.
                connection?.Dispose();
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
