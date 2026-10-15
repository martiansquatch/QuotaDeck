namespace QuotaDeck.Readers.Antigravity;

using System.Globalization;
using System.Text;
using System.Text.Json;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Readers.Shared;

/// <summary>The OAuth token Antigravity holds for a Google account.</summary>
public sealed record AntigravityCredentials(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string AuthMethod,
    string? ProjectId,
    string? Email)
{
    public bool IsExpired(DateTimeOffset now) => this.ExpiresAt <= now;
}

/// <summary>
/// Reads the account's credential, and holds it while the sources it came from are unchanged.
///
/// Port of Codenotch <c>Sources/Providers/AntigravityCredentials.swift</c> (<c>load()</c> and
/// <c>read()</c>, lines 119-317): three sources in order — the <c>gemini</c>/<c>antigravity</c>
/// keychain item (a <c>go-keyring-base64:</c> envelope), the OMP <c>auth_credentials</c> row, and
/// <c>~/.gemini/oauth_creds.json</c> — the first unexpired one wins, otherwise the first one found.
/// Re-reads when the newest source modification time changes, with a five-minute recheck
/// (<c>CredentialCache</c>); the keychain's own modification date is part of that probe, read from
/// its attributes without prompting, exactly as the Claude reader does.
///
/// The keychain read is the shared <see cref="MacKeychain"/>: enumeration of attributes only, then
/// a persistent-reference read with interaction off, and <c>/usr/bin/security</c> used solely as
/// the rescue when macOS refuses the data. Nothing here can raise a dialogue.
/// </summary>
public sealed class AntigravityCredentialStore
{
    /// <summary>The service Antigravity files its item under.</summary>
    public const string KeychainService = "gemini";

    /// <summary>The account Antigravity files its item under.</summary>
    public const string KeychainAccount = "antigravity";

    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(5);

    private readonly AntigravityOptions _options;
    private readonly IKeychain _keychain;

    private AntigravityCredentials? _held;
    private DateTimeOffset? _heldStamp;
    private DateTimeOffset _lastReadAt = DateTimeOffset.MinValue;

    public AntigravityCredentialStore(AntigravityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this._options = options;
        this._keychain = options.Keychain ?? new MacKeychain();
    }

    /// <summary>Whatever a previous read is holding, without touching any source.</summary>
    public AntigravityCredentials? Held => this._held;

    /// <summary>
    /// The credential, or null when no source has one. Throws only for a genuinely unreadable
    /// account (none of the three sources has anything usable) — the reader swallows that, because
    /// the request count is a better answer than an auth error for an account Google will not read.
    /// </summary>
    public async Task<AntigravityCredentials?> LoadAsync(CancellationToken cancellationToken)
    {
        var now = this._options.TimeProvider.GetUtcNow();
        var stamp = this.SourceStamp();
        if (this._held is not null
            && stamp == this._heldStamp
            && now - this._lastReadAt < RecheckAfter)
        {
            return this._held;
        }

        this._lastReadAt = now;
        this._heldStamp = stamp;

        var found = this._options.Profile.Slug is null
            ? await this.ReadDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await this.ReadProfileAsync(this._options.Profile, cancellationToken).ConfigureAwait(false);

        this._held = found;
        return found;
    }

    private async Task<AntigravityCredentials?> ReadDefaultAsync(CancellationToken cancellationToken)
    {
        AntigravityCredentials? keychain = null;
        if (await this.ReadKeychainAsync(cancellationToken).ConfigureAwait(false) is { } decoded)
        {
            keychain = decoded;
            if (!decoded.IsExpired(this._options.TimeProvider.GetUtcNow()))
            {
                return decoded;
            }
        }

        var omp = AntigravityOmp.Credentials(this._options.HomeDirectory);
        if (omp is not null)
        {
            if (!omp.IsExpired(this._options.TimeProvider.GetUtcNow()))
            {
                return omp;
            }

            keychain ??= omp;
        }

        var json = ReadJsonCredentials(this._options.Profile.AuthPath);
        if (json is not null)
        {
            if (!json.IsExpired(this._options.TimeProvider.GetUtcNow()))
            {
                return json;
            }

            keychain ??= json;
        }

        return keychain;
    }

    private async Task<AntigravityCredentials?> ReadProfileAsync(
        AntigravityProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ReadJsonCredentials(profile.AuthPath) is { } json)
        {
            return json;
        }

