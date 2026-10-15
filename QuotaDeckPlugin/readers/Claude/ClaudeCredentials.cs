namespace QuotaDeck.Readers.Claude;

using System.Text;
using System.Text.Json;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Readers.Shared;

// Port of Codenotch Sources/Providers/ClaudeCredentials.swift (struct ClaudeCredentials: load(),
// isExpired, read(services:interactive:), decode(_:services:), wasRefused(_:), wasTransient(_:),
// explain(_:), and final class ClaudeKeychain: load(), forgetCached()) plus the reading half of
// Sources/Providers/KeychainPrompt.swift (KeychainSecret.read's rescue through `/usr/bin/security`).

/// <summary>The OAuth token Claude Code keeps in the login keychain.</summary>
public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset ExpiresAt, string? SubscriptionType)
{
    /// <summary>
    /// Judged by the caller, never by the reader: "signed out" and "the token has aged out
    /// overnight" call for different behaviour and only one of them is worth alarming anyone about.
    /// </summary>
    public bool IsExpired(DateTimeOffset now) => this.ExpiresAt <= now;

    /// <summary>
    /// Turn the stored JSON into a credential.
    ///
    /// The failure vocabulary is the whole job: an undecodable item means Claude Code has never
    /// signed in here (<see cref="ReaderError.NeedsAuth"/>), and a present-but-emptied one —
    /// <c>{"accessToken":"","expiresAt":0}</c>, which Claude Code really does write on sign-out —
    /// means somebody was signed in and had it taken out from under them
    /// (<see cref="ReaderError.SignedOutByOwner"/>, which keeps the last reading).
    /// </summary>
    public static ClaudeCredentials Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        string? accessToken = null;
        double? expiresAt = null;
        string? subscriptionType = null;
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object
                || !oauth.TryGetProperty("accessToken", out var token)
                || token.ValueKind != JsonValueKind.String
                || !oauth.TryGetProperty("expiresAt", out var expiry)
                || expiry.ValueKind != JsonValueKind.Number)
            {
                throw new ReaderException(ReaderError.NeedsAuth);
            }

            accessToken = token.GetString();
            expiresAt = expiry.GetDouble();
            if (oauth.TryGetProperty("subscriptionType", out var plan) && plan.ValueKind == JsonValueKind.String)
            {
                subscriptionType = plan.GetString();
            }
        }
        catch (JsonException)
        {
            throw new ReaderException(ReaderError.NeedsAuth);
        }

        // A present-but-emptied credential is a real state, not a theoretical one. Left alone it
        // becomes `Bearer ` on the wire and a puzzling 401; reporting it as needsAuth is worse, as
        // that means "never signed in" and erases a perfectly good reading every time the owning
        // app does this.
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new ReaderException(ReaderError.SignedOutByOwner);
        }

        return new ClaudeCredentials(
            accessToken,
            DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAt!.Value),
            subscriptionType);
    }
}

/// <summary>
/// One login's token, read as rarely as the keychain allows.
///
/// One of these per profile, because each profile's token is a separate keychain item with its own
/// access list: macOS prompts once per item, and a cache shared between them would hand the personal
/// token to the work ring.
///
/// QuotaDeck never writes this item. Refreshing is left to Claude Code — minting a token would mean
/// writing a credential this app does not own and racing the owner for it — so an expired token is
/// reported as such and Claude Code renews it in the ordinary course of being used.
///
/// Codenotch's "Allow access…" machinery (<c>PromptPermission</c>, <c>KeychainRefusal</c>) is not
/// ported: it exists for a button, and this PR has no UI. Every read here is a background read, so
/// every read has interaction off.
/// </summary>
public sealed class ClaudeKeychain
{
    private readonly IReadOnlyList<string> _services;
    private readonly string _account;
    private readonly IKeychain _keychain;
    private readonly IProcessRunner _process;
    private readonly CredentialCache<ClaudeCredentials> _cache;

