namespace QuotaDeck.Core.Readers;

using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaDeck.Core.Model;

/// <summary>
/// The reader memory as one JSON file under the plugin's data directory.
///
/// The file's shape follows Codenotch's <c>UsageArchive</c> entries
/// (<c>Sources/Model/UsageArchive.swift</c>, <c>struct Entry</c> and <c>load()</c>/<c>save(_:)</c>):
/// a reading is stored with its windows and the moment it was taken, and it is restored marked stale
/// for that moment rather than trusting whatever status was current when it was written. The
/// back-off deadline is stored beside it, for the reason given on <see cref="IReaderStateStore"/>.
/// </summary>
public sealed class JsonFileReaderStateStore : IReaderStateStore
{
    private readonly string _filePath;
    private readonly object _gate = new();

    public JsonFileReaderStateStore(string filePath) => this._filePath = filePath;

    public ReaderMemory? Load(string readerId)
    {
        lock (this._gate)
        {
            var entry = this.ReadFile().Readings.GetValueOrDefault(readerId);
            return entry?.ToMemory();
        }
    }

    public void Save(string readerId, ReaderMemory memory)
    {
        lock (this._gate)
        {
            var file = this.ReadFile();
            file.Readings[readerId] = Entry.From(memory);
            this.WriteFile(file);
        }
    }

    public void Forget(string readerId)
    {
        lock (this._gate)
        {
            var file = this.ReadFile();
            if (file.Readings.Remove(readerId))
            {
                this.WriteFile(file);
            }
        }
    }

    public DateTimeOffset? LoadBackoffUntil(string readerId)
    {
        lock (this._gate)
        {
            return this.ReadFile().BackoffUntil.GetValueOrDefault(readerId);
        }
    }

    public void SaveBackoffUntil(string readerId, DateTimeOffset? until)
    {
        lock (this._gate)
        {
            var file = this.ReadFile();
            if (until is { } deadline)
            {
                file.BackoffUntil[readerId] = deadline;
            }
            else if (!file.BackoffUntil.Remove(readerId))
            {
                return;
            }

            this.WriteFile(file);
        }
    }

    private ReaderStateFile ReadFile()
    {
        try
        {
            if (!File.Exists(this._filePath))
            {
                return new ReaderStateFile();
            }

            return JsonSerializer.Deserialize<ReaderStateFile>(File.ReadAllText(this._filePath), JsonOptions)
                ?? new ReaderStateFile();
        }
        catch
        {
            // A reader must never fail because memory is unreadable; it starts from nothing.
            return new ReaderStateFile();
        }
    }

