namespace QuotaDeck.Core.Model;

/// <summary>The seven states a provider cell can be in.</summary>
public enum ProviderStatusKind
{
    Ok,
    Stale,
    NeedsAuth,
    SignedOutByOwner,
    AccessDenied,
    Unsupported,
    Error,
}

/// <summary>
/// What the numbers behind a snapshot are worth naming as.
///
/// Port of Codenotch <c>Sources/Model/UsageModel.swift</c> (<c>enum ProviderStatus</c> and its
/// <c>isStale</c> / <c>staleSince</c>). Modelled as one record with a kind and optional payloads
/// rather than a closed hierarchy so that equality, tests and (later) persistence stay simple; the
/// seven cases are exactly the Swift ones.
/// </summary>
public sealed record ProviderStatus
{
    private ProviderStatus(ProviderStatusKind kind, DateTimeOffset? since, string? message)
    {
        this.Kind = kind;
        this.Since = since;
        this.Message = message;
    }

    public ProviderStatusKind Kind { get; }

    /// <summary>When the reading behind this status was actually taken. Set for Stale only.</summary>
    public DateTimeOffset? Since { get; }

    /// <summary>The reason for Unsupported, or the server's own words for Error.</summary>
    public string? Message { get; }

    public static ProviderStatus Ok { get; } = new(ProviderStatusKind.Ok, null, null);

    public static ProviderStatus Stale(DateTimeOffset since) =>
        new(ProviderStatusKind.Stale, since, null);

    /// <summary>No usable credential — the user has to sign in again.</summary>
    public static ProviderStatus NeedsAuth { get; } = new(ProviderStatusKind.NeedsAuth, null, null);

    /// <summary>
    /// The owning app emptied its own stored credential. Distinct from NeedsAuth because the last
    /// reading is kept: somebody was signed in, worked, and had the credential taken out from under
    /// them — the numbers taken before that are still the truth about the account.
    /// </summary>
    public static ProviderStatus SignedOutByOwner { get; } =
        new(ProviderStatusKind.SignedOutByOwner, null, null);

    /// <summary>The credential exists and was refused. Not the same as being signed out.</summary>
    public static ProviderStatus AccessDenied { get; } =
        new(ProviderStatusKind.AccessDenied, null, null);

    /// <summary>The account is readable, but there is genuinely no quota being counted.</summary>
    public static ProviderStatus Unsupported(string reason) =>
        new(ProviderStatusKind.Unsupported, null, reason);

    public static ProviderStatus Error(string message) =>
        new(ProviderStatusKind.Error, null, message);

    public bool IsStale => this.Kind == ProviderStatusKind.Stale;

    public DateTimeOffset? StaleSince => this.Kind == ProviderStatusKind.Stale ? this.Since : null;
}
