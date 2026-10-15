namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;

/// <summary>
/// Mirrors Codenotch <c>Tests/SnapshotTests.swift</c> (<c>SnapshotTests</c>,
/// <c>RemainingHeadlineTests</c>, <c>MeteredFractionTests</c>, <c>WeeklyExhaustionBlockTests</c>) plus
/// <c>Tests/HideDepletedAccountsTests.swift</c>.
/// </summary>
public class SnapshotTests
{
    [Fact]
    public void HeadlineIsThePrimaryWindow()
    {
        var snapshot = TestData.Snapshot(
            windows:
            [
                TestData.Window(id: "session", usedFraction: 0.73),
                TestData.Window(id: "all", usedFraction: 0.07),
            ],
            headlineId: "session");

        Assert.Equal("session", snapshot.Headline?.Id);
        Assert.Equal(0.73, snapshot.UsedFraction!.Value, 4);
    }

    /// <summary>
    /// The headline stays on the session even when another window is higher. Picking the highest made
    /// the face mean "session" one minute and "weekly" the next, which reads as the number being
    /// wrong — and disagrees with the vendor's own panel, which always leads with the session.
    /// </summary>
    [Fact]
    public void HeadlineDoesNotJumpToAHigherWindow()
    {
        var snapshot = TestData.Snapshot(
            windows:
            [
                TestData.Window(id: "session", usedFraction: 0.22),
                TestData.Window(id: "weekly_all", usedFraction: 0.24),
            ],
            headlineId: "session");

        Assert.Equal("session", snapshot.Headline?.Id);
        Assert.Equal(0.22, snapshot.UsedFraction!.Value, 4);
    }

    /// <summary>With no declared headline the first window is it.</summary>
    [Fact]
    public void NoDeclaredHeadlineMeansTheFirstWindow()
    {
        var snapshot = TestData.Snapshot(
            windows:
            [
                TestData.Window(id: "one", usedFraction: 0.1),
                TestData.Window(id: "two", usedFraction: 0.9),
            ],
            headlineId: null);

        Assert.Equal("one", snapshot.Headline?.Id);
    }

    /// <summary>
    /// If the declared window is missing from the response the face shows no reading rather than
    /// promoting a different one. A blank is honest; a weekly percentage wearing the session's place
    /// is not.
    /// </summary>
    [Fact]
    public void AMissingDeclaredHeadlineIsNotReplaced()
    {
        var snapshot = TestData.Snapshot(
            windows: [TestData.Window(id: "weekly_all", usedFraction: 0.9)],
            headlineId: "session");

        Assert.Null(snapshot.Headline);
        Assert.Null(snapshot.UsedFraction);
        Assert.True(snapshot.HasReading);
    }

    /// <summary>No windows is not "nothing used" — it is "nothing known".</summary>
    [Fact]
    public void NoWindowsReadsAsNoReading()
    {
        var snapshot = TestData.Snapshot(windows: [], headlineId: null);
        Assert.Null(snapshot.UsedFraction);
        Assert.Equal("—", snapshot.HeadlineText());
        Assert.False(snapshot.HasReading);
    }

    /// <summary>A provider that reports only what is left gets a count, not a percentage.</summary>
    [Fact]
    public void CountOnlyWindowsPrintTheCount()
    {
        var snapshot = TestData.Snapshot(
            windows: [TestData.Window(id: "pro", label: "Pro searches", remaining: 2)],
            headlineId: "pro");

        Assert.Null(snapshot.UsedFraction);
        Assert.Equal("2", snapshot.HeadlineText());
    }

    [Theory]
    [InlineData(9_999, "9999")]
    [InlineData(651_061, "651k")]
    [InlineData(1_128_771, "1.1M")]
    [InlineData(2_000_000, "2.0M")]
    public void LargeCountsAreCompacted(int count, string expected)
    {
        Assert.Equal(expected, LimitWindow.Compact(count));
    }

    [Fact]
    public void ACountOnlyHeadlineIsCompacted()
    {
        var snapshot = TestData.Snapshot(
            windows: [TestData.Window(id: "month", used: 651_061)],
            headlineId: "month");
        Assert.Equal("651k", snapshot.HeadlineText());
    }

    [Fact]
    public void OnlyOfficialNumbersAreShownUnqualified()
    {
        Assert.Equal("", Fidelity.Official.Qualifier());
        Assert.Equal("~", Fidelity.Derived.Qualifier());
        Assert.Equal("~", Fidelity.Manual.Qualifier());
    }

