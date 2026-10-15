namespace QuotaDeck.Readers.Tests.Antigravity;

using QuotaDeck.Readers.Antigravity;

/// <summary>
/// Profile identity and discovery, ported from Codenotch <c>Tests/AntigravityProfileTests.swift</c>
/// (lines 26-134): the default directory, the <c>antigravity-&lt;slug&gt;</c> extras, the internal
/// flavours that are not accounts, and the markers a profile directory must carry.
/// </summary>
public class AntigravityProfileTests
{
    private static string NewHome() =>
        Path.Combine(Path.GetTempPath(), "qd-antigravity-profile-" + Guid.NewGuid().ToString("N"));

    private static string HomeWithLayout(params (string Directory, string[] Files)[] layout)
    {
        var home = NewHome();
        var gemini = Path.Combine(home, ".gemini");
        Directory.CreateDirectory(gemini);
        foreach (var (directory, files) in layout)
        {
            var url = Path.Combine(gemini, directory);
            Directory.CreateDirectory(url);
            foreach (var file in files)
            {
                var path = Path.Combine(url, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
            }
        }

        return home;
    }

    private static bool HasCredential(AntigravityProfile profile) => File.Exists(profile.AuthPath);

    [Fact]
    public void DefaultIdentityAndPathsStayCompatible()
    {
        var profile = AntigravityProfile.Default("/Users/test");

        Assert.Null(profile.Slug);
        Assert.Equal("antigravity", profile.Id);
        Assert.Equal("Antigravity", profile.DisplayName);
        Assert.Equal("/Users/test/.gemini/antigravity", profile.ConfigDirectory);
        Assert.Equal("/Users/test/.gemini/antigravity/oauth_creds.json", profile.AuthPath);
        Assert.Equal("/Users/test/.gemini/antigravity/brain", profile.BrainDirectory);
    }

    [Fact]
    public void NamedProfileIdentityAndPaths()
    {
        var profile = new AntigravityProfile("work", "/Users/test/.gemini/antigravity-work");

        Assert.Equal("antigravity-work", profile.Id);
        Assert.Equal("Antigravity (work)", profile.DisplayName);
        Assert.Equal("/Users/test/.gemini/antigravity-work/oauth_creds.json", profile.AuthPath);
    }

    [Fact]
    public void DirectorySlugsIgnoreInternalFlavours()
    {
        Assert.Equal("work", AntigravityProfile.SlugFromDirectoryName("antigravity-work"));
        Assert.Equal("client-1", AntigravityProfile.SlugFromDirectoryName("antigravity-client-1"));
        Assert.Null(AntigravityProfile.SlugFromDirectoryName("antigravity"));
        Assert.Null(AntigravityProfile.SlugFromDirectoryName("antigravity-"));
        Assert.Null(AntigravityProfile.SlugFromDirectoryName("antigravity-ide"));
        Assert.Null(AntigravityProfile.SlugFromDirectoryName("antigravity-cli"));
        Assert.Null(AntigravityProfile.SlugFromDirectoryName("antigravity-backup"));
        Assert.Null(AntigravityProfile.SlugFromDirectoryName("antigravity-api"));
        Assert.Null(AntigravityProfile.SlugFromDirectoryName("not-antigravity-work"));
    }

    [Fact]
    public void DiscoveryFindsProfilesInStableOrder()
    {
        var home = HomeWithLayout(
            ("antigravity", ["oauth_creds.json"]),
            ("antigravity-work", ["oauth_creds.json"]),
            ("antigravity-alpha", ["oauth_creds.json"]),
            ("antigravity-ide", ["oauth_creds.json"]),
            ("antigravity-cli", ["oauth_creds.json"]),
            ("antigravity-backup", ["agent.db"]),
            ("antigravity-empty", []),
            ("antigravity-settings-only", ["settings.json"]),
            ("antigravity-no-creds", ["brain/uuid/transcript.jsonl"]),
            ("other-tool", ["oauth_creds.json"]));

        var found = AntigravityProfile.Discover(home, HasCredential);

        Assert.Equal(new[] { "antigravity", "antigravity-alpha", "antigravity-work" }, found.Select(profile => profile.Id));
        Assert.Equal(new[] { "Antigravity", "Antigravity (alpha)", "Antigravity (work)" }, found.Select(profile => profile.DisplayName));
    }

    [Fact]
    public void DiscoveryRequiresCredentials()
    {
        var home = HomeWithLayout(
            ("antigravity-work", ["oauth_creds.json"]),
            ("antigravity-settings", ["settings.json"]),
            ("antigravity-state", ["state"]),
            ("antigravity-brain-only", ["brain/agent.db"]));

        var found = AntigravityProfile.Discover(home, HasCredential);

        Assert.Equal(new[] { "antigravity", "antigravity-work" }, found.Select(profile => profile.Id));
    }

    [Fact]
    public void DiscoveryRespectsTheCredentialFilter()
    {
        var home = HomeWithLayout(
            ("antigravity-work", ["oauth_creds.json"]),
            ("antigravity-test", ["oauth_creds.json"]));

        var found = AntigravityProfile.Discover(home, profile => profile.Slug != "test");

        Assert.Equal(new[] { "antigravity", "antigravity-work" }, found.Select(profile => profile.Id));
    }

    [Fact]
    public void AMissingGeminiDirectoryStillYieldsTheDefaultProfile()
    {
        var home = NewHome();

        var found = AntigravityProfile.Discover(home, HasCredential);

        Assert.Single(found);
        Assert.Equal("antigravity", found[0].Id);
    }

    [Fact]
    public void TheModuleIsPresentOnItsMarkers()
    {
        var home = NewHome();
        Assert.False(AntigravityModule.IsPresent(home, [], null));

        Directory.CreateDirectory(Path.Combine(home, ".gemini", "antigravity"));
        Assert.True(AntigravityModule.IsPresent(home, [], null));
    }

    [Fact]
    public void TheModuleIsPresentOnAnAppBundleOrTheCli()
    {
        var apps = NewHome();
        var home = NewHome();
        Directory.CreateDirectory(Path.Combine(apps, "Antigravity.app"));
        Assert.True(AntigravityModule.IsPresent(home, [apps], null));

        var bin = NewHome();
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "agy"), string.Empty);
        Assert.True(AntigravityModule.IsPresent(home, [], bin));
        Assert.False(AntigravityModule.IsPresent(home, [], Path.Combine(bin, "missing")));
    }
}