    public ClaudeKeychain(
        IReadOnlyList<string> services,
        string account,
        IKeychain keychain,
        IProcessRunner process,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keychain);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(time);

        this._services = services;
        this._account = account;
        this._keychain = keychain;
        this._process = process;
        this._cache = new CredentialCache<ClaudeCredentials>(
            isExpired: credentials => credentials.IsExpired(time.GetUtcNow()),
            isPermanentFailure: exception =>
                exception is ReaderException { Error.Kind: ReaderErrorKind.AccessDenied },
            time: time);
    }

    /// <summary>The service names this login's token may be filed under, suffixed first.</summary>
    public IReadOnlyList<string> Services => this._services;

    /// <summary>What is held, for a caller that must not cause a keychain read.</summary>
    public ClaudeCredentials? Held => this._cache.Held;

    /// <summary>
    /// Reads whatever is stored, expired or not. Judging expiry is the caller's job.
    /// </summary>
    public Task<ClaudeCredentials> LoadAsync(CancellationToken cancellationToken = default) =>
        this._cache.ValueAsync(
            itemModifiedAt: () => KeychainQuery.ModifiedAt(this._keychain, this._services),
            reload: () => this.ReadFreshAsync(cancellationToken));

    /// <summary>Forget the held copy. Called when the server rejects the credential.</summary>
    public void ForgetCached() => this._cache.Forget();

    private async Task<ClaudeCredentials> ReadFreshAsync(CancellationToken cancellationToken)
    {
        // The newest item under this service, rather than asking for "one" and trusting the answer:
        // Claude Code files a new item on every token rotation instead of updating one in place, so
        // an account used for months accumulates several under the same service name, and
        // kSecMatchLimitOne gives no ordering guarantee across them.
        if (KeychainQuery.Newest(this._keychain, this._services) is not { } winner)
        {
            throw new ReaderException(ReaderError.NeedsAuth);
        }

        var read = this._keychain.ReadItem(winner.PersistentRef);
        if (read.Status != KeychainStatus.Success || read.Data is null)
        {
            // The status matters: "not found" means the item was deleted between enumeration and
            // this read — Claude Code rotating at the exact wrong instant — whereas a refusal means
            // it is there and this app is not on its access list. Those need very different advice.
            if (KeychainStatus.WasRefused(read.Status)
                && await this.RescueAsync(winner, cancellationToken).ConfigureAwait(false) is { } rescued)
            {
                return ClaudeCredentials.Decode(rescued);
            }

            // A machine just woken from a long sleep answers -25320: awake enough to run background
            // work, not awake enough to show a dialogue even if one were needed. The credential is
            // unaffected, so this is "not right now", not "signed out".
            if (KeychainStatus.WasTransient(read.Status))
            {
                throw new ReaderException(ReaderError.CredentialExpired);
            }

            throw new ReaderException(
                KeychainStatus.WasRefused(read.Status) ? ReaderError.AccessDenied : ReaderError.NeedsAuth);
        }

        return ClaudeCredentials.Decode(read.Data);
    }

    /// <summary>
    /// Read the item by name through <c>/usr/bin/security</c>. It is Apple-signed and is how these
    /// items were written, so it is on their access list and inside their partition list. The secret
    /// comes straight back into this process; nothing is written and it is never logged.
    /// </summary>
    private async Task<byte[]?> RescueAsync(KeychainItem winner, CancellationToken cancellationToken)
    {
        var result = await this._process.RunAsync(
            new ProcessRequest
            {
                FileName = "/usr/bin/security",
                Arguments = ["find-generic-password", "-a", this._account, "-s", winner.Service, "-w"],
                CaptureStandardError = false,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(result.StandardOutput);
        var end = bytes.Length;
        while (end > 0 && (bytes[end - 1] == (byte)'\n' || bytes[end - 1] == (byte)'\r'))
        {
            end--;
        }

        return end == 0 ? null : bytes[..end];
    }
}
