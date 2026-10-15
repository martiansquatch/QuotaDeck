namespace QuotaDeck.Readers.Tests.OpenCode;

using Microsoft.Data.Sqlite;
using QuotaDeck.Readers.OpenCode;
using QuotaDeck.Readers.Shared;

/// <summary>
/// The read-only store helper, pinned to the two things it exists to get right: seeing the
/// write-ahead log when it can, and leaving the file exactly as found.
/// </summary>
public class SqliteReadOnlyTests
{
    /// <summary>
    /// The one thing that must be true before any of this works: the native library resolves in the
    /// tests project from the NuGet <c>runtimes/&lt;rid&gt;/native</c> layout. (The plugin's own bin
    /// layout is a different question — see the reader's DllNotFound fallback.)
    /// </summary>
    [Fact]
    public void TheNativeLibraryLoadsFromTheNugetRuntimesLayout()
    {
        using var directory = new TempDirectory();
        var path = directory.File("native.db");

        using (var database = new SqliteConnection($"Data Source={path}"))
        {
            database.Open();
            using var version = database.CreateCommand();
            version.CommandText = "SELECT sqlite_version()";
            Assert.False(string.IsNullOrEmpty(version.ExecuteScalar() as string));
        }

        using var readOnly = SqliteReadOnly.Open(path);
        Assert.NotNull(readOnly);
    }

    [Fact]
    public void AMissingFileOpensAsNull()
    {
        using var directory = new TempDirectory();
        Assert.Null(SqliteReadOnly.Open(directory.File("absent.db")));
    }

    [Fact]
    public void RowsReadValuesAsText()
    {
        using var directory = new TempDirectory();
        var path = directory.File("text.db");
        using var database = Open(path);
        Exec(database, "CREATE TABLE t (name text, count integer, missing text)");
        Exec(database, "INSERT INTO t VALUES ('a', 42, NULL)");

        var rows = SqliteReadOnly.Rows(database, "SELECT name, count, missing FROM t", 3);

        var row = Assert.Single(rows);
        Assert.Equal(["a", "42", ""], row);
    }

    [Fact]
    public void ColumnSkipsNullAndKeepsEmptyStrings()
    {
        using var directory = new TempDirectory();
        var path = directory.File("column.db");
        using var database = Open(path);
        Exec(database, "CREATE TABLE t (name text)");
        Exec(database, "INSERT INTO t VALUES ('x'), (NULL), ('')");

        var values = SqliteReadOnly.Column(database, "SELECT name FROM t ORDER BY rowid");

        Assert.Equal(["x", ""], values);
    }

    [Fact]
    public void AFailedStatementAnswersNoRowsRatherThanThrowing()
    {
        using var directory = new TempDirectory();
        var path = directory.File("failed.db");
        using var database = Open(path);
        Exec(database, "CREATE TABLE t (name text)");

        Assert.Empty(SqliteReadOnly.Rows(database, "SELECT * FROM missing_table", 2));
        Assert.Empty(SqliteReadOnly.Rows(database, "this is not sql", 2));
        Assert.Empty(SqliteReadOnly.Column(database, "SELECT name FROM missing_table"));
    }

    [Fact]
    public void ColumnBindsTheNumberedParameter()
    {
        using var directory = new TempDirectory();
        var path = directory.File("bind.db");
        using var database = Open(path);
        Exec(database, "CREATE TABLE t (name text)");
        Exec(database, "INSERT INTO t VALUES ('abc'), ('def')");

        var hit = SqliteReadOnly.Column(database, "SELECT name FROM t WHERE name = ?1", "def");

        Assert.Equal(["def"], hit);
    }

