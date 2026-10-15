namespace QuotaDeck.Core.Readers;

using QuotaDeck.Core.Model;

/// <summary>The reader memory kept in RAM only. For tests, and for a run that need not survive it.</summary>
public sealed class InMemoryReaderStateStore : IReaderStateStore
{
    private readonly Dictionary<string, ReaderMemory> _readings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _backoffUntil = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ReaderMemory? Load(string readerId)
    {
        lock (this._gate)
        {
            return this._readings.TryGetValue(readerId, out var memory) ? memory : null;
        }
    }

    public void Save(string readerId, ReaderMemory memory)
    {
        lock (this._gate)
        {
            this._readings[readerId] = memory;
        }
    }

    public void Forget(string readerId)
    {
        lock (this._gate)
        {
            this._readings.Remove(readerId);
        }
    }

    public DateTimeOffset? LoadBackoffUntil(string readerId)
    {
        lock (this._gate)
        {
            return this._backoffUntil.TryGetValue(readerId, out var until) ? until : null;
        }
    }

    public void SaveBackoffUntil(string readerId, DateTimeOffset? until)
    {
        lock (this._gate)
        {
            if (until is { } deadline)
            {
                this._backoffUntil[readerId] = deadline;
            }
            else
            {
                this._backoffUntil.Remove(readerId);
            }
        }
    }

    /// <summary>Test seam: what is remembered right now, without inventing a reader.</summary>
    public ReaderMemory? Peek(string readerId) => this.Load(readerId);

    /// <summary>Test seam: seed a remembered reading without a fetch.</summary>
    public void Seed(string readerId, ProviderSnapshot snapshot, DateTimeOffset fetchedAt) =>
        this.Save(readerId, new ReaderMemory(snapshot, fetchedAt));
}
