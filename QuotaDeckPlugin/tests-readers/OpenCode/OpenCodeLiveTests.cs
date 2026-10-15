namespace QuotaDeck.Readers.Tests.OpenCode;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Settings;
using QuotaDeck.Readers.OpenCode;
using QuotaDeck.Readers.Shared;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// The one test that reads this machine: the real reader, the real transport, OpenCode's real data
/// directory — all strictly read-only. Gated on <c>QUOTADECK_LIVE_OPENCODE=1</c> so an ordinary test
/// run never touches the network or the store.
///
/// It asserts only that a Go snapshot with a <c>rolling</c> window came back; the run's report (which
/// source answered, which window ids) is written to the test output, never to the repository, and
/// never includes percentages, dates, keys or org ids. It also pins the observable half of the
/// read-only promise on a live directory: the fetch adds no file, and <c>auth.json</c> — which the
/// owner does not rewrite mid-fetch — keeps its size and modification time. The live store's own
/// <c>-wal</c>/<c>-shm</c> are deliberately not compared that way: the running owner writes them on
/// its own schedule, so their mtime is not this reader's to control. Modification-freedom itself is
/// pinned on synthetic stores by <c>OpeningNeverCreatesOrModifiesFiles</c>.
///
/// xunit v2 has no runtime skip, so the un-gated run reports this as passed rather than skipped.
/// </summary>
[Collection("OpenCode environment")]
public class OpenCodeLiveTests
{
    [Fact]
    public async Task TheRealMachineServesAGoSnapshot()
    {
        if (Environment.GetEnvironmentVariable("QUOTADECK_LIVE_OPENCODE") != "1")
        {
            return;
        }

        var directory = OpenCodeCredentials.ResolveDataDirectory();
        var authPath = Path.Combine(directory, "auth.json");
        Assert.True(File.Exists(Path.Combine(directory, "opencode.db")), "no OpenCode store to read");

        var namesBefore = FileNames(directory);
        var authBefore = FileState(authPath);

        var context = new ReaderContext
        {
            Config = new ReaderConfig(),
            StateStore = new InMemoryReaderStateStore(),
            TimeProvider = TimeProvider.System,
            DataDirectory = Path.Combine(Path.GetTempPath(), "quotadeck-live-reader"),
            Http = new HttpClientTransport(),
            Process = new FakeProcessRunner(),
        };

        var snapshot = await new OpenCodeReader(context).FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
        Assert.Contains(snapshot.Windows, window => window.Id == "rolling");
        Assert.Equal("Go", snapshot.Plan);

        // The report: source and window ids only.
        Console.WriteLine(
            $"live opencode: source={OriginOf(directory)} windows=[{string.Join(", ", snapshot.Windows.Select(window => window.Id))}]");

        // The read-only promise, observable on a live directory: nothing was added, and the file
        // the owner leaves alone kept its every byte.
        Assert.Equal(namesBefore, FileNames(directory));
        Assert.Equal(authBefore, FileState(authPath));
    }

    /// <summary>Which of the three sources the combined loader would claim — labels, never values.</summary>
    private static string OriginOf(string directory)
    {
        var authPath = Path.Combine(directory, "auth.json");
        if (OpenCodeCredentials.LoadGoKey(authPath) is not null)
        {
            return "auth.json (opencode-go)";
        }

        if (OpenCodeCredentials.LoadFromDatabase(Path.Combine(directory, "opencode.db")) is { } stored)
        {
            return stored.OAuth ? "opencode.db (oauth)" : "opencode.db (go key)";
        }

        return OpenCodeCredentials.LoadOAuth(authPath) is not null ? "auth.json (opencode oauth)" : "none";
    }

    private static string[] FileNames(string directory) =>
        Directory.GetFiles(directory)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;

    private static (DateTime ModifiedUtc, long Length)? FileState(string path) =>
        File.Exists(path) ? (File.GetLastWriteTimeUtc(path), new FileInfo(path).Length) : null;
}
