namespace QuotaDeck.Readers.Tests.Codex;

using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Settings;
using QuotaDeck.Readers.Codex;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// The provider-module surface of Codenotch <c>Sources/Providers/CodexLocalProvider.swift</c>: one
/// key for the default profile, with the extra <c>.codex-&lt;slug&gt;</c> profiles discovered and
/// logged rather than exposed.
/// </summary>
public class CodexModuleTests
{
    [Fact]
    public void IdentityAndPaletteAreCodex()
    {
        var module = new CodexModule();

        Assert.Equal("codex", module.ProviderId);
        Assert.Equal("Codex", module.DisplayName);
        Assert.Equal(new ProviderPalette(0x10, 0xA3, 0x7F), module.Palette);
    }

    [Fact]
    public void IsPresentFollowsTheDefaultProfilesAuthFile()
    {
        using var home = new CodexTestHome();
        var module = new CodexModule { HomeDirectory = home.Root };

        Assert.False(module.IsPresent(Context()));

        // An auth file in a sibling profile does not make the default profile present.
        home.WithLayout(new Dictionary<string, string[]> { [".codex-work"] = ["auth.json"] });
        Assert.False(module.IsPresent(Context()));

        home.WithLayout(new Dictionary<string, string[]> { [".codex"] = ["auth.json"] });
        Assert.True(module.IsPresent(Context()));
    }

    [Fact]
    public void CreateReaderBuildsTheDefaultProfileReader()
    {
        using var home = new CodexTestHome();
        home.WithLayout(new Dictionary<string, string[]>
        {
            [".codex"] = ["auth.json"],
            [".codex-work"] = ["auth.json"],
        });

        var module = new CodexModule { HomeDirectory = home.Root };
        var reader = module.CreateReader(Context());

        Assert.Equal("codex", reader.Id);
        Assert.Equal("Codex", reader.DisplayName);
    }

    [Fact]
    public void ExtraProfilesAreLoggedOnceAndNeverHandedOut()
    {
        using var home = new CodexTestHome();
        home.WithLayout(new Dictionary<string, string[]>
        {
            [".codex"] = ["auth.json"],
            [".codex-alpha"] = ["config.toml"],
            [".codex-work"] = ["auth.json"],
        });

        var messages = new List<string>();
        var module = new CodexModule { HomeDirectory = home.Root, LogSink = messages.Add };

        var first = module.CreateReader(Context());
        var second = module.CreateReader(Context());

        Assert.Equal("codex", first.Id);
        Assert.Equal("codex", second.Id);
        var message = Assert.Single(messages);
        Assert.Contains("codex-alpha", message);
        Assert.Contains("codex-work", message);
    }

    [Fact]
    public void NoExtraProfilesMeansNoNoise()
    {
        using var home = new CodexTestHome();
        home.WithLayout(new Dictionary<string, string[]> { [".codex"] = ["auth.json"] });

        var messages = new List<string>();
        var module = new CodexModule { HomeDirectory = home.Root, LogSink = messages.Add };
        module.CreateReader(Context());

        Assert.Empty(messages);
    }

    private static ReaderContext Context() => new()
    {
        Config = new ReaderConfig(),
        StateStore = new InMemoryReaderStateStore(),
        TimeProvider = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)),
        DataDirectory = Path.Combine(Path.GetTempPath(), "quotadeck-module-tests"),
        Http = new FakeTransport(),
        Process = new FakeProcessRunner(),
    };
}
