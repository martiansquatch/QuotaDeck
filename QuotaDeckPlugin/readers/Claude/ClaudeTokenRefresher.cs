namespace QuotaDeck.Readers.Claude;

using QuotaDeck.Core.Readers.Services;

// Port of Codenotch Sources/Providers/ClaudeTokenRefresher.swift (final class ClaudeTokenRefresher:
// shouldRenew(expiry:now:margin:attemptedFor:lastAttempt:cooldown:), considerRenewing(now:),
// run(_:timeout:), and the margins) and of Sources/Providers/ClaudeCLI.swift's use for it.

/// <summary>How a renewal attempt ended, for tests and for a log line.</summary>
public enum ClaudeRenewalState
{
    /// <summary>Nothing needed doing.</summary>
    Idle,

    /// <summary>The expiry moved forward.</summary>
    Refreshed,

    /// <summary>Ran, or could not be run, and the token is still going to expire.</summary>
    Failed,
}

public sealed record ClaudeRenewalOutcome(ClaudeRenewalState State, DateTimeOffset? Until, string? Message);

/// <summary>
/// Keeps the Claude OAuth token in the login keychain from ageing out.
///
/// This app only ever *reads* that item; only the standalone Claude Code command ever writes it. On
/// a Mac where Claude Code is used through the desktop app, the item is therefore written once and
/// then rots — the desktop app renews its own copy elsewhere — and eight hours later every reading
/// stops, with nothing the user can do from inside this plugin.
///
/// **How it renews, and why that is a compatibility mechanism rather than an interface.** Running
/// <c>claude -p</c> with an empty stdin makes the command go through its whole start-up — which is
/// where it checks the token's age and renews it — and then exit non-zero with "Input must be
/// provided…", because no prompt ever arrives. None of that is promised by anyone, so the *outcome*
/// is what is judged: unless the expiry actually moved, this reports failure and stops trying.
///
/// Codenotch drives this from a one-minute timer. QuotaDeck has no per-provider timer of its own, so
/// the reader calls it at the top of every fetch — the plugin's tick is the timer — and the gate is
/// what keeps that from meaning anything: one attempt per expiry value, ever, plus a cooldown.
/// </summary>
public sealed class ClaudeTokenRefresher
{
    /// <summary>
    /// How close to expiry is close enough.
    ///
    /// **Must stay under Claude Code's own five minutes.** Its start-up renews the token only when
    /// <c>now + 300s &gt;= expiresAt</c>; launching it any earlier is a no-op, which this would then
    /// correctly report as a failure and stop retrying.
    /// </summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(4);

    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The arguments <c>claude</c> is run with, and nothing else: no prompt is ever supplied.</summary>
    public static IReadOnlyList<string> Arguments { get; } = ["-p", "--no-session-persistence", "--strict-mcp-config"];

    private readonly Func<DateTimeOffset?> _expiry;
    private readonly Func<CancellationToken, Task<DateTimeOffset?>> _reload;
    private readonly Func<string?> _cli;
    private readonly IProcessRunner _process;
    private readonly string _scratchDirectory;
    private readonly TimeSpan _margin;
    private readonly TimeSpan _cooldown;
    private readonly TimeSpan _timeout;

    private bool _isRunning;
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset? _attemptedFor;

    public ClaudeTokenRefresher(
        Func<DateTimeOffset?> expiry,
        Func<CancellationToken, Task<DateTimeOffset?>> reload,
        Func<string?> cli,
        IProcessRunner process,
        string scratchDirectory,
        TimeSpan? margin = null,
        TimeSpan? cooldown = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(expiry);
        ArgumentNullException.ThrowIfNull(reload);
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(process);

        this._expiry = expiry;
        this._reload = reload;
        this._cli = cli;
        this._process = process;
        this._scratchDirectory = scratchDirectory;
        this._margin = margin ?? Margin;
        this._cooldown = cooldown ?? Cooldown;
        this._timeout = timeout ?? Timeout;
    }

    public ClaudeRenewalOutcome Outcome { get; private set; } = new(ClaudeRenewalState.Idle, null, null);

    /// <summary>Exposed for the test that proves the guard is claimed before the first await.</summary>
    public bool IsRunning => this._isRunning;

    /// <summary>
    /// Whether a launch is worth making. Pure, so every branch is testable without a clock, a
    /// keychain or a subprocess.
    /// </summary>
    public static bool ShouldRenew(
        DateTimeOffset? expiry,
        DateTimeOffset now,
        TimeSpan margin,
        DateTimeOffset? attemptedFor,
        DateTimeOffset? lastAttempt,
        TimeSpan cooldown)
    {
        // Nothing read yet: never launch on a guess.
        if (expiry is not { } known)
        {
            return false;
        }

        // Plenty of time left. Also the case where launching would do nothing, because the
        // command's own gate has not opened either.
        if (known - now >= margin)
        {
            return false;
        }

        // Already spent an attempt on this exact token. This is the whole no-retry-loop guarantee:
        // a launch that failed to move the expiry leaves the same value here next tick, and never
        // runs again.
        if (known == attemptedFor)
        {
            return false;
        }

        if (lastAttempt is { } last && now - last < cooldown)
        {
            return false;
        }

        return true;
    }

    /// <summary>One tick of the refresher: decide, launch, judge the outcome.</summary>
    public async Task ConsiderRenewingAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // Claimed synchronously, before anything is awaited: it is what makes "at most one launch"
        // true unconditionally.
        if (this._isRunning)
        {
            return;
        }

        this._isRunning = true;
        try
        {
            var current = this._expiry();
            if (current is not { } expiry
                || !ShouldRenew(expiry, now, this._margin, this._attemptedFor, this._lastAttempt, this._cooldown))
            {
                return;
            }

            this._lastAttempt = now;
            this._attemptedFor = expiry;

            if (this._cli() is not { } cli)
            {
                this.Fail(
                    "Claude's saved login is about to expire and the `claude` command isn't installed "
                    + "to renew it. Run Claude Code once to sign in again.");
                return;
            }

            try
            {
                var result = await this._process.RunAsync(
                    new ProcessRequest
                    {
                        FileName = cli,
                        Arguments = Arguments.ToList(),

                        // The same fixed directory `/usage` runs from, never the plugin's own.
                        WorkingDirectory = this._scratchDirectory,
                        Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["PWD"] = this._scratchDirectory,
                        },
                        CaptureStandardError = false,
                        Timeout = this._timeout,
                    },
                    cancellationToken).ConfigureAwait(false);

                // The exit status is not the answer: refusing an empty prompt is a *non-zero* exit
                // and a successful renewal at the same time. The outcome below is the answer.
                _ = result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                this.Fail("Couldn't run `claude` to renew Claude's saved login.");
                return;
            }

            var after = await this._reload(cancellationToken).ConfigureAwait(false);
            if (after is not { } renewed || renewed <= expiry)
            {
                this.Fail("Claude usage needs its sign-in renewed — run `claude` once in a terminal.");
                return;
            }

            this.Outcome = new ClaudeRenewalOutcome(ClaudeRenewalState.Refreshed, renewed, null);
        }
        finally
        {
            this._isRunning = false;
        }
    }

    private void Fail(string message) =>
        this.Outcome = new ClaudeRenewalOutcome(ClaudeRenewalState.Failed, null, message);
}
