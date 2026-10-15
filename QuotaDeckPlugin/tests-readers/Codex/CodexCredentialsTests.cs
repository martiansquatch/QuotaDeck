namespace QuotaDeck.Readers.Tests.Codex;

using System.Text.Json;
using QuotaDeck.Core.Readers;
using QuotaDeck.Readers.Codex;

/// <summary>
/// Port of the credential half of Codenotch <c>Tests/CodexProfileTests.swift</c>
/// (<c>testAccountLabelsComeFromEachProfilesCredentials</c>, lines 86-97) extending the
/// <c>CodexCredentials</c> judgements of <c>Sources/Providers/CodexCredentials.swift</c>.
/// </summary>
public class CodexCredentialsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void AMissingAuthFileNeedsAuth()
    {
        using var home = new CodexTestHome();
        var exception = Assert.Throws<ReaderException>(
            () => CodexCredentials.Load(Path.Combine(home.Root, ".codex", "auth.json"), Now));

        Assert.Equal(ReaderErrorKind.NeedsAuth, exception.Error.Kind);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"tokens\":null}")]
    [InlineData("{\"tokens\":{\"account_id\":\"personal\"}}")]
    [InlineData("{\"tokens\":{\"access_token\":\"token\"}}")]
    [InlineData("{\"tokens\":{\"access_token\":\"   \",\"account_id\":\"personal\"}}")]
    [InlineData("{\"tokens\":{\"access_token\":\"token\",\"account_id\":\" \\n \"}}")]
    [InlineData("{\"tokens\":{\"access_token\":25,\"account_id\":\"personal\"}}")]
    public void UnusableTokensNeedAuth(string json)
    {
        var exception = Assert.Throws<ReaderException>(() => CodexCredentials.LoadFromJson(json, Now));

        Assert.Equal(ReaderErrorKind.NeedsAuth, exception.Error.Kind);
    }

    [Fact]
    public void AnExpiredAccessTokenIsCredentialExpired()
    {
        using var home = new CodexTestHome();
        var profile = CodexProfile.Default(home.Root);
        CodexTestData.WriteAuth(profile, "personal", accessTokenExpiresAt: Now.ToUnixTimeSeconds());

        var exception = Assert.Throws<ReaderException>(() => CodexCredentials.Load(profile.AuthPath, Now));

        Assert.Equal(ReaderErrorKind.CredentialExpired, exception.Error.Kind);
    }

    [Fact]
    public void AFutureExpiryStillLoadsAndTheTokenIsKeptUntrimmed()
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["tokens"] = new Dictionary<string, object>
            {
                ["access_token"] = "  padded-token  ",
                ["account_id"] = "personal",
            },
        });

        var credential = CodexCredentials.LoadFromJson(json, Now);

        Assert.Equal("  padded-token  ", credential.AccessToken);
        Assert.Equal("personal", credential.AccountId);
    }

    [Fact]
    public void ClaimsDecodeBase64UrlAndStandardBase64()
    {
        var claims = new Dictionary<string, object> { ["email"] = "personal@example.test" };

        // The fixture style: base64url, no padding.
        var urlToken = CodexTestData.Jwt(claims);
        Assert.Equal(
            "personal@example.test",
            CodexCredentials.Claims(urlToken)?.GetProperty("email").GetString());

        // Foundation's base64EncodedString() writes standard base64 with padding; that must decode too.
        var json = JsonSerializer.SerializeToUtf8Bytes(claims);
        var padded = $"header.{Convert.ToBase64String(json)}.signature";
        Assert.Equal(
            "personal@example.test",
            CodexCredentials.Claims(padded)?.GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("nodots")]
    [InlineData("header.###.signature")]
    [InlineData("header.bm90LWpzb24.signature")]
    [InlineData("")]
    public void UnreadableClaimsAreNil(string token)
    {
        Assert.Null(CodexCredentials.Claims(token));
    }

    [Fact]
    public void AccountLabelsAndPlanComeFromEachProfilesIdToken()
    {
        using var home = new CodexTestHome();
        home.WithLayout(new Dictionary<string, string[]>
        {
            [".codex"] = [],
            [".codex-work"] = ["config.toml"],
        });

        var personal = CodexProfile.Default(home.Root);
        var work = CodexProfile.Discover(home.Root).Single(profile => profile.Slug == "work");
        CodexTestData.WriteAuth(personal, "personal", plan: "plus");
        CodexTestData.WriteAuth(work, "work", plan: "pro");

        Assert.Equal("personal@example.test", CodexCredentials.Account(personal.AuthPath)?.Label);
        Assert.Equal("plus", CodexCredentials.Account(personal.AuthPath)?.Plan);
        Assert.Equal("work@example.test", CodexCredentials.Account(work.AuthPath)?.Label);
        Assert.Equal("pro", CodexCredentials.Account(work.AuthPath)?.Plan);
    }

    [Fact]
    public void AMissingOrUnreadableIdTokenYieldsNoAccount()
    {
        using var home = new CodexTestHome();
        var profile = CodexProfile.Default(home.Root);

        Assert.Null(CodexCredentials.Account(profile.AuthPath));

        home.WriteFile(".codex/auth.json", "{\"tokens\":{\"access_token\":\"token\",\"account_id\":\"personal\"}}");
        Assert.Null(CodexCredentials.Account(profile.AuthPath));

        home.WriteFile(".codex/auth.json", "{\"tokens\":{\"id_token\":\"header.###.signature\"}}");
        Assert.Null(CodexCredentials.Account(profile.AuthPath));
    }

    [Fact]
    public void AProfileNeverFallsBackToAnotherProfilesCredential()
    {
        using var home = new CodexTestHome();
        home.WithLayout(new Dictionary<string, string[]>
        {
            [".codex"] = [],
            [".codex-work"] = ["config.toml"],
        });

        var personal = CodexProfile.Default(home.Root);
        var work = CodexProfile.Discover(home.Root).Single(profile => profile.Slug == "work");
        CodexTestData.WriteAuth(personal, "personal", token: "personal-token");

        var exception = Assert.Throws<ReaderException>(() => CodexCredentials.Load(work.AuthPath, Now));
        Assert.Equal(ReaderErrorKind.NeedsAuth, exception.Error.Kind);
        Assert.Null(CodexCredentials.Account(work.AuthPath));
    }
}
