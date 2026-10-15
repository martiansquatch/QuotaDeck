namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Readers.Claude;

/// <summary>
/// A scripted <c>claude /usage</c>: the answer is a delegate, so a test can count spawns, change
/// the wording between ticks, or fail the way a signed-out CLI fails. The text is parsed by the real
/// parser — the part worth testing is what the text means, not the plumbing that obtained it.
/// </summary>
public sealed class FakeClaudeUsageCli : IClaudeUsageCli
{
    private readonly Func<ClaudeProfile, DateTimeOffset, string> _answer;

    public FakeClaudeUsageCli(Func<string> answer)
        : this((_, _) => answer())
    {
    }

    public FakeClaudeUsageCli(Func<ClaudeProfile, DateTimeOffset, string> answer) => this._answer = answer;

    /// <summary>How many times <c>claude</c> was spawned.</summary>
    public int Spawns { get; private set; }

    /// <summary>The profiles and moments it was asked for, in order.</summary>
    public List<(string ProfileId, DateTimeOffset At)> Calls { get; } = [];

    public Task<CliReading> ReadWithPlanAsync(
        ClaudeProfile profile,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        this.Spawns++;
        this.Calls.Add((profile.Id, now));

        // Throws, when the delegate throws: a signed-out CLI is a ReaderException out of here, which
        // the reader is supposed to swallow.
        var text = this._answer(profile, now);
        return Task.FromResult(new CliReading(ClaudeUsageCli.Parse(text, now), ClaudeUsageCli.Plan(text)));
    }

    /// <summary>A real answer, with the two windows and the prose tail below them.</summary>
    public static string RealAnswer(DateTimeOffset now, int session = 38, int weekly = 4) =>
        $"""
        You are currently using your subscription to power your Claude Code usage

        Current session: {session}% used · resets {Berlin(now.Add(TimeSpan.FromHours(3)))} (Europe/Berlin)
        Current week (all models): {weekly}% used · resets {Berlin(now.Add(TimeSpan.FromDays(6)))} (Europe/Berlin)

        What's contributing to your limits usage?
        Approximate, based on local sessions on this machine — does not include other devices.

        Last 24h · 268 requests · 3 sessions
          37% of your usage was at >150k context
        """;

    /// <summary>The wording <c>/usage</c> prints, in the zone the line names.</summary>
    public static string Berlin(DateTimeOffset at)
    {
        const string format = "MMM d 'at' h:mmtt";
        var text = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"))
            .ToString(format, System.Globalization.CultureInfo.InvariantCulture)
            .Replace("AM", "am", StringComparison.Ordinal)
            .Replace("PM", "pm", StringComparison.Ordinal);
        return text;
    }
}
