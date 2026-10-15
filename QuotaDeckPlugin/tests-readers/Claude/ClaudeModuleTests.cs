namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Core.Readers;
using QuotaDeck.Readers.Claude;

/// <summary>
/// The provider module: who it says it is, and the cheap presence probe the plugin asks before it
/// draws anything. The probe must never touch a credential prompt, which is why it enumerates
/// attributes only — and why these tests can run it against a machine that has Claude Code signed in.
/// </summary>
public class ClaudeModuleTests
{
    private const string Home = "/tmp/quotadeck-claude-home";

    private static ReaderContext Context() => new()
    {
        Config = new QuotaDeck.Core.Settings.ReaderConfig(),
        StateStore = new InMemoryReaderStateStore(),
        TimeProvider = TimeProvider.System,
        DataDirectory = Path.Combine(Path.GetTempPath(), "quotadeck-claude-module-data"),
        Http = new QuotaDeck.Readers.Tests.Support.FakeTransport(),
        Process = new QuotaDeck.Readers.Tests.Support.FakeProcessRunner(),
    };

    [Fact]
    public void TheIdentityIsTheOneTheToolsExpect()
    {
        var module = new ClaudeModule(Home, new FakeKeychain());

        Assert.Equal("claude", module.ProviderId);
        Assert.Equal("Claude", module.DisplayName);
        Assert.Equal(new ProviderPalette(0xD9, 0x77, 0x57), module.Palette);
    }

    [Fact]
    public void ItIsPresentWhenTheConfigDirectoryExists()
    {
        var files = new FakeClaudeFileSystem();
        files.AddDirectory(Path.Combine(Home, ".claude"));

        // The probe reads the real filesystem, so the home has to be a real one for this half.
        var directory = Directory.CreateTempSubdirectory("quotadeck-claude-present-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".claude"));

            Assert.True(new ClaudeModule(directory.FullName, new FakeKeychain()).IsPresent(Context()));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ItIsPresentWhenAKeychainItemExists()
    {
        var keychain = new FakeKeychain();
        keychain.Add(
            ClaudeProfile.DefaultKeychainService,
            FakeKeychain.Credential("token", DateTimeOffset.UtcNow.AddHours(1)),
            DateTimeOffset.UtcNow);

        // A home with neither a config directory nor a token is not present; the item alone is enough.
        Assert.False(new ClaudeModule(Home, new FakeKeychain()).IsPresent(Context()));
        Assert.True(new ClaudeModule(Home, keychain).IsPresent(Context()));
    }

    [Fact]
    public void ItIsNotPresentWhenThereIsNeither()
    {
        Assert.False(new ClaudeModule(Home, new FakeKeychain()).IsPresent(Context()));
    }

    [Fact]
    public void ItBuildsAReaderForTheDefaultProfile()
    {
        var module = new ClaudeModule(Home, new FakeKeychain());

        var reader = module.CreateReader(Context());

        Assert.Equal("claude", reader.Id);
        Assert.IsType<ClaudeReader>(reader);
    }
}
