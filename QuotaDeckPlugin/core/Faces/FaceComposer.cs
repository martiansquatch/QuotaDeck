namespace QuotaDeck.Core.Faces;

using QuotaDeck.Core.Model;
using QuotaDeck.Core.Settings;
using QuotaDeck.Core.Text;

/// <summary>
/// Turns a snapshot and the settings into a face.
///
/// Pure and total: every rule a key face needs lives here rather than in the renderer — the
/// remaining/used flip and the band come from <see cref="FaceFigures"/>, the corner is the
/// settings' countdown of <see cref="ResetCopy"/>, and the words for a provider with no reading are
/// a short port of Codenotch <c>Sources/Model/UsageModel.swift</c>'s <c>statusMessage</c> (lines
/// 603-631, which drops the long auth sentences for the keypad's width).
/// </summary>
public static class FaceComposer
{
    public static FaceModel Compose(ProviderSnapshot? snapshot, QuotaDeckSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (snapshot is null)
        {
            // No snapshot at all — a module with nothing ever read. The face says so and stays
            // undimmed: there is no stale reading to be quiet about.
            return new FaceModel(null, null, null, string.Empty, null, false, "…", false);
        }

        var figures = FaceFigures.For(snapshot, settings);
        var countdown = snapshot.Headline?.ResetsAt is { } resetsAt
            ? Countdown(resetsAt, now, settings.CountdownStyle)
            : null;

        return new FaceModel(
            figures.Headline,
            figures.Secondary,
            countdown is null ? null : "↻ " + countdown,
            snapshot.DisplayName,
            figures.Band,
            snapshot.Status.Kind is ProviderStatusKind.Stale or ProviderStatusKind.AccessDenied,
            StatusTextFor(snapshot),
            snapshot.Block is not null || snapshot.IsDepleted);
    }

    /// <summary>
    /// The corner countdown in the chosen unit: Codenotch's own hours form by default ("72h 00m"),
    /// or the days form ("3d 00h") when the settings ask for it.
    /// </summary>
    private static string? Countdown(DateTimeOffset resetsAt, DateTimeOffset now, CountdownStyle style) =>
        style == CountdownStyle.Days
            ? ResetCopy.CountdownWithDays(resetsAt, now)
            : ResetCopy.Countdown(resetsAt, now);

    /// <summary>
    /// The words for a provider with no windows to show. A reading of any kind outranks them, so
    /// these never sit under numbers (Swift's <c>statusMessage</c> returns nil once
    /// <c>hasReading</c>).
    /// </summary>
    private static string? StatusTextFor(ProviderSnapshot snapshot)
    {
        if (snapshot.HasReading)
        {
            return null;
        }

        return snapshot.Status.Kind switch
        {
            ProviderStatusKind.NeedsAuth => "sign in",
            ProviderStatusKind.SignedOutByOwner => "signed out",
            ProviderStatusKind.AccessDenied => "access denied",

            // The account is fine, it just has nothing metered; or the server's own words, which
            // are the only part of an API failure that says what went wrong.
            ProviderStatusKind.Unsupported => Short(snapshot.Status.Message, "not supported"),
            ProviderStatusKind.Error => Short(snapshot.Status.Message, "read failed"),

            ProviderStatusKind.Stale => "no data",

            // A first reading is still coming; Swift says "Waiting for the first reading…".
            _ => "waiting…",
        };
    }

    private static string Short(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message;
}
