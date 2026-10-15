namespace QuotaDeck.Readers.Tests.Antigravity;

using Microsoft.Data.Sqlite;
using QuotaDeck.Readers.Antigravity;

/// <summary>
/// The OMP usage-history slots, ported from Codenotch <c>AntigravityProvider.swift</c>
/// (<c>ompUsageWindows(forEmail:)</c>, lines 330-425): newest ten rows, newest per slot, always all
/// four slots, empty ones as 0.0.
/// </summary>
public class AntigravityOmpTests
{
    private static string NewHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "qd-antigravity-omp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    private static void CreateDatabase(string home, bool withRows)
    {
        var path = AntigravityOmp.DatabasePath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE usage_history (provider TEXT, email TEXT, limit_id TEXT, label TEXT, " +
                "window_label TEXT, used_fraction REAL, resets_at REAL, recorded_at INTEGER, id INTEGER);";
            create.ExecuteNonQuery();
        }

        if (!withRows)
        {
            return;
        }

        void Insert(string email, string limitId, string label, string window, double used, double resetsAt, long recordedAt, int id)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO usage_history (provider, email, limit_id, label, window_label, used_fraction, resets_at, recorded_at, id) " +
                "VALUES ('google-antigravity', @email, @limit, @label, @window, @used, @resets, @recorded, @id);";
            insert.Parameters.AddWithValue("@email", email);
            insert.Parameters.AddWithValue("@limit", limitId);
            insert.Parameters.AddWithValue("@label", label);
            insert.Parameters.AddWithValue("@window", window);
            insert.Parameters.AddWithValue("@used", used);
            insert.Parameters.AddWithValue("@resets", resetsAt);
            insert.Parameters.AddWithValue("@recorded", recordedAt);
            insert.Parameters.AddWithValue("@id", id);
            insert.ExecuteNonQuery();
        }

        Insert("user@example.com", "gemini-5h:google:abc", "Google Gemini 5h", "5-hour", 0.42, 1_800_000_000_000, 100, 1);
        Insert("user@example.com", "gemini-5h:google:abc", "Google Gemini 5h", "5-hour", 0.17, 1_800_000_100_000, 200, 2);
        Insert("user@example.com", "gemini-weekly:google:abc", "Google Gemini weekly", "weekly", 0.30, 1_800_000_200_000, 300, 3);
        Insert("user@example.com", "claude-5h", "Claude 5h", "5-hour", 0.61, 0, 400, 4);
    }

    [Fact]
    public void AMissingDatabaseIsEmpty()
    {
        Assert.Empty(AntigravityOmp.UsageWindows(NewHome(), null));
    }

    [Fact]
    public void AnEmptyDatabaseReturnsAllFourZeroSlots()
    {
        var home = NewHome();
        CreateDatabase(home, withRows: false);

        var windows = AntigravityOmp.UsageWindows(home, null);

        Assert.Equal(new[] { "gemini-hourly", "gemini-weekly", "3p-hourly", "3p-weekly" }, windows.Select(window => window.Id));
        Assert.All(windows, window => Assert.Equal(0.0, window.UsedFraction!.Value));
        Assert.Equal(TimeSpan.FromDays(7), windows[1].Duration);
        Assert.Null(windows[0].Duration);
    }

    [Fact]
    public void TheNewestRowPerSlotWins()
    {
        var home = NewHome();
        CreateDatabase(home, withRows: true);

        var windows = AntigravityOmp.UsageWindows(home, "user@example.com");

        Assert.Equal(0.17, windows.Single(window => window.Id == "gemini-hourly").UsedFraction!.Value, 4);
        Assert.Equal(0.30, windows.Single(window => window.Id == "gemini-weekly").UsedFraction!.Value, 4);
        Assert.Equal(0.61, windows.Single(window => window.Id == "3p-hourly").UsedFraction!.Value, 4);
        Assert.Equal(0.0, windows.Single(window => window.Id == "3p-weekly").UsedFraction!.Value, 4);
    }

    [Fact]
    public void TheResetTimeIsMilliseconds()
    {
        var home = NewHome();
        CreateDatabase(home, withRows: true);

        var windows = AntigravityOmp.UsageWindows(home, "user@example.com");

        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_100_000),
            windows.Single(window => window.Id == "gemini-hourly").ResetsAt);
        Assert.Null(windows.Single(window => window.Id == "3p-hourly").ResetsAt);
    }

    /// <summary>With no email in hand, the most recent non-empty email in the table is the one read.</summary>
    [Fact]
    public void WithoutAnEmailTheMostRecentOneIsChosen()
    {
        var home = NewHome();
        CreateDatabase(home, withRows: true);

        var windows = AntigravityOmp.UsageWindows(home, null);

        Assert.Equal(0.17, windows.Single(window => window.Id == "gemini-hourly").UsedFraction!.Value, 4);
    }

    [Fact]
    public void AnotherEmailsRowsAreNotMixedIn()
    {
        var home = NewHome();
        CreateDatabase(home, withRows: true);
        using (var connection = new SqliteConnection($"Data Source={AntigravityOmp.DatabasePath(home)}"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO usage_history (provider, email, limit_id, label, window_label, used_fraction, resets_at, recorded_at, id) " +
                "VALUES ('google-antigravity', 'other@example.com', 'gemini-5h:google:abc', 'Google', '5-hour', 0.95, 0, 999, 9);";
            insert.ExecuteNonQuery();
        }

        var windows = AntigravityOmp.UsageWindows(home, "user@example.com");

        Assert.Equal(0.17, windows.Single(window => window.Id == "gemini-hourly").UsedFraction!.Value, 4);
    }

    /// <summary>
    /// Reading the OMP store must leave no sidecar files beside it: the store belongs to another
    /// tool, and `-wal`/`-shm`/`-journal` files are writes into its directory.
    /// </summary>
    [Fact]
    public void OpeningTheOmpDatabaseCreatesNoSidecarFiles()
    {
        var home = NewHome();
        CreateDatabase(home, withRows: true);
        var directory = Path.GetDirectoryName(AntigravityOmp.DatabasePath(home))!;
        var before = Directory.GetFiles(directory).OrderBy(path => path).ToList();

        AntigravityOmp.UsageWindows(home, "user@example.com");
        AntigravityOmp.Credentials(home);

        var after = Directory.GetFiles(directory).OrderBy(path => path).ToList();
        Assert.Equal(before, after);
        Assert.DoesNotContain(after, path =>
            path.EndsWith("-wal", StringComparison.Ordinal)
            || path.EndsWith("-shm", StringComparison.Ordinal)
            || path.EndsWith("-journal", StringComparison.Ordinal));
    }
}
