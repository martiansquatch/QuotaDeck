namespace QuotaDeck.Core.Model;

using QuotaDeck.Core.Text;

/// <summary>
/// One provider's reading, as every surface consumes it.
///
/// Port of Codenotch <c>Sources/Model/UsageModel.swift</c> (<c>struct ProviderSnapshot</c> and its
/// derived properties: <c>headline</c>, <c>usedFraction</c>, <c>bandOverride</c>,
/// <c>fiveHourWindow</c>, <c>weeklyLimitWindow</c>, <c>weeklyWindow</c>, <c>weeklyFraction</c>,
/// <c>spentWeeklyBlock</c>, <c>isDepleted</c>, <c>blockingSpentWeek()</c>, <c>headlineText</c>,
/// <c>hasReading</c>). Local-runtime fields and provider-account plumbing are left out: QuotaDeck's
/// readers are all remote-account readers.
/// </summary>
public sealed record ProviderSnapshot
{
    public required string Id { get; init; }

    /// <summary>The provider's own name, or the one chosen for the account in settings.</summary>
    public required string DisplayName { get; init; }

    public Fidelity Fidelity { get; init; } = Fidelity.Official;

    public ProviderStatus Status { get; init; } = ProviderStatus.Ok;

    public IReadOnlyList<LimitWindow> Windows { get; init; } = [];

    /// <summary>
    /// Which window the headline face means, declared by the provider rather than left to position.
    /// Without it the headline is "whichever window happens to be first", and a window dropping out
    /// of the response silently promotes another one — the face keeps its shape and quietly changes
    /// its subject.
    /// </summary>
    public string? HeadlineId { get; init; }

    /// <summary>Which window the weekly figure draws, when it is switched on.</summary>
    public string? WeeklyId { get; init; }

    /// <summary>
    /// Set when something is blocked right now. Deliberately separate from the windows: it is not a
    /// measurement, it is a door being shut.
    /// </summary>
    public UsageBlock? Block { get; init; }

    /// <summary>The account's named tier, when the provider publishes one.</summary>
    public string? Plan { get; init; }

    /// <summary>Whose reading this is, when the provider can name the account.</summary>
    public string? AccountLabel { get; init; }

    /// <summary>
    /// When the reading was taken. Codenotch keeps this beside the snapshot (in its archive); the
    /// reader base stamps it on every fresh success, and the degradation rule reads it back.
    /// </summary>
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>
    /// The number on the face: the provider's declared primary window — for Claude, the current
    /// session. Not the most-constrained window: picking whichever limit is highest means the
    /// headline silently changes meaning session one minute, weekly the next, and disagrees with the
    /// vendor's own panel, which always leads with the session.
    ///
    /// If the declared window is missing from the response the face shows no reading rather than
    /// promoting a different one. A blank is honest; a weekly percentage wearing the session's place
    /// is not.
    /// </summary>
    public LimitWindow? Headline =>
        this.HeadlineId is null
            ? this.Windows.Count > 0 ? this.Windows[0] : null
            : this.Windows.FirstOrDefault(window => window.Id == this.HeadlineId);

    public double? UsedFraction => this.Headline?.UsedFraction;

    public UsageBand? BandOverride => this.Headline?.BandOverride;

    /// <summary>
    /// The five-hour window, where the provider has one: the headline when it is that window,
    /// otherwise the account's own. Never a grouped window — a group is one model's or one feature's
    /// allowance, kept off the face for the same reason it cannot stand for the account here.
    /// </summary>
    public LimitWindow? FiveHourWindow
    {
        get
        {
            if (this.Headline is { IsFiveHour: true } headline)
            {
                return headline;
            }

            return this.Windows.FirstOrDefault(window => window.IsFiveHour && window.Group is null);
        }
    }