    /// <summary>
    /// With the owner still running, the WAL-seeing form reads a row committed after the reader
    /// opened — the write has not been checkpointed into the main file, and it is still the reading
    /// a face must show. The sidecars were already on disk, so opening and reading add nothing.
    /// </summary>
    [Fact]
    public void ALiveWritersUncheckpointedRowIsVisible()
    {
        using var directory = new TempDirectory();

        // The writer has committed once, so the sidecars exist before the reader arrives.
        var (databasePath, keeper) = WritableWalDatabase(directory, "later");
        using (keeper)
        {
            var before = FileNames(directory);

            using var readOnly = SqliteReadOnly.Open(databasePath);
            Assert.NotNull(readOnly);

            // Committed after the reader opened, and still only in the write-ahead log.
            Exec(keeper, "INSERT INTO t VALUES ('after-open')");

            Assert.Equal(
                ["base", "later", "after-open"],
                SqliteReadOnly.Column(readOnly, "SELECT x FROM t ORDER BY rowid"));
            Assert.Equal(before, FileNames(directory));
        }
    }

    /// <summary>
    /// The owner has quit and checkpointed: the directory holds only the store. The helper reads it
    /// without creating the <c>-shm</c>/<c>-wal</c> sidecars a plain <c>mode=ro</c> open writes into
    /// the owner's directory on its first statement — the shape the "never write under another
    /// tool's state" rule exists for.
    /// </summary>
    [Fact]
    public void AnAppQuitStoreIsReadWithoutCreatingSidecars()
    {
        using var directory = new TempDirectory();
        var path = directory.File("opencode.db");
        using (var setup = Open(path))
        {
            Exec(setup, "PRAGMA journal_mode=WAL");
            Exec(setup, "CREATE TABLE t (x text)");
            Exec(setup, "INSERT INTO t VALUES ('base')");
        }

        Assert.Equal(["opencode.db"], FileNames(directory));

        using (var readOnly = SqliteReadOnly.Open(path))
        {
            Assert.NotNull(readOnly);
            Assert.Equal(["base"], SqliteReadOnly.Column(readOnly, "SELECT x FROM t ORDER BY rowid"));
        }

        Assert.Equal(["opencode.db"], FileNames(directory));
    }

    /// <summary>
    /// When the WAL-seeing form cannot serve the store, the helper falls back to
    /// <c>immutable=1</c>, which ignores the write-ahead log — the older, checkpointed reading
    /// rather than an error.
    ///
    /// The forcing condition is a write-ahead log that exists but cannot be read; an unreadable
    /// <c>-shm</c> fails the same way. A store with no sidecars at all skips the WAL-seeing form
    /// entirely (see <see cref="AnAppQuitStoreIsReadWithoutCreatingSidecars"/>), because the attempt
    /// itself would leave a zero-length <c>-wal</c> behind.
    /// </summary>
    [Fact]
    public void AnUnreadableWalFallsBackToImmutable()
    {
        if (OperatingSystem.IsWindows())
        {
            // File permissions cannot make the log unreadable there; the fallback's forcing
            // condition does not exist, so the test has nothing to say.
            return;
        }

        using var writable = new TempDirectory();
        using var fixture = new TempDirectory();

        var (databasePath, keeper) = WritableWalDatabase(writable, "later");
        using (keeper)
        {
            CopyStore(databasePath, fixture.File("opencode.db"));
        }

        using (var readOnly = SqliteReadOnly.Open(fixture.File("opencode.db")))
        {
            // With the log readable this is the fresh, write-ahead-log-seeing view.
            Assert.Equal(["base", "later"], SqliteReadOnly.Column(readOnly, "SELECT x FROM t ORDER BY rowid"));
        }

        var wal = fixture.File("opencode.db-wal");
        var original = File.GetUnixFileMode(wal);
        File.SetUnixFileMode(wal, UnixFileMode.None);
        try
        {
            using var readOnly = SqliteReadOnly.Open(fixture.File("opencode.db"));
            Assert.NotNull(readOnly);

            // immutable=1 ignores the log, so only the checkpointed row is visible — still a
            // reading, just an older one. The alternative would be no reading at all.
            Assert.Equal(["base"], SqliteReadOnly.Column(readOnly, "SELECT x FROM t ORDER BY rowid"));
        }
        finally
        {
            File.SetUnixFileMode(wal, original);
        }
    }

