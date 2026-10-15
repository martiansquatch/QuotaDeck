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
/// never includes percentages, dates, keys or org ids. It also pins the read-only promise: the store
/// and its sidecars must have the same size and modification time afterwards.
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
        var databasePath = Path.Combine(directory, "opencode.db");
        var walPath = databasePath + "-wal";
        var watched = new[] { authPath, databasePath, walPath }.Where(File.Exists).ToArray();
        Assert.NotEmpty(watched);
        var before = watched.ToDictionary(path => path, FileState);

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

        foreach (var path in watched)
        {
            Assert.Equal(before[path], FileState(path));
        }
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

    private static (DateTime ModifiedUtc, long Length)? FileState(string path) =>
        File.Exists(path) ? (File.GetLastWriteTimeUtc(path), new FileInfo(path).Length) : null;
}
