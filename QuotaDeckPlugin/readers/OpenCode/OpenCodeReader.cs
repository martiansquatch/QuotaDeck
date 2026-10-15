namespace QuotaDeck.Readers.OpenCode;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;

/// <summary>
/// Reads OpenCode Go plan usage through <see cref="OpenCodeProvider"/> and publishes it as a
/// snapshot.
///
/// Port of Codenotch <c>Sources/Providers/OpenCodeProvider.swift</c> (<c>fetchSnapshot()</c> lines
/// 61-105: the credential re-read, the parse, the snapshot shape and the constants). The rate-limit
/// bookkeeping that lives beside it in Swift — the hold-off check, the consecutive counter, the
/// back-off save — belongs to <see cref="UsageReaderBase"/> here, the same bargain every QuotaDeck
/// reader makes.
///
/// The credential is re-read from disk on every fetch: an ordinary file, not a keychain item, so
/// reading it puts no prompt in front of anyone. The files come from OpenCode's own data directory
/// (<c>$XDG_DATA_HOME/opencode</c> or <c>~/.local/share/opencode</c>), injectable for tests, and
/// never from <see cref="ReaderContext.DataDirectory"/>, which is QuotaDeck's own store.
/// </summary>
public sealed class OpenCodeReader : UsageReaderBase
{
    public const string ReaderId = "opencode";

    public const string ReaderDisplayName = "OpenCode";

    /// <summary>The plan the Go credential's windows meter.</summary>
    public const string PlanName = "Go";

    private readonly ReaderContext _context;
    private readonly TimeProvider _time;
    private readonly string? _dataDirectory;

    /// <param name="context">The services every reader is handed.</param>
    /// <param name="dataDirectory">
    /// OpenCode's data directory, for tests; null resolves <c>$XDG_DATA_HOME/opencode</c> or
    /// <c>~/.local/share/opencode</c> on each fetch, like the Swift computed property.
    /// </param>
    public OpenCodeReader(ReaderContext context, string? dataDirectory = null)
        : base(context.Config, context.StateStore, context.TimeProvider)
    {
        ArgumentNullException.ThrowIfNull(context);

        this._context = context;
        this._time = context.TimeProvider;
        this._dataDirectory = dataDirectory;
    }

    public override string Id => ReaderId;

    public override string DisplayName => ReaderDisplayName;

    protected override async Task<ProviderSnapshot> ReadAsync(Freshness freshness, CancellationToken cancellationToken)
    {
        // Re-read on every fetch; a machine with no sign-in we may claim is a sign-out, not an error
        // to paper over.
        var directory = this._dataDirectory ?? OpenCodeCredentials.ResolveDataDirectory();
        var credential = OpenCodeCredentials.Load(directory)
            ?? throw new ReaderException(ReaderError.NeedsAuth);

        var body = await OpenCodeProvider
            .FetchUsageAsync(this._context.Http, this._time, credential, cancellationToken)
            .ConfigureAwait(false);
        var windows = OpenCodeUsage.Windows(body);

        return new ProviderSnapshot
        {
            Id = this.Id,
            DisplayName = this.DisplayName,
            Fidelity = Fidelity.Official,
            Status = ProviderStatus.Ok,
            Windows = windows,
            // The ring means the rolling window; the weekly figure draws on its own. The provider
            // declares both rather than leaving the face to "whichever window happens to be first".
            HeadlineId = "rolling",
            WeeklyId = "weekly",
            Plan = PlanName,
            // The key carries no address, so there is no account label to show; Swift's account()
            // source line ("OpenCode") has no counterpart in this reader contract.
        };
    }
}
