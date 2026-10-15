namespace Loupedeck.QuotaDeckPlugin
{
    using System;
    using System.Threading.Tasks;
    using QuotaDeck.Core.Readers.Services;

    /// <summary>
    /// The macOS notification sink: <c>osascript</c> run through the shared process runner, because
    /// the Plugin Service has no notification surface of its own.
    ///
    /// Never throws: a notification that cannot be delivered must not take down the alert path that
    /// produced it. On any platform other than macOS it is a no-op. Every delivery attempt is logged
    /// so the dev harness can see what would have been shown.
    /// </summary>
    public sealed class MacNotifier
    {
        private readonly IProcessRunner _process;

        public MacNotifier(IProcessRunner process) => this._process = process;

        public async Task NotifyAsync(String title, String body)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            try
            {
                var script = $"display notification \"{Escape(body)}\" with title \"{Escape(title)}\"";
                var result = await this._process.RunAsync(new ProcessRequest
                {
                    FileName = "/usr/bin/osascript",
                    Arguments = ["-e", script],
                    Timeout = TimeSpan.FromSeconds(10),
                }).ConfigureAwait(false);

                PluginLog.Info($"notification ({result.ExitCode}): {title} — {body}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "notification failed");
            }
        }

        /// <summary>
        /// AppleScript string escaping: backslashes first, then quotes. Either order reversed would
        /// escape the escape it just added.
        /// </summary>
        internal static String Escape(String value) =>
            value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
