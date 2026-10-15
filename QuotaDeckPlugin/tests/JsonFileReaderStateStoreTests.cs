namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;

/// <summary>
/// The state store's file round-trip: every field of a reading survives save/load, a remembered
/// reading comes back marked stale for the moment it was taken, the back-off is kept beside it, and
/// a corrupt or unwritable file reads as an empty store rather than failing a fetch.
/// </summary>
public class JsonFileReaderStateStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "quotaDeck-state-" + Guid.NewGuid().ToString("N") + ".json");

    /// <summary>One store per path, however the path was spelled: a second instance over the same
    /// file would race its read-modify-write cycles and lose entries.</summary>
    [Fact]
    public void TheSamePathYieldsTheSameInstance()
    {
        var path = TempPath();

        Assert.Same(JsonFileReaderStateStore.ForPath(path), JsonFileReaderStateStore.ForPath(path));

        // "dir/./file" is the same file: the key is the normalised full path.
        var unnormalised = Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path));
        Assert.Same(JsonFileReaderStateStore.ForPath(path), JsonFileReaderStateStore.ForPath(unnormalised));

        Assert.NotSame(JsonFileReaderStateStore.ForPath(path), JsonFileReaderStateStore.ForPath(TempPath()));
    }

    [Fact]
    public void AReadingRoundTripsWithEveryField()
    {
        var store = JsonFileReaderStateStore.ForPath(TempPath());
        var fetchedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var resetsAt = fetchedAt.AddHours(5);

        var snapshot = new ProviderSnapshot
        {
            Id = "claude",
            DisplayName = "Claude",
            Fidelity = Fidelity.Derived,
            Status = ProviderStatus.Ok,
            Windows =
            [
                new LimitWindow
                {
                    Id = "session",
                    Group = "Spark",
                    Label = "5-hour limit",
                    UsedFraction = 0.73,
                    Remaining = 27,
                    Used = 73,
                    Detail = "detail",
                    UsedText = "73/100",
                    ResetsAt = resetsAt,
                    Duration = TimeSpan.FromHours(5),
                    BandOverride = UsageBand.Critical,
                    PrefersUsedText = true,
                    Money = new UsageMoneyBreakdown("USD", 12.5, 7.5),
                },
                new LimitWindow
                {
                    Id = "weekly",
                    Label = "Weekly limit",
                },
            ],
            HeadlineId = "session",
            WeeklyId = "weekly",
            Plan = "Max",
            AccountLabel = "Work account",
        };

        store.Save("claude", new ReaderMemory(snapshot, fetchedAt));

        var memory = store.Load("claude");
        Assert.NotNull(memory);
        Assert.Equal(fetchedAt, memory!.FetchedAt);

        var loaded = memory.Snapshot;
        Assert.Equal("claude", loaded.Id);
        Assert.Equal("Claude", loaded.DisplayName);
        Assert.Equal(Fidelity.Derived, loaded.Fidelity);
        Assert.Equal("session", loaded.HeadlineId);
        Assert.Equal("weekly", loaded.WeeklyId);
        Assert.Equal("Max", loaded.Plan);
        Assert.Equal("Work account", loaded.AccountLabel);
        Assert.Equal(fetchedAt, loaded.FetchedAt);

        // A remembered reading is dated, not trusted: it comes back stale for when it was taken.
        Assert.Equal(ProviderStatus.Stale(fetchedAt), loaded.Status);

        Assert.Equal(2, loaded.Windows.Count);
        var session = loaded.Windows[0];
        Assert.Equal("session", session.Id);
        Assert.Equal("Spark", session.Group);
        Assert.Equal("5-hour limit", session.Label);
        Assert.Equal(0.73, session.UsedFraction!.Value, 10);
        Assert.Equal(27, session.Remaining);
        Assert.Equal(73, session.Used);
        Assert.Equal("detail", session.Detail);
        Assert.Equal("73/100", session.UsedText);
        Assert.Equal(resetsAt, session.ResetsAt);
        Assert.Equal(TimeSpan.FromHours(5), session.Duration);
        Assert.Equal(UsageBand.Critical, session.BandOverride);
        Assert.True(session.PrefersUsedText);
        Assert.NotNull(session.Money);
        Assert.Equal("USD", session.Money!.Currency);
        Assert.Equal(12.5, session.Money.Spent, 10);
        Assert.Equal(7.5, session.Money.Remaining, 10);

        // Nulls survive too: a window with no reading, reset date, duration, band or money comes
        // back empty rather than defaulted.
        var weekly = loaded.Windows[1];
        Assert.Equal("weekly", weekly.Id);
        Assert.Null(weekly.Group);
        Assert.Equal("Weekly limit", weekly.Label);
        Assert.Null(weekly.UsedFraction);
        Assert.Null(weekly.Remaining);
        Assert.Null(weekly.Used);
        Assert.Null(weekly.Detail);
        Assert.Null(weekly.UsedText);
        Assert.Null(weekly.ResetsAt);
        Assert.Null(weekly.Duration);
        Assert.Null(weekly.BandOverride);
        Assert.False(weekly.PrefersUsedText);
        Assert.Null(weekly.Money);
    }

    [Theory]
    [InlineData(Fidelity.Official)]
    [InlineData(Fidelity.Derived)]
    [InlineData(Fidelity.Manual)]
    public void FidelityRoundTrips(Fidelity fidelity)
    {
        var store = JsonFileReaderStateStore.ForPath(TempPath());
        var snapshot = TestData.Snapshot(fidelity: fidelity);

        store.Save("claude", new ReaderMemory(snapshot, TestData.Now));

        Assert.Equal(fidelity, store.Load("claude")!.Snapshot.Fidelity);
    }

    [Fact]
    public void ForgetRemovesTheReadingAndKeepsTheBackoff()
    {
        var store = JsonFileReaderStateStore.ForPath(TempPath());
        var until = TestData.Now.AddMinutes(15);
        store.Save("claude", new ReaderMemory(TestData.SessionAndWeekly(0.4, 0.2), TestData.Now));
        store.SaveBackoffUntil("claude", until);

        store.Forget("claude");

        Assert.Null(store.Load("claude"));
        Assert.Equal(until, store.LoadBackoffUntil("claude"));

        store.SaveBackoffUntil("claude", null);
        Assert.Null(store.LoadBackoffUntil("claude"));

        // Removing an entry that was never there is still not an error.
        store.SaveBackoffUntil("claude", null);
    }

    [Fact]
    public void ACorruptFileReadsAsAnEmptyStore()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ this is not json");
        var store = JsonFileReaderStateStore.ForPath(path);

        Assert.Null(store.Load("claude"));
        Assert.Null(store.LoadBackoffUntil("claude"));

        // And it can be written again afterwards.
        store.SaveBackoffUntil("claude", TestData.Now.AddMinutes(15));
        Assert.Equal(TestData.Now.AddMinutes(15), store.LoadBackoffUntil("claude"));
    }

    [Fact]
    public void AnUnwritablePathIsSurvivable()
    {
        // A directory where the file should be: readable as absent, and impossible to replace.
        var path = TempPath();
        Directory.CreateDirectory(path);
        var store = JsonFileReaderStateStore.ForPath(path);

        Assert.Null(store.Load("claude"));

        // The writes are lost, not thrown: memory is never worth failing a fetch over.
        store.Save("claude", new ReaderMemory(TestData.SessionAndWeekly(0.4, 0.2), TestData.Now));
        store.SaveBackoffUntil("claude", TestData.Now.AddMinutes(15));

        Assert.Null(store.Load("claude"));
        Assert.Null(store.LoadBackoffUntil("claude"));
    }
}
