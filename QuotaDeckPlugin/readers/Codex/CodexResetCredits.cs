namespace QuotaDeck.Readers.Codex;

/// <summary>One banked rate-limit reset.</summary>
public sealed record CodexResetCredit(string Id, string Status, DateTimeOffset? ExpiresAt);

/// <summary>
/// The ChatGPT backend's list of unused rate-limit resets.
///
/// Port of Codenotch <c>Sources/Model/UsageResetCredits.swift</c>
/// (<c>struct UsageResetCredits</c>: fields, <c>available</c> at lines 31-34, <c>nextExpiry</c> at
/// line 47) with the decode half in <c>Sources/Providers/CodexUsage.swift</c>
/// (<c>resetCredits(from:)</c>, lines 369-386).
///
/// The count is its own field because the details list can be capped ("Codex can return a truncated
/// list of credits"), so it is trusted over <see cref="Credits"/>.Count.
/// </summary>
public sealed record CodexResetCredits(int AvailableCount, IReadOnlyList<CodexResetCredit> Credits)
{
    /// <summary>The credits still available, soonest expiry first (UsageResetCredits.swift:31-34).</summary>
    public IReadOnlyList<CodexResetCredit> Available =>
        this.Credits
            .Where(credit => credit.Status == "available")
            .OrderBy(credit => credit.ExpiresAt ?? DateTimeOffset.MaxValue)
            .ToList();

    /// <summary>The soonest expiry among the available credits, not the first payload row (UsageResetCredits.swift:47).</summary>
    public DateTimeOffset? NextExpiry
    {
        get
        {
            DateTimeOffset? soonest = null;
            foreach (var credit in this.Credits)
            {
                if (credit.Status != "available" || credit.ExpiresAt is not { } expiry)
                {
                    continue;
                }

                if (soonest is null || expiry < soonest)
                {
                    soonest = expiry;
                }
            }

            return soonest;
        }
    }
}