    private void WriteFile(ReaderStateFile file)
    {
        try
        {
            var directory = Path.GetDirectoryName(this._filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written beside the real file and moved over it, so a crash mid-write cannot leave a
            // half-file where the memory was.
            var temporary = this._filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temporary, this._filePath, overwrite: true);
        }
        catch
        {
            // Losing memory is survivable; failing a fetch over it is not.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class ReaderStateFile
    {
        [JsonPropertyName("readings")]
        public Dictionary<string, Entry> Readings { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("backoffUntil")]
        public Dictionary<string, DateTimeOffset> BackoffUntil { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class Entry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("fidelity")]
        public string FidelityName { get; set; } = "official";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "ok";

        [JsonPropertyName("windows")]
        public List<WindowEntry> Windows { get; set; } = [];

        [JsonPropertyName("fetchedAt")]
        public DateTimeOffset FetchedAt { get; set; }

        [JsonPropertyName("headlineId")]
        public string? HeadlineId { get; set; }

        [JsonPropertyName("weeklyId")]
        public string? WeeklyId { get; set; }

        [JsonPropertyName("plan")]
        public string? Plan { get; set; }

        [JsonPropertyName("accountLabel")]
        public string? AccountLabel { get; set; }

        public static Entry From(ReaderMemory memory)
        {
            var snapshot = memory.Snapshot;
            return new Entry
            {
                Id = snapshot.Id,
                DisplayName = snapshot.DisplayName,
                FidelityName = snapshot.Fidelity.ToString().ToLowerInvariant(),
                Status = snapshot.Status.Kind.ToString().ToLowerInvariant(),
                Windows = [.. snapshot.Windows.Select(WindowEntry.From)],
                FetchedAt = memory.FetchedAt,
                HeadlineId = snapshot.HeadlineId,
                WeeklyId = snapshot.WeeklyId,
                Plan = snapshot.Plan,
                AccountLabel = snapshot.AccountLabel,
            };
        }

        public ReaderMemory ToMemory()
        {
            var snapshot = new ProviderSnapshot
            {
                Id = this.Id,
                DisplayName = this.DisplayName,
                Fidelity = this.FidelityName.Equals("derived", StringComparison.OrdinalIgnoreCase)
                    ? Fidelity.Derived
                    : this.FidelityName.Equals("manual", StringComparison.OrdinalIgnoreCase)
                        ? Fidelity.Manual
                        : Fidelity.Official,

                // A remembered reading is dated, not trusted: its age is the honest part.
                Status = ProviderStatus.Stale(this.FetchedAt),
                Windows = [.. this.Windows.Select(window => window.ToWindow())],
                HeadlineId = this.HeadlineId,
                WeeklyId = this.WeeklyId,
                Plan = this.Plan,
                AccountLabel = this.AccountLabel,
                FetchedAt = this.FetchedAt,
            };

            return new ReaderMemory(snapshot, this.FetchedAt);
        }
    }

    private sealed class WindowEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("group")]
        public string? Group { get; set; }

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("usedFraction")]
        public double? UsedFraction { get; set; }

        [JsonPropertyName("remaining")]
        public int? Remaining { get; set; }

        [JsonPropertyName("used")]
        public int? Used { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("usedText")]
        public string? UsedText { get; set; }

        [JsonPropertyName("resetsAt")]
        public DateTimeOffset? ResetsAt { get; set; }

        [JsonPropertyName("durationSeconds")]
        public double? DurationSeconds { get; set; }

        [JsonPropertyName("bandOverride")]
        public string? BandOverride { get; set; }

        [JsonPropertyName("prefersUsedText")]
        public bool PrefersUsedText { get; set; }

        [JsonPropertyName("money")]
        public MoneyEntry? Money { get; set; }

        public static WindowEntry From(LimitWindow window) => new()
        {
            Id = window.Id,
            Group = window.Group,
            Label = window.Label,
            UsedFraction = window.UsedFraction,
            Remaining = window.Remaining,
            Used = window.Used,
            Detail = window.Detail,
            UsedText = window.UsedText,
            ResetsAt = window.ResetsAt,
            DurationSeconds = window.Duration?.TotalSeconds,
            BandOverride = window.BandOverride?.ToString().ToLowerInvariant(),
            PrefersUsedText = window.PrefersUsedText,
            Money = window.Money is { } money ? new MoneyEntry { Currency = money.Currency, Spent = money.Spent, Remaining = money.Remaining } : null,
        };

        public LimitWindow ToWindow() => new()
        {
            Id = this.Id,
            Group = this.Group,
            Label = this.Label,
            UsedFraction = this.UsedFraction,
            Remaining = this.Remaining,
            Used = this.Used,
            Detail = this.Detail,
            UsedText = this.UsedText,
            ResetsAt = this.ResetsAt,
            Duration = this.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            BandOverride = this.BandOverride switch
            {
                "ample" => UsageBand.Ample,
                "watch" => UsageBand.Watch,
                "critical" => UsageBand.Critical,
                "exhausted" => UsageBand.Exhausted,
                _ => null,
            },
            PrefersUsedText = this.PrefersUsedText,
            Money = this.Money is { } money ? new UsageMoneyBreakdown(money.Currency, money.Spent, money.Remaining) : null,
        };
    }

    private sealed class MoneyEntry
    {
        [JsonPropertyName("currency")]
        public string Currency { get; set; } = "";

        [JsonPropertyName("spent")]
        public double Spent { get; set; }

        [JsonPropertyName("remaining")]
        public double Remaining { get; set; }
    }
}
