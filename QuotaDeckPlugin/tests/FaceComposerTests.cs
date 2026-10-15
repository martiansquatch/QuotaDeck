namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Faces;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Settings;

/// <summary>
/// FaceComposer gathers the face-building rules from Codenotch's drawing sites and
/// <c>UsageModel.swift</c>'s <c>statusMessage</c>: the remaining/used flip, the countdown corner,
/// the quieting of stale or refused readings, the block flag, and the words shown when there is no
/// reading at all.
/// </summary>
public class FaceComposerTests
{
    private static readonly QuotaDeckSettings Settings = new();

    [Fact]
    public void ShowingRemainingFlipsTheHeadlineAndTheWeeklyFigure()
    {
        var snapshot = TestData.SessionAndWeekly(0.73, 0.41);

        var remaining = FaceComposer.Compose(snapshot, new QuotaDeckSettings { ShowRemaining = true }, TestData.Now);
        Assert.Equal("27%", remaining.Headline);
        Assert.Equal("wk 59%", remaining.Secondary);
        Assert.Equal(UsageBand.Critical, remaining.Band);
        Assert.Null(remaining.StatusText);

        var used = FaceComposer.Compose(snapshot, new QuotaDeckSettings { ShowRemaining = false }, TestData.Now);
        Assert.Equal("73%", used.Headline);
        Assert.Equal("wk 41%", used.Secondary);
        Assert.Equal(UsageBand.Critical, used.Band);
    }

    [Fact]
    public void TheCornerIsTheCountdownWithTheRefreshGlyph()
    {
        var snapshot = TestData.SessionAndWeekly(0.5, 0.4, sessionResetsAt: TestData.Now.AddMinutes(125));

        var face = FaceComposer.Compose(snapshot, Settings, TestData.Now);

        Assert.Equal("↻ 2h 05m", face.Corner);

        var passed = FaceComposer.Compose(
            TestData.SessionAndWeekly(0.5, 0.4, sessionResetsAt: TestData.Now.AddSeconds(-1)),
            Settings,
            TestData.Now);
        Assert.Null(passed.Corner);
    }

    /// <summary>
    /// The corner follows the countdown style: the default is Codenotch's hours form, and the days
    /// form shortens a multi-day window; below a day the two read the same.
    /// </summary>
    [Fact]
    public void TheCornerCountdownFollowsTheSettingsStyle()
    {
        var snapshot = TestData.SessionAndWeekly(0.5, 0.4, sessionResetsAt: TestData.Now.AddHours(71));

        var hours = FaceComposer.Compose(
            snapshot,
            new QuotaDeckSettings { CountdownStyle = CountdownStyle.Hours },
            TestData.Now);
        Assert.Equal("↻ 71h 00m", hours.Corner);

        var days = FaceComposer.Compose(
            snapshot,
            new QuotaDeckSettings { CountdownStyle = CountdownStyle.Days },
            TestData.Now);
        Assert.Equal("↻ 2d 23h", days.Corner);

        var soon = TestData.SessionAndWeekly(0.5, 0.4, sessionResetsAt: TestData.Now.AddMinutes(125));
        Assert.Equal(
            "↻ 2h 05m",
            FaceComposer.Compose(soon, new QuotaDeckSettings { CountdownStyle = CountdownStyle.Days }, TestData.Now).Corner);
        Assert.Equal(
            "↻ 2h 05m",
            FaceComposer.Compose(soon, new QuotaDeckSettings { CountdownStyle = CountdownStyle.Hours }, TestData.Now).Corner);
    }

    [Fact]
    public void StaleAndRefusedReadingsAreDimmed()
    {
        var reading = TestData.SessionAndWeekly(0.5, 0.4);

        Assert.False(FaceComposer.Compose(reading, Settings, TestData.Now).Dimmed);
        Assert.True(FaceComposer.Compose(reading with { Status = ProviderStatus.Stale(TestData.Now) }, Settings, TestData.Now).Dimmed);
        Assert.True(FaceComposer.Compose(reading with { Status = ProviderStatus.AccessDenied }, Settings, TestData.Now).Dimmed);
        Assert.False(FaceComposer.Compose(reading with { Status = ProviderStatus.NeedsAuth }, Settings, TestData.Now).Dimmed);
    }

    [Fact]
    public void BlocksAndSpentWindowsMarkTheFaceBlocked()
    {
        Assert.False(FaceComposer.Compose(TestData.SessionAndWeekly(0.3, 0.4), Settings, TestData.Now).Blocked);
        Assert.True(FaceComposer.Compose(TestData.SessionAndWeekly(1.0, 0.4), Settings, TestData.Now).Blocked);
        Assert.True(FaceComposer.Compose(TestData.SessionAndWeekly(0.3, 1.0), Settings, TestData.Now).Blocked);
        Assert.True(FaceComposer.Compose(
            TestData.SessionAndWeekly(0.3, 0.4) with { Block = new UsageBlock { Reason = "Paused" } },
            Settings,
            TestData.Now).Blocked);
    }

    [Theory]
    [InlineData("NeedsAuth", "sign in")]
    [InlineData("SignedOutByOwner", "signed out")]
    [InlineData("AccessDenied", "access denied")]
    public void TheNoReadingWordsForTheAuthStates(string status, string expected)
    {
        var snapshot = TestData.Snapshot(windows: [], status: Status(status));

        var face = FaceComposer.Compose(snapshot, Settings, TestData.Now);

        Assert.Equal(expected, face.StatusText);
        Assert.Equal("—", face.Headline);
    }

    [Fact]
    public void UnsupportedAndErrorKeepTheProvidersOwnWords()
    {
        Assert.Equal("free plan", FaceComposer.Compose(
            TestData.Snapshot(windows: [], status: ProviderStatus.Unsupported("free plan")), Settings, TestData.Now).StatusText);

        Assert.Equal("HTTP 503", FaceComposer.Compose(
            TestData.Snapshot(windows: [], status: ProviderStatus.Error("HTTP 503")), Settings, TestData.Now).StatusText);
    }

    [Fact]
    public void StaleWithNoWindowsSaysNoDataAndOkSaysWaiting()
    {
        Assert.Equal("no data", FaceComposer.Compose(
            TestData.Snapshot(windows: [], status: ProviderStatus.Stale(TestData.Now)), Settings, TestData.Now).StatusText);

        Assert.Equal("waiting…", FaceComposer.Compose(
            TestData.Snapshot(windows: [], status: ProviderStatus.Ok), Settings, TestData.Now).StatusText);
    }

    [Fact]
    public void AReadingOutranksTheStatusWords()
    {
        var snapshot = TestData.SessionAndWeekly(0.5, 0.4) with { Status = ProviderStatus.NeedsAuth };

        var face = FaceComposer.Compose(snapshot, Settings, TestData.Now);

        Assert.Null(face.StatusText);
        Assert.Equal("50%", face.Headline);
    }

    [Fact]
    public void NoSnapshotAtAllSaysSoWithoutABandOrCountdown()
    {
        var face = FaceComposer.Compose(null, Settings, TestData.Now);

        Assert.Null(face.Headline);
        Assert.Null(face.Secondary);
        Assert.Null(face.Corner);
        Assert.Null(face.Band);
        Assert.False(face.Dimmed);
        Assert.False(face.Blocked);
        Assert.Equal("…", face.StatusText);
    }

    private static ProviderStatus Status(string name) => name switch
    {
        "NeedsAuth" => ProviderStatus.NeedsAuth,
        "SignedOutByOwner" => ProviderStatus.SignedOutByOwner,
        "AccessDenied" => ProviderStatus.AccessDenied,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
