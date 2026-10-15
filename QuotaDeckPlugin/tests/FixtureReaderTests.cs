namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Settings;

/// <summary>
/// The fixture reader re-reads its file on every fetch and reports a missing one as an ordinary
/// failed read — never a throw and never a stale cache.
/// </summary>
public class FixtureReaderTests
{
    private static string FixturePath() =>
        Path.Combine(Path.GetTempPath(), "quotaDeck-fixture-" + Guid.NewGuid().ToString("N") + ".json");

    private static string Json(double used) =>
        $$"""
        { "id": "claude", "displayName": "Claude",
          "windows": [{ "id": "session", "label": "5-hour limit", "usedFraction": {{used.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }],
          "headlineId": "session" }
        """;

    [Fact]
    public async Task AFetchReadsTheFileAndAChangedFileShowsUp()
    {
        var path = FixturePath();
        File.WriteAllText(path, Json(0.25));
        var store = new InMemoryReaderStateStore();
        var reader = new FixtureReader("claude", "Claude", new ReaderConfig(), store, TimeProvider.System, path);

        var first = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(0.25, first.UsedFraction!.Value, 10);

        File.WriteAllText(path, Json(0.75));
        var second = await reader.FetchAsync(Freshness.Standard);
        Assert.Equal(0.75, second.UsedFraction!.Value, 10);
    }

    [Fact]
    public async Task ABrokenFixtureFailsAsAReadAndTheLastGoodSurvives()
    {
        var path = FixturePath();
        File.WriteAllText(path, Json(0.25));
        var reader = new FixtureReader("claude", "Claude", new ReaderConfig(), new InMemoryReaderStateStore(), TimeProvider.System, path);
        Assert.False((await reader.FetchAsync(Freshness.Standard)).Status.IsStale);

        File.WriteAllText(path, "{ not json");
        var broken = await reader.FetchAsync(Freshness.Standard);

        // The failure is the generic one, never a throw, and the reading just taken is kept.
        Assert.NotEqual(ProviderStatusKind.Error, broken.Status.Kind);
        Assert.Equal(0.25, broken.UsedFraction!.Value, 10);
    }
}
