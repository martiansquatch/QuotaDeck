namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Faces;
using QuotaDeck.Core.Model;

/// <summary>
/// The fixture format: a hand-written reading parses into a snapshot, survives a JSON round trip,
/// and anything malformed reads as null instead of throwing.
/// </summary>
public class FixtureSnapshotTests
{
    [Fact]
    public void EveryFieldSurvivesARoundTrip()
    {
        var resetsAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var snapshot = new ProviderSnapshot
        {
            Id = "claude",
            DisplayName = "Claude",
            Fidelity = Fidelity.Derived,
            Status = ProviderStatus.Stale(DateTimeOffset.FromUnixTimeSeconds(1_799_000_000)),
            Windows =
            [
                new LimitWindow
                {
                    Id = "session",
                    Group = "Spark",
                    Label = "5-hour limit",
                    UsedFraction = 0.73,
                    ResetsAt = resetsAt,
                    Duration = TimeSpan.FromHours(5),
                },
                new LimitWindow
                {
                    Id = "weekly",
                    Label = "Weekly limit",
                    UsedFraction = 0.41,
                },
            ],
            HeadlineId = "session",
            WeeklyId = "weekly",
            Plan = "Max",
            AccountLabel = "Work account",
        };

        var parsed = FixtureSnapshot.Parse(FixtureSnapshot.ToJson(snapshot));

        Assert.NotNull(parsed);
        Assert.Equal("claude", parsed!.Id);
        Assert.Equal("Claude", parsed.DisplayName);
        Assert.Equal(Fidelity.Derived, parsed.Fidelity);
        Assert.Equal(snapshot.Status, parsed.Status);
        Assert.Equal("session", parsed.HeadlineId);
        Assert.Equal("weekly", parsed.WeeklyId);
        Assert.Equal("Max", parsed.Plan);
        Assert.Equal("Work account", parsed.AccountLabel);
        Assert.Equal(snapshot.Windows, parsed.Windows);
    }

    [Fact]
    public void ANeedsAuthFixtureWithNoWindowsParses()
    {
        var parsed = FixtureSnapshot.Parse(
            """{ "id": "muse", "displayName": "Muse", "status": { "kind": "needsAuth" }, "windows": [] }""");

        Assert.NotNull(parsed);
        Assert.Equal(ProviderStatus.NeedsAuth, parsed!.Status);
        Assert.Empty(parsed.Windows);
        Assert.Equal(Fidelity.Official, parsed.Fidelity);
    }

    [Fact]
    public void AStaleFixtureKeepsItsSince()
    {
        var parsed = FixtureSnapshot.Parse(
            """
            { "id": "devin", "displayName": "Devin", "status": { "kind": "stale", "since": "2027-03-12T00:00:00+00:00" },
              "windows": [{ "id": "session", "label": "Session limit", "usedFraction": 0.34 }],
              "headlineId": "session" }
            """);

        Assert.NotNull(parsed);
        Assert.Equal(ProviderStatusKind.Stale, parsed!.Status.Kind);
        Assert.Equal(DateTimeOffset.Parse("2027-03-12T00:00:00+00:00"), parsed.Status.StaleSince);
        Assert.Equal(0.34, parsed.UsedFraction!.Value, 10);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "displayName": "No id" }""")]
    [InlineData("""{ "id": "x", "displayName": "X", "status": { "kind": "signedin" } }""")]
    [InlineData("""{ "id": "x", "displayName": "X", "fidelity": "guessed" }""")]
    [InlineData("""{ "id": "x", "displayName": "X", "windows": [{ "label": "no id" }] }""")]
    [InlineData("")]
    public void MalformedFixturesReadAsNullWithoutThrowing(string json)
    {
        Assert.Null(FixtureSnapshot.Parse(json));
    }

    [Fact]
    public void AMissingFileReadsAsNullWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), "quotaDeck-missing-" + Guid.NewGuid().ToString("N") + ".json");

        Assert.Null(FixtureSnapshot.LoadFile(path));
    }
}
