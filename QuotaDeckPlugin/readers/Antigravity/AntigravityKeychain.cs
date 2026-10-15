namespace QuotaDeck.Readers.Antigravity;

using QuotaDeck.Core.Readers.Services;

/// <summary>
/// The one keychain read this reader needs: Antigravity's generic-password item, read through
/// <c>/usr/bin/security</c> rather than the Security framework so it can be scripted in tests with
/// the process runner.
///
/// TODO: replace with <c>readers/Shared/MacKeychain.cs</c> once the Claude reader PR that adds it
/// lands; this is the minimal local equivalent so this PR does not touch shared files.
/// </summary>
internal static class AntigravityKeychain
{
    public const string Service = "gemini";

    public const string Account = "antigravity";

    /// <summary>
    /// The stored secret, or null when the item is absent or the read is refused. Never interactive:
    /// the command runs with no terminal and a failure is simply "no credential from this source".
    /// </summary>
    public static async Task<string?> ReadAsync(IProcessRunner process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        ProcessResult result;
        try
        {
            result = await process.RunAsync(
                new ProcessRequest
                {
                    FileName = "/usr/bin/security",
                    Arguments = ["find-generic-password", "-s", Service, "-a", Account, "-w"],
                    Timeout = TimeSpan.FromSeconds(10),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        return result.ExitCode == 0 ? result.StandardOutput.TrimEnd('\r', '\n') : null;
    }
}
