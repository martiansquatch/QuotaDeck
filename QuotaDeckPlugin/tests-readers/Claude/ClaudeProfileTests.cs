namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Readers.Claude;

/// <summary>
/// A second Claude Code login kept under <c>~/.claude-&lt;slug&gt;</c> is its own account, with its
/// own token, its own limits and its own sessions. Discovery is ported and tested even though this
/// build draws only the default login: the reader asks discovery one question — is this the only
/// login? — and that answer decides whether <c>claude /usage</c> may stand in for the ring.
///
/// Ported from Codenotch <c>Tests/ClaudeProfileTests.swift</c>, with the Swift fixtures' synthetic
/// developer-home paths replaced by <c>/tmp/…</c> ones, the keychain service fixtures recomputed for
/// them, and the addresses genericised — no personal name, address or account is carried over.
/// </summary>
public class ClaudeProfileTests
{
    private const string Home = "/tmp/quotadeck-claude-home";

    /// <summary>Discovery asks whether Claude Code ever filed a token for the directory.</summary>
    private static FakeKeychain SignedIn(string home, params string[] slugs)
    {
        var keychain = new FakeKeychain();
        foreach (var slug in slugs)
        {
            var directory = slug.Length == 0 ? Path.Combine(home, ".claude") : Path.Combine(home, ".claude-" + slug);
            keychain.Add(
                ClaudeProfile.DefaultKeychainService + "-" + ClaudeProfile.KeychainSuffixForPath(directory),
                [1, 2, 3],
                new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                account: "testaccount");
        }

        return keychain;
    }

    private static FakeClaudeFileSystem HomeWith(params (string Directory, string[] Files)[] layout)
    {
        var files = new FakeClaudeFileSystem();
        foreach (var (directory, names) in layout)
        {
            files.AddDirectory(Path.Combine(Home, directory));
            foreach (var name in names)
            {
                files.AddFile(Path.Combine(Home, directory, name), []);
            }
        }

        return files;
    }

    // MARK: - Identity

    [Fact]
    public void TheDefaultProfileIsUnchanged()
    {
        var profile = ClaudeProfile.Default(Home, new FakeClaudeFileSystem(), "testaccount");

        Assert.Null(profile.Slug);
        Assert.Equal("claude", profile.Id);
        Assert.Equal("Claude", profile.DisplayName);
        Assert.Equal(Path.Combine(Home, ".claude/sessions"), profile.SessionsDirectory);
        Assert.Equal("Claude Code", profile.SourceName);
        Assert.Equal("claude", profile.SignInCommand);
    }

    [Fact]
    public void AProfileIsNamedAfterItsSlug()
    {
        var profile = new ClaudeProfile("work", Path.Combine(Home, ".claude-work"), new FakeClaudeFileSystem(), "testaccount");

        Assert.Equal("claude-work", profile.Id);
        Assert.Equal("Claude (work)", profile.DisplayName);
        Assert.Equal(Path.Combine(Home, ".claude-work/sessions"), profile.SessionsDirectory);
    }

    /// <summary>Claude Code files a non-default profile's token under the bare name plus a hash of the path.</summary>
    [Fact]
    public void TheKeychainServiceCarriesClaudeCodesHashOfThePath()
    {
        var profile = new ClaudeProfile(
            "work", Path.Combine(Home, ".claude-work"), new FakeClaudeFileSystem(), "testaccount");

        // `shasum -a 256` of "/tmp/quotadeck-claude-home/.claude-work", first eight hex digits.
        Assert.Equal("Claude Code-credentials-95c2c250", profile.KeychainService);
    }

    /// <summary>The path is hashed as Claude Code sees it, and Claude Code does not see a trailing slash.</summary>
    [Fact]
    public void ATrailingSlashDoesNotChangeTheHash()
    {
        var slashed = new ClaudeProfile(
            "work", Path.Combine(Home, ".claude-work") + "/", new FakeClaudeFileSystem(), "testaccount");

        Assert.Equal("Claude Code-credentials-95c2c250", slashed.KeychainService);
    }

    /// <summary>The default offers the suffix Claude Code uses when CLAUDE_CONFIG_DIR is exported, and the bare name.</summary>
    [Fact]
    public void TheDefaultProfileOffersBothTheSuffixedAndBareServices()
    {
        var profile = ClaudeProfile.Default(Home, new FakeClaudeFileSystem(), "testaccount");

        // `shasum -a 256` of "/tmp/quotadeck-claude-home/.claude", first eight hex digits.
        Assert.Equal(
            ["Claude Code-credentials-042defc2", "Claude Code-credentials"],
            profile.KeychainServices);
    }