        return AntigravityOmp.CredentialsFromDatabase(
            Path.Combine(profile.ConfigDirectory, "agent.db"));
    }

    /// <summary>
    /// The newest <c>gemini</c>/<c>antigravity</c> item, read by persistent reference with
    /// interaction off. Antigravity files a new item on every rotation rather than updating one in
    /// place, so "the newest" is the live one; a refusal is retried through the rescue, and
    /// anything else is simply "no credential from this source".
    /// </summary>
    private async Task<AntigravityCredentials?> ReadKeychainAsync(CancellationToken cancellationToken)
    {
        if (KeychainQuery.Newest(this._keychain, [KeychainService], KeychainAccount) is not { } winner)
        {
            return null;
        }

        var read = this._keychain.ReadItem(winner.PersistentRef);
        if (read.Status == KeychainStatus.Success && read.Data is not null)
        {
            return DecodeKeychain(Encoding.UTF8.GetString(read.Data));
        }

        // A refusal means the item is there and this app is not on its access list — the state
        // `/usr/bin/security` rescues, because it is Apple's own tool and Antigravity wrote the
        // item through it. Any other status has no rescue to offer.
        if (KeychainStatus.WasRefused(read.Status)
            && await this.RescueAsync(winner, cancellationToken).ConfigureAwait(false) is { } rescued)
        {
            return DecodeKeychain(Encoding.UTF8.GetString(rescued));
        }

        return null;
    }

    /// <summary>
    /// Read the item by name through <c>/usr/bin/security</c>. It is Apple-signed and is how these
    /// items were written, so it is on their access list and inside their partition list. The secret
    /// comes straight back into this process; nothing is written and it is never logged.
    /// </summary>
    private async Task<byte[]?> RescueAsync(KeychainItem winner, CancellationToken cancellationToken)
    {
        var result = await this._options.Process.RunAsync(
            new ProcessRequest
            {
                FileName = "/usr/bin/security",
                Arguments = ["find-generic-password", "-a", KeychainAccount, "-s", winner.Service, "-w"],
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

    /// <summary>
    /// The newest modification time across the sources. The file sources contribute their mtimes
    /// (they are read directly), and for the default profile the keychain contributes the newest
    /// item's modification date from its attributes — the same probe Codenotch's <c>load()</c>
    /// builds, so a rotation is noticed at the next fetch rather than at the five-minute recheck.
    /// </summary>
    private DateTimeOffset? SourceStamp()
    {
        var paths = this._options.Profile.Slug is null
            ? new[] { this._options.Profile.AuthPath, AntigravityOmp.DatabasePath(this._options.HomeDirectory) }
            : [this._options.Profile.AuthPath, Path.Combine(this._options.Profile.ConfigDirectory, "agent.db")];

        DateTimeOffset? newest = null;
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) is var modified)
                {
                    var stamp = new DateTimeOffset(modified, TimeSpan.Zero);
                    if (newest is null || stamp > newest)
                    {
                        newest = stamp;
                    }
                }
            }
            catch
            {
                // An unreadable source simply does not contribute to the stamp.
            }
        }

        if (this._options.Profile.Slug is null
            && KeychainQuery.ModifiedAt(this._keychain, [KeychainService], KeychainAccount) is { } keychain
            && (newest is null || keychain > newest))
        {
            newest = keychain;
        }

        return newest;
    }

    /// <summary>
    /// Go's keyring package base64-encodes the payload behind this marker rather than writing raw
    /// JSON; without stripping it the value is not JSON at all. The marker is optional here, as in
    /// the Swift decoder.
    /// </summary>
    private const string GoKeyringPrefix = "go-keyring-base64:";

    public static AntigravityCredentials? DecodeKeychain(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var text = stored.Trim();
        if (text.StartsWith(GoKeyringPrefix, StringComparison.Ordinal))
        {
            text = text[GoKeyringPrefix.Length..];
        }

        try
        {
            var payload = Convert.FromBase64String(text);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var authMethod = root.TryGetProperty("auth_method", out var method) ? method.GetString() : null;
            if (authMethod is null || !root.TryGetProperty("token", out var token))
            {
                return null;
            }

            var access = token.TryGetProperty("access_token", out var accessProperty) ? accessProperty.GetString() : null;
            var expiry = token.TryGetProperty("expiry", out var expiryProperty) ? expiryProperty.GetString() : null;
            if (string.IsNullOrEmpty(access) || ParseTimestamp(expiry) is not { } expiresAt)
            {
                return null;
            }

            return new AntigravityCredentials(access, expiresAt, authMethod, null, null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// An RFC 3339 timestamp with an offset, with or without fractional seconds — the field is
    /// written by Go, not by us, and "2026-08-31T21:53:49.575961+07:00" must read as the instant it
    /// names, not as UTC and not as epoch milliseconds.
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static AntigravityCredentials? ReadJsonCredentials(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var access = root.TryGetProperty("access_token", out var token) ? token.GetString() : null;
            if (string.IsNullOrEmpty(access))
            {
                return null;
            }

            var email = root.TryGetProperty("email", out var emailProperty) ? emailProperty.GetString() : null;

            // `projectId` first, as Swift's `payload.projectId ?? payload.project_id`; a missing or
            // null camelCase field reads the snake_case one.
            var project = StringProperty(root, "projectId") ?? StringProperty(root, "project_id");

            return new AntigravityCredentials(access, ExpiryFromMilliseconds(root), "consumer", project, email);
        }
        catch
        {
            return null;
        }
    }

    private static string? StringProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset ExpiryFromMilliseconds(JsonElement root)
    {
        if (root.TryGetProperty("expiry_date", out var property) && property.ValueKind == JsonValueKind.Number)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)property.GetDouble());
        }

        return DateTimeOffset.MaxValue;
    }

    /// <summary>Whether a profile has any credential source at all, judged without reading secrets.</summary>
    public static bool HasCredential(AntigravityProfile profile)
    {
        if (File.Exists(profile.AuthPath))
        {
            return true;
        }

        var database = profile.Slug is null
            ? AntigravityOmp.DatabasePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            : Path.Combine(profile.ConfigDirectory, "agent.db");
        return AntigravityOmp.CredentialsFromDatabase(database) is not null;
    }
}
