namespace QuotaDeck.Core.Readers;

using QuotaDeck.Core.Model;

/// <summary>The last good reading for one provider, remembered across launches.</summary>
public sealed record ReaderMemory(ProviderSnapshot Snapshot, DateTimeOffset FetchedAt);

/// <summary>
/// What a failed fetch does to what is on screen.
///
/// Port of Codenotch <c>Sources/Model/UsageStore.swift</c>: <c>status(for:)</c> (lines 949-982),
/// <c>supersedesHistory(_:)</c> (lines 906-921), <c>degraded(provider:error:)</c> (lines 856-904) and
/// <c>placeholder(_:)</c> (lines 984-996).
/// </summary>
public static class Degradation
{
    /// <summary>
    /// A failed fetch never invents a number: it either re-shows the last good one marked stale, or
    /// shows the cell with no reading at all.
    /// </summary>
    public static ProviderSnapshot Degrade(
        string id,
        string displayName,
        ReaderMemory? lastGood,
        ProviderStatus status,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        if (SupersedesHistory(status) || lastGood is null)
        {
            return Placeholder(id, displayName, status);
        }

        // A stale-but-recent reading is still worth showing undimmed; past the window it gets marked
        // and the ring dims.
        var age = now - lastGood.FetchedAt;
        var snapshot = lastGood.Snapshot;
        return age > staleAfter
            ? snapshot with { Status = ProviderStatus.Stale(lastGood.FetchedAt) }
            : snapshot;
    }

    /// <summary>
    /// True when the new status makes any remembered reading untrue rather than merely old.
    /// </summary>
    public static bool SupersedesHistory(ProviderStatus status) => status.Kind switch
    {
        ProviderStatusKind.NeedsAuth => true,
        ProviderStatusKind.Unsupported => true,

        // A refusal says nothing about the reading — the credential is there and still valid, we were
        // simply not let in to re-read it. Discarding the last number would punish someone for
        // pressing the wrong button.
        ProviderStatusKind.AccessDenied => false,

        // The account was not closed and the numbers were not wrong — the owning app dropped its own
        // token.
        ProviderStatusKind.SignedOutByOwner => false,

        _ => false,
    };

    /// <summary>The status a reader failure reads as.</summary>
    public static ProviderStatus StatusFor(ReaderError error, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Kind switch
        {
            ReaderErrorKind.NeedsAuth => ProviderStatus.NeedsAuth,

            // An expired token ages the reading rather than discarding it: the number was true when
            // it was taken, and the token will refresh itself in the ordinary course of using the
            // app that owns it.
            ReaderErrorKind.CredentialExpired => ProviderStatus.Stale(now),

            // Not an error the user can do anything about, and the last good reading is still roughly
            // true, so it reads as staleness.
            ReaderErrorKind.RateLimited => ProviderStatus.Stale(now),

            ReaderErrorKind.SignedOutByOwner => ProviderStatus.SignedOutByOwner,
            ReaderErrorKind.AccessDenied => ProviderStatus.AccessDenied,

            // Nothing is known about the account, so a remembered reading stays and simply ages;
            // this is only what a provider with nothing to show says.
            ReaderErrorKind.TimedOut => ProviderStatus.Error("no response"),

            ReaderErrorKind.NothingMetered => ProviderStatus.Unsupported(error.Reason ?? "Nothing metered"),

            ReaderErrorKind.BadResponse => ProviderStatus.Error($"HTTP {error.Status}"),

            // The server's own words. It is the only part of such a failure that says what went
            // wrong — the status is 200 either way.
            ReaderErrorKind.ApiError => ProviderStatus.Error(error.Name ?? "api error"),

            _ => ProviderStatus.Error("unknown error"),
        };
    }

    /// <summary>
    /// The status any unexpected exception reads as. Codenotch shows the exception's own description;
    /// a message-less exception would show nothing at all, so its type name stands in.
    /// </summary>
    public static ProviderStatus StatusFor(Exception error, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is ReaderException readerException)
        {
            return StatusFor(readerException.Error, now);
        }

        var message = error.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = error.GetType().Name;
        }

        return ProviderStatus.Error(message);
    }

    /// <summary>Enough to draw the cell even when a fetch has never succeeded.</summary>
    public static ProviderSnapshot Placeholder(string id, string displayName, ProviderStatus status)
    {
        return new ProviderSnapshot
        {
            Id = id,
            DisplayName = displayName,
            Fidelity = Fidelity.Official,
            Status = status,
            Windows = [],
        };
    }
}
