namespace QuotaDeck.Core.Faces;

using System.Text.Json;
using System.Text.Json.Serialization;
using QuotaDeck.Core.Model;

/// <summary>
/// The on-disk shape of a fixture: a hand-written provider reading the plugin can render without a
/// reader, a credential or a network.
///
/// The JSON is deliberately the flat form a person edits by hand —
/// <c>{ id, displayName, fidelity, status: { kind, since?, message? }, windows: [{ id, group?,
/// label, usedFraction?, resetsAt?, durationSeconds? }], headlineId?, weeklyId?, plan?,
/// accountLabel? }</c> — and every failure (missing file, bad JSON, unknown kind) reads as null,
/// never an exception: a broken fixture must cost a face, not the plugin.
/// </summary>
public static class FixtureSnapshot
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Parse fixture JSON. Null when the text is not a usable fixture.</summary>
    public static ProviderSnapshot? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FixtureDto>(json, Options)?.ToSnapshot();
        }
        catch
        {
            // Any malformed fixture is "no fixture"; the caller shows the not-installed face.
            return null;
        }
    }

    /// <summary>Read a fixture file. Null when it is missing or unusable.</summary>
    public static ProviderSnapshot? LoadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The fixture JSON for a snapshot — the inverse of <see cref="Parse"/>, for tests and tooling.</summary>
    public static string ToJson(ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(FixtureDto.From(snapshot), Options);
    }

    private sealed class FixtureDto
    {
        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        [JsonPropertyName("fidelity")]
        public string? FidelityName { get; set; }

        public StatusDto? Status { get; set; }

        public List<WindowDto>? Windows { get; set; }

        public string? HeadlineId { get; set; }

        public string? WeeklyId { get; set; }

        public string? Plan { get; set; }

        public string? AccountLabel { get; set; }

        public ProviderSnapshot? ToSnapshot()
        {
            if (string.IsNullOrWhiteSpace(this.Id) || string.IsNullOrWhiteSpace(this.DisplayName))
            {
                return null;
            }

            if (!TryFidelity(this.FidelityName, out var fidelity) || !TryStatus(this.Status, out var status))
            {
                return null;
            }

            var windows = new List<LimitWindow>();
            foreach (var window in this.Windows ?? [])
            {
                if (window?.ToWindow() is not { } parsed)
                {
                    return null;
                }

                windows.Add(parsed);
            }

            return new ProviderSnapshot
            {
                Id = this.Id,
                DisplayName = this.DisplayName,
                Fidelity = fidelity,
                Status = status,
                Windows = windows,
                HeadlineId = this.HeadlineId,
                WeeklyId = this.WeeklyId,
                Plan = this.Plan,
                AccountLabel = this.AccountLabel,
            };
        }

        public static FixtureDto From(ProviderSnapshot snapshot) => new()
        {
            Id = snapshot.Id,
            DisplayName = snapshot.DisplayName,
            FidelityName = snapshot.Fidelity.ToString().ToLowerInvariant(),
            Status = StatusDto.From(snapshot.Status),
            Windows = [.. snapshot.Windows.Select(WindowDto.From)],
            HeadlineId = snapshot.HeadlineId,
            WeeklyId = snapshot.WeeklyId,
            Plan = snapshot.Plan,
            AccountLabel = snapshot.AccountLabel,
        };

        private static bool TryFidelity(string? name, out Fidelity fidelity)
        {
            switch (name?.Trim().ToLowerInvariant())
            {
                case null or "" or "official":
                    fidelity = Fidelity.Official;
                    return true;
                case "derived":
                    fidelity = Fidelity.Derived;
                    return true;
                case "manual":
                    fidelity = Fidelity.Manual;
                    return true;
                default:
                    fidelity = Fidelity.Official;
                    return false;
            }
        }

        private static bool TryStatus(StatusDto? status, out ProviderStatus parsed)
        {
            parsed = ProviderStatus.Ok;
            if (status is null)
            {
                return true;
            }

            switch (status.Kind?.Trim().ToLowerInvariant())
            {
                case null or "" or "ok":
                    parsed = ProviderStatus.Ok;
                    return true;
                case "stale":
                    parsed = ProviderStatus.Stale(status.Since ?? DateTimeOffset.MinValue);
                    return true;
                case "needsauth":
                    parsed = ProviderStatus.NeedsAuth;
                    return true;
                case "signedoutbyowner":
                    parsed = ProviderStatus.SignedOutByOwner;
                    return true;
                case "accessdenied":
                    parsed = ProviderStatus.AccessDenied;
                    return true;
                case "unsupported":
                    parsed = ProviderStatus.Unsupported(status.Message ?? "");
                    return true;
                case "error":
                    parsed = ProviderStatus.Error(status.Message ?? "");
                    return true;
                default:
                    return false;
            }
        }
    }

    private sealed class StatusDto
    {
        public string? Kind { get; set; }

        public DateTimeOffset? Since { get; set; }

        public string? Message { get; set; }

        public static StatusDto From(ProviderStatus status) => new()
        {
            Kind = status.Kind.ToString().ToLowerInvariant(),
            Since = status.Since,
            Message = status.Message,
        };
    }

    private sealed class WindowDto
    {
        public string? Id { get; set; }

        public string? Group { get; set; }

        public string? Label { get; set; }

        public double? UsedFraction { get; set; }

        public DateTimeOffset? ResetsAt { get; set; }

        public double? DurationSeconds { get; set; }

        public LimitWindow? ToWindow()
        {
            if (string.IsNullOrWhiteSpace(this.Id) || string.IsNullOrWhiteSpace(this.Label))
            {
                return null;
            }

            return new LimitWindow
            {
                Id = this.Id,
                Group = this.Group,
                Label = this.Label,
                UsedFraction = this.UsedFraction,
                ResetsAt = this.ResetsAt,
                Duration = this.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            };
        }

        public static WindowDto From(LimitWindow window) => new()
        {
            Id = window.Id,
            Group = window.Group,
            Label = window.Label,
            UsedFraction = window.UsedFraction,
            ResetsAt = window.ResetsAt,
            DurationSeconds = window.Duration?.TotalSeconds,
        };
    }
}
