namespace QuotaDeck.Readers.Tests.OpenCode;

using System.Text.Json;
using QuotaDeck.Readers.OpenCode;

/// <summary>
/// The credential is borrowed from OpenCode's own sign-in, so only OpenCode's two ids may ever be
/// claimed — any other entry is somebody else's account. Port of Codenotch
/// <c>Tests/OpenCodeUsageTests.swift</c> (<c>OpenCodeCredentialsTests</c>).
/// </summary>
[Collection("OpenCode environment")]
public class OpenCodeCredentialsTests
{
    [Fact]
    public void ReadsTheGoKey()
    {
        using var directory = new TempDirectory();
        directory.Write("auth.json", """{"opencode-go":{"type":"api","key":"sk-go-live"}}""");

        var credential = OpenCodeCredentials.LoadGoKey(directory.File("auth.json"));

        Assert.Equal("sk-go-live", credential?.Token);
        Assert.False(credential!.OAuth);
    }

    [Fact]
    public void ReadsABareStringEntry()
    {
        using var directory = new TempDirectory();
        directory.Write("auth.json", """{"opencode-go":"sk-go-live"}""");

        Assert.Equal("sk-go-live", OpenCodeCredentials.LoadGoKey(directory.File("auth.json"))?.Token);
    }

    [Fact]
    public void NeverClaimsAnotherVendorsKey()
    {
        using var directory = new TempDirectory();
        directory.Write("auth.json", """{"openai":{"type":"api","key":"sk-openai"},"google":{"type":"api","key":"g-key"}}""");

        Assert.Null(OpenCodeCredentials.LoadGoKey(directory.File("auth.json")));
    }

    [Fact]
    public void AnEmptyKeyIsMissing()
    {
        using var directory = new TempDirectory();
        directory.Write("auth.json", """{"opencode-go":{"type":"api","key":""}}""");

        Assert.Null(OpenCodeCredentials.LoadGoKey(directory.File("auth.json")));
    }

    /// <summary>
    /// The alternative object shapes: the first non-empty of key/apiKey/api_key/token/accessToken
    /// wins, and an empty one is skipped rather than claimed.
    /// </summary>
    [Fact]
    public void ReadsTheAlternativeApiKeyShapes()
    {
        using var directory = new TempDirectory();
        directory.Write("auth.json", """{"opencode-go":{"apiKey":"","api_key":"sk-alt","token":"sk-later"}}""");

        Assert.Equal("sk-alt", OpenCodeCredentials.LoadGoKey(directory.File("auth.json"))?.Token);
    }

