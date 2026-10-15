namespace QuotaDeck.Core.Tests;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Readers;

/// <summary>
/// Mirrors Codenotch <c>Tests/UsageResponseTests.swift</c> (<c>SupersedingStatusTests</c>,
/// <c>ExpiredCredentialTests</c>) and <c>Sources/Model/UsageStore.swift</c>'s <c>status(for:)</c>,
/// <c>degraded(provider:error:)</c> and <c>placeholder(_:)</c>: what each failure reads as, which
/// failures make the remembered reading untrue, and how the reading ages while a failure repeats.
/// </summary>
public class DegradationTests
{
    private static readonly DateTimeOffset Now = TestData.Now;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    [Fact]
    public void EveryReaderFailureMapsToItsStatus()
    {
        Assert.Equal(ProviderStatusKind.NeedsAuth, Degradation.StatusFor(ReaderError.NeedsAuth, Now).Kind);
        Assert.Equal(ProviderStatus.Stale(Now), Degradation.StatusFor(ReaderError.CredentialExpired, Now));
        Assert.Equal(ProviderStatus.Stale(Now), Degradation.StatusFor(ReaderError.RateLimited(), Now));
        Assert.Equal(ProviderStatusKind.SignedOutByOwner, Degradation.StatusFor(ReaderError.SignedOutByOwner, Now).Kind);
        Assert.Equal(ProviderStatusKind.AccessDenied, Degradation.StatusFor(ReaderError.AccessDenied, Now).Kind);
        Assert.Equal(ProviderStatus.Error("no response"), Degradation.StatusFor(ReaderError.TimedOut, Now));
        Assert.Equal(ProviderStatus.Unsupported("free plan"), Degradation.StatusFor(ReaderError.NothingMetered("free plan"), Now));
        Assert.Equal(ProviderStatus.Error("HTTP 500"), Degradation.StatusFor(ReaderError.BadResponse(500), Now));
        Assert.Equal(ProviderStatus.Error("rate_limit_error"), Degradation.StatusFor(ReaderError.ApiError("rate_limit_error"), Now));
    }

    [Fact]
    public void OnlyNeedsAuthAndUnsupportedSupersedeTheReading()
    {
        Assert.True(Degradation.SupersedesHistory(ProviderStatus.NeedsAuth));
        Assert.True(Degradation.SupersedesHistory(ProviderStatus.Unsupported("free plan")));

        Assert.False(Degradation.SupersedesHistory(ProviderStatus.AccessDenied));
        Assert.False(Degradation.SupersedesHistory(ProviderStatus.SignedOutByOwner));
        Assert.False(Degradation.SupersedesHistory(ProviderStatus.Stale(Now)));
        Assert.False(Degradation.SupersedesHistory(ProviderStatus.Error("HTTP 500")));
        Assert.False(Degradation.SupersedesHistory(ProviderStatus.Ok));
    }

    [Fact]
    public void ARecentLastGoodReadingSurvivesAFailureUndimmed()
    {
        var snapshot = TestData.SessionAndWeekly(0.73, 0.41);
        var lastGood = new ReaderMemory(snapshot, Now.AddMinutes(-5));

        var degraded = Degradation.Degrade("claude", "Claude", lastGood, ProviderStatus.Stale(Now), Now, StaleAfter);

        Assert.Equal(snapshot, degraded);
        Assert.Equal(ProviderStatusKind.Ok, degraded.Status.Kind);
    }

    [Fact]
    public void AnOldLastGoodReadingIsKeptButMarkedStale()
    {
        var snapshot = TestData.SessionAndWeekly(0.73, 0.41);
        var fetchedAt = Now.AddMinutes(-16);

        var degraded = Degradation.Degrade(
            "claude",
            "Claude",
            new ReaderMemory(snapshot, fetchedAt),
            ProviderStatus.Error("HTTP 500"),
            Now,
            StaleAfter);

        Assert.Equal(ProviderStatusKind.Stale, degraded.Status.Kind);
        Assert.Equal(fetchedAt, degraded.Status.StaleSince);
        Assert.Equal(0.73, degraded.UsedFraction);
    }

    /// <summary>The dimming boundary itself: fifteen minutes exactly is still recent.</summary>
    [Fact]
    public void TheDimmingBoundaryIsStrictlyPastFifteenMinutes()
    {
        var snapshot = TestData.SessionAndWeekly(0.73, 0.41);

        var exactly = Degradation.Degrade(
            "claude", "Claude", new ReaderMemory(snapshot, Now.AddMinutes(-15)), ProviderStatus.Stale(Now), Now, StaleAfter);
        Assert.Equal(ProviderStatusKind.Ok, exactly.Status.Kind);

        var past = Degradation.Degrade(
            "claude", "Claude", new ReaderMemory(snapshot, Now.AddMinutes(-15).AddSeconds(-1)), ProviderStatus.Stale(Now), Now, StaleAfter);
        Assert.Equal(ProviderStatusKind.Stale, past.Status.Kind);
    }

    [Fact]
    public void WithoutAReadingThePlaceholderCarriesTheStatus()
    {
        var placeholder = Degradation.Degrade("codex", "Codex", null, ProviderStatus.NeedsAuth, Now, StaleAfter);

        Assert.Equal("codex", placeholder.Id);
        Assert.Equal("Codex", placeholder.DisplayName);
        Assert.Empty(placeholder.Windows);
        Assert.Equal(ProviderStatus.NeedsAuth, placeholder.Status);
    }

    [Fact]
    public void ASupersedingStatusDropsTheRememberedReading()
    {
        var lastGood = new ReaderMemory(TestData.SessionAndWeekly(0.73, 0.41), Now.AddMinutes(-1));

        var placeholder = Degradation.Degrade("claude", "Claude", lastGood, ProviderStatus.NeedsAuth, Now, StaleAfter);

        Assert.Empty(placeholder.Windows);
        Assert.Equal(ProviderStatus.NeedsAuth, placeholder.Status);
    }

    /// <summary>A refusal says nothing about the reading: the credential exists and was valid, we were
    /// simply not let in to re-read it.</summary>
    [Fact]
    public void ARefusalKeepsTheReading()
    {
        var snapshot = TestData.SessionAndWeekly(0.73, 0.41);
        var lastGood = new ReaderMemory(snapshot, Now.AddMinutes(-1));

        var kept = Degradation.Degrade("claude", "Claude", lastGood, ProviderStatus.AccessDenied, Now, StaleAfter);

        Assert.Equal(snapshot, kept);
    }

    [Fact]
    public void ExceptionsMapThroughTheSameStatuses()
    {
        var mapped = Degradation.StatusFor(new ReaderException(ReaderError.NeedsAuth), Now);
        Assert.Equal(ProviderStatus.NeedsAuth, mapped);

        Assert.Equal(ProviderStatus.Error("boom"), Degradation.StatusFor(new InvalidOperationException("boom"), Now));

        // A message-less exception would show nothing at all, so its type name stands in.
        Assert.Equal(ProviderStatus.Error("MessageLessException"), Degradation.StatusFor(new MessageLessException(), Now));
    }

    private sealed class MessageLessException : Exception
    {
        public MessageLessException()
            : base(string.Empty)
        {
        }
    }
}
