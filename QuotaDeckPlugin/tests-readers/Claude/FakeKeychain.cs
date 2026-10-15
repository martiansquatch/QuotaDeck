namespace QuotaDeck.Readers.Tests.Claude;

using System.Text;
using QuotaDeck.Readers.Shared;

/// <summary>
/// A scripted login keychain: items with service, account, data, modification date and a status the
/// next read of that item answers with. Every enumeration and every data read is counted, because
/// "did it go back and ask, and with interaction off?" is the question the CredentialCache and the
/// reader's counters exist to answer.
/// </summary>
public sealed class FakeKeychain : IKeychain
{
    private readonly object _gate = new();
    private readonly List<FakeItem> _items = [];
    private int _nextRef = 1;

    /// <summary>Every service name an enumeration asked for, in order.</summary>
    public List<string> Enumerations { get; } = [];

    /// <summary>Every persistent reference a data read asked for, in order.</summary>
    public List<byte[]> Reads { get; } = [];

    /// <summary>How many data reads were attempted.</summary>
    public int ReadCount
    {
        get
        {
            lock (this._gate)
            {
                return this.Reads.Count;
            }
        }
    }

    public sealed class FakeItem
    {
        public required string Service { get; init; }

        public string? Account { get; init; }

        public byte[] Data { get; set; } = [];

        public DateTimeOffset Modified { get; set; } = DateTimeOffset.UnixEpoch;

        /// <summary>What the next data read answers with; <see cref="KeychainStatus.Success"/> unless scripted.</summary>
        public int ReadStatus { get; set; } = KeychainStatus.Success;

        public byte[] Reference { get; init; } = [];
    }

    /// <summary>One item, with its bytes and when it was last written.</summary>
    public FakeItem Add(string service, byte[] data, DateTimeOffset modified, string? account = null)
    {
        lock (this._gate)
        {
            var item = new FakeItem
            {
                Service = service,
                Account = account,
                Data = data,
                Modified = modified,
                Reference = Encoding.ASCII.GetBytes($"ref-{this._nextRef++}"),
            };
            this._items.Add(item);
            return item;
        }
    }

    /// <summary>One item holding a credential document.</summary>
    public FakeItem AddCredential(
        string service,
        string accessToken,
        DateTimeOffset expiresAt,
        DateTimeOffset modified,
        string? subscriptionType = null,
        string? account = null) =>
        this.Add(service, Credential(accessToken, expiresAt, subscriptionType), modified, account);

    /// <summary>The JSON Claude Code files in the item.</summary>
    public static byte[] Credential(string accessToken, DateTimeOffset expiresAt, string? subscriptionType = null)
    {
        var plan = subscriptionType is null ? string.Empty : $",\"subscriptionType\":\"{subscriptionType}\"";
        return Encoding.UTF8.GetBytes(
            $"{{\"claudeAiOauth\":{{\"accessToken\":\"{accessToken}\","
            + $"\"expiresAt\":{expiresAt.ToUnixTimeMilliseconds()}{plan}}}}}");
    }

    public IReadOnlyList<KeychainItem> Items(string service, string? account = null)
    {
        lock (this._gate)
        {
            this.Enumerations.Add(service);
            return this._items
                .Where(item => item.Service == service && (account is null || item.Account == account))
                .Select(item => new KeychainItem(item.Modified, item.Reference, item.Service))
                .ToList();
        }
    }

    public KeychainReadResult ReadItem(byte[] persistentRef)
    {
        lock (this._gate)
        {
            this.Reads.Add(persistentRef);
            var item = this._items.FirstOrDefault(candidate => candidate.Reference.AsSpan().SequenceEqual(persistentRef));
            return item is null
                ? new KeychainReadResult(KeychainStatus.ItemNotFound, null)
                : new KeychainReadResult(item.ReadStatus, item.ReadStatus == KeychainStatus.Success ? item.Data : null);
        }
    }
}
