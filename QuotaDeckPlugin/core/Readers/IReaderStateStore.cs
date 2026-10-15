namespace QuotaDeck.Core.Readers;

using QuotaDeck.Core.Model;

/// <summary>
/// Where a reader's memory lives between fetches and across launches: the last good reading and any
/// rate-limit deadline.
///
/// Port of Codenotch <c>Sources/Model/UsageArchive.swift</c> (<c>load()</c> / <c>save(_:)</c> for the
/// last good readings, <c>loadBackoffUntil(providerID:)</c> / <c>saveBackoffUntil(_:providerID:)</c>
/// for the back-off), with the UserDefaults backing replaced by an injected store so the reader base
/// can be tested without touching a file or a preference domain.
/// </summary>
public interface IReaderStateStore
{
    /// <summary>The remembered reading, if any.</summary>
    ReaderMemory? Load(string readerId);

    void Save(string readerId, ReaderMemory memory);

    /// <summary>
    /// Drop what is remembered about one reader. Signing out has to reach this, or the face keeps
    /// showing the last reading — dimmed and dated, but still that account's numbers.
    /// </summary>
    void Forget(string readerId);

    /// <summary>
    /// When the endpoint may next be called, remembered across launches. Without this, every relaunch
    /// starts with a clean slate and fires a request immediately — so a development loop walks
    /// straight into the rate limit it is being punished by, and keeps the punishment alive. Kept per
    /// reader: the limit is per account, so one profile being told to slow down says nothing about
    /// another.
    /// </summary>
    DateTimeOffset? LoadBackoffUntil(string readerId);

    void SaveBackoffUntil(string readerId, DateTimeOffset? until);
}