    /// <summary>
    /// Opening and reading never writes: the store, its log and its sidecar keep their size and
    /// modification time, and no file is added beside them.
    /// </summary>
    [Fact]
    public void OpeningNeverCreatesOrModifiesFiles()
    {
        using var writable = new TempDirectory();
        var (databasePath, keeper) = WritableWalDatabase(writable, "later");
        using (keeper)
        {
            var beforeNames = FileNames(writable);
            var watched = new[] { databasePath, databasePath + "-wal", databasePath + "-shm" }
                .Where(File.Exists)
                .Select(path => (Path: path, Length: new FileInfo(path).Length, Modified: File.GetLastWriteTimeUtc(path)))
                .ToArray();
            Assert.Equal(3, watched.Length);

            using (var readOnly = SqliteReadOnly.Open(databasePath))
            {
                Assert.NotNull(readOnly);
                Assert.Equal(["base", "later"], SqliteReadOnly.Column(readOnly, "SELECT x FROM t ORDER BY rowid"));
            }

            foreach (var (path, length, modified) in watched)
            {
                Assert.Equal(length, new FileInfo(path).Length);
                Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
            }

            Assert.Equal(beforeNames, FileNames(writable));
            Assert.Empty(Directory.GetFiles(writable.Path, "*-journal"));
        }
    }

    /// <summary>The schema probe, ported but not depended on by the Go path.</summary>
    [Fact]
    public void TheSchemaProbeReadsTheTableNamesItWasWrittenFor()
    {
        using var directory = new TempDirectory();
        var path = directory.File("schema.db");
        using var database = Open(path);
        Exec(database, "CREATE TABLE message (id text)");
        Exec(database, "CREATE TABLE session_message (id text)");

        // 1.x keeps both `message` and `session_message`; `message` wins, because only 2.x has
        // `session_v2` and only 1.x has `message`.
        Assert.True(OpenCodeSchema.HasTable(database, "message"));
        Assert.False(OpenCodeSchema.HasTable(database, "session_v2"));
        Assert.Equal(OpenCodeSchemaVersion.V1, OpenCodeSchema.Of(database));

        Exec(database, "CREATE TABLE session_v2 (id text)");
        Assert.Equal(OpenCodeSchemaVersion.V2, OpenCodeSchema.Of(database));
    }

    [Fact]
    public void TheSchemaOfAnEmptyStoreIsNeither()
    {
        using var directory = new TempDirectory();
        var path = directory.File("empty-schema.db");
        using var database = Open(path);

        Assert.Null(OpenCodeSchema.Of(database));
        Assert.False(OpenCodeSchema.HasTable(database, "message"));
    }

    private static SqliteConnection Open(string path)
    {
        // Pooling off: these fixtures need Close() to actually checkpoint and release the file,
        // which is what tells a "the owning app quit" story apart from "it is still running".
        var database = new SqliteConnection($"Data Source={path};Pooling=False");
        database.Open();
        return database;
    }

    private static void Exec(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A store with one checkpointed row and one write-ahead-log row, the writer still open.
    /// </summary>
    private static (string Path, SqliteConnection Keeper) WritableWalDatabase(TempDirectory directory, params string[] logRows)
    {
        var path = directory.File("writable.db");
        using (var setup = Open(path))
        {
            Exec(setup, "PRAGMA journal_mode=WAL");
            Exec(setup, "CREATE TABLE t (x text)");
            Exec(setup, "INSERT INTO t VALUES ('base')");
        }

        var keeper = Open(path);
        foreach (var row in logRows)
        {
            using var insert = keeper.CreateCommand();
            insert.CommandText = "INSERT INTO t VALUES ($value)";
            insert.Parameters.AddWithValue("$value", row);
            insert.ExecuteNonQuery();
        }

        return (path, keeper);
    }

    private static void CopyStore(string source, string destination)
    {
        File.Copy(source, destination);
        File.Copy(source + "-wal", destination + "-wal");
        if (File.Exists(source + "-shm"))
        {
            File.Copy(source + "-shm", destination + "-shm");
        }
    }

    /// <summary>The directory's file names, sorted: what a read must leave exactly as it found it.</summary>
    private static string[] FileNames(TempDirectory directory) =>
        Directory.GetFiles(directory.Path)
            .Select(System.IO.Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;
}
