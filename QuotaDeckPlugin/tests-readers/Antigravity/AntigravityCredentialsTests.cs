namespace QuotaDeck.Readers.Tests.Antigravity;

using System.Text;
using QuotaDeck.Readers.Antigravity;
using QuotaDeck.Readers.Shared;
using QuotaDeck.Readers.Tests.Claude;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// The keychain payload's shape and the source order, ported from Codenotch
/// <c>Tests/AntigravityTests.swift</c> (<c>AntigravityCredentialsTests</c>, lines 7-73) and
/// <c>AntigravityProfileTests.swift</c>'s credential cases. The keychain is the shared
/// <see cref="FakeKeychain"/> the Claude tests script; the security tool is only the refusal
/// rescue.
/// </summary>
public class AntigravityCredentialsTests
{
    private const string Payload =
        """{"auth_method":"consumer","token":{"access_token":"ya29.token","expiry":"2126-08-31T21:53:49.575961+07:00","refresh_token":"r","token_type":"Bearer"}}""";

    private static string Stored(string json) =>
        "go-keyring-base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ItDecodesTheGoKeyringEnvelope()
    {
        var credentials = AntigravityCredentialStore.DecodeKeychain(Stored(Payload));

        Assert.NotNull(credentials);
        Assert.Equal("ya29.token", credentials!.AccessToken);
        Assert.Equal("consumer", credentials.AuthMethod);
        Assert.False(credentials.IsExpired(DateTimeOffset.Parse("2026-09-01T00:00:00Z")));
    }

