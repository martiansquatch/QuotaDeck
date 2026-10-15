namespace QuotaDeck.Readers.Tests.Support;

/// <summary>
/// A clock the test moves by hand. Timers still come from the system provider: the reader base and
/// the process runner start real ones, and those delays have to actually schedule.
/// </summary>
public sealed class FakeClock : TimeProvider
{
    public FakeClock(DateTimeOffset now) => this.Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => this.Now;

    public void Advance(TimeSpan by) => this.Now = this.Now.Add(by);

    public void Advance(double seconds) => this.Advance(TimeSpan.FromSeconds(seconds));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state, dueTime, period);
}
