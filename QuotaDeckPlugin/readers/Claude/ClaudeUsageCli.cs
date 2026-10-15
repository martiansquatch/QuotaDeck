namespace QuotaDeck.Readers.Claude;

using System.Globalization;
using System.Text.RegularExpressions;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;

// Port of Codenotch Sources/Providers/ClaudeUsageCLI.swift (struct ClaudeUsageCLI: locate(home:root:),
// arguments, timeout, scratchDirectory(applicationSupport:), read(profile:now:), plan(in:),
// readWithPlan(profile:now:), run(binary:profile:), parse(_:now:), kind(forWeek:),
// resetDate(from:now:)) and of Sources/Providers/ClaudeCLI.swift (enum ClaudeCLI: candidates,
// desktopOwned, nodeVersionCandidates(home:), nodeToolCandidates(home:), standalone(...),
// isDesktopOwned(_:)).

/// <summary>What one <c>claude /usage</c> print said.</summary>
public sealed record CliReading(IReadOnlyList<LimitWindow> Windows, string? Plan);

/// <summary>
/// The CLI, as the reader asks it. An interface rather than a static helper so a test can script the
/// text without spawning Claude Code: the part worth testing — what the text means — is downstream
/// of how it was obtained.
/// </summary>
public interface IClaudeUsageCli
{
    Task<CliReading> ReadWithPlanAsync(ClaudeProfile profile, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Claude Code's own <c>/usage</c>, asked of the binary rather than of the endpoint behind it.
///
/// The token path works, but it cannot stop asking: Claude Code files a *new* keychain item on every
/// rotation, and the new item's access list does not carry this app. <c>claude "/usage"</c> answers
/// with the same figures, off a credential the CLI already holds, and needs no keychain access from
/// this app at all. It costs a subprocess, so it is throttled by its caller.
/// </summary>
public sealed class ClaudeUsageCli : IClaudeUsageCli
{
    /// <summary>Long enough for a cold Node start, short enough that a wedged process cannot hold a refresh open.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Print mode, no transcript, no MCP servers. Started interactively, `/usage` also files a
    /// session under <c>&lt;config&gt;/projects/</c> and can put up the workspace-trust dialogue;
    /// `--print` skips the dialogue and `--no-session-persistence` skips the transcript.
    /// `--strict-mcp-config` with no `--mcp-config` means no MCP server at all.
    /// </summary>
    public static IReadOnlyList<string> Arguments { get; } =
        ["--print", "--no-session-persistence", "--strict-mcp-config", "/usage"];

    /// <summary>Every path Claude Code installs itself to, newest installer first.</summary>
    private static readonly string[] SearchPaths =
    [
        ".local/bin/claude",     // the native installer
        ".claude/local/claude",  // the migrate-from-npm layout
        ".bun/bin/claude",
        ".volta/bin/claude",     // Volta's shim directory
        "Library/pnpm/claude",   // pnpm's global bin on macOS
        ".npm-global/bin/claude", // npm with a user-level prefix
    ];

    /// <summary>Relative to the filesystem root, so a test can point the whole search at a temporary directory.</summary>
    private static readonly string[] SystemPaths =
    [
        "opt/homebrew/bin/claude",
        "usr/local/bin/claude",
    ];

    private readonly string _binary;
    private readonly string _dataDirectory;
    private readonly IProcessRunner _process;
    private readonly IClaudeFileSystem _fileSystem;

    public ClaudeUsageCli(string binary, string dataDirectory, IProcessRunner process, IClaudeFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrEmpty(binary);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(fileSystem);

        this._binary = binary;
        this._dataDirectory = dataDirectory;
        this._process = process;
        this._fileSystem = fileSystem;
    }

    public string Binary => this._binary;

    /// <summary>
    /// Nil means Claude Code is not installed in any of the places it installs itself, and the
    /// caller should use the token path instead.
    /// </summary>
    public static string? Locate(string homeDirectory, IClaudeFileSystem fileSystem, string root = "/")
    {
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        // Version-manager trees come after the fixed paths and before the system ones, so an
        // installation Claude Code maintains itself still wins over a copy npm happens to have left
        // in an old Node tree.
        var candidates = SearchPaths
            .Select(path => Path.Combine(homeDirectory, path))
            .Concat(NodeVersionCandidates(homeDirectory, fileSystem))
            .Concat(SystemPaths.Select(path => Path.Combine(root, path)));

        return candidates.FirstOrDefault(fileSystem.IsExecutable);
    }

    /// <summary>
    /// Where a Node version manager puts an <c>npm install -g</c> — a directory named for the Node
    /// version, which no fixed path can spell. Newest version first: upgrading Node leaves every
    /// older tree in place, and only the current one is certainly the install being run.
    /// </summary>
    public static IReadOnlyList<string> NodeVersionCandidates(string homeDirectory, IClaudeFileSystem fileSystem)
    {
        var versions = Path.Combine(homeDirectory, ".nvm", "versions", "node");
        var names = fileSystem.ListNames(versions);
        return names
            .OrderByDescending(name => name, Comparer<string>.Create(CompareNumeric))
            .Select(name => Path.Combine(versions, name, "bin", "claude"))
            .ToList();
    }

    /// <summary>
    /// Where <c>/usage</c> is run from: one directory, kept for the life of the install. Claude Code
    /// keys the transcript folder it writes on the working directory, so a fresh temporary directory
    /// per call leaves a new, never-revisited project folder behind on every poll.
    /// </summary>
    public static string ScratchDirectory(string dataDirectory, IClaudeFileSystem fileSystem)
    {
        var directory = Path.Combine(dataDirectory, "usage-scratch");
        fileSystem.CreateDirectory(directory);
        return directory;
    }

    public async Task<CliReading> ReadWithPlanAsync(
        ClaudeProfile profile,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // A directory of its own, so nothing Claude Code writes on the way past lands in whatever
        // directory the service happened to be launched from.
        var scratch = ScratchDirectory(this._dataDirectory, this._fileSystem);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PWD"] = scratch };

        // Only for a named profile. Pointing the variable at `~/.claude` explicitly is not the same
        // as leaving it unset — Claude Code reads `.claude.json` from beside the home directory when
        // it is unset and from inside the config directory when it is set.
        if (profile.Slug is not null)
        {
            environment["CLAUDE_CONFIG_DIR"] = profile.ConfigDirectory;
        }

        var result = await this._process.RunAsync(
            new ProcessRequest
            {
                FileName = this._binary,
                Arguments = Arguments.ToList(),
                WorkingDirectory = scratch,
                Environment = environment,
                CaptureStandardError = false,
                Timeout = Timeout,
            },
            cancellationToken).ConfigureAwait(false);

        // A non-zero exit is Claude Code declining to answer, which in practice means it has no
        // login of its own. A timeout lands here too: the process was terminated, so its exit is not
        // a zero, and "no answer" is the same answer.
        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new ReaderException(ReaderError.NeedsAuth);
        }

        if (string.IsNullOrEmpty(result.StandardOutput))
        {
            throw new ReaderException(ReaderError.BadResponse(0));
        }

        return new CliReading(Parse(result.StandardOutput, now), Plan(result.StandardOutput));
    }

    /// <summary>
    /// The lines <c>/usage</c> leads with, e.g.
    /// <code>
    /// Current session: 38% used · resets Sep 7 at 2:59pm (Europe/Berlin)
    /// Current week (all models): 4% used · resets Sep 14 at 5:59am (Europe/Berlin)
    /// </code>
    /// Everything below them is prose about what drove the usage, and is ignored — it is
    /// approximate by its own admission, and none of it is a limit.
    /// </summary>
    private static readonly Regex Line = new(
        @"^Current (?:(session)|week \(([^)]+)\)):\s*(\d+)%\s*used(?:\s*·\s*resets\s*(.+?))?\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The phrases the plan line can carry, in the order they are looked for.</summary>
    private static readonly string[] PlanPhrases = ["Max 20x", "Max 5x", "extra usage", "Max", "Pro", "Team"];

    /// <summary>The two spellings a reset time arrives in.</summary>
    private static readonly string[] ResetFormats = ["MMM d 'at' h:mmtt", "MMM d 'at' htt"];

    /// <summary>
    /// The year the reset line's month and day are read under. It is a leap year, and it is thrown
    /// away immediately: the real year is chosen from the candidates around `now`.
    /// </summary>
    private const string ParseAnchor = "2000 ";

    /// <summary>The named tier <c>/usage</c> prints above the windows, copied as printed.</summary>
    public static string? Plan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // The first four lines, empty ones dropped — the same split Swift's
        // `split(whereSeparator: \.isNewline)` gives.
        var head = string.Join(
            "\n",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(4));

        foreach (var phrase in PlanPhrases)
        {
            var match = head.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                continue;
            }

            if (phrase == "Max"
                && (head.Contains("Max 5x", StringComparison.OrdinalIgnoreCase)
                    || head.Contains("Max 20x", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return head[match..(match + phrase.Length)];
        }

        return null;
    }

    /// <summary>
    /// Reads what <c>/usage</c> said. Throws <see cref="ReaderException"/> for an answer that names
    /// no session window: without it there is no headline, and the caller falls back to the token
    /// path rather than drawing a ring with a hole in it.
    /// </summary>
    public static List<LimitWindow> Parse(string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);

        var windows = new List<LimitWindow>();
        foreach (Match match in Line.Matches(text))
        {
            if (!double.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
            {
                continue;
            }

            var kind = match.Groups[1].Success
                ? ClaudeUsageResponse.SessionId
                : KindForWeek(match.Groups[2].Value);
            if (windows.Any(window => window.Id == kind))
            {
                continue;
            }

            windows.Add(new LimitWindow
            {
                Id = kind,

                // The same labels the endpoint path produces, so a reading archived under one source
                // still matches when the other takes over.
                Label = ClaudeUsageResponse.LabelForKind(kind),
                UsedFraction = percent / 100,

                // Kept even when the date is unparseable: losing a percentage that parsed perfectly
                // well because the wording of a date changed is the worse failure of the two.
                ResetsAt = match.Groups[4].Success ? ResetDate(match.Groups[4].Value, now) : null,
                Duration = ClaudeUsageResponse.DurationForKind(kind),
            });
        }

        if (!windows.Any(window => window.Id == ClaudeUsageResponse.SessionId))
        {
            throw new ReaderException(ReaderError.BadResponse(0));
        }

        windows.Sort(ClaudeUsageResponse.DisplayOrderCompare);
        return windows;
    }

    /// <summary><c>all models</c> → <c>weekly_all</c>, <c>Opus</c> → <c>weekly_opus</c>.</summary>
    public static string KindForWeek(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var name = text.ToLowerInvariant() == "all models"
            ? "all"
            : text.ToLowerInvariant().Replace(' ', '_');
        return $"weekly_{name}";
    }

    /// <summary>
    /// <c>Sep 7 at 2:59pm (Europe/Berlin)</c> → a moment.
    ///
    /// No year is printed, so one is chosen: the candidate nearest <paramref name="now"/>, over last
    /// year, this year and next.
    /// </summary>
    public static DateTimeOffset? ResetDate(string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);

        var stamp = text.Trim();
        var zone = TimeZoneInfo.Local;

        // The zone comes last, in brackets, and has to come off before the am/pm fix below —
        // `America/...` carries an "am" of its own.
        var open = stamp.LastIndexOf('(');
        if (open >= 0 && stamp.EndsWith(')'))
        {
            var name = stamp[(open + 1)..^1];
            zone = FindZone(name) ?? zone;
            stamp = stamp[..open].Trim();
        }

        stamp = stamp.Replace("am", "AM", StringComparison.Ordinal)
            .Replace("pm", "PM", StringComparison.Ordinal);

        // Two spellings, because the minutes are dropped when they are zero: `Sep 7 at 2:59pm`, but
        // `Sep 7 at 3pm` on the hour. A single `h:mm` pattern reads the first and rejects the second,
        // which is a window that loses its reset time for one hour in sixty.
        //
        // The line carries no year, and .NET would fill the missing component from the *current*
        // date — which makes the parse itself depend on the wall clock: "Feb 29" does not parse at
        // all in a non-leap year, before the candidate loop below can choose a leap one. The year is
        // decided after this parse, so a fixed leap-year anchor reads the components the line does
        // carry, and `DaysInMonth` decides which years they fit.
        DateTime parsed = default;
        var ok = false;
        foreach (var format in ResetFormats)
        {
            if (DateTime.TryParseExact(
                    ParseAnchor + stamp,
                    "yyyy " + format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out parsed))
            {
                ok = true;
                break;
            }
        }

        if (!ok)
        {
            return null;
        }

        var thisYear = TimeZoneInfo.ConvertTime(now, zone).Year;
        DateTimeOffset? nearest = null;
        foreach (var year in (int[])[thisYear - 1, thisYear, thisYear + 1])
        {
            // Feb 29 exists only in a leap year, and Swift's `calendar.date(from:)` answers nil for a
            // candidate that does not — it never throws. `new DateTime(...)` does, and an exception
            // here would escape the parse and cost the whole CLI reading, so the impossible
            // candidates are skipped the way the calendar skips them.
            if (parsed.Day > DateTime.DaysInMonth(year, parsed.Month))
            {
                continue;
            }

            var local = new DateTime(year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, 0, DateTimeKind.Unspecified);
            var candidate = new DateTimeOffset(local, zone.GetUtcOffset(local));
            if (nearest is null || Math.Abs((candidate - now).Ticks) < Math.Abs((nearest.Value - now).Ticks))
            {
                nearest = candidate;
            }
        }

        return nearest;
    }

    private static TimeZoneInfo? FindZone(string name)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// `.numeric`, as Swift means it: `v20.20.2` below `v22.22.3`, which a plain string comparison
    /// gets backwards.
    /// </summary>
    private static int CompareNumeric(string left, string right)
    {
        var i = 0;
        var j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsAsciiDigit(left[i]) && char.IsAsciiDigit(right[j]))
            {
                long leftValue = 0;
                while (i < left.Length && char.IsAsciiDigit(left[i]))
                {
                    leftValue = Math.Min(long.MaxValue / 100, leftValue) * 10 + (left[i] - '0');
                    i++;
                }

                long rightValue = 0;
                while (j < right.Length && char.IsAsciiDigit(right[j]))
                {
                    rightValue = Math.Min(long.MaxValue / 100, rightValue) * 10 + (right[j] - '0');
                    j++;
                }

                if (leftValue != rightValue)
                {
                    return leftValue.CompareTo(rightValue);
                }
            }
            else
            {
                if (left[i] != right[j])
                {
                    return left[i].CompareTo(right[j]);
                }

                i++;
                j++;
            }
        }

        return (left.Length - i).CompareTo(right.Length - j);
    }
}

