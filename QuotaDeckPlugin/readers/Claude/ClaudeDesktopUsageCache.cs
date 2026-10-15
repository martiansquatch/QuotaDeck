namespace QuotaDeck.Readers.Claude;

using System.Globalization;
using System.Text;
using QuotaDeck.Core.Model;
using ZstdSharp;

// Port of Codenotch Sources/Providers/ClaudeDesktopUsageCache.swift (struct ClaudeDesktopUsageCache:
// Reading, read(organization:now:), recentEntries(), reading(from:organization:now:),
// contents(of:forOrganization:), parse(entry:), key(in:), usageOrganization(inKey:),
// httpDate(inTrailer:), decompress(frame:), the byte readers and the caps).
//
// Claude Desktop is Chromium, so its `GET /api/organizations/<id>/usage` — the very response its own
// usage panel draws — lands in a Simple Cache entry on disk, and this reads that entry. No token, no
// cookie, no keychain, no request to Anthropic, no subprocess, and no write of any kind.
//
// Two deliberate differences from the Swift original, both stated so they are not mistaken for
// drift:
//   * The zstd frame's length is found by walking the frame header and block headers here, because
//     the decoder this port uses exposes no `ZSTD_findFrameCompressedSize`. The walk is followed by
//     a decode of exactly that slice, and the decoder refuses trailing bytes, so a walked length
//     that is wrong is caught rather than trusted.
//   * The `cedar_ember=1` response is skipped whole rather than read for its reset block: core's
//     snapshot has no reset-credit field to carry the block to. Its sibling plain key — Desktop
//     asks for both — is what answers instead.
//   * The modification time comes from its own probe rather than from the open handle's fstat,
//     which no portable file API offers; the worst case is a torn snapshot, which parses to nothing.

/// <summary>Claude's own limits, read out of Claude Desktop's HTTP cache.</summary>
public sealed class ClaudeDesktopUsageCache
{
    /// <summary>`SimpleFileHeader` is a 64-bit magic and three 32-bit fields, then struct padding.</summary>
    public const int HeaderBytes = 24;

    /// <summary>
    /// Entries larger than this are skipped without being read. A usage response is ~4 kB on disk,
    /// so this is orders of magnitude of headroom and still refuses the multi-megabyte asset entries
    /// that share the directory.
    /// </summary>
    public const long MaxEntryBytes = 512 * 1024;

    /// <summary>How many entries a full scan will look inside, newest first.</summary>
    public const int MaxEntriesExamined = 400;

    /// <summary>
    /// Cap on the decompressed body. Enforced by the decoder itself — a frame that would exceed it
    /// is an error, never an overrun — so a corrupt or hostile entry cannot be turned into a large
    /// allocation.
    /// </summary>
    public const int MaxDecompressedBytes = 256 * 1024;

    /// <summary>
    /// A key longer than this is not one of ours, and reading it would be the one unbounded read in
    /// here. Chromium's own limit is far lower.
    /// </summary>
    public const int MaxKeyBytes = 8 * 1024;

    /// <summary>`kSimpleInitialMagicNumber`.</summary>
    private const ulong EntryMagic = 0xfcfb_6d1b_a772_5c30;

    /// <summary>The frame magic every zstd stream starts with, checked before the decoder is handed anything.</summary>
    private static readonly byte[] ZstdMagic = [0x28, 0xb5, 0x2f, 0xfd];

    private static readonly string[] HttpDateNames = ["date:", "Date:"];

    private readonly string _directory;
    private readonly IClaudeFileSystem _fileSystem;

    public ClaudeDesktopUsageCache(string directory, IClaudeFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        this._directory = directory;
        this._fileSystem = fileSystem;
    }

    /// <summary>Where Claude Desktop keeps its HTTP cache, under a home directory computed at runtime.</summary>
    public static string DefaultDirectory(string homeDirectory) =>
        Path.Combine(homeDirectory, "Library", "Application Support", "Claude", "Cache", "Cache_Data");

