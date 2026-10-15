namespace QuotaDeck.Readers.Antigravity;

using System.Globalization;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// A language server this reader started, kept for as long as the reader lives.
///
/// Port of Codenotch <c>Sources/Providers/AntigravityBridge.swift</c>'s <c>Owned</c> class: one
/// instance under a lock (two polls must not start two servers), the port left at 0 for the server
/// to choose, and the pid recovered so <c>lsof</c> can find it. The server takes about eight
/// seconds to authenticate after binding, so the caller polls rather than restarting it per fetch.
///
/// A server that has not bound within the polling budget is *kept and re-polled* on the next fetch,
/// as Swift's <c>Owned</c> behaves: <c>endpointOrStart</c> holds the started process and returns its
/// endpoint (ports still empty), <c>pid</c> answers while the process runs, and
/// <c>AntigravityProvider.resolveEndpoint()</c> calls <c>listeningPorts(ofPID:)</c> again on every
/// later attempt — so a slow server is found when it finally binds and a second one is never
/// started. One deliberate difference: in Swift a process that dies leaves the held endpoint
/// answering forever, so no replacement can ever start; here a pid that <c>ps -p</c> no longer
/// describes as ours is forgotten, letting a later fetch start a replacement.
///
/// The start itself goes through <see cref="IProcessRunner"/>: the server is launched as a detached
/// background command (<c>/bin/sh -c "… &amp; echo $!"</c>) so the runner's own wait-to-exit
/// contract is kept, its pid comes back on stdout, and stdout/stderr of the server itself are
/// discarded. Tests script exactly that with the fake runner.
/// </summary>
public sealed class AntigravityServer
{
    /// <summary>
    /// A word from the started server's own command line (<c>--app_data_dir quotadeck-bridge</c>).
    /// SIGTERM is only ever sent to a pid whose <c>ps -p</c> line still carries it: pids are reused,
    /// and signalling whatever now holds the number is not this reader's to do.
    /// </summary>
    public const string BridgeMarker = "quotadeck-bridge";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly AntigravityOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private int? _pid;
    private string? _token;
    private AntigravityBridge.Endpoint? _endpoint;

    public AntigravityServer(AntigravityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this._options = options;
    }

    /// <summary>The pid of a server this instance started, while it is believed alive.</summary>
    public int? Pid => this._pid;

    public AntigravityBridge.Endpoint? Current => this._endpoint;

    /// <summary>
    /// The endpoint already running, or a freshly started server's once it has bound a port. Null
    /// when there is nothing to start — or when the server has not bound yet, in which case the pid
    /// is kept and the next call re-polls the same process rather than starting another.
    /// </summary>
    public async Task<AntigravityBridge.Endpoint?> EndpointOrStartAsync(CancellationToken cancellationToken)
    {
        if (this._endpoint is { } current)
        {
            return current;
        }

        await this._gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this._endpoint is { } held)
            {
                return held;
            }

            int pid;
            string token;
            if (this._pid is { } kept
                && this._token is { } keptToken
                && await this.IsOursAsync(kept, cancellationToken).ConfigureAwait(false))
            {
                // Re-poll the server a previous fetch started: Swift's Owned keeps the process and
                // its endpoint (ports still empty) for exactly this, so a slow-starting server is
                // found here as soon as it binds instead of being started again and orphaned.
                pid = kept;
                token = keptToken;
            }
            else
            {
                // No server, or one that is gone — forget a dead pid so this fetch can replace it.
                this._pid = null;
                this._token = null;

                var binary = AntigravityBridge.ServerBinary(this._options.ApplicationDirectories);
                if (binary is null)
                {
                    return null;
                }

                token = Guid.NewGuid().ToString();
                var started = await this._options.Process.RunAsync(
                    new ProcessRequest
                    {
                        FileName = "/bin/sh",
                        Arguments = ["-c", DetachedStartCommand(binary, AntigravityBridge.Arguments(token))],
                        Timeout = TimeSpan.FromSeconds(15),
                    },
                    cancellationToken).ConfigureAwait(false);

                if (started.ExitCode != 0
                    || !int.TryParse(started.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                {
                    return null;
                }

                this._pid = pid;
                this._token = token;
            }

            // Binding is near-instant; authenticating is not — measured at about eight seconds,
            // because it is a round trip to Google. Bounded so a server that never binds costs one
            // poll's wait and no more; the next fetch re-polls this same process.
            for (var attempt = 0; attempt < this._options.ServerPollAttempts; attempt++)
            {
                var ports = await AntigravityBridge.ListeningPortsAsync(this._options.Process, pid, cancellationToken).ConfigureAwait(false);
                if (ports.Count > 0)
                {
                    this._endpoint = new AntigravityBridge.Endpoint(ports, token);
                    return this._endpoint;
                }

                await Task.Delay(this._options.ServerPollInterval, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            this._gate.Release();
        }
    }

    /// <summary>
    /// SIGTERM the server this instance started, if any. Safe to call more than once.
    ///
    /// The remembered pid is checked against <c>ps -p &lt;pid&gt; -o command=</c> first: by the time
    /// the reader is disposed the server may already be gone, and macOS may have handed its number
    /// to something else. A pid whose command line no longer carries
    /// <see cref="BridgeMarker"/> is not signalled.
    /// </summary>
    public void Stop()
    {
        var pid = this._pid;
        this._pid = null;
        this._token = null;
        this._endpoint = null;
        if (pid is null)
        {
            return;
        }

        try
        {
            if (!IsOurs(this.ProbeCommandLine(pid.Value)))
            {
                return;
            }

            this._options.Process.RunAsync(new ProcessRequest
            {
                FileName = "/bin/kill",
                Arguments = ["-TERM", pid.Value.ToString(CultureInfo.InvariantCulture)],
                Timeout = TimeSpan.FromSeconds(5),
            }).GetAwaiter().GetResult();
        }
        catch
        {
            // The server is already gone, or not ours to signal any more.
        }
    }

    /// <summary>Whether <c>ps -p</c> still describes the started server.</summary>
    private async Task<bool> IsOursAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            var result = await this._options.Process.RunAsync(
                new ProcessRequest
                {
                    FileName = "/bin/ps",
                    Arguments = ["-p", pid.ToString(CultureInfo.InvariantCulture), "-o", "command="],
                    Timeout = ProbeTimeout,
                },
                cancellationToken).ConfigureAwait(false);

            return result.ExitCode == 0 && IsOurs(result.StandardOutput);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private string? ProbeCommandLine(int pid)
    {
        var result = this._options.Process.RunAsync(new ProcessRequest
        {
            FileName = "/bin/ps",
            Arguments = ["-p", pid.ToString(CultureInfo.InvariantCulture), "-o", "command="],
            Timeout = ProbeTimeout,
        }).GetAwaiter().GetResult();

        return result.ExitCode == 0 ? result.StandardOutput : null;
    }

    private static bool IsOurs(string? commandLine) =>
        commandLine is not null && commandLine.Contains(BridgeMarker, StringComparison.Ordinal);

    private static string DetachedStartCommand(string binary, IReadOnlyList<string> arguments)
    {
        var command = string.Join(" ", new[] { binary }.Concat(arguments).Select(Quote));
        return $"{command} >/dev/null 2>&1 & echo $!";
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