/// <summary>
/// Where the standalone Claude Code command lives, if it is installed.
///
/// Deliberately *not* the copy inside the Claude desktop app: that copy keeps its OAuth token in
/// the desktop app's own store and never writes the login keychain — the item this app reads —
/// so renewing with it would look like it worked and change nothing here.
///
/// Port of <c>ClaudeCLI.swift</c>, used only by the token refresher.
/// </summary>
public static class ClaudeStandaloneCli
{
    /// <summary>The usual install locations, in the order a shell would find them.</summary>
    private static readonly string[] Candidates =
    [
        "/opt/homebrew/bin/claude",      // Homebrew on Apple silicon
        "/usr/local/bin/claude",         // Homebrew on Intel, and the installer
        "~/.local/bin/claude",           // the standalone installer's default
        "/usr/bin/claude",
    ];

    /// <summary>Anything under here belongs to the desktop app and is not usable for this.</summary>
    private const string DesktopOwned = "/Library/Application Support/Claude/";

    /// <summary>The fixed locations a global npm install lands in, outside the paths the installers use.</summary>
    private static readonly string[] NodeToolPaths =
    [
        ".volta/bin/claude",      // Volta's shim directory
        "Library/pnpm/claude",    // pnpm's global bin on macOS
        ".npm-global/bin/claude", // npm with a user-level prefix
    ];

    public static string? Locate(string homeDirectory, IClaudeFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        // `candidates` first, so a copy Claude Code's own installer maintains is preferred over one
        // npm happens to have left in a Node tree.
        var all = Candidates
            .Select(Expand)
            .Concat(NodeToolPaths.Select(path => Path.Combine(homeDirectory, path)))
            .Concat(ClaudeUsageCli.NodeVersionCandidates(homeDirectory, fileSystem));

        foreach (var path in all)
        {
            if (!fileSystem.IsExecutable(path))
            {
                continue;
            }

            // Resolved because the Homebrew entry is a symlink into the Caskroom, and the desktop
            // app's copy could equally be linked somewhere on PATH.
            var resolved = Resolve(path);
            if (!IsDesktopOwned(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    public static bool IsDesktopOwned(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Contains(DesktopOwned, StringComparison.Ordinal);
    }

    private static string Expand(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(ClaudeProfile.HomeDirectory, path.TrimStart('~', '/'));
        }

        return path;
    }

    private static string Resolve(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return target?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }
}