    /// <summary>
    /// One reading, and where it came from.
    /// </summary>
    /// <param name="Windows">The same windows the other two Claude paths produce.</param>
    /// <param name="CapturedAt">
    /// When the numbers were true: the response's own `Date:`, or the entry's modification time
    /// where that header is missing. Not "now" — a cache entry is by nature already old, and how old
    /// is the whole question of whether it may be shown as live.
    /// </param>
    /// <param name="EntryFile">The entry it was read from, for a log line when a reading looks wrong.</param>
    public sealed record Reading(IReadOnlyList<LimitWindow> Windows, DateTimeOffset CapturedAt, string EntryFile)
    {
        /// <summary>
        /// Whether these numbers may still be presented as live. A negative age is two clocks
        /// disagreeing — the header's is the server's — not a reading from the future.
        /// </summary>
        public bool IsFresh(DateTimeOffset now, TimeSpan window)
        {
            var age = now - this.CapturedAt;
            return age < window && age > -window;
        }
    }

    /// <summary>One entry, parsed: the body, whose usage it is, and when the response was written.</summary>
    public sealed record Parsed(byte[] Body, string Organization, DateTimeOffset? Date);

    /// <summary>
    /// The most recent usable reading for one organization, or nil.
    ///
    /// The organization is not optional and not cosmetic. Claude Desktop is signed into exactly one
    /// account, while this reader draws a ring per Claude Code profile — so handing Desktop's
    /// numbers to whichever ring asked first would put the personal account's session percentage on
    /// the work ring. The cache key carries the organization's UUID and Claude Code records the same
    /// UUID per profile, so the two can simply be required to match.
    ///
    /// Freshness is deliberately not judged here. This returns the newest reading there is, with
    /// the timestamp it actually has; whether that is recent enough to show as live is the caller's
    /// call. It always scans rather than remembering the last winning file: Desktop asks for both
    /// `…/usage` and `…/usage?skip_spend=1` — two keys, two files — and alternates which one it
    /// refreshes, so trusting one file's own age says nothing about whether the other has since
    /// become the newer answer.
    /// </summary>
    public Reading? Read(string organization, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(organization);

        // Newest first, so the live entry turns up within a handful of files.
        foreach (var entry in this.RecentEntries())
        {
            if (this.ReadEntry(entry, organization, now) is { } reading)
            {
                return reading;
            }
        }

        return null;
    }

    /// <summary>Candidate entry files, most recently modified first, capped.</summary>
    private List<DirectoryFile> RecentEntries()
    {
        var candidates = new List<DirectoryFile>();
        foreach (var file in this._fileSystem.ListFiles(this._directory))
        {
            // `_0` is the stream file that holds the body. Chromium writes `_1` and `_s` beside it
            // for other streams, and the index lives in a subdirectory this listing already skips.
            if (!Path.GetFileName(file.Path).EndsWith("_0", StringComparison.Ordinal))
            {
                continue;
            }

            if (file.Size <= HeaderBytes || file.Size > MaxEntryBytes)
            {
                continue;
            }

            candidates.Add(file);
        }

        candidates.Sort((left, right) => right.Modified.CompareTo(left.Modified));
        return candidates.Count > MaxEntriesExamined ? candidates[..MaxEntriesExamined] : candidates;
    }

