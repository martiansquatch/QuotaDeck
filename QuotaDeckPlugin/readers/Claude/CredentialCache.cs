namespace QuotaDeck.Readers.Claude;

// Port of Codenotch Sources/Providers/CredentialCache.swift (final class CredentialCache<Credential>:
// value(itemModifiedAt:reload:), held, forget()), with the reload made async because the refusal
// rescue behind it runs `/usr/bin/security`.

/// <summary>
/// Holds a borrowed credential so the keychain is read as rarely as possible.
///
/// Every read of the *data* is a chance for macOS to interrupt someone, and re-reading an unchanged
/// item cannot produce a different answer — it can only produce another dialogue. So the question is
/// whether the item has **changed**, which the injected probe answers without touching the data and
/// without prompting. Expiry is left to the caller, who is the one that knows what an expired token
/// means.
///
/// A failure is remembered too, and stamped with the same probe result: a refusal must not be
/// retried on every tick, and a *transient* failure (a Mac in dark wake) is not a verdict about the
/// item at all — it falls through to the retry window even while the item has not moved.
/// </summary>
public sealed class CredentialCache<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly Func<T, bool> _isExpired;
    private readonly Func<Exception, bool> _isPermanentFailure;
    private readonly TimeProvider _time;
    private readonly TimeSpan _recheckAfter;
    private readonly TimeSpan _retryAfterFailure;

    private T? _stored;
    private DateTimeOffset? _attemptedStamp;
    private DateTimeOffset? _attemptedAt;
    private Exception? _lastError;

    public CredentialCache(
        Func<T, bool> isExpired,
        Func<Exception, bool> isPermanentFailure,
        TimeProvider time,
        TimeSpan? recheckAfter = null,
        TimeSpan? retryAfterFailure = null)
    {
        ArgumentNullException.ThrowIfNull(isExpired);
        ArgumentNullException.ThrowIfNull(isPermanentFailure);
        ArgumentNullException.ThrowIfNull(time);

        this._isExpired = isExpired;
        this._isPermanentFailure = isPermanentFailure;
        this._time = time;
        this._recheckAfter = recheckAfter ?? TimeSpan.FromMinutes(5);
        this._retryAfterFailure = retryAfterFailure ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>What is already in hand, and nothing more — never asks macOS anything.</summary>
    public T? Held
    {
        get
        {
            lock (this._gate)
            {
                return this._stored;
            }
        }
    }

    /// <summary>
    /// The held credential, or a fresh one when the item behind it has moved.
    ///
    /// The probe must not read the item's data — the whole point is that it can be called freely.
    /// The reload runs outside the lock: it can block on the keychain, and holding a lock across that
    /// would stall every other caller behind it.
    /// </summary>
    public async Task<T> ValueAsync(Func<DateTimeOffset?> itemModifiedAt, Func<Task<T>> reload)
    {
        ArgumentNullException.ThrowIfNull(itemModifiedAt);
        ArgumentNullException.ThrowIfNull(reload);

        T? held;
        DateTimeOffset? askedStamp;
        DateTimeOffset? askedAt;
        Exception? failure;
        lock (this._gate)
        {
            held = this._stored;
            askedStamp = this._attemptedStamp;
            askedAt = this._attemptedAt;
            failure = this._lastError;
        }

        // Still good: nothing to decide.
        if (held is not null && !this._isExpired(held))
        {
            return held;
        }

        // Expired, or nothing held at all. Ask the cheap question first.
        var current = itemModifiedAt();

        // Have we already put this exact version of the item to macOS?
        bool alreadyAsked;
        if (current is { } probe && askedStamp is { } stamp && probe == stamp)
        {
            if (failure is not null && !this._isPermanentFailure(failure))
            {
                // A transient failure is not a verdict about what the item holds, so it waits out
                // the retry window even though the item has not moved.
                alreadyAsked = askedAt is { } at && this._time.GetUtcNow() - at < this._retryAfterFailure;
            }
            else
            {
                alreadyAsked = true;
            }
        }
        else if (current is not null && askedStamp is not null)
        {
            // Different versions: nothing to reuse, whatever the last answer was.
            alreadyAsked = false;
        }
        else if (askedAt is { } reportedAt)
        {
            // Nothing to compare against, so wait it out instead — and wait longer after a refusal,
            // because a refusal retried on a timer is precisely what this exists to stop.
            var wait = failure is null ? this._recheckAfter : this._retryAfterFailure;
            alreadyAsked = this._time.GetUtcNow() - reportedAt < wait;
        }
        else
        {
            alreadyAsked = false;
        }

        if (alreadyAsked)
        {
            if (held is not null)
            {
                return held;
            }

            // Nothing held and macOS has already refused this item: give back the same answer rather
            // than raising the same dialogue again.
            if (failure is not null)
            {
                throw failure;
            }
        }

        try
        {
            var fresh = await reload().ConfigureAwait(false);
            lock (this._gate)
            {
                this._stored = fresh;
                this._attemptedStamp = current;
                this._attemptedAt = this._time.GetUtcNow();
                this._lastError = null;
            }

            return fresh;
        }
        catch (Exception exception)
        {
            lock (this._gate)
            {
                // The attempt is recorded, the credential is not: a failure must never be handed
                // back as if it were one.
                this._attemptedStamp = current;
                this._attemptedAt = this._time.GetUtcNow();
                this._lastError = exception;
            }

            throw;
        }
    }

    /// <summary>
    /// Drop what is held, so the next read goes to the keychain for real. Called when the server
    /// rejects the credential — the one signal the copy in hand is wrong despite not having expired,
    /// which is what signing into a different account looks like from here.
    /// </summary>
    public void Forget()
    {
        lock (this._gate)
        {
            this._stored = null;
            this._attemptedStamp = null;
            this._attemptedAt = null;
            this._lastError = null;
        }
    }
}
