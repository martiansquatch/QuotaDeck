namespace QuotaDeck.Readers.OpenCode;

using Microsoft.Data.Sqlite;
using QuotaDeck.Readers.Shared;

/// <summary>Which OpenCode wrote <c>opencode.db</c>.</summary>
public enum OpenCodeSchemaVersion
{
    /// <summary>OpenCode 1.x: <c>message</c>/<c>session</c> tables, the role inside the row's JSON.</summary>
    V1,

    /// <summary>OpenCode 2.x: <c>session_message</c>/<c>session_v2</c>, the role a column.</summary>
    V2,
}

/// <summary>
/// The schema probe, ported from Codenotch <c>Sources/Providers/OpenCodeSchema.swift</c>
/// (<c>of(_:)</c> lines 30-35 and <c>hasTable(_:in:)</c> lines 37-42).
///
/// 2.x renamed <c>message</c> to <c>session_message</c> and <c>session</c> to <c>session_v2</c>.
/// Neither shape can be read with the other's SQL — a query naming a table that does not exist does
/// not return nothing, it fails to prepare, and the read-only helper answers an empty set for that,
/// which reads as "spent nothing" rather than as "cannot read". Probed rather than assumed, because
/// both are in the wild and there is no version string in the store to ask.
///
/// **<c>session_message</c> alone does not say 2.x.** 1.18 already ships that table beside
/// <c>message</c> and <c>session</c>; what only 2.x has is <c>session_v2</c>, and what every 1.x has
/// is <c>message</c>. The Go usage path does not depend on this probe at all — the
/// <c>credential</c> table is the same for both schemas, and that is all
/// <see cref="OpenCodeCredentials"/> reads — but the probe is ported for the message-table readers
/// this folder will grow (OpenCode Gemini usage), which do.
/// </summary>
public static class OpenCodeSchema
{
    /// <summary>Which shape the database is, or null for one that is neither.</summary>
    public static OpenCodeSchemaVersion? Of(SqliteConnection? database)
    {
        if (HasTable(database, "session_v2"))
        {
            return OpenCodeSchemaVersion.V2;
        }

        if (HasTable(database, "message"))
        {
            return OpenCodeSchemaVersion.V1;
        }

        if (HasTable(database, "session_message"))
        {
            return OpenCodeSchemaVersion.V2;
        }

        return null;
    }

    /// <summary>
    /// Whether the store has a table with this name. The name is bound as <c>?1</c> rather than
    /// interpolated — the Swift version splices the name into the SQL text; parameter binding is
    /// the same query without the injection-shaped string building.
    /// </summary>
    public static bool HasTable(SqliteConnection? database, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return SqliteReadOnly
            .Column(database, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = ?1", name)
            .Count > 0;
    }
}