    /// <summary>
    /// The face can show what is left instead of what is spent. The figure is the card's own left
    /// half, so the two never disagree by a point.
    /// </summary>
    [Theory]
    [InlineData(0.0, "0%", "100%")]
    [InlineData(0.095, "10%", "90%")]
    [InlineData(0.125, "13%", "87%")]
    [InlineData(0.003, "0.3%", "99.7%")]
    [InlineData(1.0, "100%", "0%")]
    [InlineData(1.28, "128%", "0%")]
    [InlineData(0.998, "100%", "0.2%")]
    public void RemainingReadsFromTheOtherEnd(double used, string spentText, string remainingText)
    {
        var snapshot = TestData.Snapshot(windows: [TestData.Window(id: "w", usedFraction: used)], headlineId: "w");
        Assert.Equal(spentText, snapshot.HeadlineText());
        Assert.Equal(remainingText, snapshot.HeadlineText(showingRemaining: true));
    }

    [Fact]
    public void RemainingAgreesWithTheCardsLeftHalf()
    {
        foreach (var used in new[] { 0.0, 0.095, 0.003, 1.0, 1.28 })
        {
            var snapshot = TestData.Snapshot(windows: [TestData.Window(id: "w", usedFraction: used)], headlineId: "w");
            Assert.Equal(QuotaDeck.Core.Text.Percent.Halves(used).Left + "%", snapshot.HeadlineText(showingRemaining: true));
        }
    }

    /// <summary>A count of what is left already reads from the asked-for end.</summary>
    [Fact]
    public void RemainingCountsAreUnchanged()
    {
        var snapshot = TestData.Snapshot(
            windows: [TestData.Window(id: "r", remaining: 2), TestData.Window(id: "u", used: 651_061)],
            headlineId: "r");
        Assert.Equal("2", snapshot.HeadlineText(showingRemaining: true));

        var counted = snapshot with { HeadlineId = "u" };
        Assert.Equal("651k", counted.HeadlineText(showingRemaining: true));
    }

    [Fact]
    public void AnExplicitUsedTextIsUnchanged()
    {
        var snapshot = TestData.Snapshot(
            windows:
            [
                TestData.Window(id: "u", usedFraction: 0.25, usedText: "$4.20 spent", prefersUsedText: true),
            ],
            headlineId: "u");

        Assert.Equal("$4.20 spent", snapshot.HeadlineText(showingRemaining: true));
    }

    /// <summary>
    /// The provider-declared weekly allowance, kept separately from what a second ring draws: a
    /// compact secondary figure still needs to know a weekly reading exists alongside its own
    /// five-hour one.
    /// </summary>
    [Fact]
    public void WeeklyLimitWindowIsIndependentOfTheHeadline()
    {
        var snapshot = TestData.SessionAndWeekly(0.5, 0.3);
        Assert.Equal("weekly", snapshot.WeeklyLimitWindow?.Id);
        Assert.Equal("weekly", snapshot.WeeklyWindow?.Id);
        Assert.Equal(0.3, snapshot.WeeklyFraction!.Value, 4);

        var swapped = snapshot with { HeadlineId = "weekly", WeeklyId = "session" };
        Assert.Equal("session", swapped.WeeklyLimitWindow?.Id);
        Assert.Equal("session", swapped.WeeklyWindow?.Id);
    }

    /// <summary>Two rings reporting the same number is worse than one.</summary>
    [Fact]
    public void TheWeeklyWindowIsNilWhenTheHeadlineIsDrawingIt()
    {
        var snapshot = TestData.Snapshot(
            windows: [TestData.Window(id: "weekly", usedFraction: 0.9)],
            headlineId: "weekly",
            weeklyId: "weekly");

        Assert.NotNull(snapshot.WeeklyLimitWindow);
        Assert.Null(snapshot.WeeklyWindow);
        Assert.Null(snapshot.WeeklyFraction);
    }

    /// <summary>The five-hour window is found by length, not by id, and never from a group.</summary>
    [Fact]
    public void FiveHourWindowIsFoundByItsLength()
    {
        var snapshot = TestData.Snapshot(
            windows:
            [
                TestData.Window(id: "primary", label: "5h limit", usedFraction: 0.5, duration: TimeSpan.FromHours(5)),
                TestData.Window(id: "spark", label: "5h limit", usedFraction: 0.2, group: "Spark", duration: TimeSpan.FromHours(5)),
            ],
            headlineId: "primary");

        Assert.True(snapshot.Headline!.IsFiveHour);
        Assert.Equal("primary", snapshot.FiveHourWindow?.Id);
    }

    [Fact]
    public void AlmostFiveHoursCounts()
    {
        Assert.True(TestData.Window(duration: TimeSpan.FromSeconds((5 * 3600) - 59)).IsFiveHour);
        Assert.False(TestData.Window(duration: TimeSpan.FromSeconds((5 * 3600) + 60)).IsFiveHour);
        Assert.False(TestData.Window().IsFiveHour);
    }
}

