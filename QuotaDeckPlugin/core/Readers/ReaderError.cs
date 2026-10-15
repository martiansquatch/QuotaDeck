namespace QuotaDeck.Core.Readers;

/// <summary>The failures a reader may report.</summary>
public enum ReaderErrorKind
{
    NeedsAuth,
    AccessDenied,
    CredentialExpired,
    SignedOutByOwner,
    TimedOut,
    BadResponse,
    ApiError,
    RateLimited,
    NothingMetered,
}

/// <summary>
/// The failure vocabulary a reader throws in.
///
/// Port of Codenotch <c>Sources/Providers/UsageProvider.swift</c> (<c>enum UsageProviderError</c>,
/// lines 130-172). The distinctions are the point:
/// <list type="bullet">
/// <item><description><see cref="NeedsAuth"/> means nobody ever signed in here, so there is nothing to show.</description></item>
/// <item><description><see cref="SignedOutByOwner"/> means somebody was signed in and had the credential taken out from under
/// them — the numbers taken before that are still the truth about the account, so the last reading survives.</description></item>
/// <item><description><see cref="AccessDenied"/> is a credential that exists and was refused; telling someone to sign in when they
/// are signed in and merely pressed Deny sends them to fix something that is not broken.</description></item>
/// <item><description><see cref="CredentialExpired"/> is a token that will refresh itself when the app that owns it next runs;
/// the last reading is still true, just old.</description></item>
/// <item><description><see cref="NothingMetered"/> is not an error at all: the account is readable and genuinely has no quota
/// being counted, and it must not be shown as one.</description></item>
/// </list>
/// </summary>
public sealed record ReaderError
{
    private ReaderError(
        ReaderErrorKind kind,
        int? status = null,
        string? name = null,
        TimeSpan? retryAfter = null,
        string? reason = null)
    {
        this.Kind = kind;
        this.Status = status;
        this.Name = name;
        this.RetryAfter = retryAfter;
        this.Reason = reason;
    }

    public ReaderErrorKind Kind { get; }

    /// <summary>The HTTP status, for <see cref="ReaderErrorKind.BadResponse"/>.</summary>
    public int? Status { get; }

    /// <summary>The endpoint's own name for its failure, for <see cref="ReaderErrorKind.ApiError"/>.</summary>
    public string? Name { get; }

    /// <summary>The server's own retry hint, when it gave one.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Why there is nothing to meter, for <see cref="ReaderErrorKind.NothingMetered"/>.</summary>
    public string? Reason { get; }

    /// <summary>No usable credential — the user has to sign in again.</summary>
    public static ReaderError NeedsAuth { get; } = new(ReaderErrorKind.NeedsAuth);

    /// <summary>The credential is there, and the platform refused to hand it over.</summary>
    public static ReaderError AccessDenied { get; } = new(ReaderErrorKind.AccessDenied);

    /// <summary>The credential is there but has expired; the app that owns it will refresh it.</summary>
    public static ReaderError CredentialExpired { get; } = new(ReaderErrorKind.CredentialExpired);

    /// <summary>The owning app emptied its own stored credential.</summary>
    public static ReaderError SignedOutByOwner { get; } = new(ReaderErrorKind.SignedOutByOwner);

    /// <summary>The fetch never came back inside the deadline. Says nothing about the account.</summary>
    public static ReaderError TimedOut { get; } = new(ReaderErrorKind.TimedOut);

    /// <summary>The endpoint answered, but not with anything we understand.</summary>
    public static ReaderError BadResponse(int status) => new(ReaderErrorKind.BadResponse, status: status);

    /// <summary>
    /// The endpoint answered with its own named business failure — some gateways report these under
    /// HTTP 200, so the name is the useful half of the answer and a status cannot carry it.
    /// </summary>
    public static ReaderError ApiError(string name) => new(ReaderErrorKind.ApiError, name: name);

    /// <summary>Asked to slow down. Carries the server's own retry hint when it gave one.</summary>
    public static ReaderError RateLimited(TimeSpan? retryAfter = null) =>
        new(ReaderErrorKind.RateLimited, retryAfter: retryAfter);

    /// <summary>
    /// The account is readable, but there is genuinely no quota being counted — a free plan that
    /// reports an included limit of zero, for instance. Not an error, and it must not be shown as
    /// one.
    /// </summary>
    public static ReaderError NothingMetered(string reason) =>
        new(ReaderErrorKind.NothingMetered, reason: reason);
}

/// <summary>
/// What a reader may throw. Every other exception is a bug being reported, and is carried as one.
/// </summary>
public sealed class ReaderException : Exception
{
    public ReaderException(ReaderError error)
        : base(Describe(error)) =>
        this.Error = error;

    public ReaderException(ReaderError error, Exception innerException)
        : base(Describe(error), innerException) =>
        this.Error = error;

    public ReaderError Error { get; }

    private static string Describe(ReaderError error) => error.Kind switch
    {
        ReaderErrorKind.BadResponse => $"HTTP {error.Status}",
        ReaderErrorKind.ApiError => error.Name ?? "api error",
        ReaderErrorKind.NothingMetered => error.Reason ?? "nothing metered",
        _ => error.Kind.ToString(),
    };
}