    /// <summary>Without stripping the marker the value is not JSON at all.</summary>
    [Fact]
    public void RawJSONWithoutTheMarkerStillWorks()
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(Payload));

        Assert.Equal("ya29.token", AntigravityCredentialStore.DecodeKeychain(raw)!.AccessToken);
    }

    /// <summary>An offset timestamp is the instant it names — not UTC, not epoch milliseconds.</summary>
    [Fact]
    public void ItParsesAnOffsetTimestampAtTheRightInstant()
    {
        var parsed = AntigravityCredentialStore.ParseTimestamp("2026-08-31T21:53:49.575961+07:00");

        Assert.NotNull(parsed);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 14, 53, 49, TimeSpan.Zero).AddTicks(5759610), parsed!.Value);
    }

    [Fact]
    public void ItParsesWholeSecondsToo()
    {
        Assert.NotNull(AntigravityCredentialStore.ParseTimestamp("2026-08-31T21:53:49+07:00"));
    }

    [Fact]
    public void AnExpiredTokenIsRecognised()
    {
        var credentials = AntigravityCredentialStore.DecodeKeychain(Stored(Payload.Replace("2126-", "2020-")));

        Assert.NotNull(credentials);
        Assert.True(credentials!.IsExpired(DateTimeOffset.Parse("2026-09-01T00:00:00Z")));
    }

    [Fact]
    public void GarbageIsRejectedRatherThanCrashing()
    {
        Assert.Null(AntigravityCredentialStore.DecodeKeychain("not base64 at all"));
    }

    // MARK: - Source order

    private static string NewHome() =>
        Path.Combine(Path.GetTempPath(), "qd-antigravity-creds-" + Guid.NewGuid().ToString("N"));

    private static string FutureExpiryIso() => DateTimeOffset.UtcNow.AddHours(2).ToString("o");

    private static string PayloadWith(string token, string expiry) =>
        Stored("{\"auth_method\":\"consumer\",\"token\":{\"access_token\":\"" + token + "\",\"expiry\":\"" + expiry + "\"}}");

    /// <summary>One <c>gemini</c>/<c>antigravity</c> keychain item, holding a credential envelope.</summary>
    private static FakeKeychain.FakeItem AddKeychainItem(
        FakeKeychain keychain, string token, string expiry, DateTimeOffset modified) =>
        keychain.Add(
            AntigravityCredentialStore.KeychainService,
            Encoding.UTF8.GetBytes(PayloadWith(token, expiry)),
            modified,
            AntigravityCredentialStore.KeychainAccount);

    private static void WriteJsonCredential(string home, string token, DateTimeOffset expires, string email, string project)
    {
        Directory.CreateDirectory(Path.Combine(home, ".gemini", "antigravity"));
        File.WriteAllText(
            Path.Combine(home, ".gemini", "antigravity", "oauth_creds.json"),
            "{ \"access_token\": \"" + token + "\", \"expiry_date\": " + expires.ToUnixTimeMilliseconds()
            + ", \"email\": \"" + email + "\", \"project_id\": \"" + project + "\" }");
    }

    [Fact]
    public async Task TheKeychainWinsWhenItIsUnexpired()
    {
        var home = NewHome();
        var keychain = new FakeKeychain();
        AddKeychainItem(keychain, "keychain-token", FutureExpiryIso(), DateTimeOffset.UtcNow.AddMinutes(-1));
        WriteJsonCredential(home, "json-token", DateTimeOffset.UtcNow.AddHours(1), "user@example.com", "example-project");

        var store = new AntigravityCredentialStore(Options(home, new QueueingProcessRunner(), keychain: keychain));
        var credentials = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("keychain-token", credentials!.AccessToken);
    }

    /// <summary>Antigravity files a new item on every rotation; the newest one is the live token.</summary>
    [Fact]
    public async Task TheNewestKeychainItemWins()
    {
        var home = NewHome();
        var keychain = new FakeKeychain();
        AddKeychainItem(keychain, "old-token", FutureExpiryIso(), DateTimeOffset.UtcNow.AddDays(-3));
        AddKeychainItem(keychain, "new-token", FutureExpiryIso(), DateTimeOffset.UtcNow.AddMinutes(-5));
        AddKeychainItem(keychain, "oldest-token", FutureExpiryIso(), DateTimeOffset.UtcNow.AddDays(-30));

        var store = new AntigravityCredentialStore(Options(home, new QueueingProcessRunner(), keychain: keychain));
        var credentials = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("new-token", credentials!.AccessToken);
    }

    /// <summary>
    /// A keychain write inside the five-minute window is still noticed: the item's modification
    /// date is part of the change probe, read from its attributes without a prompt.
    /// </summary>
    [Fact]
    public async Task TheKeychainModificationDateIsPartOfTheChangeProbe()
    {
        var home = NewHome();
        var keychain = new FakeKeychain();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var item = AddKeychainItem(keychain, "first-token", FutureExpiryIso(), clock.Now - TimeSpan.FromMinutes(10));
        var store = new AntigravityCredentialStore(Options(home, new QueueingProcessRunner(), clock, keychain));

        var first = await store.LoadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        item.Data = Encoding.UTF8.GetBytes(PayloadWith("second-token", FutureExpiryIso()));
        item.Modified = clock.Now;
        var rotated = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("first-token", first!.AccessToken);
        Assert.Equal("second-token", rotated!.AccessToken);
    }

    [Fact]
    public async Task AnExpiredKeychainFallsToTheOmpStore()
    {
        var home = NewHome();
        var keychain = new FakeKeychain();
        AddKeychainItem(keychain, "keychain-token", "2020-01-01T00:00:00Z", DateTimeOffset.UtcNow.AddMinutes(-1));
        CreateOmpCredentials(home, "omp-token", DateTimeOffset.UtcNow.AddHours(1), "user@example.com", "example-project");

        var credentials = await new AntigravityCredentialStore(
            Options(home, new QueueingProcessRunner(), keychain: keychain)).LoadAsync(CancellationToken.None);

        Assert.Equal("omp-token", credentials!.AccessToken);
        Assert.Equal("user@example.com", credentials.Email);
        Assert.Equal("example-project", credentials.ProjectId);
    }

    [Fact]
    public async Task AnExpiredKeychainAndOmpFallToTheJsonFile()
    {
        var home = NewHome();
        var keychain = new FakeKeychain();
        AddKeychainItem(keychain, "keychain-token", "2020-01-01T00:00:00Z", DateTimeOffset.UtcNow.AddMinutes(-1));
        WriteJsonCredential(home, "json-token", DateTimeOffset.UtcNow.AddHours(1), "user@example.com", "example-project");

        var credentials = await new AntigravityCredentialStore(
            Options(home, new QueueingProcessRunner(), keychain: keychain)).LoadAsync(CancellationToken.None);

        Assert.Equal("json-token", credentials!.AccessToken);
        Assert.Equal("example-project", credentials.ProjectId);
    }

    /// <summary>Everything expired: the first source found is still kept, so the caller can name the account.</summary>
    [Fact]
    public async Task WhenEverythingIsExpiredTheFirstFoundIsKept()
    {
        var home = NewHome();
        var keychain = new FakeKeychain();
        AddKeychainItem(keychain, "keychain-token", "2020-01-01T00:00:00Z", DateTimeOffset.UtcNow.AddMinutes(-1));
        WriteJsonCredential(home, "json-token", DateTimeOffset.UtcNow.AddDays(-1), "user@example.com", "example-project");

        var credentials = await new AntigravityCredentialStore(
            Options(home, new QueueingProcessRunner(), keychain: keychain)).LoadAsync(CancellationToken.None);

        Assert.Equal("keychain-token", credentials!.AccessToken);
    }

    /// <summary>
    /// An unchanged source is not read again inside the five-minute window — the keychain read is
    /// the one that costs the most, and the recheck is what covers an untracked write.
    /// </summary>
    [Fact]
    public async Task AnUnchangedSourceIsHeldUntilTheRecheck()
    {
        var home = NewHome();
        var keychain = new FakeKeychain();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var item = AddKeychainItem(keychain, "first-token", FutureExpiryIso(), clock.Now - TimeSpan.FromMinutes(10));
        var store = new AntigravityCredentialStore(Options(home, new QueueingProcessRunner(), clock, keychain));

        var first = await store.LoadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        var held = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("first-token", first!.AccessToken);
        Assert.Equal("first-token", held!.AccessToken);
        Assert.Equal(1, keychain.ReadCount);

        // Nothing moved, not even the modification date: only the five-minute recheck re-reads.
        clock.Advance(TimeSpan.FromMinutes(6));
        item.Data = Encoding.UTF8.GetBytes(PayloadWith("second-token", FutureExpiryIso()));
        var reread = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("second-token", reread!.AccessToken);
        Assert.Equal(2, keychain.ReadCount);
    }

    // MARK: - The refusal rescue

    /// <summary>
    /// Interaction off means macOS answers with a refusal status rather than a dialogue; the
    /// rescue is `/usr/bin/security`, which is on the item's own partition list.
    /// </summary>
    [Fact]
    public async Task ARefusedItemIsRescuedThroughTheSecurityTool()
    {
        var home = NewHome();
        var keychain = new FakeKeychain();
        var item = AddKeychainItem(keychain, "unused", FutureExpiryIso(), DateTimeOffset.UtcNow.AddMinutes(-1));
        item.ReadStatus = KeychainStatus.AuthFailed;
        var runner = new QueueingProcessRunner();
        runner.Respond("/usr/bin/security", 0, PayloadWith("rescued-token", FutureExpiryIso()) + "\n");

        var credentials = await new AntigravityCredentialStore(Options(home, runner, keychain: keychain))
            .LoadAsync(CancellationToken.None);

        Assert.Equal("rescued-token", credentials!.AccessToken);
        var rescue = runner.Requests.Single(request => request.FileName == "/usr/bin/security");
        Assert.Equal(
            new[] { "find-generic-password", "-a", "antigravity", "-s", "gemini", "-w" },
            rescue.Arguments);
    }

    [Fact]
    public async Task ARefusedItemWithNoRescueIsNoCredential()
    {
        var home = NewHome();
        var keychain = new FakeKeychain();
        var item = AddKeychainItem(keychain, "unused", FutureExpiryIso(), DateTimeOffset.UtcNow.AddMinutes(-1));
        item.ReadStatus = KeychainStatus.InteractionNotAllowed;
        var runner = new QueueingProcessRunner();

        var credentials = await new AntigravityCredentialStore(Options(home, runner, keychain: keychain))
            .LoadAsync(CancellationToken.None);

        Assert.Null(credentials);
        Assert.Equal(1, runner.Count("/usr/bin/security"));
    }

    /// <summary>"No such item" is not a refusal, and must not spend a security-tool call.</summary>
    [Fact]
    public async Task ANotFoundReadIsNotRescued()
    {
        var home = NewHome();
        var keychain = new FakeKeychain();
        var item = AddKeychainItem(keychain, "unused", FutureExpiryIso(), DateTimeOffset.UtcNow.AddMinutes(-1));
        item.ReadStatus = KeychainStatus.ItemNotFound;
        var runner = new QueueingProcessRunner();
        runner.Respond("/usr/bin/security", 0, PayloadWith("rescued-token", FutureExpiryIso()));

        var credentials = await new AntigravityCredentialStore(Options(home, runner, keychain: keychain))
            .LoadAsync(CancellationToken.None);

        Assert.Null(credentials);
        Assert.Equal(0, runner.Count("/usr/bin/security"));
    }

    // MARK: - The JSON file's project id

    /// <summary>Swift reads `projectId` first and falls back to `project_id`.</summary>
    [Fact]
    public async Task ProjectIdWinsOverProjectIdSnakeCase()
    {
        var home = NewHome();
        Directory.CreateDirectory(Path.Combine(home, ".gemini", "antigravity"));
        File.WriteAllText(
            Path.Combine(home, ".gemini", "antigravity", "oauth_creds.json"),
            "{\"access_token\":\"json-token\",\"expiry_date\":4102444800000,"
            + "\"projectId\":\"camel-case\",\"project_id\":\"snake-case\"}");

        var credentials = await new AntigravityCredentialStore(Options(home, new QueueingProcessRunner()))
            .LoadAsync(CancellationToken.None);

        Assert.Equal("camel-case", credentials!.ProjectId);
    }

    private static AntigravityOptions Options(
        string home,
        QueueingProcessRunner runner,
        TimeProvider? clock = null,
        FakeKeychain? keychain = null) => new()
    {
        Profile = AntigravityProfile.Default(home),
        HomeDirectory = home,
        DataDirectory = Path.Combine(home, "data"),
        Http = new FakeTransport(),
        Process = runner,
        TimeProvider = clock ?? TimeProvider.System,
        Keychain = keychain ?? new FakeKeychain(),
    };

    internal static void CreateOmpCredentials(
        string home,
        string access,
        DateTimeOffset expires,
        string email,
        string projectId)
    {
        var path = AntigravityOmp.DatabasePath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE auth_credentials (provider TEXT, data TEXT, updated_at INTEGER);";
            create.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO auth_credentials (provider, data, updated_at) VALUES ('google-antigravity', @data, 1);";
        insert.Parameters.AddWithValue(
            "@data",
            $$"""{"access":"{{access}}","expires":{{expires.ToUnixTimeMilliseconds()}},"projectId":"{{projectId}}","email":"{{email}}"}""");
        insert.ExecuteNonQuery();
    }
}
