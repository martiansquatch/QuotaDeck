namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Core.Readers;
using QuotaDeck.Readers.Claude;
using QuotaDeck.Readers.Shared;
using QuotaDeck.Readers.Tests.Support;

/// <summary>
/// The token path: what the stored JSON means, which duplicate item is read, what a refusal turns
/// into, and how rarely the data read — the one call that used to raise a keychain dialogue — is
/// allowed to happen.
///
/// Ported from Codenotch <c>Tests/ClaudeOAuthProviderTests.swift</c> (the credential cache and
/// refusal suites) and <c>Sources/Providers/ClaudeCredentials.swift</c>'s own expectations.
/// </summary>
public class ClaudeKeychainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 16, 20, 0, TimeSpan.Zero);

    private readonly FakeKeychain _keychain = new();

    private readonly FakeProcessRunner _process = new();

    private readonly FakeClock _clock = new(Now);

    private const string Service = "Claude Code-credentials-01234567";

    private const string Account = "testaccount";

    private ClaudeKeychain Keychain(string? account = null, string? service = null) =>
        new([service ?? Service], account ?? Account, this._keychain, this._process, this._clock);

    // MARK: - The stored JSON

    [Fact]
    public void AStoredCredentialDecodes()
    {
        var expires = new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero);
        var credentials = ClaudeCredentials.Decode(FakeKeychain.Credential("token", expires, "max"));

        Assert.Equal("token", credentials.AccessToken);
        Assert.Equal(expires, credentials.ExpiresAt);
        Assert.Equal("max", credentials.SubscriptionType);
    }

    /// <summary>An emptied credential is a real state, and it is not "never signed in".</summary>
    [Fact]
    public void AnEmptiedCredentialReadsAsSignedOutByOwner()
    {
        var error = Assert.Throws<ReaderException>(() => ClaudeCredentials.Decode(
            """{"claudeAiOauth":{"accessToken":"","expiresAt":0}}"""u8.ToArray()));

        Assert.Equal(ReaderErrorKind.SignedOutByOwner, error.Error.Kind);
    }

    [Fact]
    public void AnUndecodableItemReadsAsNeedsAuth()
    {
        var error = Assert.Throws<ReaderException>(() => ClaudeCredentials.Decode("not json"u8.ToArray()));

        Assert.Equal(ReaderErrorKind.NeedsAuth, error.Error.Kind);
    }

    [Fact]
    public void AnItemWithoutAnExpiryReadsAsNeedsAuth()
    {
        var error = Assert.Throws<ReaderException>(() => ClaudeCredentials.Decode(
            """{"claudeAiOauth":{"accessToken":"token"}}"""u8.ToArray()));

        Assert.Equal(ReaderErrorKind.NeedsAuth, error.Error.Kind);
    }

    // MARK: - Reading one

    [Fact]
    public async Task NoItemUnderTheServiceIsNeedsAuth()
    {
        var error = await Assert.ThrowsAsync<ReaderException>(() => this.Keychain().LoadAsync());

        Assert.Equal(ReaderErrorKind.NeedsAuth, error.Error.Kind);
        Assert.Empty(this._keychain.Reads);
    }

    [Fact]
    public async Task AnItemIsRead()
    {
        this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now, "team");

        var credentials = await this.Keychain().LoadAsync();

        Assert.Equal("token", credentials.AccessToken);
        Assert.Equal("team", credentials.SubscriptionType);
    }

    /// <summary>
    /// Claude Code files a new item on every rotation; kSecMatchLimitOne gives no ordering guarantee
    /// across them, so the newest one wins.
    /// </summary>
    [Fact]
    public async Task TheNewestItemAcrossServicesWins()
    {
        var suffixed = ClaudeProfile.DefaultKeychainService + "-01234567";
        this._keychain.AddCredential(
            ClaudeProfile.DefaultKeychainService, "old-token", this._clock.Now.AddHours(8), this._clock.Now.AddHours(-6));
        this._keychain.AddCredential(suffixed, "new-token", this._clock.Now.AddHours(8), this._clock.Now.AddMinutes(-1));

        var credentials = await new ClaudeKeychain(
            [suffixed, ClaudeProfile.DefaultKeychainService],
            Account,
            this._keychain,
            this._process,
            this._clock).LoadAsync();

        Assert.Equal("new-token", credentials.AccessToken);
    }

    // MARK: - Refusals

    /// <summary>
    /// A refusal is retried through `/usr/bin/security`, which is Apple-signed and is how these items
    /// were written, so it is on their access list.
    /// </summary>
    [Fact]
    public async Task ARefusalIsRescuedThroughTheSecurityTool()
    {
        var item = this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now);
        item.ReadStatus = KeychainStatus.InteractionNotAllowed;
        this._process.Respond(
            "/usr/bin/security",
            0,
            System.Text.Encoding.UTF8.GetString(
                FakeKeychain.Credential("rescued-token", this._clock.Now.AddHours(8))) + "\n",
            arguments: ["find-generic-password", "-a", Account, "-s", Service, "-w"]);

        var credentials = await this.Keychain().LoadAsync();

        Assert.Equal("rescued-token", credentials.AccessToken);
        var request = Assert.Single(this._process.Requests);
        Assert.Equal("/usr/bin/security", request.FileName);
        Assert.False(request.CaptureStandardError);
    }

    /// <summary>A rescue that cannot run is the refusal it started as.</summary>
    [Fact]
    public async Task ARefusalWithNoRescueIsAccessDenied()
    {
        var item = this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now);
        item.ReadStatus = KeychainStatus.UserCanceled;
        this._process.Respond("/usr/bin/security", 1, string.Empty);

        var error = await Assert.ThrowsAsync<ReaderException>(() => this.Keychain().LoadAsync());

        Assert.Equal(ReaderErrorKind.AccessDenied, error.Error.Kind);
    }

    [Fact]
    public async Task ARefusalWhoseRescueIsEmptyIsAccessDenied()
    {
        var item = this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now);
        item.ReadStatus = KeychainStatus.AuthFailed;
        this._process.Respond("/usr/bin/security", 0, "\n");

        var error = await Assert.ThrowsAsync<ReaderException>(() => this.Keychain().LoadAsync());

        Assert.Equal(ReaderErrorKind.AccessDenied, error.Error.Kind);
    }

    /// <summary>In dark wake there is no UI possible; the credential is fine, the moment is not.</summary>
    [Fact]
    public async Task ATranientStatusIsCredentialExpired()
    {
        var item = this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now);
        item.ReadStatus = KeychainStatus.InDarkWake;

        var error = await Assert.ThrowsAsync<ReaderException>(() => this.Keychain().LoadAsync());

        Assert.Equal(ReaderErrorKind.CredentialExpired, error.Error.Kind);
    }

    /// <summary>"Not found" on the read means the item was deleted between enumeration and the read.</summary>
    [Fact]
    public async Task ANotFoundsStatusIsNeedsAuth()
    {
        var item = this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now);
        item.ReadStatus = KeychainStatus.ItemNotFound;

        var error = await Assert.ThrowsAsync<ReaderException>(() => this.Keychain().LoadAsync());

        Assert.Equal(ReaderErrorKind.NeedsAuth, error.Error.Kind);
    }

    // MARK: - Holding it

    /// <summary>Re-reading an unchanged item cannot produce a different answer, only another dialogue.</summary>
    [Fact]
    public async Task AnUnchangedItemIsReadOnce()
    {
        this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now.AddHours(-1));
        var keychain = this.Keychain();

        _ = await keychain.LoadAsync();
        _ = await keychain.LoadAsync();

        Assert.Equal(1, this._keychain.ReadCount);
    }

    /// <summary>An expired held token is re-read once the item has moved.</summary>
    [Fact]
    public async Task AnExpiredHeldTokenIsReadAgainWhenTheItemMoves()
    {
        var item = this._keychain.AddCredential(Service, "token", this._clock.Now.AddMinutes(1), this._clock.Now.AddHours(-1));
        var keychain = this.Keychain();

        _ = await keychain.LoadAsync();
        this._clock.Advance(TimeSpan.FromMinutes(5));
        item.Modified = this._clock.Now.AddMinutes(1);
        item.Data = FakeKeychain.Credential("rotated", this._clock.Now.AddHours(8));

        var credentials = await keychain.LoadAsync();

        Assert.Equal("rotated", credentials.AccessToken);
        Assert.Equal(2, this._keychain.ReadCount);
    }

    /// <summary>
    /// A *valid* held token is served from memory whatever the item does. That is the rule the Swift
    /// one states outright — the question is whether there is a good token in hand, not whether the
    /// item moved — and it is what keeps a rotation Claude Code has just written from costing another
    /// read of a credential that is still good.
    /// </summary>
    [Fact]
    public async Task AValidHeldTokenIsServedWithoutLookingAtTheItem()
    {
        var item = this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now.AddHours(-1));
        var keychain = this.Keychain();

        _ = await keychain.LoadAsync();
        item.Modified = this._clock.Now.AddMinutes(-1);
        item.Data = FakeKeychain.Credential("rotated", this._clock.Now.AddHours(8));

        var credentials = await keychain.LoadAsync();

        Assert.Equal("token", credentials.AccessToken);
        Assert.Equal(1, this._keychain.ReadCount);
    }

    /// <summary>
    /// A transient failure is not a verdict about the credential, so it is retried after the cache's
    /// own window even while the item has not moved — the field incident was one dark-wake read
    /// replayed for three hours.
    /// </summary>
    [Fact]
    public async Task ATransientFailureIsRetriedAfterItsWindow()
    {
        var item = this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now.AddHours(-1));
        item.ReadStatus = KeychainStatus.AuthorizationInternal;
        var keychain = this.Keychain();

        await Assert.ThrowsAsync<ReaderException>(() => keychain.LoadAsync());
        item.ReadStatus = KeychainStatus.Success;

        // Inside the window: the same answer, without asking macOS again.
        await Assert.ThrowsAsync<ReaderException>(() => keychain.LoadAsync());
        Assert.Equal(1, this._keychain.ReadCount);

        this._clock.Advance(TimeSpan.FromMinutes(5));

        var credentials = await keychain.LoadAsync();

        Assert.Equal("token", credentials.AccessToken);
        Assert.Equal(2, this._keychain.ReadCount);
    }

    /// <summary>
    /// A refusal is a verdict about this version of the item — asking again would put the same
    /// dialogue back in front of someone — so it is not retried while the stamp is unchanged.
    /// </summary>
    [Fact]
    public async Task ARefusalIsNotRetriedWhileTheItemIsUnchanged()
    {
        var item = this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now.AddHours(-1));
        item.ReadStatus = KeychainStatus.InteractionNotAllowed;
        var keychain = this.Keychain();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var error = await Assert.ThrowsAsync<ReaderException>(() => keychain.LoadAsync());
            Assert.Equal(ReaderErrorKind.AccessDenied, error.Error.Kind);
            this._clock.Advance(TimeSpan.FromMinutes(30));
        }

        // One read, and the three rescues it tried are not counted here: what must not happen is a
        // fourth attempt against macOS.
        Assert.Equal(1, this._keychain.ReadCount);
    }

    [Fact]
    public async Task ForgettingTheCacheReadsAgain()
    {
        this._keychain.AddCredential(Service, "token", this._clock.Now.AddHours(8), this._clock.Now.AddHours(-1));
        var keychain = this.Keychain();

        _ = await keychain.LoadAsync();
        keychain.ForgetCached();
        _ = await keychain.LoadAsync();

        Assert.Equal(2, this._keychain.ReadCount);
    }

    /// <summary>A profile with no item at all never reaches the data read.</summary>
    [Fact]
    public async Task NothingIsReadWhenThereIsNoItem()
    {
        _ = await Assert.ThrowsAsync<ReaderException>(() => this.Keychain().LoadAsync());

        Assert.Empty(this._keychain.Reads);
    }
}
