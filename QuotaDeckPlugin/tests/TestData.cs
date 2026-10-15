namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Policy;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Settings;

/// <summary>Snapshots and windows built by hand, for tests that need a shape rather than a reader.</summary>
internal static class TestData
{
    /// <summary>A fixed instant. No test in this project reads the wall clock.</summary>
    internal static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    internal static LimitWindow Window(
        string id = "session",
        string label = "Current session",
        double? usedFraction = null,
        string? group = null,
        DateTimeOffset? resetsAt = null,
        TimeSpan? duration = null,
        int? remaining = null,
        int? used = null,
        string? detail = null,
        string? usedText = null,
        bool prefersUsedText = false,
        UsageBand? bandOverride = null,
        UsageMoneyBreakdown? money = null) =>
        new()
        {
            Id = id,
            Label = label,
            Group = group,
            UsedFraction = usedFraction,
            Remaining = remaining,
            Used = used,
            Detail = detail,
            UsedText = usedText,
            PrefersUsedText = prefersUsedText,
            ResetsAt = resetsAt,
            Duration = duration,
            BandOverride = bandOverride,
            Money = money,
        };

    internal static ProviderSnapshot Snapshot(
        string id = "claude",
        string displayName = "Claude",
        IReadOnlyList<LimitWindow>? windows = null,
        string? headlineId = "session",
        string? weeklyId = null,
        ProviderStatus? status = null,
        Fidelity fidelity = Fidelity.Official,
        UsageBlock? block = null,
        string? plan = null,
        DateTimeOffset? fetchedAt = null) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Fidelity = fidelity,
            Status = status ?? ProviderStatus.Ok,
            Windows = windows ?? [Window(usedFraction: 0.5)],
            HeadlineId = headlineId,
            WeeklyId = weeklyId,
            Block = block,
            Plan = plan,
            FetchedAt = fetchedAt ?? Now,
        };

    /// <summary>A headline session window plus a weekly one, the shape most readers return.</summary>
    internal static ProviderSnapshot SessionAndWeekly(
        double session,
        double weekly,
        DateTimeOffset? sessionResetsAt = null,
        DateTimeOffset? weeklyResetsAt = null,
        UsageBlock? block = null) =>
        Snapshot(
            windows:
            [
                Window(id: "session", label: "5-hour limit", usedFraction: session, resetsAt: sessionResetsAt),
                Window(id: "weekly", label: "Weekly limit", usedFraction: weekly, resetsAt: weeklyResetsAt),
            ],
            headlineId: "session",
            weeklyId: "weekly",
            block: block);
}
