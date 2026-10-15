namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Readers.Claude;
using ZstdSharp;

/// <summary>
/// Reading Claude's limits out of Claude Desktop's HTTP cache.
///
/// Hermetic throughout: it exists to read a directory belonging to another application, and the one
/// thing that must never decide whether these pass is whether the developer happens to have Claude
/// Desktop open. Entries are built byte by byte — magic, key, a zstd body compressed here, and the
/// NUL-separated header trailer — with an invented organization and synthetic percentages.
///
/// Ported from Codenotch <c>Tests/ClaudeDesktopUsageCacheTests.swift</c>, with the pre-compressed
/// base64 bodies replaced by bodies the test compresses itself (the redaction notes ask for the
/// fixtures to be rebuilt rather than moved), and with the reset-credit cases dropped: core's
/// snapshot has no reset-credit field for a block to reach, so `cedar_ember` entries are skipped.
/// </summary>
public class ClaudeDesktopUsageCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 16, 20, 0, TimeSpan.Zero);

    private static ClaudeDesktopUsageCache Cache(FakeClaudeFileSystem files, string home) =>
        new(ClaudeDesktopUsageCache.DefaultDirectory(home), files);

    private const string Home = "/tmp/quotadeck-claude-home";

    // MARK: - A valid entry

    [Fact]
    public void AValidEntryYieldsTheSessionAndWeeklyWindows()
    {
        var parsed = Assert.IsType<ClaudeDesktopUsageCache.Parsed>(
            ClaudeDesktopUsageCache.Parse(DesktopEntry.Build()));

        Assert.Equal(DesktopEntry.Organization, parsed.Organization);

        var windows = ClaudeUsageResponse.Decode(System.Text.Encoding.UTF8.GetString(parsed.Body)).LimitWindows(Now);
        Assert.Equal(["session", "weekly_all"], windows.Select(window => window.Id));
        Assert.Equal(["Current session", "All models"], windows.Select(window => window.Label));
        Assert.Equal(0.30, windows[0].UsedFraction!.Value, 4);
        Assert.Equal(0.74, windows[1].UsedFraction!.Value, 4);

        // Absolute reset times survive the round trip, which is what lets them keep being used after
        // the reading itself has gone stale.
        Assert.NotNull(windows[0].ResetsAt);
        Assert.NotNull(windows[1].ResetsAt);
    }

    [Fact]
    public void TheResponseDateBecomesTheTimestamp()
    {
        var parsed = Assert.IsType<ClaudeDesktopUsageCache.Parsed>(
            ClaudeDesktopUsageCache.Parse(DesktopEntry.Build()));

        Assert.Equal(DesktopEntry.FixtureDate, parsed.Date);
    }

    /// <summary>Header names arrive lower-cased over HTTP/2 and HTTP/3 and original-case over HTTP/1.1.</summary>
    [Theory]
    [InlineData("date")]
    [InlineData("Date")]
    public void TheDateIsFoundInEitherCase(string name)
    {
        var trailer = new List<byte> { 0x00 };
        trailer.AddRange(System.Text.Encoding.ASCII.GetBytes($"{name}:Wed, 09 Sep 2026 16:12:08 GMT"));
        trailer.Add(0);

        Assert.Equal(DesktopEntry.FixtureDate, ClaudeDesktopUsageCache.HttpDate(trailer.ToArray()));
    }

    [Fact]
    public void AnEntryWithNoDateHeaderStillParses()
    {
        var parsed = Assert.IsType<ClaudeDesktopUsageCache.Parsed>(
            ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(responseDate: null)));

        // Nil here, not a guess: the caller substitutes the file's own modification time.
        Assert.Null(parsed.Date);
        Assert.NotEmpty(parsed.Body);
    }

    [Fact]
    public void AnUnparseableDateHeaderIsIgnoredRatherThanFatal()
    {
        var parsed = Assert.IsType<ClaudeDesktopUsageCache.Parsed>(
            ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(responseDate: "not a date at all")));

        Assert.Null(parsed.Date);
    }

    // MARK: - Windows the response may or may not carry

    [Fact]
    public void FiveHourAloneIsASessionWindow()
    {
        var body = DesktopEntry.Compress("""
        { "five_hour": { "utilization": 7.0, "resets_at": "2026-09-09T18:39:59.636345+00:00" } }
        """);
        var parsed = Assert.IsType<ClaudeDesktopUsageCache.Parsed>(
            ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(body: body)));

        var windows = ClaudeUsageResponse.Decode(System.Text.Encoding.UTF8.GetString(parsed.Body)).LimitWindows(Now);
        var window = Assert.Single(windows);
        Assert.Equal("session", window.Id);
        Assert.Equal(0.07, window.UsedFraction!.Value, 4);
    }

    [Fact]
    public void SevenDayAloneIsTheAllModelsWindow()
    {
        var body = DesktopEntry.Compress("""
        { "seven_day": { "utilization": 88.0, "resets_at": "2026-09-16T16:59:59.636373+00:00" } }
        """);
        var parsed = Assert.IsType<ClaudeDesktopUsageCache.Parsed>(
            ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(body: body)));

        var windows = ClaudeUsageResponse.Decode(System.Text.Encoding.UTF8.GetString(parsed.Body)).LimitWindows(Now);
        var window = Assert.Single(windows);
        Assert.Equal("weekly_all", window.Id);
        Assert.Equal(0.88, window.UsedFraction!.Value, 4);
    }

    /// <summary>`weekly_scoped` comes back for whichever model the plan scopes; the name is in `scope.model`.</summary>
    [Fact]
    public void AScopedWeeklyWindowKeepsTheModelName()
    {
        var body = DesktopEntry.Compress("""
        { "limits": [
            { "kind": "session", "percent": 12, "resets_at": "2026-09-09T18:39:59.636345+00:00" },
            { "kind": "weekly_scoped", "percent": 55, "resets_at": "2026-09-16T16:59:59.636373+00:00",
              "scope": { "model": { "display_name": "Opus" } } } ] }
        """);
        var parsed = Assert.IsType<ClaudeDesktopUsageCache.Parsed>(
            ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(body: body)));

        var windows = ClaudeUsageResponse.Decode(System.Text.Encoding.UTF8.GetString(parsed.Body)).LimitWindows(Now);
        Assert.Equal(["session", "weekly_scoped"], windows.Select(window => window.Id));
        Assert.Equal(["Current session", "Opus"], windows.Select(window => window.Label));
        Assert.Equal(0.55, windows[1].UsedFraction!.Value, 4);
    }

    // MARK: - Keys

    [Theory]
    [InlineData("")]
    [InlineData("?skip_spend=1")]
    [InlineData("?a=1&b=2")]
    [InlineData("#fragment")]
    public void TheUsageKeyIsRecognisedWithAndWithoutAQuery(string suffix)
    {
        Assert.Equal(
            DesktopEntry.Organization,
            ClaudeDesktopUsageCache.UsageOrganization(DesktopEntry.UsageKey(query: suffix)));
    }

    [Fact]
    public void KeysThatAreNotTheUsageEndpointAreIgnored()
    {
        var organization = DesktopEntry.Organization;
        string[] cases =
        [
            // Another endpoint under the same organization.
            $"1/0/https://claude.ai/api/organizations/{organization}/projects",

            // The usage endpoint with something after it.
            $"1/0/https://claude.ai/api/organizations/{organization}/usage/detail",

            // No organization segment at all.
            "1/0/https://claude.ai/api/usage",

            // The right shape on the wrong host. Without this check the reader would decompress any
            // site's body that matched the path.
            $"1/0/https://example.com/api/organizations/{organization}/usage",
            string.Empty,
            "not a url",
        ];

        foreach (var key in cases)
        {
            Assert.Null(ClaudeDesktopUsageCache.UsageOrganization(key));
        }
    }

    /// <summary>The key is read out of a prefix, so the thousands of unrelated entries are rejected on their URL.</summary>
    [Fact]
    public void TheKeyIsReadableFromAPrefixOfTheEntry()
    {
        var whole = DesktopEntry.Build();
        var prefix = whole.AsSpan(
            0, Math.Min(whole.Length, ClaudeDesktopUsageCache.HeaderBytes + ClaudeDesktopUsageCache.MaxKeyBytes));

        Assert.Equal(DesktopEntry.UsageKey(), ClaudeDesktopUsageCache.Key(prefix));
    }

    [Fact]
    public void KeyReadingRefusesAnythingItCannotTrust()
    {
        // Too short, wrong magic, and a length that runs past the buffer.
        Assert.Null(ClaudeDesktopUsageCache.Key([]));
        Assert.Null(ClaudeDesktopUsageCache.Key(new byte[ClaudeDesktopUsageCache.HeaderBytes]));
        Assert.Null(ClaudeDesktopUsageCache.Key(DesktopEntry.Build(magic: 0)));

        foreach (var declared in new uint[] { 0, 1 << 20, uint.MaxValue, ClaudeDesktopUsageCache.MaxKeyBytes + 1 })
        {
            Assert.Null(ClaudeDesktopUsageCache.Key(DesktopEntry.Build(declaredKeyLength: declared)));
        }
    }

    [Fact]
    public void AnEntryWhoseKeyIsNotAUsageRequestIsIgnored()
    {
        var key = $"1/0/https://claude.ai/api/organizations/{DesktopEntry.Organization}/projects";

        Assert.Null(ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(key: key)));
    }

    /// <summary>The dataset names its own query; a reset-credit entry is not a usage reading here.</summary>
    [Fact]
    public void AResetCreditEntryIsSkipped()
    {
        Assert.True(ClaudeDesktopUsageCache.RequestsResetCredits(
            DesktopEntry.UsageKey(query: "?skip_spend=1&cedar_ember=1")));
        Assert.False(ClaudeDesktopUsageCache.RequestsResetCredits(DesktopEntry.UsageKey()));

        var files = new FakeClaudeFileSystem();
        files.AddFile(
            Path.Combine(ClaudeDesktopUsageCache.DefaultDirectory(Home), "reset_0"),
            DesktopEntry.Build(key: DesktopEntry.UsageKey(query: "?cedar_ember=1")),
            Now);

        Assert.Null(Cache(files, Home).Read(DesktopEntry.Organization, Now));
    }

    // MARK: - Malformed entries

    [Fact]
    public void AFileWithoutTheSimpleCacheMagicIsIgnored()
    {
        Assert.Null(ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(magic: 0x0123_4567_89ab_cdef)));
    }

    [Fact]
    public void AnEmptyOrTinyFileIsIgnored()
    {
        Assert.Null(ClaudeDesktopUsageCache.Parse([]));
        Assert.Null(ClaudeDesktopUsageCache.Parse(new byte[4]));
        Assert.Null(ClaudeDesktopUsageCache.Parse(new byte[ClaudeDesktopUsageCache.HeaderBytes]));
    }

    [Fact]
    public void AnEntryWithNoBodyIsIgnored()
    {
        Assert.Null(ClaudeDesktopUsageCache.Parse(
            DesktopEntry.Build(body: [], responseDate: null)));
    }

    [Fact]
    public void ABodyThatIsNotZstdIsIgnored()
    {
        // Plausible, uncompressed JSON — what a `content-encoding: identity` response would leave.
        var body = System.Text.Encoding.UTF8.GetBytes("""{"five_hour":{"utilization":30.0}}""");

        Assert.Null(ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(body: body)));
    }

    [Fact]
    public void ACorruptZstdFrameIsIgnored()
    {
        var body = DesktopEntry.Compress(DesktopEntry.UsageJson());

        // Keep the magic — so the reader gets as far as the decoder — and wreck everything after it.
        for (var index = 4; index < body.Length; index++)
        {
            body[index] ^= 0xff;
        }

        Assert.Null(ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(body: body)));
    }

    /// <summary>A prefix that stops inside the compressed frame cannot decode, and must say so.</summary>
    [Fact]
    public void AnEntryTruncatedInsideTheFrameIsIgnored()
    {
        var whole = DesktopEntry.Build();
        var bodyStart = ClaudeDesktopUsageCache.HeaderBytes + DesktopEntry.UsageKey().Length;

        foreach (var length in (int[])[bodyStart, bodyStart + 4, bodyStart + 16, bodyStart + 40])
        {
            Assert.Null(ClaudeDesktopUsageCache.Parse(whole.AsSpan(0, length)));
        }
    }

    /// <summary>
    /// Every prefix of a real entry — what a half-written or evicted file looks like. Once the frame
    /// is complete a prefix legitimately parses, because the header block after it is not needed;
    /// what must never happen is a trap, or a body that differs from the whole entry's.
    /// </summary>
    [Fact]
    public void NoPrefixOfAnEntryTrapsOrParsesToSomethingElse()
    {
        var whole = DesktopEntry.Build();
        var expected = Assert.IsType<ClaudeDesktopUsageCache.Parsed>(ClaudeDesktopUsageCache.Parse(whole)).Body;

        for (var length = 0; length <= whole.Length; length++)
        {
            var parsed = ClaudeDesktopUsageCache.Parse(whole.AsSpan(0, length));
            if (parsed is null)
            {
                continue;
            }

            Assert.Equal(expected, parsed.Body);
            Assert.Equal(DesktopEntry.Organization, parsed.Organization);
        }
    }

    /// <summary>A span taken from the middle of another buffer parses the same as one that starts at zero.</summary>
    [Fact]
    public void ASliceOfALargerBufferParsesTheSameAsAWholeOne()
    {
        var padded = new byte[64 + DesktopEntry.Build().Length];
        DesktopEntry.Build().CopyTo(padded, 64);

        var parsed = Assert.IsType<ClaudeDesktopUsageCache.Parsed>(
            ClaudeDesktopUsageCache.Parse(padded.AsSpan(64)));

        Assert.Equal(DesktopEntry.Organization, parsed.Organization);
        Assert.Equal(DesktopEntry.FixtureDate, parsed.Date);
    }

    /// <summary>Valid zstd carrying something other than a usage response gets as far as a body, then is dropped.</summary>
    [Fact]
    public void ValidZstdCarryingSomethingOtherThanUsageIsIgnored()
    {
        var body = DesktopEntry.Compress("""{"hello":"world"}""");
        Assert.NotNull(ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(body: body)));

        var files = new FakeClaudeFileSystem();
        files.AddFile(Path.Combine(ClaudeDesktopUsageCache.DefaultDirectory(Home), "a_0"), DesktopEntry.Build(body: body), Now);

        Assert.Null(Cache(files, Home).Read(DesktopEntry.Organization, Now));
    }

    // MARK: - Size caps

    [Fact]
    public void AnOversizedBodyIsRefusedByTheOutputCap()
    {
        var big = System.Text.Encoding.UTF8.GetBytes(new string('a', 330_000));
        var oversized = DesktopEntry.Compress(
            System.Text.Encoding.UTF8.GetString(big));

        Assert.Null(ClaudeDesktopUsageCache.Parse(DesktopEntry.Build(body: oversized)));
    }

    [Fact]
    public void AnOversizedEntryFileIsNeverOpened()
    {
        // Bigger than the per-entry cap, and otherwise a perfectly good entry.
        var bytes = DesktopEntry.Build();
        var padded = new byte[ClaudeDesktopUsageCache.MaxEntryBytes + 1];
        bytes.CopyTo(padded, 0);

        var files = new FakeClaudeFileSystem();
        files.AddFile(Path.Combine(ClaudeDesktopUsageCache.DefaultDirectory(Home), "big_0"), padded, Now);

        Assert.Null(Cache(files, Home).Read(DesktopEntry.Organization, Now));
    }

    // MARK: - The zstd frame walker

    /// <summary>
    /// The walker's answer is the frame the compressor actually produced, for payloads that exercise
    /// every block shape: tiny frames with a declared content size, one written without one (what a
    /// chunked response leaves), and a payload large enough to need more than one block.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(500)]
    [InlineData(20_000)]
    [InlineData(200_000)]
    public void TheWalkedFrameLengthIsTheCompressorsOwn(int size)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(new string('z', size));
        using var compressor = new Compressor(3);
        var frame = compressor.Wrap(payload).ToArray();

        Assert.Equal(frame.Length, ClaudeDesktopUsageCache.FrameCompressedSize(frame));

        // And the walker refuses anything shorter than the frame, byte by byte at the boundary.
        Assert.Null(ClaudeDesktopUsageCache.FrameCompressedSize(frame.AsSpan(0, frame.Length - 1)));
        Assert.Null(ClaudeDesktopUsageCache.FrameCompressedSize([]));
        Assert.Null(ClaudeDesktopUsageCache.FrameCompressedSize([0x28, 0xb5, 0x2f, 0xfd, 0x00]));
    }

    /// <summary>A frame written from a stream declares no size, which is what a chunked response looks like.</summary>
    [Fact]
    public void AFrameWithoutADeclaredContentSizeIsWalked()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(new string('y', 5000));
        using var compressor = new Compressor(3);
        compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_contentSizeFlag, 0);
        var frame = compressor.Wrap(payload).ToArray();

        Assert.Equal(frame.Length, ClaudeDesktopUsageCache.FrameCompressedSize(frame));
    }

    // MARK: - Scanning a directory

    [Fact]
    public void AMissingCacheDirectoryReadsAsNothing() =>
        Assert.Null(new ClaudeDesktopUsageCache("/tmp/quotadeck-absent-cache", new SystemClaudeFileSystem())
            .Read(DesktopEntry.Organization, Now));

    [Fact]
    public void AnEmptyCacheDirectoryReadsAsNothing() =>
        Assert.Null(Cache(new FakeClaudeFileSystem(), Home).Read(DesktopEntry.Organization, Now));

    [Fact]
    public void FilesThatAreNotCacheEntriesAreIgnored()
    {
        var files = new FakeClaudeFileSystem();
        var directory = ClaudeDesktopUsageCache.DefaultDirectory(Home);
        files.AddFile(Path.Combine(directory, "the-real-index"), new byte[4096], Now);
        files.AddFile(Path.Combine(directory, "notanentry"), "hello"u8.ToArray(), Now);
        files.AddFile(Path.Combine(directory, "somefile_1"), DesktopEntry.Build(), Now);

        Assert.Null(Cache(files, Home).Read(DesktopEntry.Organization, Now));
    }

    /// <summary>
    /// Claude Desktop is signed into one account while this reader draws a ring per profile, so the
    /// wrong account's session percentage must not reach the wrong ring.
    /// </summary>
    [Fact]
    public void AnEntryForAnotherOrganizationIsIgnored()
    {
        var files = new FakeClaudeFileSystem();
        files.AddFile(Path.Combine(ClaudeDesktopUsageCache.DefaultDirectory(Home), "a_0"), DesktopEntry.Build(), Now);

        Assert.Null(Cache(files, Home).Read("99999999-8888-7777-6666-555555555555", Now));
    }

    /// <summary>Desktop asks for `…/usage` and `…/usage?skip_spend=1`; the newer of the two answers.</summary>
    [Fact]
    public void TheMostRecentOfSeveralUsageEntriesWins()
    {
        // Both keys really do occur at once, and they hash to two different files.
        var older = DesktopEntry.Build(body: DesktopEntry.Compress("""
        { "five_hour": { "utilization": 7.0, "resets_at": "2026-09-09T18:39:59.636345+00:00" } }
        """), responseDate: null, key: DesktopEntry.UsageKey(query: string.Empty));
        var newer = DesktopEntry.Build(responseDate: null);

        var files = new FakeClaudeFileSystem();
        var directory = ClaudeDesktopUsageCache.DefaultDirectory(Home);
        files.AddFile(Path.Combine(directory, "older_0"), older, Now.AddSeconds(-60));
        files.AddFile(Path.Combine(directory, "newer_0"), newer, Now);

        var reading = Assert.IsType<ClaudeDesktopUsageCache.Reading>(
            Cache(files, Home).Read(DesktopEntry.Organization, Now));

        Assert.Equal(0.30, reading.Windows[0].UsedFraction!.Value, 4);
        Assert.EndsWith("newer_0", reading.EntryFile);
    }

    /// <summary>With no `Date:` header the entry's own modification time stands in, not "now".</summary>
    [Fact]
    public void WithNoDateHeaderTheFilesModificationTimeIsUsed()
    {
        var written = new DateTimeOffset(2026, 9, 9, 15, 0, 0, TimeSpan.Zero);
        var files = new FakeClaudeFileSystem();
        files.AddFile(
            Path.Combine(ClaudeDesktopUsageCache.DefaultDirectory(Home), "a_0"),
            DesktopEntry.Build(responseDate: null),
            written);

        var reading = Assert.IsType<ClaudeDesktopUsageCache.Reading>(
            Cache(files, Home).Read(DesktopEntry.Organization, Now));

        Assert.Equal(written, reading.CapturedAt);
    }

    /// <summary>Chromium evicts entries while we are looking at them; a vanished file is not an error.</summary>
    [Fact]
    public void AnEntryThatVanishesDuringTheScanIsSkipped()
    {
        var directory = ClaudeDesktopUsageCache.DefaultDirectory(Home);
        var files = new FakeClaudeFileSystem();
        files.AddFile(Path.Combine(directory, "doomed_0"), DesktopEntry.Build(responseDate: null), Now.AddSeconds(60));
        files.AddFile(
            Path.Combine(directory, "survivor_0"),
            DesktopEntry.Build(
                body: DesktopEntry.Compress("""
                { "five_hour": { "utilization": 7.0, "resets_at": "2026-09-09T18:39:59.636345+00:00" } }
                """),
                responseDate: null),
            Now);

        // The doomed one is newer, so the scan reaches it first — and it is gone by then.
        files.Vanished = path => Path.GetFileName(path) == "doomed_0";

        var reading = Assert.IsType<ClaudeDesktopUsageCache.Reading>(
            Cache(files, Home).Read(DesktopEntry.Organization, Now));

        Assert.EndsWith("survivor_0", reading.EntryFile);
    }

    [Fact]
    public void AMissingDirectoryReadsAsNothingThroughTheRealFileSystem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quotadeck-claude-absent-" + Guid.NewGuid().ToString("N"));

        Assert.Null(new ClaudeDesktopUsageCache(directory, new SystemClaudeFileSystem())
            .Read(DesktopEntry.Organization, Now));
    }

    /// <summary>
    /// The real filesystem, once: the fake proves the format, this proves the plumbing — a listing
    /// with sizes and modification times, a capped read, and a chosen newest entry.
    /// </summary>
    [Fact]
    public void TheRealFileSystemReadsAnEntryFromADirectory()
    {
        var directory = Directory.CreateTempSubdirectory("quotadeck-claude-cache-");
        try
        {
            File.WriteAllBytes(
                Path.Combine(directory.FullName, "entry_0"),
                DesktopEntry.Build(responseDate: null));
            File.SetLastWriteTimeUtc(
                Path.Combine(directory.FullName, "entry_0"),
                new DateTime(2026, 9, 9, 16, 5, 0, DateTimeKind.Utc));

            var reading = Assert.IsType<ClaudeDesktopUsageCache.Reading>(
                new ClaudeDesktopUsageCache(directory.FullName, new SystemClaudeFileSystem())
                    .Read(DesktopEntry.Organization, Now));

            Assert.Equal(new DateTimeOffset(2026, 9, 9, 16, 5, 0, TimeSpan.Zero), reading.CapturedAt);
            Assert.Equal(["session", "weekly_all"], reading.Windows.Select(window => window.Id));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // MARK: - Fresh, stale, unavailable

    [Fact]
    public void ARecentSnapshotIsFresh()
    {
        var reading = Assert.IsType<ClaudeDesktopUsageCache.Reading>(
            ClaudeDesktopUsageCache.Parse(DesktopEntry.Build()) is { } parsed
                ? new ClaudeDesktopUsageCache.Reading(
                    ClaudeUsageResponse.Decode(System.Text.Encoding.UTF8.GetString(parsed.Body)).LimitWindows(Now),
                    parsed.Date!.Value,
                    "entry_0")
                : null);

        Assert.True(reading.IsFresh(Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void ASnapshotOlderThanTheWindowIsNotFresh()
    {
        var captured = Now.AddMinutes(-90);
        var reading = new ClaudeDesktopUsageCache.Reading([], captured, "entry_0");

        Assert.False(reading.IsFresh(Now, TimeSpan.FromMinutes(30)));

        // A negative age is two clocks disagreeing, not a reading from the future.
        Assert.True(new ClaudeDesktopUsageCache.Reading([], Now.AddSeconds(60), "entry_0")
            .IsFresh(Now, TimeSpan.FromMinutes(30)));
    }
}
