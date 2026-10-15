namespace QuotaDeck.Readers.Tests.Antigravity;

using System.Text;
using QuotaDeck.Readers.Antigravity;

/// <summary>
/// The keychain payload's shape and the source order, ported from Codenotch
/// <c>Tests/AntigravityTests.swift</c> (<c>AntigravityCredentialsTests</c>, lines 7-73) and
/// <c>AntigravityProfileTests.swift</c>'s credential cases.
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
        var runner = new QueueingProcessRunner();
        runner.Respond("/usr/bin/security", 0, PayloadWith("keychain-token", FutureExpiryIso()));
        WriteJsonCredential(home, "json-token", DateTimeOffset.UtcNow.AddHours(1), "user@example.com", "example-project");

        var store = new AntigravityCredentialStore(Options(home, runner));
        var credentials = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("keychain-token", credentials!.AccessToken);
    }

    [Fact]
    public async Task AnExpiredKeychainFallsToTheOmpStore()
    {
        var home = NewHome();
        var runner = new QueueingProcessRunner();
        runner.Respond("/usr/bin/security", 0, PayloadWith("keychain-token", "2020-01-01T00:00:00Z"));
        CreateOmpCredentials(home, "omp-token", DateTimeOffset.UtcNow.AddHours(1), "user@example.com", "example-project");

        var credentials = await new AntigravityCredentialStore(Options(home, runner)).LoadAsync(CancellationToken.None);

        Assert.Equal("omp-token", credentials!.AccessToken);
        Assert.Equal("user@example.com", credentials.Email);
        Assert.Equal("example-project", credentials.ProjectId);
    }

    [Fact]
    public async Task AnExpiredKeychainAndOmpFallToTheJsonFile()
    {
        var home = NewHome();
        var runner = new QueueingProcessRunner();
        runner.Respond("/usr/bin/security", 0, PayloadWith("keychain-token", "2020-01-01T00:00:00Z"));
        WriteJsonCredential(home, "json-token", DateTimeOffset.UtcNow.AddHours(1), "user@example.com", "example-project");

        var credentials = await new AntigravityCredentialStore(Options(home, runner)).LoadAsync(CancellationToken.None);

        Assert.Equal("json-token", credentials!.AccessToken);
        Assert.Equal("example-project", credentials.ProjectId);
    }

    /// <summary>Everything expired: the first source found is still kept, so the caller can name the account.</summary>
    [Fact]
    public async Task WhenEverythingIsExpiredTheFirstFoundIsKept()
    {
        var home = NewHome();
        var runner = new QueueingProcessRunner();
        runner.Respond("/usr/bin/security", 0, PayloadWith("keychain-token", "2020-01-01T00:00:00Z"));
        WriteJsonCredential(home, "json-token", DateTimeOffset.UtcNow.AddDays(-1), "user@example.com", "example-project");

        var credentials = await new AntigravityCredentialStore(Options(home, runner)).LoadAsync(CancellationToken.None);

        Assert.Equal("keychain-token", credentials!.AccessToken);
    }

    /// <summary>
    /// An unchanged source is not read again inside the five-minute window — the keychain read is
    /// the one that can raise a dialogue, so it is the one that must not be on the tick.
    /// </summary>
    [Fact]
    public async Task AnUnchangedSourceIsHeldUntilTheRecheck()
    {
        var home = NewHome();
        var runner = new QueueingProcessRunner();
        var clock = new Support.FakeClock(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        runner.Respond("/usr/bin/security", 0, PayloadWith("first-token", FutureExpiryIso()));
        runner.Respond("/usr/bin/security", 0, PayloadWith("second-token", FutureExpiryIso()));
        var store = new AntigravityCredentialStore(Options(home, runner, clock));

        var first = await store.LoadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        var held = await store.LoadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(6));
        var reread = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("first-token", first!.AccessToken);
        Assert.Equal("first-token", held!.AccessToken);
        Assert.Equal("second-token", reread!.AccessToken);
        Assert.Equal(2, runner.Count("/usr/bin/security"));
    }

    [Fact]
    public async Task ARefusedKeychainIsNotACredential()
    {
        var home = NewHome();
        var runner = new QueueingProcessRunner();
        runner.Respond("/usr/bin/security", 1, "");

        var credentials = await new AntigravityCredentialStore(Options(home, runner)).LoadAsync(CancellationToken.None);

        Assert.Null(credentials);
    }

    private static AntigravityOptions Options(
        string home,
        QueueingProcessRunner runner,
        TimeProvider? clock = null) => new()
    {
        Profile = AntigravityProfile.Default(home),
        HomeDirectory = home,
        DataDirectory = Path.Combine(home, "data"),
        Http = new Support.FakeTransport(),
        Process = runner,
        TimeProvider = clock ?? TimeProvider.System,
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