/// <summary>
/// A spent weekly allowance shuts the headline with it, for every provider: the headline's room is
/// unusable, so the ring must not read green.
/// </summary>
public class WeeklyExhaustionBlockTests
{
    private static readonly DateTimeOffset Reset = DateTimeOffset.FromUnixTimeSeconds(1_790_553_600);

    private static ProviderSnapshot Snapshot(double? headlineFraction, double? weeklyFraction, string? weeklyId = "weekly")
    {
        var windows = new List<LimitWindow> { TestData.Window(id: "session", usedFraction: headlineFraction) };
        if (weeklyId is not null)
        {
            windows.Add(TestData.Window(id: "weekly", label: "Weekly", usedFraction: weeklyFraction, resetsAt: Reset));
        }

        return TestData.Snapshot(windows: windows, headlineId: "session", weeklyId: weeklyId);
    }

    [Fact]
    public void SpentWeekBlocksARoomyHeadline()
    {
        var blocked = Snapshot(0.2, 1).BlockingSpentWeek();
        Assert.NotNull(blocked.Block);
        Assert.Equal("Weekly limit reached", blocked.Block!.Reason);
        Assert.Equal(Reset, blocked.Block.ResetsAt);
        Assert.True(blocked.Block.IsWeeklyExhaustion);

        // The headline keeps its own reading — only the ring's band changes.
        Assert.Equal(0.2, blocked.UsedFraction!.Value, 4);
        Assert.Equal("20%", blocked.HeadlineText());
    }

    [Fact]
    public void OverQuotaWeekBlocks() => Assert.NotNull(Snapshot(0.2, 1.28).BlockingSpentWeek().Block);

    [Fact]
    public void NearlySpentWeekDoesNotBlock() => Assert.Null(Snapshot(0.2, 0.999).BlockingSpentWeek().Block);

    [Fact]
    public void WeekWithoutADenominatorDoesNotBlock() => Assert.Null(Snapshot(0.2, null).BlockingSpentWeek().Block);

    [Fact]
    public void MissingWeekDoesNotBlock() => Assert.Null(Snapshot(0.2, null, weeklyId: null).BlockingSpentWeek().Block);

    [Fact]
    public void AProviderBlockIsNeverReplaced()
    {
        var providerBlock = new UsageBlock { Reason = "Paused" };
        var snapshot = Snapshot(0.2, 1) with { Block = providerBlock };
        Assert.Equal(providerBlock, snapshot.BlockingSpentWeek().Block);
    }

    [Fact]
    public void SpentWeekBlocksEvenAsTheHeadline()
    {
        var snapshot = Snapshot(0.2, 1) with { HeadlineId = "weekly" };
        Assert.Equal("Weekly limit reached", snapshot.BlockingSpentWeek().Block!.Reason);
    }
}

/// <summary>
/// Hiding accounts with nothing left to spend: either the headline window or the weekly allowance at
/// 0% remaining. Stale readings never count — an old number is not proof the account is empty now.
/// </summary>
public class DepletedAccountTests
{
    [Fact]
    public void HealthyAccountIsNotDepleted() => Assert.False(TestData.SessionAndWeekly(0.5, 0.3).IsDepleted);

    [Fact]
    public void SpentHeadlineDepletesWithAHealthyWeek()
    {
        // A spent 5-hour beside weekly room — the week's room cannot be spent while the short window
        // is shut.
        Assert.True(TestData.SessionAndWeekly(1.0, 0.3).IsDepleted);
    }

    [Fact]
    public void SpentWeekDepletesWithHeadlineRoom() => Assert.True(TestData.SessionAndWeekly(0.5, 1.0).IsDepleted);

    [Fact]
    public void SpentWeeklyHeadlineDepletes()
    {
        var snapshot = TestData.Snapshot(
            windows: [TestData.Window(id: "weekly_all", usedFraction: 1.0)],
            headlineId: "weekly_all",
            weeklyId: "weekly_all");
        Assert.True(snapshot.IsDepleted);
    }

    [Fact]
    public void StaleSpentWindowsDoNotDeplete()
    {
        var stale = TestData.SessionAndWeekly(1.0, 0.3) with { Status = ProviderStatus.Stale(TestData.Now) };
        Assert.False(stale.IsDepleted);
    }

    [Fact]
    public void NoReadingDoesNotDeplete() => Assert.False(TestData.Snapshot(windows: [], headlineId: null).IsDepleted);

    [Fact]
    public void AProviderPauseWithRoomDoesNotDeplete()
    {
        var paused = TestData.SessionAndWeekly(0.5, 0.3) with { Block = new UsageBlock { Reason = "Rate limited" } };
        Assert.False(paused.IsDepleted);
    }
}
