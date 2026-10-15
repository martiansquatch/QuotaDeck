namespace QuotaDeck.Core.Tests;

using System.Text.Json;
using QuotaDeck.Core.Settings;

/// <summary>
/// The threshold and order rules of Codenotch <c>Sources/Settings/Preferences.swift</c> (the
/// watch/critical <c>didSet</c> clamps and the load-time repair) and
/// <c>Tests/ProviderOrderTests.swift</c> (a stored order is reconciled with the providers that
/// actually exist, never obeyed blindly).
/// </summary>
public class SettingsTests
{
    [Fact]
    public void TheDefaultsAreTheShippedOnes()
    {
        var settings = new QuotaDeckSettings();

        Assert.True(settings.ShowRemaining);
        Assert.Equal(0.50, settings.WatchLimit);
        Assert.Equal(0.70, settings.CriticalLimit);
    }

    [Fact]
    public void ClampingKeepsTheWatchLimitBelowTheCriticalOne()
    {
        Assert.Equal(0.60, QuotaDeckSettings.ClampWatchLimit(0.60, 0.70));

        // The two edges: a hair above zero and a hair below the critical limit.
        Assert.Equal(0.01, QuotaDeckSettings.ClampWatchLimit(0.001, 0.70));
        Assert.Equal(0.69, QuotaDeckSettings.ClampWatchLimit(0.80, 0.70));
    }

    [Fact]
    public void ClampingKeepsTheCriticalLimitAboveTheWatchOne()
    {
        Assert.Equal(0.70, QuotaDeckSettings.ClampCriticalLimit(0.70, 0.50));

        Assert.Equal(0.71, QuotaDeckSettings.ClampCriticalLimit(0.20, 0.70));
        Assert.Equal(1.0, QuotaDeckSettings.ClampCriticalLimit(2.0, 0.50));
    }

    /// <summary>A stored pair that crossed over is repaired the moment it is read: the critical
    /// limit is kept and the watch limit drops under it.</summary>
    [Fact]
    public void NormalizeRepairsACrossedPairOnLoad()
    {
        var settings = Load("""{ "watchLimit": 0.90, "criticalLimit": 0.40 }""");

        settings.Normalize();

        Assert.Equal(0.40, settings.CriticalLimit);
        Assert.Equal(0.39, settings.WatchLimit);
    }

    [Fact]
    public void NormalizeRepairsNonFiniteAndOutOfRangeLimits()
    {
        var settings = new QuotaDeckSettings { WatchLimit = double.NaN, CriticalLimit = double.PositiveInfinity };
        settings.Normalize();
        Assert.Equal(0.50, settings.WatchLimit);
        Assert.Equal(0.70, settings.CriticalLimit);

        settings = Load("""{ "watchLimit": -3, "criticalLimit": 5 }""");
        settings.Normalize();
        Assert.Equal(0.01, settings.WatchLimit);
        Assert.Equal(1.0, settings.CriticalLimit);
    }

    [Fact]
    public void NormalizeFillsInMissingCollections()
    {
        var settings = Load("""{ "showRemaining": false }""");

        settings.Normalize();

        Assert.False(settings.ShowRemaining);
        Assert.NotNull(settings.Providers);
        Assert.NotNull(settings.Order);
    }

    /// <summary>A stored id no provider claims is not corruption — a profile not on this machine
    /// today — and a provider the order has never seen must still appear, at the end.</summary>
    [Fact]
    public void UnknownStoredProviderIdsAreDroppedAndNewIdsAreAppended()
    {
        var settings = new QuotaDeckSettings { Order = ["cursor", "claude-work", "claude"] };
        Assert.Equal(
            new[] { "cursor", "claude" },
            ProviderOrder.Arrange(new[] { "claude", "cursor" }, settings.Order, id => id));

        settings.Order = ["codex", "claude"];
        var arranged = ProviderOrder.Arrange(new[] { "claude", "cursor", "codex" }, settings.Order, id => id);
        Assert.Equal(new[] { "codex", "claude", "cursor" }, arranged);
        Assert.Equal(3, arranged.Count);
    }

    [Fact]
    public void ProviderConfigurationLoadsAndDefaultsOnFirstUse()
    {
        var settings = Load("""{ "providers": { "claude": { "enabled": false, "busyIntervalSeconds": 45 } } }""");
        settings.Normalize();

        Assert.False(settings.Providers["claude"].Enabled);
        Assert.Equal(45, settings.Providers["claude"].BusyIntervalSeconds);

        var created = settings.ForProvider("codex");
        Assert.True(created.Enabled);
        Assert.Equal(30, created.BusyIntervalSeconds);
        Assert.Equal(300, created.IdleIntervalSeconds);
        Assert.Equal(15, created.StaleAfterMinutes);
    }

    private static QuotaDeckSettings Load(string json) =>
        JsonSerializer.Deserialize<QuotaDeckSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
}
