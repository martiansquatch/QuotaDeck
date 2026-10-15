namespace QuotaDeck.Readers.Tests.Codex;

using System.Text.Json;
using QuotaDeck.Readers.Codex;

/// <summary>
/// Synthetic Codex fixtures: JWT-shaped tokens and <c>auth.json</c> files whose claims carry only
/// reserved example data (<c>*@example.test</c> account ids "personal"/"work"). Mirror of
/// <c>writeAuth</c> in Codenotch <c>Tests/CodexProfileTests.swift</c> (lines 31-38).
/// </summary>
internal static class CodexTestData
{
    /// <summary>A <c>header.&lt;base64url claims&gt;.signature</c> token, like a real one.</summary>
    public static string Jwt(object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        var encoded = Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"header.{encoded}.signature";
    }

    /// <summary>
    /// Writes an auth.json for one profile. The access token stays the caller's opaque string unless
    /// <paramref name="accessTokenExpiresAt"/> is given, in which case it becomes a JWT carrying
    /// that <c>exp</c> — the shape the credential check reads.
    /// </summary>
    public static void WriteAuth(
        CodexProfile profile,
        string accountId,
        string token = "fake",
        string plan = "plus",
        double? accessTokenExpiresAt = null)
    {
        var idClaims = new Dictionary<string, object>
        {
            ["email"] = $"{accountId}@example.test",
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_plan_type"] = plan,
            },
        };

        var accessToken = accessTokenExpiresAt is { } expiry
            ? Jwt(new Dictionary<string, object> { ["exp"] = expiry })
            : token;

        var auth = new Dictionary<string, object>
        {
            ["tokens"] = new Dictionary<string, object>
            {
                ["access_token"] = accessToken,
                ["account_id"] = accountId,
                ["id_token"] = Jwt(idClaims),
            },
        };

        Directory.CreateDirectory(profile.ConfigDirectory);
        File.WriteAllText(profile.AuthPath, JsonSerializer.Serialize(auth));
    }
}