    /// <summary>One entry, read and parsed, or nil for every way that can fail.</summary>
    private Reading? ReadEntry(DirectoryFile entry, string organization, DateTimeOffset now)
    {
        // The header and at most a key's worth after it first: a scan otherwise pulls a few hundred
        // megabytes of unrelated cached responses through memory to find one 4 kB file, and this app
        // would have read the bodies of pages it has no business looking at. Only a key that names
        // *this* account's usage endpoint earns a full read.
        var head = this._fileSystem.Read(entry.Path, HeaderBytes + MaxKeyBytes);
        if (Key(head) is not { } key || UsageOrganization(key) != organization)
        {
            return null;
        }

        // Reset-credit entries carry the same usage response plus a block core's snapshot has no
        // field for; the plain sibling key is the usage reading.
        if (RequestsResetCredits(key))
        {
            return null;
        }

        var bytes = this._fileSystem.Read(entry.Path, (int)MaxEntryBytes);
        if (Parse(bytes) is not { } parsed)

        {
            return null;
        }

        // The organization is checked twice on purpose: against a *prefix* above to decide whether
        // the entry is worth reading whole, and against the bytes that were actually parsed here.
        // Chromium can rewrite the file between those two reads, and the one thing that must never
        // happen is another account's numbers reaching this ring.
        if (parsed.Organization != organization)
        {
            return null;
        }

        ClaudeUsageResponse response;
        try
        {
            response = ClaudeUsageResponse.Decode(Encoding.UTF8.GetString(parsed.Body));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        var capturedAt = parsed.Date ?? this._fileSystem.Stamp(entry.Path)?.Modified ?? now;
        var windows = response.LimitWindows(capturedAt);

        // A response that parses but names no window is not a reading. Letting it through would hand
        // the caller an empty snapshot, which draws a ring with a hole in it rather than falling
        // through to a source that works.
        return windows.Count == 0 ? null : new Reading(windows, capturedAt, entry.Path);
    }

    /// <summary>
    /// Pulls the usage JSON, its organization and its `Date:` out of one entry's bytes. Public so
    /// the tests can drive it on synthetic entries: every branch below is a shape Chromium can
    /// produce, and most are unreachable from a fixture that has to go through the filesystem first.
    /// </summary>
    public static Parsed? Parse(ReadOnlySpan<byte> entry)
    {
        if (Key(entry) is not { } key || UsageOrganization(key) is not { } organization)
        {
            return null;
        }

        // `keyLength` is a byte count, and the key decoded from exactly that many bytes, so this
        // lands where the key ended.
        var bodyStart = HeaderBytes + (int)ReadUInt32(entry, 12);
        if (entry.Length - bodyStart <= ZstdMagic.Length
            || !entry.Slice(bodyStart, ZstdMagic.Length).SequenceEqual(ZstdMagic))
        {
            return null;
        }

        var frame = entry[bodyStart..];
        if (FrameCompressedSize(frame) is not { } frameSize
            || Decompress(frame[..frameSize]) is not { } body)
        {
            return null;
        }

        // Everything after the frame is the stream Chromium wrote next, which is where the header
        // block lives. Bounding the search to it keeps `date:` from being found in the body's own
        // JSON.
        return new Parsed(body, organization, HttpDate(frame[frameSize..]));
    }

    /// <summary>
    /// The cache key an entry begins with, or nil when these bytes are not a Simple Cache entry.
    ///
    /// Only ever reads inside what it has: the key length is a number *out of the file*, so it is
    /// corruption- and attacker-controlled and is bounded before it is believed.
    /// </summary>
    public static string? Key(ReadOnlySpan<byte> entry)
    {
        if (entry.Length <= HeaderBytes || ReadUInt64(entry, 0) != EntryMagic)
        {
            return null;
        }

        var keyLength = (int)ReadUInt32(entry, 12);
        if (keyLength <= 0 || keyLength > MaxKeyBytes || HeaderBytes + keyLength > entry.Length)
        {
            return null;
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(entry.Slice(HeaderBytes, keyLength));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Which organization a cache key's usage URL names, or nil when the key is not a usage request
    /// at all.
    ///
    /// Keys carry a cache-partition prefix — `1/0/https://claude.ai/…` — so this matches the URL
    /// inside rather than anchoring at the start. The host check is not decoration: without it this
    /// would decompress the body of any site that happened to serve a path shaped like Anthropic's.
    /// </summary>
    public static string? UsageOrganization(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!key.Contains("claude.ai", StringComparison.Ordinal)
            && !key.Contains("anthropic.com", StringComparison.Ordinal))
        {
            return null;
        }

        const string marker = "/api/organizations/";
        var start = key.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        // Query and fragment are no part of the shape — the request has carried `?skip_spend=1`,
        // and could carry anything next.
        var rest = key[(start + marker.Length)..];
        var stop = rest.IndexOfAny(['?', '#']);
        var path = stop >= 0 ? rest[..stop] : rest;

        // `<id>/usage`: one segment and then the endpoint, so that
        // `/api/organizations/<id>/usage/something-else` is not mistaken for it.
        var segments = path.Split('/', StringSplitOptions.None);
        return segments.Length == 2 && segments[0].Length > 0 && segments[1] == "usage" ? segments[0] : null;
    }

    /// <summary>Whether the key's query asks for the reset-credit block.</summary>
    public static bool RequestsResetCredits(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var parts = key.Split('?');
        return parts.Length > 1
            && parts[1].Split('&').Contains("cedar_ember=1", StringComparer.Ordinal);
    }

    /// <summary>
    /// The response's `Date:`, found in the header block Chromium stores after the body.
    ///
    /// That block is a run of NUL-separated `name:value` strings, which is enough structure to read
    /// one field out of without parsing the pickle around it. Names arrive lower-cased over HTTP/2
    /// and HTTP/3 and in their original case over HTTP/1.1, so both are tried.
    /// </summary>
    public static DateTimeOffset? HttpDate(ReadOnlySpan<byte> trailer)
    {
        foreach (var name in HttpDateNames)
        {
            var needle = new byte[name.Length + 1];
            Encoding.ASCII.GetBytes(name, needle.AsSpan(1));
            var start = trailer.IndexOf(needle);
            if (start < 0)
            {
                continue;
            }

            var valueStart = start + needle.Length;
            var end = trailer[valueStart..].IndexOf((byte)0);
            if (end < 0)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(trailer.Slice(valueStart, end)).Trim();
            if (TryHttpDate(text, out var date))
            {
                return date;
            }
        }

        return null;
    }

    /// <summary>RFC 9110's `IMF-fixdate`, the only form a `Date:` header written this decade takes.</summary>
    public static bool TryHttpDate(string text, out DateTimeOffset date) =>
        DateTimeOffset.TryParseExact(
            text, "r", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>
    /// The compressed length of the zstd frame at the start of <paramref name="bytes"/>, or nil when
    /// there is not one complete frame there.
    ///
    /// The decoder rejects trailing data rather than stopping at the frame's end, so the length has
    /// to be found before it is handed anything. The walk follows RFC 8878: the frame header's
    /// variable fields, then each block's three-byte header, skipping each block's payload —
    /// one byte for an RLE block, `Block_Size` bytes otherwise.
    /// </summary>
    public static int? FrameCompressedSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6 || !bytes[..4].SequenceEqual(ZstdMagic))
        {
            return null;
        }

        // Frame_Header_Descriptor: bits 7-6 Frame_Content_Size_flag, bit 5 Single_Segment_flag,
        // bit 3 Reserved (must be zero), bit 2 Content_Checksum_flag, bits 1-0 Dictionary_ID_flag.
        var descriptor = bytes[4];
        if ((descriptor & 0x08) != 0)
        {
            return null;
        }

        var contentSizeFlag = descriptor >> 6;
        var singleSegment = (descriptor & 0x20) != 0;
        var dictionaryIdFlag = descriptor & 0x03;
        var checksum = (descriptor & 0x04) != 0;

        var index = 5;
        if (!singleSegment)
        {
            index += 1;
        }

        index += dictionaryIdFlag switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };
        index += contentSizeFlag switch
        {
            0 => singleSegment ? 1 : 0,
            1 => 2,
            2 => 4,
            _ => 8,
        };

        if (index > bytes.Length)
        {
            return null;
        }

        while (true)
        {
            if (index + 3 > bytes.Length)
            {
                return null;
            }

            var header = bytes[index] | (bytes[index + 1] << 8) | (bytes[index + 2] << 16);
            var last = (header & 1) != 0;
            var type = (header >> 1) & 0x03;
            var size = header >> 3;
            if (type == 3)
            {
                return null;
            }

            index += 3;

            // RLE_Block carries exactly one byte; raw and compressed blocks carry Block_Size bytes.
            index += type == 1 ? 1 : size;
            if (index > bytes.Length)
            {
                return null;
            }

            if (last)
            {
                break;
            }
        }

        if (checksum)
        {
            index += 4;
        }

        return index > bytes.Length ? null : index;
    }

    /// <summary>
    /// One zstd frame, decompressed, with the output cap as the only guard — the cap refuses both a
    /// frame that declares an oversized body and one that declares nothing, which is what a chunked
    /// response leaves behind.
    /// </summary>
    private static byte[]? Decompress(ReadOnlySpan<byte> frame)
    {
        if (frame.IsEmpty)
        {
            return null;
        }

        try
        {
            var output = new byte[MaxDecompressedBytes];
            using var decompressor = new Decompressor();
            var written = decompressor.Unwrap(frame, output);
            return written > 0 && written <= output.Length ? output[..written] : null;
        }
        catch (ZstdException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        (uint)(bytes[offset]
            | (bytes[offset + 1] << 8)
            | (bytes[offset + 2] << 16)
            | (bytes[offset + 3] << 24));

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset)
    {
        ulong value = 0;
        for (var index = 7; index >= 0; index--)
        {
            value = (value << 8) | bytes[offset + index];
        }

        return value;
    }
}
