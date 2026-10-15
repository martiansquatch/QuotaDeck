namespace QuotaDeck.Readers.Tests.Claude;

using System.Text;
using ZstdSharp;

/// <summary>
/// Builds one Chromium Simple Cache entry file's bytes.
///
/// Written out here rather than copied from a real cache so that the format assumptions are
/// *stated*: the 8-byte magic, the three 32-bit fields, and the four bytes of struct padding that a
/// reader which counts to 20 instead of 24 lands inside the key. Copied from Codenotch's
/// <c>ClaudeDesktopUsageCacheTests.Entry</c> with the payloads rebuilt here — the Swift fixtures were
/// pre-compressed blobs, and this port compresses in the test instead.
/// </summary>
public static class DesktopEntry
{
    public const ulong Magic = 0xfcfb_6d1b_a772_5c30;

    /// <summary>The organization the fixture keys name. Invented.</summary>
    public const string Organization = "11111111-2222-3333-4444-555555555555";

    /// <summary>`Wed, 09 Sep 2026 16:12:08 GMT`, the response date the fixtures carry.</summary>
    public static readonly DateTimeOffset FixtureDate = new(2026, 9, 9, 16, 12, 8, TimeSpan.Zero);

    public static string UsageKey(string organization = Organization, string query = "?skip_spend=1") =>
        $"1/0/https://claude.ai/api/organizations/{organization}/usage{query}";

    /// <summary>The response the endpoint sends, in the shape the cache holds it.</summary>
    public static string UsageJson(
        int session = 30,
        int weekly = 74,
        string sessionReset = "2026-09-09T18:39:59.636345+00:00",
        string weeklyReset = "2026-09-16T16:59:59.636373+00:00") =>
        $$"""
        {
          "five_hour": { "utilization": {{session}}, "resets_at": "{{sessionReset}}", "limit_dollars": null },
          "seven_day": { "utilization": {{weekly}}, "resets_at": "{{weeklyReset}}", "limit_dollars": null },
          "limits": [
            { "kind": "session", "percent": {{session}}, "resets_at": "{{sessionReset}}", "scope": null },
            { "kind": "weekly_all", "percent": {{weekly}}, "resets_at": "{{weeklyReset}}", "scope": null }
          ]
        }
        """;

    /// <summary>One zstd frame, exactly as Cloudflare serves the response.</summary>
    public static byte[] Compress(string json)
    {
        using var compressor = new Compressor(3);
        return compressor.Wrap(Encoding.UTF8.GetBytes(json)).ToArray();
    }

    /// <summary>An entry's bytes, with the header block Chromium writes after the body.</summary>
    public static byte[] Build(
        string? key = null,
        byte[]? body = null,
        string? responseDate = "Wed, 09 Sep 2026 16:12:08 GMT",
        ulong magic = Magic,
        uint? declaredKeyLength = null)
    {
        key ??= UsageKey();
        body ??= Compress(UsageJson());

        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(magic));
        bytes.AddRange(BitConverter.GetBytes(5u));

        var keyBytes = Encoding.UTF8.GetBytes(key);
        bytes.AddRange(BitConverter.GetBytes(declaredKeyLength ?? (uint)keyBytes.Length));

        // The fourth 32-bit field, then the padding Chromium's C++ struct carries.
        bytes.AddRange(BitConverter.GetBytes(0xdead_beefu));
        bytes.AddRange(new byte[4]);

        bytes.AddRange(keyBytes);
        bytes.AddRange(body);
        bytes.AddRange(Trailer(responseDate));
        return bytes.ToArray();
    }

    /// <summary>A stand-in for the pickled `HttpResponseInfo`: the NUL-separated header block.</summary>
    public static byte[] Trailer(string? responseDate)
    {
        var bytes = new List<byte> { 0x01, 0x00, 0x00, 0x00 };
        bytes.AddRange(Encoding.ASCII.GetBytes("HTTP/1.1 200"));
        bytes.Add(0);
        if (responseDate is not null)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes($"date:{responseDate}"));
            bytes.Add(0);
        }

        bytes.AddRange(Encoding.ASCII.GetBytes("content-encoding:zstd"));
        bytes.AddRange([0, 0]);
        return bytes.ToArray();
    }
}
