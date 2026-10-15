namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;

/// <summary>
/// The tick loop's memory: a first sweep is due, the busy and idle intervals are respected, a
/// rollover cuts ahead of both, and looks are spaced fifteen seconds apart.
/// </summary>
public class RefreshCoordinatorTests
{
    private static ProviderSnapshot Snapshot(string id, DateTimeOffset? resetsAt = null) =>
        TestData.Snapshot(
            id: id,
            windows: [TestData.Window(resetsAt: resetsAt)],
            headlineId: "session");

    [Fact]
    public void AFirstSweepIsDueAndTheSecondIsNot()
    {
        var coordinator = new RefreshCoordinator();
        var now = TestData.Now;

        Assert.Equal(new[] { "claude", "cursor" }, coordinator.Due([Snapshot("claude"), Snapshot("cursor")], now));
        Assert.Empty(coordinator.Due([Snapshot("claude"), Snapshot("cursor")], now));
    }

    [Fact]
    public void IdlePollsAtFiveMinutes()
    {
        var coordinator = new RefreshCoordinator();
        var now = TestData.Now;
        coordinator.Due([Snapshot("claude")], now);

        Assert.Empty(coordinator.Due([Snapshot("claude")], now.AddSeconds(299)));
        Assert.Equal(new[] { "claude" }, coordinator.Due([Snapshot("claude")], now.AddSeconds(300)));
    }

    [Fact]
    public void BusyPollsAtThirtySeconds()
    {
        var coordinator = new RefreshCoordinator();
        var now = TestData.Now;
        coordinator.Due([Snapshot("claude")], now, busy: true);

        Assert.Empty(coordinator.Due([Snapshot("claude")], now.AddSeconds(29), busy: true));
        Assert.Equal(new[] { "claude" }, coordinator.Due([Snapshot("claude")], now.AddSeconds(30), busy: true));
    }

    [Fact]
    public void ARolloverCutsAheadOfTheInterval()
    {
        var coordinator = new RefreshCoordinator();
        var now = TestData.Now;
        coordinator.Due([Snapshot("claude")], now);

        // The window fell between the last attempt and this tick: fetch even though the idle
        // interval has barely started.
        var rolled = Snapshot("claude", resetsAt: now.AddSeconds(10));
        Assert.Equal(new[] { "claude" }, coordinator.Due([rolled], now.AddSeconds(15)));
    }

    [Fact]
    public void LooksAreSpacedFifteenSecondsApartPerProvider()
    {
        var coordinator = new RefreshCoordinator();
        var now = TestData.Now;

        Assert.True(coordinator.Look("claude", now));
        Assert.False(coordinator.Look("claude", now.AddSeconds(14.9)));
        Assert.True(coordinator.Look("claude", now.AddSeconds(15)));

        // The spacing is per provider; a look at one says nothing about another.
        Assert.True(coordinator.Look("cursor", now.AddSeconds(15)));
    }
}
