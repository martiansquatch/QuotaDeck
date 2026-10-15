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
/// The start itself goes through <see cref="IProcessRunner"/>: the server is launched as a detached
/// background command (<c>/bin/sh -c "… &amp; echo $!"</c>) so the runner's own wait-to-exit
/// contract is kept, its pid comes back on stdout, and stdout/stderr of the server itself are
/// discarded. Tests script exactly that with the fake runner.
/// </summary>
public sealed class AntigravityServer
{
    private readonly AntigravityOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private int? _pid;
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
    /// when there is nothing to start or it never bound.
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

            var binary = AntigravityBridge.ServerBinary(this.Applications());
            if (binary is null)
            {
                return null;
            }

            var token = Guid.NewGuid().ToString();
            var started = await this._options.Process.RunAsync(
                new ProcessRequest
                {
                    FileName = "/bin/sh",
                    Arguments = ["-c", DetachedStartCommand(binary, AntigravityBridge.Arguments(token))],
                    Timeout = TimeSpan.FromSeconds(15),
                },
                cancellationToken).ConfigureAwait(false);

            if (started.ExitCode != 0
                || !int.TryParse(started.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                return null;
            }

            this._pid = pid;

            // Binding is near-instant; authenticating is not — measured at about eight seconds,
            // because it is a round trip to Google. Bounded so a server that never binds costs one
            // poll's wait and no more, and the next poll finds it already running.
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

    /// <summary>SIGTERM the server this instance started, if any. Safe to call more than once.</summary>
    public void Stop()
    {
        var pid = this._pid;
        this._pid = null;
        this._endpoint = null;
        if (pid is null)
        {
            return;
        }

        try
        {
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

    private IReadOnlyList<string> Applications() =>
        ["/Applications", Path.Combine(this._options.HomeDirectory, "Applications")];

    private static string DetachedStartCommand(string binary, IReadOnlyList<string> arguments)
    {
        var command = string.Join(" ", new[] { binary }.Concat(arguments).Select(Quote));
        return $"{command} >/dev/null 2>&1 & echo $!";
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
