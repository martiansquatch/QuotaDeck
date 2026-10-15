namespace QuotaDeck.Readers.Tests.Antigravity;

using System.Net;
using System.Text;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Settings;
using QuotaDeck.Readers.Antigravity;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// The source order — language server, direct Cloud Code call, OMP history, request count — and the
/// credential rules around it. Ported from Codenotch <c>Tests/AntigravityTests.swift</c>
/// (<c>AntigravitySourceOrderTests</c>, lines 1773-1829, and the count cases, lines 531-568).
/// </summary>
public class AntigravityReaderTests
{
    private const string QuotaBody =
        """{"response":{"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-weekly","displayName":"Weekly Limit Remaining","window":"weekly","remainingFraction":0.8,"resetTime":"2026-09-07T14:12:34Z"}]}]}}""";

    private static readonly DateTimeOffset Now = new(new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Local));

    private static string NewDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static LimitWindow Window(string id, double used, string label = "5-hour Limit", TimeSpan? duration = null) =>
        new() { Id = id, Label = label, UsedFraction = used, Duration = duration };

    private static AntigravityOptions Options(
        QueueingProcessRunner runner,
        string home,
        string data,
        RecordingTransport? transport = null,
        FakeClock? clock = null,
        Func<CancellationToken, Task<IReadOnlyList<LimitWindow>?>>? localQuota = null) => new()
    {
        Profile = AntigravityProfile.Default(home),
        HomeDirectory = home,
        DataDirectory = data,
        Http = transport ?? new RecordingTransport(),
        Process = runner,
        TimeProvider = clock ?? new FakeClock(Now),
        Loopback = transport ?? new RecordingTransport(),
        LocalQuotaOverride = localQuota,
        ServerPollAttempts = 2,
        ServerPollInterval = TimeSpan.FromMilliseconds(1),
    };

    private static AntigravityReader Reader(AntigravityOptions options, ReaderConfig? config = null) =>
        new(options, config ?? new ReaderConfig(), new InMemoryReaderStateStore(), options.TimeProvider);

    /// <summary>A language server that answers ends the fetch before anything is asked of macOS or Google.</summary>
    [Fact]
    public async Task AnAnsweringBridgeEndsTheFetchBeforeTheCredential()
    {
        var runner = new QueueingProcessRunner();
        var transport = new RecordingTransport();
        var windows = new List<LimitWindow> { Window("gemini-weekly", 0.2, "Weekly Limit", TimeSpan.FromDays(7)) };
        using var reader = Reader(Options(runner, NewDirectory("qd-ag-home"), NewDirectory("qd-ag-data"), transport, localQuota: _ => Task.FromResult<IReadOnlyList<LimitWindow>?>(windows)));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(new[] { "gemini-weekly" }, snapshot.Windows.Select(window => window.Id));
        Assert.Equal(Fidelity.Official, snapshot.Fidelity);
        Assert.Equal("bridge", reader.LastSource);
        Assert.Empty(transport.Requests);
        Assert.Equal(0, runner.Count("/usr/bin/security"));
    }

    /// <summary>
    /// Once the server has answered, its going away means Antigravity was closed: the reading is
    /// aged rather than replaced with a count, and the token is not asked for.
    /// </summary>
    [Fact]
    public async Task OnceBridgedItDoesNotFallBackToTheToken()
    {
        var runner = new QueueingProcessRunner();
        var transport = new RecordingTransport();
        var clock = new FakeClock(Now);
        var answers = new Queue<IReadOnlyList<LimitWindow>?>();
        answers.Enqueue([Window("gemini-weekly", 0.2, "Weekly Limit", TimeSpan.FromDays(7))]);
        answers.Enqueue(null);
        var options = Options(runner, NewDirectory("qd-ag-home"), NewDirectory("qd-ag-data"), transport, clock, localQuota: _ => Task.FromResult(answers.Dequeue()));
        using var reader = Reader(options, new ReaderConfig { StaleAfterMinutes = 0.00001 });

        var first = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(Fidelity.Official, first.Fidelity);

        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Stale, second.Status.Kind);
        Assert.Equal(new[] { "gemini-weekly" }, second.Windows.Select(window => window.Id));
        Assert.Empty(transport.Requests);
        Assert.Equal(0, runner.Count("/usr/bin/security"));
    }

    /// <summary>The bridge's failure is discovered by ps, then the direct call answers.</summary>
    [Fact]
    public async Task TheDirectCallIsUsedWhenTheBridgeIsSilent()
    {
        var runner = new QueueingProcessRunner();
        runner.Respond("/bin/ps", 0, "1 /sbin/launchd\n");
        runner.Respond("/usr/bin/security", 0, KeychainPayload("cloud-token", DateTimeOffset.UtcNow.AddHours(2)));
        var transport = new RecordingTransport();
        transport.Respond(HttpStatusCode.OK, QuotaBody);
        using var reader = Reader(Options(runner, NewDirectory("qd-ag-home"), NewDirectory("qd-ag-data"), transport));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal("cloud", reader.LastSource);
        Assert.Equal(Fidelity.Official, snapshot.Fidelity);
        Assert.Equal(new[] { "gemini-weekly" }, snapshot.Windows.Select(window => window.Id));

        var request = transport.Requests.Single();
        Assert.Contains("daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary", request.Url, StringComparison.Ordinal);
        Assert.Equal("Bearer cloud-token", request.Request.Headers.GetValues("Authorization").Single());
        Assert.Contains("cl=963137146", request.Request.Headers.GetValues("User-Agent").Single(), StringComparison.Ordinal);
        Assert.Equal("{}", request.Body);
    }

    [Fact]
    public async Task TheCloudCallCarriesTheProjectWhenTheCredentialNamesOne()
    {
        var home = NewDirectory("qd-ag-home");
        Directory.CreateDirectory(Path.Combine(home, ".gemini", "antigravity"));
        File.WriteAllText(
            Path.Combine(home, ".gemini", "antigravity", "oauth_creds.json"),
            $$"""{"access_token":"json-token","expiry_date":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}},"email":"user@example.com","project_id":"example-project"}""");
        var runner = new QueueingProcessRunner();
        runner.Respond("/bin/ps", 0, "1 /sbin/launchd\n");
        runner.Respond("/usr/bin/security", 1, "");
        var transport = new RecordingTransport();
        transport.Respond(HttpStatusCode.OK, QuotaBody);
        using var reader = Reader(Options(runner, home, NewDirectory("qd-ag-data"), transport));

        await reader.FetchAsync(Freshness.Standard);

        Assert.Equal("{\"project\":\"example-project\"}", transport.Requests.Single().Body);
    }

    /// <summary>The OMP history answers when the cloud refuses, without a credential at all.</summary>
    [Fact]
    public async Task TheOmpHistoryIsUsedWhenTheCloudRefuses()
    {
        var home = NewDirectory("qd-ag-home");
        WriteOmpUsageHistory(home);
        var runner = new QueueingProcessRunner();
        runner.Respond("/bin/ps", 0, "1 /sbin/launchd\n");
        runner.Respond("/usr/bin/security", 1, "");
        var transport = new RecordingTransport();
        using var reader = Reader(Options(runner, home, NewDirectory("qd-ag-data"), transport));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal("omp", reader.LastSource);
        Assert.Equal(Fidelity.Official, snapshot.Fidelity);
        Assert.Equal(new[] { "gemini-hourly", "gemini-weekly", "3p-hourly", "3p-weekly" }, snapshot.Windows.Select(window => window.Id));
        Assert.Equal(0.17, snapshot.Windows.Single(window => window.Id == "gemini-hourly").UsedFraction!.Value, 4);
    }

    /// <summary>
    /// Nothing answered and nothing has ever bridged: the count is the honest answer, and a failed
    /// credential read never surfaces as an auth error.
    /// </summary>
    [Fact]
    public async Task TheRequestCountIsShownWhenNothingElseAnswers()
    {
        var home = NewDirectory("qd-ag-home");
        var transcript = Path.Combine(home, ".gemini", "antigravity", "brain", "t1", ".system_generated", "logs");
        Directory.CreateDirectory(transcript);
        File.WriteAllText(
            Path.Combine(transcript, "transcript.jsonl"),
            $$"""{"created_at":"{{Now.AddHours(-2).ToUniversalTime():o}}","source":"MODEL","type":"PLANNER_RESPONSE"}""");
        var runner = new QueueingProcessRunner();
        runner.Respond("/bin/ps", 0, "1 /sbin/launchd\n");
        runner.Respond("/usr/bin/security", 1, "");
        using var reader = Reader(Options(runner, home, NewDirectory("qd-ag-data")));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal("requests", reader.LastSource);
        Assert.Equal(Fidelity.Derived, snapshot.Fidelity);
        var window = snapshot.Windows.Single();
        Assert.Equal("requests", window.Id);
        Assert.Equal(1, window.Used);
        Assert.Null(window.UsedFraction);
        Assert.Equal("Requests today · no limit published", window.Label);
    }

    /// <summary>The CLI has no token, so the loopback request must carry no CSRF header at all.</summary>
    [Fact]
    public async Task AClILanguageServerIsAskedWithoutAToken()
    {
        var runner = new QueueingProcessRunner();
        runner.Respond("/bin/ps", 0, "1 /sbin/launchd\n34221 agy\n");
        runner.Respond("/usr/sbin/lsof", 0, "agy 34221 local 3u IPv4 0x1 0t0 TCP 127.0.0.1:54166 (LISTEN)\n");
        var transport = new RecordingTransport();
        transport.Respond(HttpStatusCode.OK, QuotaBody);
        using var reader = Reader(Options(runner, NewDirectory("qd-ag-home"), NewDirectory("qd-ag-data"), transport));

        var snapshot = await reader.FetchAsync(Freshness.Standard);

        Assert.Equal("bridge", reader.LastSource);
        Assert.Equal(new[] { "gemini-weekly" }, snapshot.Windows.Select(window => window.Id));
        var request = transport.Requests.Single();
        Assert.Contains(":54166/", request.Url, StringComparison.Ordinal);
        Assert.False(request.Request.Headers.Contains("x-codeium-csrf-token"));
    }

    /// <summary>The bridged flag is persisted, so a restart still knows the server once answered.</summary>
    [Fact]
    public async Task TheBridgedFlagSurvivesARestart()
    {
        var home = NewDirectory("qd-ag-home");
        var data = NewDirectory("qd-ag-data");
        var windows = new List<LimitWindow> { Window("gemini-weekly", 0.2, "Weekly Limit", TimeSpan.FromDays(7)) };
        using (var first = Reader(Options(new QueueingProcessRunner(), home, data, localQuota: _ => Task.FromResult<IReadOnlyList<LimitWindow>?>(windows))))
        {
            await first.FetchAsync(Freshness.Standard);
        }

        Assert.True(File.Exists(Path.Combine(data, "antigravity-bridged")));

        using var restarted = Reader(Options(new QueueingProcessRunner(), home, data, localQuota: _ => Task.FromResult<IReadOnlyList<LimitWindow>?>(null)));
        var snapshot = await restarted.FetchAsync(Freshness.Standard);

        Assert.Equal(ProviderStatusKind.Stale, snapshot.Status.Kind);
        Assert.Empty(snapshot.Windows);
    }

    // MARK: - Headline and weekly selection

    [Fact]
    public void TheHeadlineIsTheMostUsedUsableGeminiWindow()
    {
        var windows = new List<LimitWindow>
        {
            Window("gemini-hourly", 0.2, duration: TimeSpan.FromHours(5)),
            Window("gemini-weekly", 0.9, "Weekly Limit", TimeSpan.FromDays(7)),
            Window("3p-hourly", 0.95, duration: TimeSpan.FromHours(5)),
        };

        Assert.Equal("gemini-weekly", AntigravityReader.ResolveHeadlineId(windows));
    }

    /// <summary>An exhausted lane does not displace a usable one unless every lane is exhausted.</summary>
    [Fact]
    public void AnExhaustedLaneDoesNotDisplaceAUsableOne()
    {
        var windows = new List<LimitWindow>
        {
            Window("gemini-hourly", 1.0, duration: TimeSpan.FromHours(5)),
            Window("gemini-weekly", 0.5, "Weekly Limit", TimeSpan.FromDays(7)),
        };

        Assert.Equal("gemini-weekly", AntigravityReader.ResolveHeadlineId(windows));
    }

    [Fact]
    public void WhenEveryLaneIsExhaustedTheMostUsedLeads()
    {
        var windows = new List<LimitWindow>
        {
            Window("gemini-hourly", 1.0, duration: TimeSpan.FromHours(5)),
            Window("gemini-weekly", 1.2, "Weekly Limit", TimeSpan.FromDays(7)),
        };

        Assert.Equal("gemini-weekly", AntigravityReader.ResolveHeadlineId(windows));
    }

    [Fact]
    public void TiesGoToTheSmallerId()
    {
        var windows = new List<LimitWindow>
        {
            Window("gemini-hourly", 0.5, duration: TimeSpan.FromHours(5)),
            Window("gemini-weekly", 0.5, "Weekly Limit", TimeSpan.FromDays(7)),
        };

        Assert.Equal("gemini-hourly", AntigravityReader.ResolveHeadlineId(windows));
    }

    [Fact]
    public void AFiveHourPreferencePicksTheMostUsedMatchingWindow()
    {
        var windows = new List<LimitWindow>
        {
            Window("gemini-hourly", 0.4, duration: TimeSpan.FromHours(5)),
            Window("gemini-weekly", 0.9, "Weekly Limit", TimeSpan.FromDays(7)),
        };

        Assert.Equal("gemini-hourly", AntigravityReader.ResolveHeadlineId(windows, "gemini", "five-hour"));
    }

    [Fact]
    public void AWeeklyPreferencePicksTheMostUsedMatchingWindow()
    {
        var windows = new List<LimitWindow>
        {
            Window("gemini-hourly", 0.9, duration: TimeSpan.FromHours(5)),
            Window("gemini-weekly", 0.4, "Weekly Limit", TimeSpan.FromDays(7)),
        };

        Assert.Equal("gemini-weekly", AntigravityReader.ResolveHeadlineId(windows, "gemini", "weekly"));
    }

    [Fact]
    public void TheThirdPartyPreferenceLeadsWithThoseWindows()
    {
        var windows = new List<LimitWindow>
        {
            Window("gemini-hourly", 0.9, duration: TimeSpan.FromHours(5)),
            Window("3p-hourly", 0.4, duration: TimeSpan.FromHours(5)),
        };

        Assert.Equal("3p-hourly", AntigravityReader.ResolveHeadlineId(windows, "3p"));
    }

    [Fact]
    public void TheWeeklyWindowIsTheMostUsedWeeklyOneForTheChosenModel()
    {
        var windows = new List<LimitWindow>
        {
            Window("gemini-weekly", 0.2, "Weekly Limit", TimeSpan.FromDays(7)),
            Window("3p-weekly", 0.8, "Weekly Limit", TimeSpan.FromDays(7)),
        };

        Assert.Equal("gemini-weekly", AntigravityReader.ResolveWeeklyId(windows));
        Assert.Equal("3p-weekly", AntigravityReader.ResolveWeeklyId(windows, "3p"));
    }

    [Fact]
    public void NoWeeklyWindowMeansNone()
    {
        Assert.Null(AntigravityReader.ResolveWeeklyId([Window("gemini-hourly", 0.5, duration: TimeSpan.FromHours(5))]));
    }

    [Fact]
    public void AnEmptyWindowListFallsBackToTheModelDefault()
    {
        Assert.Equal("gemini-hourly", AntigravityReader.ResolveHeadlineId([]));
    }

    // MARK: - Fixtures

    private static string KeychainPayload(string token, DateTimeOffset expiry)
    {
        var json = "{\"auth_method\":\"consumer\",\"token\":{\"access_token\":\"" + token + "\",\"expiry\":\"" + expiry.ToString("o") + "\"}}";
        return "go-keyring-base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static void WriteOmpUsageHistory(string home)
    {
        var path = AntigravityOmp.DatabasePath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE usage_history (provider TEXT, email TEXT, limit_id TEXT, label TEXT, " +
                "window_label TEXT, used_fraction REAL, resets_at REAL, recorded_at INTEGER, id INTEGER);";
            create.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO usage_history (provider, email, limit_id, label, window_label, used_fraction, resets_at, recorded_at, id) " +
            "VALUES ('google-antigravity', 'user@example.com', 'gemini-5h:google:abc', 'Google Gemini', '5-hour', 0.17, 0, 1, 1);";
        insert.ExecuteNonQuery();
    }
}
