namespace QuotaDeck.Readers.Antigravity;

using System.Globalization;
using System.Text;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// Asks Antigravity's own language server for the quota, and starts one when none is running.
///
/// Port of Codenotch <c>Sources/Providers/AntigravityBridge.swift</c>: discovery from the process
/// table (the IDE's server is the line carrying <c>--csrf_token</c>; the CLI is a line whose second
/// field is exactly <c>agy</c>, and asks for no token), ports found through <c>lsof -a</c> rather
/// than assumed, the same RPC Antigravity's own window uses. The started server carries the same
/// flags on the same command line, so discovery finds it on every later poll like any other.
/// </summary>
public static class AntigravityBridge
{
    /// <summary>Antigravity is built on Codeium's stack, and the header still says so.</summary>
    public const string CsrfHeader = "x-codeium-csrf-token";

    public const string ServicePath =
        "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";

    public const string QuotaUrlFormat = "https://127.0.0.1:{0}" + ServicePath;

    public static readonly TimeSpan QuotaTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Where the language server is listening, and the token it demands.</summary>
    public sealed record Endpoint(IReadOnlyList<int> Ports, string? CsrfToken);

    /// <summary>Run <c>ps</c> and <c>lsof</c> through the shared process runner.</summary>
    public static async Task<Endpoint?> DiscoverAsync(IProcessRunner process, CancellationToken cancellationToken)
    {
        var table = await RunAsync(process, "/bin/ps", ["-Ao", "pid,command"], cancellationToken).ConfigureAwait(false);
        if (table is null)
        {
            return null;
        }

        return await DiscoverAsync(
            table,
            (pid, token) => ListeningPortsAsync(process, pid, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pure selection, with the port lookup supplied — the shape tests drive.</summary>
    public static async Task<Endpoint?> DiscoverAsync(
        string processTable,
        Func<int, CancellationToken, Task<IReadOnlyList<int>>> listeningPorts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processTable);
        ArgumentNullException.ThrowIfNull(listeningPorts);

        var lines = processTable.Split('\n');

        // The IDE's language server, which is the one that carries a token.
        var ide = lines.FirstOrDefault(line =>
            line.Contains("language_server", StringComparison.Ordinal)
            && line.Contains("--csrf_token", StringComparison.Ordinal));
        if (ide is not null
            && ValueOf("--csrf_token", ide) is { } token
            && Pid(ide) is { } idePid)
        {
            var ports = await listeningPorts(idePid, cancellationToken).ConfigureAwait(false);
            if (ports.Count > 0)
            {
                return new Endpoint(ports, token);
            }
        }

        // Then the CLI, which serves the same RPC to anything on loopback.
        var cli = lines.FirstOrDefault(IsCli);
        if (cli is not null && Pid(cli) is { } cliPid)
        {
            var ports = await listeningPorts(cliPid, cancellationToken).ConfigureAwait(false);
            if (ports.Count > 0)
            {
                return new Endpoint(ports, null);
            }
        }

        return null;
    }

    /// <summary>
    /// The CLI runs as plain <c>agy</c>, so the executable's own name is matched rather than looking
    /// for "agy" anywhere in the line — three letters turn up inside real words and real paths.
    /// </summary>
    public static bool IsCli(string line)
    {
        var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2
            && Path.GetFileName(fields[1]) is "agy";
    }

    /// <summary>The word after a flag, or null when it is absent or last.</summary>
    public static string? ValueOf(string flag, string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(parts, flag);
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : null;
    }

    private static int? Pid(string line)
    {
        var first = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    /// <summary>
    /// Ports are found rather than assumed. <c>-a</c> is load-bearing: without it lsof ORs the
    /// filters and returns every listening socket on the machine.
    /// </summary>
    public static async Task<IReadOnlyList<int>> ListeningPortsAsync(
        IProcessRunner process,
        int pid,
        CancellationToken cancellationToken)
    {
        var output = await RunAsync(
            process,
            "/usr/sbin/lsof",
            ["-nP", "-a", "-p", pid.ToString(CultureInfo.InvariantCulture), "-iTCP", "-sTCP:LISTEN"],
            cancellationToken).ConfigureAwait(false);

        return output is null ? [] : ParsePorts(output);
    }

    public static IReadOnlyList<int> ParsePorts(string lsofOutput)
    {
        var ports = new List<int>();
        foreach (var line in lsofOutput.Split('\n'))
        {
            var address = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(field => field.Contains(':'));
            if (address is null)
            {
                continue;
            }

            var last = address.Split(':').LastOrDefault();
            if (int.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    /// <summary>
    /// The same flags the IDE passes its own server, minus everything that wires it to a running
    /// IDE; <c>--app_data_dir</c> is a *name* resolved by the server under <c>~/.gemini/</c>, and an
    /// absolute one is refused outright.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string token, string appDataDirectory = "quotadeck-bridge") =>
    [
        "--standalone",
        "--override_ide_name", "antigravity",
        "--subclient_type", "hub",
        "--override_user_agent_name", "antigravity",
        "--https_server_port", "0",
        "--csrf_token", token,
        "--app_data_dir", appDataDirectory,
        "--api_server_url", "https://generativelanguage.googleapis.com",
        "--cloud_code_endpoint", "https://daily-cloudcode-pa.googleapis.com",
    ];

    /// <summary>
    /// The IDE's copy of the server, found through the bundle rather than hardcoded; nil when the
    /// IDE is not installed, which is the honest answer — there is nothing to start.
    /// </summary>
    public static string? ServerBinary(IReadOnlyList<string> applications)
    {
        foreach (var directory in applications)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var app in Directory.EnumerateDirectories(directory, "*.app"))
            {
                var name = Path.GetFileNameWithoutExtension(app);
                if (!string.Equals(name, "antigravity", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var binary = Path.Combine(app, "Contents", "Resources", "bin", "language_server");
                if (IsExecutable(binary))
                {
                    return binary;
                }
            }
        }

        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ask the endpoint. Every port is tried — the server opens two and only one serves this RPC —
    /// and any status other than 200, or any failure, is null: the caller has an honest fallback.
    /// </summary>
    public static async Task<IReadOnlyList<LimitWindow>?> QuotaAsync(
        Endpoint endpoint,
        IHttpTransport transport,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var port in endpoint.Ports)
        {
            var windows = await QuotaAsync(port, endpoint.CsrfToken, transport, now, cancellationToken).ConfigureAwait(false);
            if (windows is { Count: > 0 })
            {
                return windows;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<LimitWindow>?> QuotaAsync(
        int port,
        string? token,
        IHttpTransport transport,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                string.Format(CultureInfo.InvariantCulture, QuotaUrlFormat, port));

            // `forceRefresh` is why this reads as live rather than as whatever was last looked at.
            request.Content = new StringContent("{\"forceRefresh\":true}", Encoding.UTF8, "application/json");

            // Only where there is one. Sending an empty header instead of none is not the same
            // request, and the CLI has no token to send.
            if (token is not null)
            {
                request.Headers.TryAddWithoutValidation(CsrfHeader, token);
            }

            using var response = await transport.SendAsync(request, QuotaTimeout, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode != 200)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return AntigravityQuotaParser.Parse(body, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A refusal on a cached port is the ordinary shape of "it is not running any more".
            return null;
        }
    }

    private static async Task<string?> RunAsync(
        IProcessRunner process,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await process.RunAsync(
                new ProcessRequest
                {
                    FileName = fileName,
                    Arguments = arguments,
                    Timeout = TimeSpan.FromSeconds(10),
                },
                cancellationToken).ConfigureAwait(false);

            return result.ExitCode == 0 ? result.StandardOutput : null;
        }
        catch
        {
            return null;
        }
    }
}