    /// <summary>
    /// The provider-declared weekly allowance, whether or not another surface is already using it as
    /// its headline. Kept separate from <see cref="WeeklyWindow"/>: a compact secondary figure still
    /// needs to know that a valid weekly reading exists alongside its own five-hour one.
    /// </summary>
    public LimitWindow? WeeklyLimitWindow =>
        this.WeeklyId is null
            ? null
            : this.Windows.FirstOrDefault(window => window.Id == this.WeeklyId);

    /// <summary>
    /// The window a second ring draws, when one is switched on. Nil means this provider has no
    /// second window worth a ring, which is a real answer rather than a missing one — and nil when
    /// the headline is already drawing it, because two rings reporting the same number reads as a
    /// second fact that happens to agree rather than as the same fact twice.
    /// </summary>
    public LimitWindow? WeeklyWindow
    {
        get
        {
            if (this.WeeklyLimitWindow is not { } weekly)
            {
                return null;
            }

            return this.WeeklyId == this.HeadlineId ? null : weekly;
        }
    }

    /// <summary>
    /// Nil when there is no weekly window, or when the provider reports one without a denominator —
    /// the same rule the headline face follows.
    /// </summary>
    public double? WeeklyFraction => this.WeeklyWindow?.UsedFraction;

    /// <summary>
    /// The weekly allowance is spent, shutting the headline with it even where the headline still
    /// shows room. Nil unless the provider declared a weekly window and reported it spent — without
    /// a denominator there is no exhaustion to claim. A block the provider set itself is never
    /// replaced: its own pause outranks a derived one.
    /// </summary>
    public UsageBlock? SpentWeeklyBlock
    {
        get
        {
            if (this.Block is not null)
            {
                return null;
            }

            if (this.WeeklyLimitWindow is not { } weekly || weekly.UsedFraction is not { } used || used < 1)
            {
                return null;
            }

            return new UsageBlock { Reason = "Weekly limit reached", ResetsAt = weekly.ResetsAt, IsWeeklyExhaustion = true };
        }
    }

    /// <summary>
    /// Nothing left to spend: the headline window or the weekly allowance is at 0% remaining, so the
    /// account cannot be used until a window resets. Stale readings never count — an old number is
    /// not proof the account is empty now; it may hold tokens the last fetch has not seen yet.
    /// </summary>
    public bool IsDepleted =>
        !this.Status.IsStale
        && ((this.Headline?.UsedFraction ?? 0) >= 1 || (this.WeeklyLimitWindow?.UsedFraction ?? 0) >= 1);

    /// <summary>
    /// The snapshot with a spent week attached as a block, so every surface that reads
    /// <see cref="Block"/> treats the headline as shut. Applied on publication, before any derivation
    /// swaps the headline and weekly ids around.
    /// </summary>
    public ProviderSnapshot BlockingSpentWeek()
    {
        if (this.SpentWeeklyBlock is not { } spent)
        {
            return this;
        }

        return this with { Block = spent };
    }

    /// <summary>
    /// What the face prints under the ring: the used percentage, unless it is set to show what is
    /// left instead. An explicit used-text or count line has no remaining figure to show.
    /// </summary>
    public string HeadlineText(bool showingRemaining = false)
    {
        if (this.Headline is { PrefersUsedText: true, UsedText: { } preferred })
        {
            return preferred;
        }

        if (this.UsedFraction is { } usedFraction)
        {
            // The card's own left half, not one minus the fraction: it derives from the *rounded*
            // used figure, so the face and the card never disagree by a point.
            return showingRemaining
                ? Percent.Halves(usedFraction).Left + "%"
                : Percent.Text(usedFraction) + "%";
        }

        if (this.Headline?.Remaining is { } remaining)
        {
            return LimitWindow.Compact(remaining);
        }

        if (this.Headline?.UsedText is { } usedText)
        {
            return usedText;
        }

        if (this.Headline?.Used is { } used)
        {
            return LimitWindow.Compact(used);
        }

        return "—";
    }

    /// <summary>An absent reading must not be shown as measured zero usage.</summary>
    public bool HasReading => this.Windows.Count > 0;
}
