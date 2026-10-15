namespace QuotaDeck.Core.Readers;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Settings;

/// <summary>
/// One source of usage numbers. Each reader declares how trustworthy it is and never throws.
///
/// Port of Codenotch <c>Sources/Providers/UsageProvider.swift</c> (<c>protocol UsageProvider</c>,
/// reduced to the reading half: the account, sign-in, sign-out and icon requirements belong to a
/// later phase's UI, not to the state engine).
/// </summary>
public interface IUsageReader
{
    /// <summary>Stable id, used as the settings key and the alert's provider id.</summary>
    string Id { get; }

    /// <summary>Enough to draw the face even when a fetch has never succeeded.</summary>
    string DisplayName { get; }

    /// <summary>
    /// The reading, with a say in how old it may be. Never throws: a failure comes back as a snapshot
    /// carrying the status that failure means. See <see cref="UsageReaderBase"/>.
    /// </summary>
    Task<ProviderSnapshot> FetchAsync(Freshness freshness, CancellationToken cancellationToken = default);
}
