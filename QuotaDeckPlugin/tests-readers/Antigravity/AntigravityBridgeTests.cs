namespace QuotaDeck.Readers.Tests.Antigravity;

using System.Net;
using QuotaDeck.Readers.Antigravity;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// Discovery from the process table, the started server's shape, and the RPC — ported from
/// Codenotch <c>Tests/AntigravityTests.swift</c> (<c>AntigravityBridgeTests</c>, lines 573-849).
/// Fixtures are redacted: the pid, CSRF token and username are invented.
/// </summary>
public class AntigravityBridgeTests
{
    private const string IdeLine =
        "4242 /Applications/Antigravity.app/Contents/Resources/bin/language_server --standalone " +
        "--csrf_token 11111111-2222-4333-8444-555555555555 --app_data_dir antigravity";

    private const string LsofOutput =
        "language_server 4242 local 12u IPv4 0x1 0t0 TCP 127.0.0.1:63881 (LISTEN)\n" +
        "language_server 4242 local 13u IPv4 0x2 0t0 TCP 127.0.0.1:63882 (LISTEN)";

    private static Task<IReadOnlyList<int>> Ports(int pid, CancellationToken _) =>
        Task.FromResult<IReadOnlyList<int>>([63881, 63882]);

    [Fact]
    public async Task ItReadsTheTokenFromTheProcessTable()
    {
        var endpoint = await AntigravityBridge.DiscoverAsync(
            IdeLine,
            (pid, _) =>
            {
                Assert.Equal(4242, pid);
                return Ports(pid, _);
            },
            CancellationToken.None);

        Assert.NotNull(endpoint);
        Assert.Equal("11111111-2222-4333-8444-555555555555", endpoint!.CsrfToken);
        Assert.Equal(new[] { 63881, 63882 }, endpoint.Ports);
    }

    [Fact]
    public async Task NoAntigravityMeansNoEndpoint()
    {
        Assert.Null(await AntigravityBridge.DiscoverAsync("1 /sbin/launchd", (_, _) => Task.FromResult<IReadOnlyList<int>>([]), CancellationToken.None));
    }

    [Fact]
    public async Task NoPortMeansNoEndpoint()
    {
        Assert.Null(await AntigravityBridge.DiscoverAsync("1 language_server --csrf_token abc", (_, _) => Task.FromResult<IReadOnlyList<int>>([]), CancellationToken.None));
    }

    [Fact]
    public async Task TheCliIsFoundAndAsksForNoToken()
    {
        var endpoint = await AntigravityBridge.DiscoverAsync(
            "1 /sbin/launchd\n34221 agy",
            (pid, _) =>
            {
                Assert.Equal(34221, pid);
                return Task.FromResult<IReadOnlyList<int>>([54166, 54167]);
            },
            CancellationToken.None);

        Assert.NotNull(endpoint);
        Assert.Null(endpoint!.CsrfToken);
        Assert.Equal(new[] { 54166, 54167 }, endpoint.Ports);
    }

    [Fact]
    public async Task TheCliIsFoundByItsFullPathToo()
    {
        var endpoint = await AntigravityBridge.DiscoverAsync(
            "700 /Users/someone/.local/bin/agy",
            (_, _) => Task.FromResult<IReadOnlyList<int>>([9000]),
            CancellationToken.None);

        Assert.NotNull(endpoint);
        Assert.Null(endpoint!.CsrfToken);
    }

    [Fact]
    public async Task TheIdeWinsWhenBothAreRunning()
    {
        var endpoint = await AntigravityBridge.DiscoverAsync(
            IdeLine + "\n34221 agy",
            (_, _) => Task.FromResult<IReadOnlyList<int>>([1]),
            CancellationToken.None);

        Assert.Equal("11111111-2222-4333-8444-555555555555", endpoint!.CsrfToken);
    }

    /// <summary>"agy" is three letters and turns up inside real words, so the executable's name is matched.</summary>
    [Theory]
    [InlineData("/opt/legacy/bin/server")]
    [InlineData("/usr/bin/agyllomerate")]
    [InlineData("imagy-daemon")]
    public async Task SomethingElseWithAgyInItIsNotTheCli(string command)
    {
        Assert.Null(await AntigravityBridge.DiscoverAsync(
            $"500 {command}",
            (_, _) => Task.FromResult<IReadOnlyList<int>>([1]),
            CancellationToken.None));
    }

    [Fact]
    public void ItParsesPortsFromLsof()
    {
        Assert.Equal(new[] { 63881, 63882 }, AntigravityBridge.ParsePorts(LsofOutput));
    }

