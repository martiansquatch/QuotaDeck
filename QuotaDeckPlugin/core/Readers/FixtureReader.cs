namespace QuotaDeck.Core.Readers;

using QuotaDeck.Core.Faces;
using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Settings;

/// <summary>
/// A reader for hand-written fixtures.
///
/// The file is re-read on every fetch, not cached, so editing a fixture shows up on the next tick
/// without reloading the plugin. A missing or broken fixture is reported as a failed read
/// (<see cref="ReaderErrorKind.ApiError"/>: "fixture unreadable"), which the base degrades like any
/// other failure — the face keeps whatever it had and says so.
/// </summary>
public sealed class FixtureReader : UsageReaderBase
{
    private readonly string _fixturePath;

    public FixtureReader(
        string providerId,
        string displayName,
        ReaderConfig config,
        IReaderStateStore store,
        TimeProvider time,
        string fixturePath)
        : base(config, store, time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);

        this.Id = providerId;
        this.DisplayName = displayName;
        this._fixturePath = fixturePath;
    }

    public override string Id { get; }

    public override string DisplayName { get; }

    protected override Task<ProviderSnapshot> ReadAsync(Freshness freshness, CancellationToken cancellationToken)
    {
        var snapshot = FixtureSnapshot.LoadFile(this._fixturePath)
            ?? throw new ReaderException(ReaderError.ApiError("fixture unreadable"));

        return Task.FromResult(snapshot);
    }
}
