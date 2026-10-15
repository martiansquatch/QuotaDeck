namespace QuotaDeck.Readers.Codex;

using System.Text.Json;
using QuotaDeck.Core.Readers;

/// <summary>Codex's own session, borrowed read-only — this app never refreshes or writes it.</summary>
public sealed record CodexCredential(string AccessToken, string AccountId);

/// <summary>
/// Whose reading this is: the account labels Codex's <c>id_token</c> carries, when it carries them.
/// Never logged.
/// </summary>
public sealed record CodexAccount(string? Label, string? Plan);

/// <summary>
/// Reads the credential Codex keeps in <c>&lt;configDir&gt;/auth.json</c>.
///
/// Port of Codenotch <c>Sources/Providers/CodexCredentials.swift</c> —
/// <c>load(from:now:)</c> (lines 14-39), <c>account(from:source:)</c> (lines 41-56) and
/// <c>claims(inJWT:)</c> (lines 59-70) — plus the re-read rule from
/// <c>Sources/Providers/CodexLocalProvider.swift</c> (line 46): the file is read on every fetch so
/// a rotation is picked up.
///
/// The JWT payload is decoded without verifying the signature: it supplies identity labels and a
/// local expiry hint, and the server is what validates the token.
/// </summary>
public static class CodexCredentials
{
    /// <summary>
    /// The credential over the bytes at <paramref name="authPath"/>; <see cref="ReaderError.NeedsAuth"/>
    /// when there is no usable one, <see cref="ReaderError.CredentialExpired"/> when the access
    /// token's own <c>exp</c> has passed.
    /// </summary>
    public static CodexCredential Load(string authPath, DateTimeOffset now)
    {
        string json;
        try
        {
            json = File.ReadAllText(authPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ReaderException(ReaderError.NeedsAuth);
        }

        return LoadFromJson(json, now);
    }

    /// <summary>
    /// The same judgement over bytes that arrived another way (CodexCredentials.swift:21-39).
    /// </summary>
    public static CodexCredential LoadFromJson(string json, DateTimeOffset now)
    {
        var tokens = CodexJson.Object(CodexJson.ParseObject(json), "tokens");
        var accessToken = CodexJson.Text(tokens, "access_token");
        var accountId = CodexJson.Text(tokens, "account_id");

        // Trimmed for emptiness, kept untrimmed: the server gets exactly what Codex stored.
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(accountId))
        {
            throw new ReaderException(ReaderError.NeedsAuth);
        }

        if (Expiry(accessToken!) is { } expiry && expiry <= now.ToUnixTimeMilliseconds() / 1000.0)
        {
            throw new ReaderException(ReaderError.CredentialExpired);
        }

        return new CodexCredential(accessToken!, accountId!);
    }

    /// <summary>
    /// The account labels in the <c>id_token</c>'s claims — the email as the label and the ChatGPT
    /// plan type — or nil when the file or its claims cannot supply them. Never throws.
    /// </summary>
    public static CodexAccount? Account(string authPath)
    {
        string json;
        try
        {
            json = File.ReadAllText(authPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var tokens = CodexJson.Object(CodexJson.ParseObject(json), "tokens");
        if (CodexJson.Text(tokens, "id_token") is not { } idToken || Claims(idToken) is not { } claims)
        {
            return null;
        }

        var plan = CodexJson.Text(CodexJson.Object(claims, "https://api.openai.com/auth"), "chatgpt_plan_type");
        return new CodexAccount(CodexJson.Text(claims, "email"), plan);
    }

    /// <summary>
    /// The JWT payload's claims, base64url decoded with the padding restored
    /// (CodexCredentials.swift:59-70). Nil when the token has fewer than two segments, the payload
    /// is not base64url, or it is not a JSON object. Note the "email" claim is personal data: it is
    /// used only as the account label and never logged.
    /// </summary>
    public static JsonElement? Claims(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload += new string('=', (4 - (payload.Length % 4)) % 4);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The access token's own <c>exp</c> claim, when it is a JSON number.</summary>
    private static double? Expiry(string token)
    {
        if (Claims(token) is not { } claims
            || !claims.TryGetProperty("exp", out var exp)
            || exp.ValueKind != JsonValueKind.Number
            || !exp.TryGetDouble(out var value)
            || !double.IsFinite(value))
        {
            return null;
        }

        return value;
    }
}