    [Fact]
    public void ANamedProfileOffersOnlyItsSuffixedService()
    {
        var profile = new ClaudeProfile(
            "work", Path.Combine(Home, ".claude-work"), new FakeClaudeFileSystem(), "testaccount");

        Assert.Equal(["Claude Code-credentials-95c2c250"], profile.KeychainServices);
    }

    [Fact]
    public void ProviderIDsAreRecognised()
    {
        Assert.True(ClaudeProfile.IsClaude("claude"));
        Assert.True(ClaudeProfile.IsClaude("claude-work"));
        Assert.False(ClaudeProfile.IsClaude("claudex"));
        Assert.False(ClaudeProfile.IsClaude("cursor"));
        Assert.Equal("work", ClaudeProfile.SlugFromProviderId("claude-work"));
        Assert.Null(ClaudeProfile.SlugFromProviderId("claude"));
        Assert.Null(ClaudeProfile.SlugFromProviderId("claude-"));
    }

    // MARK: - Discovery

    [Fact]
    public void DirectoryNamesAreParsedStrictly()
    {
        Assert.Equal("work", ClaudeProfile.SlugFromDirectoryName(".claude-work"));
        Assert.Equal("client-a", ClaudeProfile.SlugFromDirectoryName(".claude-client-a"));
        Assert.Null(ClaudeProfile.SlugFromDirectoryName(".claude"));
        Assert.Null(ClaudeProfile.SlugFromDirectoryName(".claude-"));
        Assert.Null(ClaudeProfile.SlugFromDirectoryName(".claude.json"));
        Assert.Null(ClaudeProfile.SlugFromDirectoryName(".claudette"));
        Assert.Null(ClaudeProfile.SlugFromDirectoryName("claude-work"));
    }

    /// <summary>The default comes first, then the rest by slug, so the rings keep their places.</summary>
    [Fact]
    public void DiscoveryFindsEveryUsedProfileInAStableOrder()
    {
        var files = HomeWith(
            (".claude", ["settings.json"]),
            (".claude-work", ["settings.json"]),
            (".claude-alpha", ["history.jsonl"]));
        var keychain = SignedIn(Home, string.Empty, "work", "alpha");

        var found = ClaudeProfile.Discover(Home, files, keychain, "testaccount");

        Assert.Equal(["claude", "claude-alpha", "claude-work"], found.Select(profile => profile.Id));
        Assert.Equal(Path.Combine(Home, ".claude-work"), found[2].ConfigDirectory);
    }

    /// <summary>An empty directory is not a profile: a permanent "sign in" ring for an account that does not exist.</summary>
    [Fact]
    public void DirectoriesClaudeCodeHasNeverUsedAreIgnored()
    {
        var files = HomeWith(
            (".claude", ["settings.json"]),
            (".claude-empty", []),
            (".claude-notes", ["README.md"]));
        var keychain = SignedIn(Home, string.Empty, "empty", "notes");

        Assert.Equal(
            ["claude"],
            ClaudeProfile.Discover(Home, files, keychain, "testaccount").Select(profile => profile.Id));
    }

    /// <summary>Any one of the files Claude Code writes on first run is enough.</summary>
    [Fact]
    public void AnyFirstRunMarkerCounts()
    {
        var files = HomeWith(
            (".claude-a", ["sessions"]),
            (".claude-b", ["projects"]),
            (".claude-c", [".claude.json"]));
        var keychain = SignedIn(Home, string.Empty, "a", "b", "c");

        Assert.Equal(
            ["claude", "claude-a", "claude-b", "claude-c"],
            ClaudeProfile.Discover(Home, files, keychain, "testaccount").Select(profile => profile.Id));
    }

    /// <summary>A file named like a profile is not one, and must not crash discovery.</summary>
    [Fact]
    public void AFileNamedLikeAProfileIsIgnored()
    {
        var files = HomeWith((".claude", ["settings.json"]));
        files.AddFile(Path.Combine(Home, ".claude-work"), "not a directory"u8.ToArray());
        var keychain = SignedIn(Home, string.Empty, "work");

        Assert.Equal(
            ["claude"],
            ClaudeProfile.Discover(Home, files, keychain, "testaccount").Select(profile => profile.Id));
    }

    /// <summary>`~/.claude` has always been read whether or not it exists yet.</summary>
    [Fact]
    public void TheDefaultIsAlwaysPresent()
    {
        var found = ClaudeProfile.Discover(Home, new FakeClaudeFileSystem(), new FakeKeychain(), "testaccount");

        Assert.Equal(["claude"], found.Select(profile => profile.Id));
    }

