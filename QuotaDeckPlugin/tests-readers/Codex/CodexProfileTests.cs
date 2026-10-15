namespace QuotaDeck.Readers.Tests.Codex;

using QuotaDeck.Readers.Codex;

/// <summary>
/// Port of Codenotch <c>Tests/CodexProfileTests.swift</c> (discovery half, lines 40-69) plus the
/// identity/slug helpers of <c>Sources/Providers/CodexProfile.swift</c>.
/// </summary>
public class CodexProfileTests
{
    [Fact]
    public void DefaultIdentityAndPathsFollowTheHomeDirectory()
    {
        using var home = new CodexTestHome();
        var profile = CodexProfile.Default(home.Root);

        Assert.Equal("codex", profile.Id);
        Assert.Equal("Codex", profile.DisplayName);
        Assert.Null(profile.Slug);
        Assert.Equal(Path.Combine(home.Root, ".codex", "auth.json"), profile.AuthPath);
        Assert.Equal(Path.Combine(home.Root, ".codex", "state_5.sqlite"), profile.StatePath);
        Assert.Equal(Path.Combine(home.Root, ".codex", "sqlite", "codex-dev.db"), profile.DesktopStorePath);
    }

    [Fact]
    public void DiscoveryIsStableAndIgnoresUnrelatedFiles()
    {
        using var home = new CodexTestHome();
        home.WithLayout(new Dictionary<string, string[]>
        {
            [".codex-work"] = ["auth.json"],
            [".codex-alpha"] = ["config.toml"],
            [".codex-empty"] = [],
            [".codex-notes"] = ["README.md"],
            [".codex-"] = ["auth.json"],
            ["codex-other"] = ["auth.json"],
        });
        home.WriteFile(".codex-file", string.Empty);

        var found = CodexProfile.Discover(home.Root);

        Assert.Equal(new[] { "codex", "codex-alpha", "codex-work" }, found.Select(profile => profile.Id));
        Assert.Equal(new[] { "Codex", "Codex (alpha)", "Codex (work)" }, found.Select(profile => profile.DisplayName));
        Assert.Equal(Path.Combine(home.Root, ".codex-work", "auth.json"), found[2].AuthPath);
    }

    [Fact]
    public void SignedOutProfilesAndMissingHomeAreSupported()
    {
        using var home = new CodexTestHome();
        home.WithLayout(new Dictionary<string, string[]>
        {
            [".codex-a"] = ["sessions"],
            [".codex-b"] = ["history.jsonl"],
            [".codex-c"] = ["state_5.sqlite"],
            [".codex-d"] = ["sqlite/codex-dev.db"],
        });

        Assert.Equal(
            new[] { "codex", "codex-a", "codex-b", "codex-c", "codex-d" },
            CodexProfile.Discover(home.Root).Select(profile => profile.Id));
        Assert.Equal(
            new[] { "codex" },
            CodexProfile.Discover(Path.Combine(home.Root, "missing")).Select(profile => profile.Id));
    }

    [Fact]
    public void SlugHelpersMatchOnlyTheCodexNamespaces()
    {
        Assert.Equal("work", CodexProfile.SlugFromDirectoryName(".codex-work"));
        Assert.Null(CodexProfile.SlugFromDirectoryName(".codex-"));
        Assert.Null(CodexProfile.SlugFromDirectoryName(".codex"));
        Assert.Null(CodexProfile.SlugFromDirectoryName("codex-work"));

        Assert.Equal("work", CodexProfile.SlugFromProviderId("codex-work"));
        Assert.Null(CodexProfile.SlugFromProviderId("codex-"));
        Assert.Null(CodexProfile.SlugFromProviderId("codex"));
        Assert.Null(CodexProfile.SlugFromProviderId("codextra"));

        Assert.True(CodexProfile.IsCodex("codex"));
        Assert.True(CodexProfile.IsCodex("codex-work"));
        Assert.False(CodexProfile.IsCodex("codextra"));
    }
}
