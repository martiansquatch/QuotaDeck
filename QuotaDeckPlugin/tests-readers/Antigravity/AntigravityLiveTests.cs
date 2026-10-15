namespace QuotaDeck.Readers.Tests.Antigravity;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Settings;
using QuotaDeck.Readers.Antigravity;
using QuotaDeck.Readers.Shared;
using Xunit.Abstractions;

/// <summary>
/// The one test that talks to this Mac: real <c>ps</c>/<c>lsof</c>, real files, the real loopback
/// transport. It may start the language server when none is running, and stops any it started.
/// Gated on <c>QUOTADECK_LIVE_ANTIGRAVITY=1</c>; xunit 2 has no dynamic skip, so without the
/// variable the test returns having asserted nothing.
/// </summary>
public class AntigravityLiveTests
{
    private readonly ITestOutputHelper _output;

    public AntigravityLiveTests(ITestOutputHelper output) => this._output = output;

    [Fact]
    public async Task ARealSnapshotComesBack()
    {
        if (Environment.GetEnvironmentVariable("QUOTADECK_LIVE_ANTIGRAVITY") != "1")
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = Path.Combine(Path.GetTempPath(), "qd-live-antigravity-" + Guid.NewGuid().ToString("N"));
        var options = new AntigravityOptions
        {
            Profile = AntigravityProfile.Default(home),
            HomeDirectory = home,
            DataDirectory = data,
            Http = new HttpClientTransport(),
            Process = new ProcessRunner(),
            TimeProvider = TimeProvider.System,
        };

        using var reader = new AntigravityReader(options, new ReaderConfig(), new InMemoryReaderStateStore(), TimeProvider.System);

        var snapshot = await reader.FetchAsync(Freshness.Standard);
        var startedPid = reader.OwnedServerPid;

        this._output.WriteLine($"source={reader.LastSource}");
        this._output.WriteLine($"windows={string.Join(",", snapshot.Windows.Select(window => window.Id))}");
        this._output.WriteLine($"fidelity={snapshot.Fidelity} status={snapshot.Status.Kind} pid={startedPid}");

        Assert.NotEmpty(snapshot.Windows);

        reader.Dispose();

        if (startedPid is { } pid)
        {
            // Any server this test started must not outlive it.
            var exited = SpinWait.SpinUntil(
                () =>
                {
                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById(pid);
                        return process.HasExited;
                    }
                    catch (ArgumentException)
                    {
                        return true;
                    }
                },
                TimeSpan.FromSeconds(3));

            this._output.WriteLine($"startedServerStopped={exited}");
            Assert.True(exited, "the started language server was left running");
        }
    }
}