    /// <summary>A plugin is not an account: no token under the directory's own service, no ring.</summary>
    [Fact]
    public void ADirectoryWithNoTokenIsNotAnAccount()
    {
        var files = HomeWith(
            (".claude", ["settings.json"]),
            (".claude-mem", ["sessions", "settings.json"]));

        Assert.Equal(
            ["claude"],
            ClaudeProfile.Discover(Home, files, new FakeKeychain(), "testaccount")
                .Select(profile => profile.Id));
        Assert.Equal(
            ["claude", "claude-mem"],
            ClaudeProfile.Discover(Home, files, SignedIn(Home, string.Empty, "mem"), "testaccount")
                .Select(profile => profile.Id));
    }

    /// <summary>The default is read whether or not it has a token: it is the one ring that says "sign in".</summary>
    [Fact]
    public void TheDefaultSurvivesHavingNoToken()
    {
        var files = HomeWith((".claude", ["settings.json"]));

        Assert.Equal(
            ["claude"],
            ClaudeProfile.Discover(Home, files, new FakeKeychain(), "testaccount")
                .Select(profile => profile.Id));
    }

    // MARK: - Reading the account file

    /// <summary>The account file is not decoded again while it is unchanged: it can be hundreds of megabytes.</summary>
    [Fact]
    public void TheAccountFileIsNotDecodedAgainWhileItIsUnchanged()
    {
        var files = new FakeClaudeFileSystem();
        var stamp = new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero);
        WriteAccount(files, "one@example.com", "org-one", stamp);
        var profile = ClaudeProfile.Default(Home, files, "testaccount");

        Assert.Equal("one@example.com", profile.SignedInAddress());

        // Same length, same stamp, different contents — nothing outside a test can produce that.
        WriteAccount(files, "two@example.com", "org-one", stamp);

