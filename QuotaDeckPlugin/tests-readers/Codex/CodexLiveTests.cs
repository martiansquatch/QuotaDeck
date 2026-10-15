namespace QuotaDeck.Readers.Tests.Codex;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Settings;
using QuotaDeck.Readers.Codex;
using QuotaDeck.Readers.Shared;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// The live read: the real reader, the real transport, the real home directory, this machine's own
/// Codex session. Gated on <c>QUOTADECK_LIVE_CODEX=1</c> and a no-op otherwise, so the ordinary
/// suite never touches the network. It asserts only that a snapshot with a primary window came
/// back; nothing about the account is printed or recorded.
/// </summary>
public class CodexLiveTests
{
    [Fact]
    public async Task LiveCodexFetchReturnsAPrimaryWindowWhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("QUOTADECK_LIVE_CODEX") != "1")
        {
            return;
        }

        var context = new ReaderContext
        {
            Config = new ReaderConfig(),
            StateStore = new InMemoryReaderStateStore(),
            TimeProvider = TimeProvider.System,
            DataDirectory = Path.Combine(Path.GetTempPath(), "quotadeck-live-codex"),
            Http = new HttpClientTransport(),
            Process = new FakeProcessRunner(),
        };

        var reader = new CodexModule().CreateReader(context);
        var snapshot = await reader.FetchAsync(Freshness.FromSource);

        Assert.Equal(ProviderStatusKind.Ok, snapshot.Status.Kind);
        Assert.Contains(snapshot.Windows, window => window.Id == "primary");
    }
}
