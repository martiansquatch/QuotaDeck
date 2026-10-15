namespace QuotaDeck.Readers.Antigravity;

using System.Globalization;
using System.Text;
using System.Text.Json;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;

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
/// (<c>CredentialCache</c>); a keychain item has no readable modification time through
/// <c>security -w</c>, so the recheck is the part that covers it.
/// </summary>
public sealed class AntigravityCredentialStore
{
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(5);

    private readonly AntigravityOptions _options;

    private AntigravityCredentials? _held;
    private DateTimeOffset? _heldStamp;
    private DateTimeOffset _lastReadAt = DateTimeOffset.MinValue;

    public AntigravityCredentialStore(AntigravityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this._options = options;
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
        if (await AntigravityKeychain.ReadAsync(this._options.Process, cancellationToken).ConfigureAwait(false) is { } secret
            && DecodeKeychain(secret) is { } decoded)
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
    /// The newest modification time across the file sources; nil when none exists. The keychain is
    /// not part of it (its attributes are not read), so the five-minute recheck covers a rotation.
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
            var project = root.TryGetProperty("project_id", out var projectId) ? projectId.GetString()
                : root.TryGetProperty("projectId", out var projectIdCamel) ? projectIdCamel.GetString()
                : null;

            return new AntigravityCredentials(access, ExpiryFromMilliseconds(root), "consumer", project, email);
        }
        catch
        {
            return null;
        }
    }

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
