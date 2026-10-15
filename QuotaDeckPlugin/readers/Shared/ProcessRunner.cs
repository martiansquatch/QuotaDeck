namespace QuotaDeck.Readers.Shared;

using System.Diagnostics;
using System.Runtime.InteropServices;
using QuotaDeck.Core.Readers.Services;

/// <summary>
/// The one process runner: a bounded wait, closed stdin, and a kill that escalates.
///
/// Readers use this for the Claude CLI, the Antigravity probes and the keychain fallback. The kill
/// is SIGTERM first, two seconds of grace, then SIGKILL: a tool that flushes its output on SIGTERM
/// keeps its chance, and one that ignores the signal does not keep the caller waiting past its
/// configured timeout.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int SigTerm = 15;

    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var process = new Process { StartInfo = StartInfo(request) };
        process.Start();

        // stdin is closed immediately: a CLI that would wait for input reads EOF instead of hanging.
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = request.CaptureStandardError ? process.StandardError.ReadToEndAsync() : null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            await Terminate(process).ConfigureAwait(false);
        }

        var output = await stdout.ConfigureAwait(false);
        var error = stderr is null ? null : await stderr.ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = output,
            StandardError = error,
            TimedOut = timedOut,
        };
    }

    private static async Task Terminate(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                process.Kill(entireProcessTree: false);
            }
            else
            {
                SendSignal(process.Id, SigTerm);
            }
        }
        catch
        {
            // Already gone, or not ours to signal; the escalation below is the safety net.
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(KillGrace).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
        }
        catch
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // Nothing more can be done; the process is detached at this point.
        }
    }

    private static ProcessStartInfo StartInfo(ProcessRequest request)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = request.CaptureStandardError,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (request.WorkingDirectory is { } workingDirectory)
        {
            info.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (request.Environment is { } environment)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    info.Environment.Remove(key);
                }
                else
                {
                    info.Environment[key] = value;
                }
            }
        }

        return info;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int pid, int signal);
}