    /// <summary>The server refuses an absolute <c>--app_data_dir</c> outright; the IDE passes a name.</summary>
    [Fact]
    public void TheStartedServerGetsANameForItsDataDirectoryNotAPath()
    {
        var arguments = AntigravityBridge.Arguments("t");
        var index = arguments.ToList().IndexOf("--app_data_dir");

        Assert.True(index >= 0);
        var value = arguments[index + 1];
        Assert.False(value.StartsWith('/'));
        Assert.DoesNotContain("~", value);
    }

    [Fact]
    public void TheStartedServerIsDiscoverableAndSelfContained()
    {
        var arguments = AntigravityBridge.Arguments("tok-123");
        var joined = string.Join(" ", arguments);

        Assert.Contains("--standalone", arguments);
        Assert.Equal("tok-123", AntigravityBridge.ValueOf("--csrf_token", joined));
        Assert.Contains("--https_server_port", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("host_bridge", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePortIsLeftForTheServerToChoose()
    {
        var joined = string.Join(" ", AntigravityBridge.Arguments("t"));

        Assert.Equal("0", AntigravityBridge.ValueOf("--https_server_port", joined));
    }

    [Fact]
    public void TheStartedServerDoesNotShareTheIdesDataDirectory()
    {
        var joined = string.Join(" ", AntigravityBridge.Arguments("t"));

        Assert.Equal("quotadeck-bridge", AntigravityBridge.ValueOf("--app_data_dir", joined));
    }

    private static AntigravityOptions Options(QueueingProcessRunner runner, int attempts = 5) => new()
    {
        Profile = AntigravityProfile.Default(Path.GetTempPath()),
        HomeDirectory = Path.Combine(Path.GetTempPath(), "qd-antigravity-server-" + Guid.NewGuid().ToString("N")),
        DataDirectory = Path.Combine(Path.GetTempPath(), "qd-antigravity-server-data-" + Guid.NewGuid().ToString("N")),
        Http = new FakeTransport(),
        Process = runner,
        TimeProvider = TimeProvider.System,
        ServerPollAttempts = attempts,
        ServerPollInterval = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task StartingOneBuildsTheServerAndPollsLsof()
    {
        var runner = new QueueingProcessRunner();
        runner.Respond("/bin/sh", 0, "4242\n");
        runner.Respond("/usr/sbin/lsof", 0, string.Empty);
        runner.Respond("/usr/sbin/lsof", 0, LsofOutput);
        var server = new AntigravityServer(Options(runner));

        var endpoint = await server.EndpointOrStartAsync(CancellationToken.None);

        Assert.NotNull(endpoint);
        Assert.Equal(new[] { 63881, 63882 }, endpoint!.Ports);
        Assert.NotNull(endpoint.CsrfToken);

        var start = runner.Requests.First(request => request.FileName == "/bin/sh");
        var command = start.Arguments[1];
        Assert.Contains("language_server", command, StringComparison.Ordinal);
        Assert.Contains("--standalone", command, StringComparison.Ordinal);
        Assert.Contains("--csrf_token", command, StringComparison.Ordinal);
        Assert.Contains(">/dev/null 2>&1 & echo $!", command, StringComparison.Ordinal);

        var lsof = runner.Requests.Where(request => request.FileName == "/usr/sbin/lsof").ToList();
        Assert.True(lsof.Count >= 2);
        Assert.All(lsof, request =>
        {
            Assert.Contains("-a", request.Arguments);
            Assert.Contains("4242", request.Arguments);
        });
    }

    [Fact]
    public async Task StartingTwiceKeepsTheFirstServer()
    {
        var runner = new QueueingProcessRunner();
        runner.Respond("/bin/sh", 0, "4242\n");
        runner.Respond("/usr/sbin/lsof", 0, LsofOutput);
        var server = new AntigravityServer(Options(runner));

        var first = await server.EndpointOrStartAsync(CancellationToken.None);
        var second = await server.EndpointOrStartAsync(CancellationToken.None);

        Assert.Equal(first!.CsrfToken, second!.CsrfToken);
        Assert.Equal(1, runner.Count("/bin/sh"));
    }

    [Fact]
    public async Task AServerThatNeverBindsIsNotRemembered()
    {
        var runner = new QueueingProcessRunner();
        runner.Respond("/bin/sh", 0, "4242\n");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            runner.Respond("/usr/sbin/lsof", 0, string.Empty);
        }

        var server = new AntigravityServer(Options(runner, attempts: 2));
        var endpoint = await server.EndpointOrStartAsync(CancellationToken.None);

        Assert.Null(endpoint);
        Assert.Null(server.Current);
    }

    [Fact]
    public async Task StopKillsTheStartedServer()
    {
        var runner = new QueueingProcessRunner();
        runner.Respond("/bin/sh", 0, "4242\n");
        runner.Respond("/usr/sbin/lsof", 0, LsofOutput);
        var server = new AntigravityServer(Options(runner));
        await server.EndpointOrStartAsync(CancellationToken.None);

        server.Stop();

        var kill = runner.Requests.Single(request => request.FileName == "/bin/kill");
        Assert.Equal(new[] { "-TERM", "4242" }, kill.Arguments);
        Assert.Null(server.Current);
    }

    [Fact]
    public void NoBinaryWithoutAnInstalledIde()
    {
        var empty = Path.Combine(Path.GetTempPath(), "qd-no-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);

        Assert.Null(AntigravityBridge.ServerBinary([empty]));
    }

    [Fact]
    public void FindsTheBinaryInsideTheApp()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes are the point of this check.
        }

        var root = Path.Combine(Path.GetTempPath(), "qd-apps-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "Antigravity.app", "Contents", "Resources", "bin");
        Directory.CreateDirectory(bin);
        var fake = Path.Combine(bin, "language_server");
        File.WriteAllText(fake, "#!/bin/sh\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Equal("language_server", Path.GetFileName(AntigravityBridge.ServerBinary([root])!));

        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(AntigravityBridge.ServerBinary([root]));
    }

    // MARK: - Loopback trust

    [Theory]
    [InlineData("https://127.0.0.1:63881/x", true)]
    [InlineData("https://localhost:1/x", true)]
    [InlineData("https://[::1]:1/x", true)]
    [InlineData("https://evil.example.com/x", false)]
    [InlineData("https://127.0.0.1.evil.com/x", false)]
    public void OnlyLoopbackHostsAreTrusted(string url, bool trusted)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        Assert.Equal(trusted, LoopbackHttpTransport.TrustsServerCertificate(request));
    }

    // MARK: - The RPC

    private const string QuotaBody =
        """{"response":{"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-weekly","displayName":"Weekly Limit Remaining","window":"weekly","remainingFraction":0.8}]}]}}""";

    [Fact]
    public async Task TheEndpointTriesEveryPort()
    {
        var transport = new FakeTransport();
        transport.Respond(HttpStatusCode.InternalServerError, "{}");
        transport.Respond(HttpStatusCode.OK, QuotaBody);
        var endpoint = new AntigravityBridge.Endpoint([63881, 63882], "token");

        var windows = await AntigravityBridge.QuotaAsync(endpoint, transport, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(windows);
        Assert.Single(windows!);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Contains(":63881/", transport.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains(":63882/", transport.Requests[1].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenEndpointSendsTheCsrfHeader()
    {
        var transport = new RecordingTransport();
        transport.Respond(HttpStatusCode.OK, QuotaBody);
        var endpoint = new AntigravityBridge.Endpoint([63881], "11111111-2222-4333-8444-555555555555");

        await AntigravityBridge.QuotaAsync(endpoint, transport, DateTimeOffset.UtcNow, CancellationToken.None);

        var request = transport.Requests.Single();
        Assert.True(request.Request.Headers.TryGetValues("x-codeium-csrf-token", out var values));
        Assert.Equal("11111111-2222-4333-8444-555555555555", values!.Single());
        Assert.Contains("\"forceRefresh\":true", request.Body, StringComparison.Ordinal);
    }

    /// <summary>Sending an empty header instead of none is not the same request; the CLI has no token.</summary>
    [Fact]
    public async Task ATokenlessEndpointSendsNoCsrfHeader()
    {
        var transport = new FakeTransport();
        transport.Respond(HttpStatusCode.OK, QuotaBody);
        var endpoint = new AntigravityBridge.Endpoint([54166], null);

        await AntigravityBridge.QuotaAsync(endpoint, transport, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.False(transport.Requests[0].Headers.Contains("x-codeium-csrf-token"));
    }

    [Fact]
    public async Task ANonOkStatusIsAFailureOfTheSource()
    {
        var transport = new FakeTransport();
        transport.Respond(HttpStatusCode.Forbidden, "{}");
        var endpoint = new AntigravityBridge.Endpoint([63881], null);

        Assert.Null(await AntigravityBridge.QuotaAsync(endpoint, transport, DateTimeOffset.UtcNow, CancellationToken.None));
    }
}
