namespace QuotaDeck.Readers.Codex;

using System.Globalization;
using System.Text.Json;

/// <summary>
/// The tolerant field reads Codenotch's Codex decoders get from <c>try?</c> — and, for the credit
/// amounts, from a second String attempt — so one unreadable value never discards its siblings.
///
/// Shared by the ports of Codenotch <c>Sources/Providers/CodexUsage.swift</c>
/// (<c>Response.init(from:)</c>, <c>RateLimit.init(from:)</c>, <c>Window.number(_:_:)</c>,
/// <c>CreditLimit.number(_:)</c>, <c>ResetCredit.init(from:)</c>) and
/// <c>Sources/Providers/CodexCredentials.swift</c> (<c>load(from:now:)</c>, <c>claims(inJWT:)</c>).
/// </summary>
internal static class CodexJson
{
    /// <summary>
    /// The parsed document's root when it is a JSON object, nil for anything else or for text that
    /// is not JSON at all. The clone outlives the document, so callers can hold one field's element
    /// without holding the whole parse.
    /// </summary>
    public static JsonElement? ParseObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A child object, or nil when the field is missing or another type.</summary>
    public static JsonElement? Object(JsonElement? parent, string name) =>
        parent is { } value
        && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    /// <summary>A child array's items; anything other than an array reads as empty.</summary>
    public static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
            : [];

    /// <summary>
    /// A JSON number as a double; a string, null or missing value reads as nil — the
    /// <c>try? container.decode(Double.self, forKey:)</c> of <c>Window.number(_:_:)</c>.
    /// </summary>
    public static double? Number(JsonElement? parent, string name)
    {
        if (parent is not { } value
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetDouble(out var number)
            || !double.IsFinite(number))
        {
            return null;
        }

        return number;
    }

    /// <summary>
    /// An amount that may arrive as a decimal string ("374.92"), like <c>CreditLimit.number(_:)</c>:
    /// a number, else a string parsed with the same strictness as Swift's <c>Double(_:)</c>.
    /// </summary>
    public static double? LooseNumber(JsonElement? parent, string name)
    {
        if (parent is not { } value
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
        }

        // AllowLeadingWhite / AllowTrailingWhite are left out on purpose: Swift's Double(" 3") is nil.
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        return property.ValueKind == JsonValueKind.String
            && property.GetString() is { } text
            && double.TryParse(text, styles, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed)
                ? parsed
                : null;
    }

    /// <summary>A child string; numbers, null and missing values read as nil.</summary>
    public static string? Text(JsonElement? parent, string name) =>
        parent is { } value
        && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>
    /// Epoch seconds as an instant. The tolerant-read improvement over Swift: a value no
    /// <see cref="DateTimeOffset"/> can hold becomes nil rather than an exception. Every normal
    /// payload is unaffected.
    /// </summary>
    public static DateTimeOffset? Timestamp(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The same tolerance for a countdown relative to <paramref name="now"/>.</summary>
    public static DateTimeOffset? Offset(DateTimeOffset now, double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            return null;
        }

        try
        {
            return now.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>A window length as a duration; a value that cannot fit a <see cref="TimeSpan"/> reads as nil.</summary>
    public static TimeSpan? FiniteDuration(double? seconds) =>
        seconds is { } value && double.IsFinite(value) && Math.Abs(value) <= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(value)
            : null;
}
