namespace QuotaDeck.Readers.Tests.OpenCode;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using QuotaDeck.Core.Readers;
using QuotaDeck.Core.Readers.Services;
using QuotaDeck.Core.Settings;
using QuotaDeck.Readers.OpenCode;
using QuotaDeck.Readers.Tests.Support;

/// <summary>A scratch directory for one test, removed on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        this.Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "quotadeck-opencode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(this.Path, name);

    public void Write(string name, string content) => System.IO.File.WriteAllText(this.File(name), content);

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Path, recursive: true);
        }
        catch
        {
            // Best effort: a leaked temp directory must not fail a test.
        }
    }
}

/// <summary>
/// The reader under test, wired to the shared fakes. The OpenCode data directory is injected; the
/// context's own DataDirectory is QuotaDeck's store and stays untouched.
/// </summary>
internal static class OpenCodeTestReader
{
    /// <summary>
    /// Verbatim in shape from <c>GET /zen/go/v1/usage</c> (the Codenotch fixture, secret redacted;
    /// already synthetic).
    /// </summary>
    public const string LivePayload =
        """
        {"usage":{
        "rolling":{"status":"ok","percent":0,"resetsAt":"2026-09-06T12:31:06.611Z"},
        "weekly":{"status":"ok","percent":0,"resetsAt":"2026-09-07T00:00:00.611Z"},
        "monthly":{"status":"ok","percent":0,"resetsAt":"2026-10-03T13:09:45.611Z"}}}
        """;

    public static FakeClock DefaultClock => new(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));

    public static (OpenCodeReader Reader, FakeTransport Transport, InMemoryReaderStateStore Store, FakeClock Clock) Create(
        string directory,
        FakeClock? clock = null)
    {
        var transport = new FakeTransport();
        var store = new InMemoryReaderStateStore();
        clock ??= DefaultClock;

        var context = new ReaderContext
        {
            Config = new ReaderConfig(),
            StateStore = store,
            TimeProvider = clock,
            DataDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quotadeck-reader-data"),
            Http = transport,
            Process = new FakeProcessRunner(),
        };

        return (new OpenCodeReader(context, directory), transport, store, clock);
    }

    /// <summary>
    /// The <c>credential</c> table shaped as OpenCode writes it, built through Microsoft.Data.Sqlite
    /// in a throwaway file — never a copy of a real store.
    /// </summary>
    public static string CreateCredentialDatabase(
        TempDirectory directory,
        params (string IntegrationId, string Value, int? Active, long Updated)[] rows)
    {
        var path = directory.File("opencode.db");
        // Pooling off so the fixture's file state is on disk when the reader claims it.
        using var database = new SqliteConnection($"Data Source={path};Pooling=False");
        database.Open();

        using (var create = database.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE credential (id text PRIMARY KEY, integration_id text, label text NOT NULL,
                 value text NOT NULL, connector_id text, method_id text, active integer,
                 time_created integer NOT NULL, time_updated integer NOT NULL)
                """;
            create.ExecuteNonQuery();
        }

        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            using var insert = database.CreateCommand();
            insert.CommandText =
                "INSERT INTO credential VALUES ($id, $integration, 'x', $value, NULL, NULL, $active, 0, $updated)";
            insert.Parameters.AddWithValue("$id", $"cred_{index}");
            insert.Parameters.AddWithValue("$integration", row.IntegrationId);
            insert.Parameters.AddWithValue("$value", row.Value);
            insert.Parameters.AddWithValue("$active", row.Active is { } active ? active : DBNull.Value);
            insert.Parameters.AddWithValue("$updated", row.Updated);
            insert.ExecuteNonQuery();
        }

        return path;
    }
}

/// <summary>
/// A second scripted transport for the two answers <see cref="FakeTransport"/> cannot express from
/// its queue: response headers (the 429's <c>Retry-After</c>) and a body that is not UTF-8. The
/// shared fake stays the default everywhere else.
/// </summary>
internal sealed class ScriptedTransport : IHttpTransport
{
    private readonly Queue<Func<HttpResponseMessage>> _queue = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<TimeSpan> Timeouts { get; } = [];

    public void Respond(HttpStatusCode status, string body = "", params (string Name, string Value)[] headers) =>
        this._queue.Enqueue(() =>
        {
            var message = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }

            return message;
        });

    public void RespondBytes(HttpStatusCode status, byte[] body, string contentType = "application/json") =>
        this._queue.Enqueue(() =>
        {
            var message = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return message;
        });

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        this.Requests.Add(request);
        this.Timeouts.Add(timeout);

        return Task.FromResult(
            this._queue.Count > 0
                ? this._queue.Dequeue()()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(string.Empty) });
    }
}