        Assert.Equal("one@example.com", profile.SignedInAddress());
    }

    /// <summary>The other half: switching account in Claude Code rewrites the file, and the ring follows it.</summary>
    [Fact]
    public void AChangedAccountFileIsDecodedAgain()
    {
        var files = new FakeClaudeFileSystem();
        WriteAccount(files, "one@example.com", "org-one", new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero));
        var profile = ClaudeProfile.Default(Home, files, "testaccount");

        Assert.Equal("one@example.com", profile.SignedInAddress());
        Assert.Equal("org-one", profile.OrganizationId());

        WriteAccount(files, "two@example.com", "org-two", new DateTimeOffset(2026, 9, 9, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal("two@example.com", profile.SignedInAddress());
        Assert.Equal("org-two", profile.OrganizationId());
    }

    /// <summary>Signing out removes the file; the held answer goes with it.</summary>
    [Fact]
    public void AnAccountFileThatGoesAwayIsNotStillAnswered()
    {
        var files = new FakeClaudeFileSystem();
        WriteAccount(files, "one@example.com", "org-one", new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero));
        var profile = ClaudeProfile.Default(Home, files, "testaccount");

        Assert.Equal("one@example.com", profile.SignedInAddress());

        files.Files.Clear();
        files.Modified.Clear();

        Assert.Null(profile.SignedInAddress());
        Assert.Null(profile.OrganizationId());
    }

    /// <summary>A reading from Claude Desktop's cache carries no plan, so the profile's own record says whose it is.</summary>
    [Fact]
    public void TheOrganizationNamesThePlan()
    {
        var files = new FakeClaudeFileSystem();
        files.AddFile(
            Path.Combine(Home, ".claude.json"),
            """{"oauthAccount":{"organizationType":"claude_enterprise"}}"""u8.ToArray(),
            new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero));

        var profile = ClaudeProfile.Default(Home, files, "testaccount");

        Assert.Equal("enterprise", profile.OrganizationPlan());
        Assert.Equal("Enterprise", ClaudeUsageResponse.PlanName(profile.OrganizationPlan()));
    }

    [Fact]
    public void NoOrganizationTypeMeansNoPlan()
    {
        var files = new FakeClaudeFileSystem();
        WriteAccount(files, "one@example.com", "org-one", new DateTimeOffset(2026, 9, 9, 8, 0, 0, TimeSpan.Zero));

        Assert.Null(ClaudeProfile.Default(Home, files, "testaccount").OrganizationPlan());
    }

    /// <summary>The default profile's file sits beside its directory, a named profile's inside it.</summary>
    [Fact]
    public void TheAccountFileOfANamedProfileSitsInsideItsDirectory()
    {
        var profile = new ClaudeProfile(
            "work", Path.Combine(Home, ".claude-work"), new FakeClaudeFileSystem(), "testaccount");

        Assert.Equal(Path.Combine(Home, ".claude-work/.claude.json"), profile.AccountFileUrl);
        Assert.Equal(Path.Combine(Home, ".claude.json"), ClaudeProfile.Default(Home).AccountFileUrl);
    }

    // MARK: - The name a ring is drawn with

    [Theory]
    [InlineData("user@example.com", "Example")]
    [InlineData("user@mail.example.com", "Mail")]
    [InlineData("user@work.example", "Work")]
    [InlineData("mail@work.example", "Work")]
    [InlineData("a@b@work.example", "Work")]
    [InlineData("user@IBM.example", "IBM")]
    [InlineData("user@McMail.example", "McMail")]
    public void TheDomainNamesTheAccount(string address, string expected)
    {
        Assert.Equal(expected, ClaudeProfile.AccountLabelForAddress(address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("someone@")]
    [InlineData("someone@.com")]
    [InlineData("someone@123.45")]
    public void NoLabelRatherThanANonsenseOne(string? address)
    {
        Assert.Null(ClaudeProfile.AccountLabelForAddress(address));
    }

    /// <summary>Two accounts that would be called the same thing fall back to the address.</summary>
    [Fact]
    public void DisplayNamesSettleAClash()
    {
        var files = new FakeClaudeFileSystem();
        files.AddFile(
            Path.Combine(Home, ".claude.json"),
            """{"oauthAccount":{"emailAddress":"user@example.com"}}"""u8.ToArray());
        files.AddFile(
            Path.Combine(Home, ".claude-work/.claude.json"),
            """{"oauthAccount":{"emailAddress":"work@example.com"}}"""u8.ToArray());

        var profiles = new List<ClaudeProfile>
        {
            ClaudeProfile.Default(Home, files, "testaccount"),
            new("work", Path.Combine(Home, ".claude-work"), files, "testaccount"),
        };

        var names = ClaudeProfile.DisplayNames(profiles);

        Assert.Equal("Claude user@example.com", names["claude"]);
        Assert.Equal("Claude work@example.com", names["claude-work"]);
    }

    [Fact]
    public void DistinctAccountsKeepTheShortName()
    {
        var files = new FakeClaudeFileSystem();
        files.AddFile(
            Path.Combine(Home, ".claude.json"),
            """{"oauthAccount":{"emailAddress":"user@example.com"}}"""u8.ToArray());
        files.AddFile(
            Path.Combine(Home, ".claude-work/.claude.json"),
            """{"oauthAccount":{"emailAddress":"user@work.example"}}"""u8.ToArray());

        var profiles = new List<ClaudeProfile>
        {
            ClaudeProfile.Default(Home, files, "testaccount"),
            new("work", Path.Combine(Home, ".claude-work"), files, "testaccount"),
        };

        var names = ClaudeProfile.DisplayNames(profiles);

        Assert.Equal("Claude Example", names["claude"]);
        Assert.Equal("Claude Work", names["claude-work"]);
    }

    /// <summary>The sign-in command names the directory, quoted, because it is run rather than printed.</summary>
    [Fact]
    public void TheSignInCommandQuotesANamedProfilesPath()
    {
        var profile = new ClaudeProfile(
            "work", "/tmp/a dir; touch /tmp/pwned", new FakeClaudeFileSystem(), "testaccount");

        Assert.Contains("'/tmp/a dir; touch /tmp/pwned'", profile.SignInCommand);
        Assert.DoesNotContain("=/tmp/a dir;", profile.SignInCommand);
    }

    [Fact]
    public void AnApostropheCannotCloseTheQuote()
    {
        var profile = new ClaudeProfile(
            "work", "/tmp/O'Brien/.claude-work", new FakeClaudeFileSystem(), "testaccount");

        Assert.Contains("'\"'\"'", profile.SignInCommand);
    }

    /// <summary>A quoted tilde does not expand, so the real path is used.</summary>
    [Fact]
    public void TheSignInCommandUsesTheRealPathRatherThanTheTilde()
    {
        var profile = new ClaudeProfile(
            "work", ClaudeProfile.HomeDirectory + "/.claude-work", new FakeClaudeFileSystem(), "testaccount");

        Assert.DoesNotContain("'~", profile.SignInCommand);
        Assert.Contains(ClaudeProfile.HomeDirectory, profile.SignInCommand);
    }

    private static void WriteAccount(
        FakeClaudeFileSystem files,
        string address,
        string organization,
        DateTimeOffset modified) =>
        files.AddFile(
            Path.Combine(Home, ".claude.json"),
            System.Text.Encoding.UTF8.GetBytes(
                $"{{\"oauthAccount\":{{\"emailAddress\":\"{address}\",\"organizationUuid\":\"{organization}\"}}}}"),
            modified);
}
