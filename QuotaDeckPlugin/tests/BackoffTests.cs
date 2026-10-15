namespace QuotaDeck.Core.Tests;

using System.Globalization;
using QuotaDeck.Core.Policy;

/// <summary>
/// The retry arithmetic of Codenotch <c>Sources/Providers/ClaudeOAuthProvider.swift</c>
/// (<c>backoff(forAttempt:retryAfter:)</c>, <c>retryAfter(from:)</c>,
/// <c>shouldHoldOff(until:slack:now:)</c>): the wait doubles from a minute and stops at fifteen, the
/// server's own hint may only raise the floor, and a hold-off is respected only while more than a
/// second of it remains.
/// </summary>
public class BackoffTests
{
    [Fact]
    public void TheWaitDoublesFromAMinuteAndStopsAtFifteenMinutes()
    {
        Assert.Equal(60d, Backoff.Compute(0).TotalSeconds);
        Assert.Equal(120d, Backoff.Compute(1).TotalSeconds);
        Assert.Equal(240d, Backoff.Compute(2).TotalSeconds);
        Assert.Equal(480d, Backoff.Compute(3).TotalSeconds);

        // 60 * 2^4 is 960, which the ceiling rounds down to fifteen minutes.
        Assert.Equal(900d, Backoff.Compute(4).TotalSeconds);
        Assert.Equal(900d, Backoff.Compute(5).TotalSeconds);
        Assert.Equal(900d, Backoff.Compute(6).TotalSeconds);

        // A negative count is the first rung, not a negative wait.
        Assert.Equal(60d, Backoff.Compute(-1).TotalSeconds);
    }

    [Fact]
    public void TheServersHintCanOnlyRaiseTheFloor()
    {
        // Retry-After 1200 beats the schedule's own minute.
        Assert.Equal(1200d, Backoff.Compute(0, TimeSpan.FromSeconds(1200)).TotalSeconds);

        // A hint above the schedule's ceiling is honoured in full, not clipped to it: only our
        // schedule is capped, never the server's hint.
        Assert.Equal(300d, Backoff.Compute(0, TimeSpan.FromSeconds(300)).TotalSeconds);
        Assert.Equal(600d, Backoff.Compute(0, TimeSpan.FromSeconds(600)).TotalSeconds);
        Assert.Equal(3600d, Backoff.Compute(0, TimeSpan.FromSeconds(3600)).TotalSeconds);
        Assert.Equal(3600d, Backoff.Compute(4, TimeSpan.FromSeconds(3600)).TotalSeconds);

        // A hint below our floor is ignored: obeying `Retry-After: 0` literally is what keeps you
        // rate limited.
        Assert.Equal(60d, Backoff.Compute(0, TimeSpan.Zero).TotalSeconds);
        Assert.Equal(900d, Backoff.Compute(5, TimeSpan.FromSeconds(30)).TotalSeconds);
        Assert.Equal(60d, Backoff.Compute(0).TotalSeconds);
    }

    [Fact]
    public void AholdOffIsRespectedOnlyWhileMoreThanASecondRemains()
    {
        var now = TestData.Now;

        Assert.True(Backoff.ShouldHoldOff(now.AddSeconds(45), now));
        Assert.True(Backoff.ShouldHoldOff(now.AddSeconds(2), now));

        // The slack: a deadline a moment away would silently stretch this poll into the next one.
        Assert.False(Backoff.ShouldHoldOff(now.AddSeconds(1), now));
        Assert.False(Backoff.ShouldHoldOff(now.AddMilliseconds(999), now));
        Assert.False(Backoff.ShouldHoldOff(now.AddSeconds(-5), now));
        Assert.False(Backoff.ShouldHoldOff(null, now));

        // The slack is a parameter, and a larger one refuses a short penalty.
        Assert.False(Backoff.ShouldHoldOff(now.AddSeconds(45), now, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void RetryAfterParsesIntegerSeconds()
    {
        var now = TestData.Now;

        Assert.Equal(120d, Backoff.RetryAfter("120", now)!.Value.TotalSeconds);
        Assert.Equal(120d, Backoff.RetryAfter("  120  ", now)!.Value.TotalSeconds);
        Assert.Equal(0d, Backoff.RetryAfter("0", now)!.Value.TotalSeconds);

        // Never negative: a date that has passed is a deadline of now.
        Assert.Equal(0d, Backoff.RetryAfter("-30", now)!.Value.TotalSeconds);
    }

    [Fact]
    public void RetryAfterParsesAnHttpDate()
    {
        var now = DateTimeOffset.Parse("Wed, 21 Oct 2015 07:28:00 GMT", CultureInfo.InvariantCulture);

        Assert.Equal(120d, Backoff.RetryAfter("Wed, 21 Oct 2015 07:30:00 GMT", now)!.Value.TotalSeconds);
        Assert.Equal(0d, Backoff.RetryAfter("Wed, 21 Oct 2015 07:00:00 GMT", now)!.Value.TotalSeconds);

        // A date well past the schedule's ceiling is still honoured (the hint is not capped)...
        Assert.Equal(7200d, Backoff.RetryAfter("Wed, 21 Oct 2015 09:28:00 GMT", now)!.Value.TotalSeconds);

        // ...up to the 24-hour sanity bound that keeps a TimeSpan constructible.
        Assert.Equal(Backoff.MaxHintSeconds, Backoff.RetryAfter("Fri, 23 Oct 2015 07:28:00 GMT", now)!.Value.TotalSeconds);
    }

    /// <summary>A broken or hostile header must not become a throw or an endless hold-off: the
    /// non-finite spellings .NET parses ("Infinity", "NaN") are not a hint, and a finite giant is
    /// bounded at 24 hours purely so the TimeSpan can be built.</summary>
    [Fact]
    public void RetryAfterRejectsNonFiniteValuesAndBoundsFiniteOnes()
    {
        Assert.Null(Backoff.RetryAfter("Infinity", TestData.Now));
        Assert.Null(Backoff.RetryAfter("-Infinity", TestData.Now));
        Assert.Null(Backoff.RetryAfter("NaN", TestData.Now));

        Assert.Equal(Backoff.MaxHintSeconds, Backoff.RetryAfter("1e300", TestData.Now)!.Value.TotalSeconds);
        Assert.Equal(120d, Backoff.RetryAfter("120", TestData.Now)!.Value.TotalSeconds);
        Assert.Equal(3600d, Backoff.RetryAfter("3600", TestData.Now)!.Value.TotalSeconds);
    }

    [Fact]
    public void RetryAfterRejectsAnythingElse()
    {
        Assert.Null(Backoff.RetryAfter(null, TestData.Now));
        Assert.Null(Backoff.RetryAfter("", TestData.Now));
        Assert.Null(Backoff.RetryAfter("   ", TestData.Now));
        Assert.Null(Backoff.RetryAfter("soon", TestData.Now));
        Assert.Null(Backoff.RetryAfter("Wed, 21 Oct 2015", TestData.Now));
    }
}