    /// <summary>
    /// An OAuth sign-in carries an org and a console, and no Go key — which is the whole reason it
    /// needs a different endpoint.
    /// </summary>
    [Fact]
    public void OAuthCarriesOrgConsoleAndExpiry()
    {
        using var entry = JsonDocument.Parse(
            """
            {"type":"oauth","methodID":"device","refresh":"rt_x","access":"st_abc",
             "expires":1792854570201,
             "metadata":{"server":"https://opencode.ai/console","orgID":"org_1",
                         "orgName":"Personal"}}
            """);

        var credential = OpenCodeCredentials.FromEntry(entry.RootElement, OpenCodeCredentials.SourceName);

        Assert.NotNull(credential);
        Assert.Equal("st_abc", credential.Token);
        Assert.True(credential.OAuth);
        Assert.Equal("org_1", credential.Org);
        Assert.Equal("https://opencode.ai/console", credential.Console?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(1_792_854_570.201, credential.Expires!.Value.ToUnixTimeMilliseconds() / 1000.0, precision: 3);
    }

    /// <summary>
    /// <c>expires</c> is a number in auth.json and a string in opencode.db; a reader that takes one
    /// shape silently loses the expiry hint on the other.
    /// </summary>
    [Fact]
    public void TheDatabaseWritesExpiresAsAString()
    {
        using var entry = JsonDocument.Parse("""{"type":"oauth","access":"st_abc","expires":"1792877970039"}""");

        var credential = OpenCodeCredentials.FromEntry(entry.RootElement, OpenCodeCredentials.SourceName);

        Assert.Equal(1_792_877_970.039, credential!.Expires!.Value.ToUnixTimeMilliseconds() / 1000.0, precision: 3);
    }

    /// <summary>
    /// An absurd but finite expiry reads as far-future, like Swift's <c>Date</c> — not missing, and
    /// never a throw that would turn the fetch into an Error status.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"oauth","access":"st_abc","expires":1e300}""")]
    [InlineData("""{"type":"oauth","access":"st_abc","expires":"1e300"}""")]
    public void AnAbsurdExpiryIsFarFutureRatherThanMissingOrThrowing(string json)
    {
        using var entry = JsonDocument.Parse(json);

        var credential = OpenCodeCredentials.FromEntry(entry.RootElement, OpenCodeCredentials.SourceName);

        Assert.Equal(DateTimeOffset.MaxValue, credential!.Expires);
    }

    /// <summary>An OAuth entry with no access token is not a credential.</summary>
    [Fact]
    public void AnOAuthEntryWithoutATokenIsMissing()
    {
        using var entry = JsonDocument.Parse("""{"type":"oauth","access":""}""");

        Assert.Null(OpenCodeCredentials.FromEntry(entry.RootElement, OpenCodeCredentials.SourceName));
    }

    /// <summary>
    /// The credential decides the route and each route refuses the other's, so this pairing is the
    /// fix rather than a detail.
    /// </summary>
    [Fact]
    public void EachCredentialGoesToTheRouteThatAcceptsIt()
    {
        var oauth = new OpenCodeCredentials.Credential("st_x", OAuth: true, Console: null, Org: null, Expires: null, OpenCodeCredentials.SourceName);
        var key = new OpenCodeCredentials.Credential("sk-go", OAuth: false, Console: null, Org: null, Expires: null, OpenCodeCredentials.SourceName);

        Assert.Equal("https://opencode.ai/inference/go/v1/usage", OpenCodeProvider.Endpoint(oauth).AbsoluteUri);
        Assert.Equal("https://opencode.ai/zen/go/v1/usage", OpenCodeProvider.Endpoint(key).AbsoluteUri);
    }

    // ---- The database. ----

    /// <summary>
    /// The case that broke the ring: no <c>auth.json</c> at all, only the database OpenCode 1.18+
    /// writes.
    /// </summary>
    [Fact]
    public void ReadsTheSignInFromTheDatabaseAlone()
    {
        using var directory = new TempDirectory();
        var database = OpenCodeTestReader.CreateCredentialDatabase(
            directory,
            ("opencode", """{"type":"oauth","access":"st_new","metadata":{"orgID":"org_1"}}""", 1, 3));

        var credential = OpenCodeCredentials.LoadFromDatabase(database);

        Assert.NotNull(credential);
        Assert.Equal("st_new", credential.Token);
        Assert.True(credential.OAuth);
        Assert.Equal("org_1", credential.Org);
    }

    /// <summary>
    /// The combined loader, on a directory holding the database and nothing else — no
    /// <c>auth.json</c> anywhere.
    /// </summary>
    [Fact]
    public void TheCombinedLoaderFindsTheDatabaseAlone()
    {
        using var directory = new TempDirectory();
        OpenCodeTestReader.CreateCredentialDatabase(
            directory,
            ("opencode", """{"type":"oauth","access":"st_new"}""", 1, 3));

        Assert.Equal("st_new", OpenCodeCredentials.Load(directory.Path)?.Token);
    }

    /// <summary>
    /// The order: an <c>auth.json</c> Go key wins over anything in the database, and a database row
    /// wins over the <c>auth.json</c> OAuth fallback.
    /// </summary>
    [Fact]
    public void TheLookupOrderHoldsAcrossAllThreeSources()
    {
        using var directory = new TempDirectory();
        directory.Write(
            "auth.json",
            """
            {"opencode-go":{"type":"api","key":"sk-file-go"},
             "opencode":{"type":"oauth","access":"st-file-oauth"}}
            """);
        OpenCodeTestReader.CreateCredentialDatabase(
            directory,
            ("opencode", """{"type":"oauth","access":"st-db-oauth"}""", 1, 9));

        Assert.Equal("sk-file-go", OpenCodeCredentials.Load(directory.Path)?.Token);
    }

    [Fact]
    public void TheDatabaseNeverClaimsAnotherVendorsRow()
    {
        using var directory = new TempDirectory();
        var database = OpenCodeTestReader.CreateCredentialDatabase(
            directory,
            ("openrouter", """{"type":"api","key":"sk-or"}""", null, 5));

        Assert.Null(OpenCodeCredentials.LoadFromDatabase(database));
    }

    /// <summary>
    /// A Go key beats an OAuth sign-in even when the OAuth row is newer, so a machine holding both
    /// reads the same account whichever file is consulted.
    /// </summary>
    [Fact]
    public void TheDatabasePrefersAGoKey()
    {
        using var directory = new TempDirectory();
        var database = OpenCodeTestReader.CreateCredentialDatabase(
            directory,
            ("opencode", """{"type":"oauth","access":"st_oauth"}""", 1, 9),
            ("opencode-go", """{"type":"api","key":"sk-go"}""", null, 1));

        var credential = OpenCodeCredentials.LoadFromDatabase(database);

        Assert.Equal("sk-go", credential?.Token);
        Assert.False(credential!.OAuth);
    }

    /// <summary>
    /// A row OpenCode has retired is not a sign-in. A row that omits <c>active</c> entirely still is
    /// — both shapes have shipped.
    /// </summary>
    [Fact]
    public void TheDatabaseSkipsRetiredRows()
    {
        using var directory = new TempDirectory();
        var database = OpenCodeTestReader.CreateCredentialDatabase(
            directory,
            ("opencode", """{"type":"oauth","access":"st_old"}""", 0, 9),
            ("opencode", """{"type":"oauth","access":"st_live"}""", null, 1));

        Assert.Equal("st_live", OpenCodeCredentials.LoadFromDatabase(database)?.Token);
    }

    /// <summary>
    /// First *parseable* row per vendor, like the Swift loop: a corrupt row is skipped and the next
    /// one considered, and the Go key's absence does not stop the OAuth row from being claimed.
    /// </summary>
    [Fact]
    public void TheDatabaseSkipsUnparseableRows()
    {
        using var directory = new TempDirectory();
        var database = OpenCodeTestReader.CreateCredentialDatabase(
            directory,
            ("opencode-go", "not json", null, 9),
            ("opencode", """{"type":"oauth","access":"st_after"}""", 1, 5));

        var credential = OpenCodeCredentials.LoadFromDatabase(database);

        Assert.Equal("st_after", credential?.Token);
        Assert.True(credential!.OAuth);
    }

    /// <summary>
    /// A database row that is a bare JSON string is skipped: <c>JSONSerialization</c> without
    /// <c>.fragmentsAllowed</c> refuses a fragment root, so Swift's database reader never sees that
    /// shape, and neither does this one. The bare-string value remains valid in <c>auth.json</c>,
    /// where the entry arrives already parsed — see <see cref="ReadsABareStringEntry"/>.
    /// </summary>
    [Fact]
    public void TheDatabaseSkipsABareStringRow()
    {
        using var directory = new TempDirectory();
        var database = OpenCodeTestReader.CreateCredentialDatabase(
            directory,
            ("opencode-go", "\"sk-bare\"", null, 1));

        Assert.Null(OpenCodeCredentials.LoadFromDatabase(database));
    }

    /// <summary>
    /// The skip order that matters: a bare-string <c>opencode-go</c> row that is newer must not
    /// shadow an older object row — Swift skips the fragment and claims <c>sk-new</c>.
    /// </summary>
    [Fact]
    public void TheDatabaseSkipsABareStringRowInFavourOfAnOlderObjectRow()
    {
        using var directory = new TempDirectory();
        var database = OpenCodeTestReader.CreateCredentialDatabase(
            directory,
            ("opencode-go", "\"sk-old\"", null, 9),
            ("opencode-go", """{"type":"api","key":"sk-new"}""", null, 1));

        Assert.Equal("sk-new", OpenCodeCredentials.LoadFromDatabase(database)?.Token);
    }

    // ---- Presence. ----

    /// <summary>
    /// Installed-but-signed-out and never-installed are different answers, so presence is asked of
    /// the files rather than of the credential.
    /// </summary>
    [Fact]
    public void PresenceNeedsEitherFile()
    {
        using var directory = new TempDirectory();
        Assert.False(OpenCodeCredentials.Present(directory.Path));

        directory.Write("opencode.db", string.Empty);
        Assert.True(OpenCodeCredentials.Present(directory.Path));
    }

    [Fact]
    public void PresenceIsTrueForAuthJsonAlone()
    {
        using var directory = new TempDirectory();
        directory.Write("auth.json", "{}");

        Assert.True(OpenCodeCredentials.Present(directory.Path));
    }

    /// <summary>
    /// The data directory resolves from the environment: XDG_DATA_HOME/opencode when set and
    /// non-empty, else ~/.local/share/opencode.
    /// </summary>
    [Fact]
    public void TheDataDirectoryFollowsXdg()
    {
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", "/tmp/xdg-home");
            Assert.Equal(Path.Combine("/tmp/xdg-home", "opencode"), OpenCodeCredentials.ResolveDataDirectory());

            Environment.SetEnvironmentVariable("XDG_DATA_HOME", string.Empty);
            var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(Path.Combine(home, ".local", "share", "opencode"), OpenCodeCredentials.ResolveDataDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
        }
    }
}
