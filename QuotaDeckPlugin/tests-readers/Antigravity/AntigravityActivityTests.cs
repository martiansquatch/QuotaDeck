namespace QuotaDeck.Readers.Tests.Antigravity;

using QuotaDeck.Readers.Antigravity;

/// <summary>
/// The transcript count, ported from Codenotch <c>Tests/AntigravityTests.swift</c>
/// (<c>AntigravityActivityTests</c>, lines 111-354). Timestamps are built from the local calendar,
/// because which local day an instant falls on is exactly what is under test.
/// </summary>
public class AntigravityActivityTests
{
    private static readonly DateTimeOffset Noon =
        new(new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Local));

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "qd-antigravity-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string trajectory, params string[] lines)
    {
        var directory = Path.Combine(root, trajectory, ".system_generated", "logs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "transcript.jsonl"), string.Join('\n', lines));
    }

    private static string Step(DateTimeOffset at, string source) =>
        $$"""{"created_at":"{{at.ToUniversalTime():o}}","source":"{{source}}","type":"PLANNER_RESPONSE"}""";

    [Fact]
    public void AnEmptyInstallBesideAUsedOneDoesNotHideIt()
    {
        var empty = NewRoot();
        var used = NewRoot();
        Write(used, "a", Step(Noon.AddHours(-3), "MODEL"));

        Assert.Equal(1, AntigravityActivity.Read([empty, used], Noon).RequestsToday);
    }

    [Fact]
    public void TwoInstallsAddUp()
    {
        var ide = NewRoot();
        var cli = NewRoot();
        Write(ide, "a", Step(Noon.AddHours(-3), "MODEL"));
        Write(cli, "b", Step(Noon.AddHours(-2), "MODEL"), Step(Noon.AddHours(-1), "MODEL"));

        Assert.Equal(3, AntigravityActivity.Read([ide, cli], Noon).RequestsToday);
    }

    [Fact]
    public void TheNewestRequestWinsAcrossInstalls()
    {
        var older = NewRoot();
        var newer = NewRoot();
        Write(older, "a", Step(Noon.AddHours(-3), "MODEL"));
        Write(newer, "b", Step(Noon.AddHours(-1), "MODEL"));

        Assert.Equal(Noon.AddHours(-1), AntigravityActivity.Read([older, newer], Noon).LastRequest);
    }

    [Fact]
    public void NoInstallsAtAllIsNotAnError()
    {
        Assert.Equal(0, AntigravityActivity.Read([], Noon).RequestsToday);
    }

    [Fact]
    public void EveryInstallsBrainIsFoundNotJustTheFirst()
    {
        var home = NewRoot();
        foreach (var name in new[] { "antigravity", "antigravity-backup", "antigravity-cli", "antigravity-ide" })
        {
            Directory.CreateDirectory(Path.Combine(home, ".gemini", name, "brain"));
        }

        Directory.CreateDirectory(Path.Combine(home, ".gemini", "history"));

        var roots = AntigravityActivity.TranscriptRoots(home);

        Assert.Equal(4, roots.Count);
        Assert.All(roots, root => Assert.Equal("brain", Path.GetFileName(root)));
        Assert.Contains(roots, root => root.Contains("antigravity-cli", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInstallWithNoBrainIsSkipped()
    {
        var home = NewRoot();
        Directory.CreateDirectory(Path.Combine(home, ".gemini", "antigravity-cli"));

        Assert.Empty(AntigravityActivity.TranscriptRoots(home));
    }

    [Fact]
    public void AQuietDayNamesTheLastTimeItWasUsed()
    {
        var root = NewRoot();
        Write(root, "a", Step(Noon.AddDays(-3), "MODEL"));

        Assert.Equal(
            "Requests today · last used 3 days ago",
            AntigravityActivity.Read([root], Noon).Label(Noon));
    }

    [Fact]
    public void YesterdayIsNamedAsYesterday()
    {
        var root = NewRoot();
        Write(root, "a", Step(Noon.AddDays(-1), "MODEL"));

        Assert.Equal("Requests today · last used yesterday", AntigravityActivity.Read([root], Noon).Label(Noon));
    }

    /// <summary>Counted in calendar days: a late evening is still yesterday, not "3 hr ago".</summary>
    [Fact]
    public void TheEveningBeforeIsStillYesterday()
    {
        var earlyMorning = new DateTimeOffset(new DateTime(2026, 8, 31, 1, 0, 0, DateTimeKind.Local));
        var previousEvening = earlyMorning.AddHours(-3);
        var root = NewRoot();
        Write(root, "a", Step(previousEvening, "MODEL"));

        Assert.Equal(
            "Requests today · last used yesterday",
            AntigravityActivity.Read([root], earlyMorning).Label(earlyMorning));
    }

    [Fact]
    public void ABusyDayKeepsThePlainLabel()
    {
        var root = NewRoot();
        Write(root, "a", Step(Noon.AddHours(-3), "MODEL"));

        Assert.Equal("Requests today · no limit published", AntigravityActivity.Read([root], Noon).Label(Noon));
    }

    [Fact]
    public void NothingEverRecordedKeepsThePlainLabel()
    {
        Assert.Equal("Requests today · no limit published", AntigravityActivity.Read([], Noon).Label(Noon));
    }

    /// <summary>The transcript interleaves user input and system checkpoints; only the model answered.</summary>
    [Fact]
    public void ItCountsOnlyWhatTheModelAnswered()
    {
        var root = NewRoot();
        Write(
            root,
            "a",
            Step(Noon.AddHours(-3), "USER_EXPLICIT"),
            Step(Noon.AddHours(-3), "SYSTEM"),
            Step(Noon.AddHours(-3), "MODEL"),
            Step(Noon.AddHours(-3), "MODEL"));

        Assert.Equal(2, AntigravityActivity.Read(root, Noon).RequestsToday);
    }

    [Fact]
    public void ItAddsUpAcrossConversations()
    {
        var root = NewRoot();
        Write(root, "a", Step(Noon.AddHours(-3), "MODEL"));
        Write(root, "b", Step(Noon.AddHours(-2), "MODEL"));

        Assert.Equal(2, AntigravityActivity.Read(root, Noon).RequestsToday);
    }

    [Fact]
    public void YesterdayIsNotToday()
    {
        var root = NewRoot();
        Write(root, "a", Step(Noon.AddDays(-1), "MODEL"), Step(Noon.AddHours(-3), "MODEL"));

        var activity = AntigravityActivity.Read(root, Noon);

        Assert.Equal(1, activity.RequestsToday);
        Assert.Equal(Noon.AddHours(-3), activity.LastRequest);
    }

    [Fact]
    public void AMissingBrainDirectoryIsNotAnError()
    {
        Assert.Equal(0, AntigravityActivity.Read(Path.Combine(NewRoot(), "nowhere"), Noon).RequestsToday);
    }

    [Fact]
    public void MalformedLinesAreSkippedRatherThanFatal()
    {
        var root = NewRoot();
        Write(root, "a", "not json", "", Step(Noon.AddHours(-3), "MODEL"));

        Assert.Equal(1, AntigravityActivity.Read(root, Noon).RequestsToday);
    }

    [Fact]
    public void FractionalAndWholeSecondTimestampsBothParse()
    {
        Assert.NotNull(AntigravityActivity.ParseTimestamp("2026-08-31T14:12:34Z"));
        Assert.NotNull(AntigravityActivity.ParseTimestamp("2026-08-31T14:12:34.123456Z"));
    }
}
