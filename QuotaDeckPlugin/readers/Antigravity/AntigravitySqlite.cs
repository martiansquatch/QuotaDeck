namespace QuotaDeck.Readers.Antigravity;

using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>
/// Read-only SQLite access, opened against a database another application may be writing.
///
/// TODO: replace with <c>readers/Shared/SqliteReadOnly.cs</c> once the OpenCode reader PR that adds
/// it lands; this is the minimal local equivalent so this PR does not touch shared files.
///
/// Opens with <c>mode=ro</c> first and falls back to <c>immutable=1</c> (which skips locking
/// entirely, for a WAL database this process cannot take a shared lock on). Every value comes back
/// as text, formatted invariantly, and any failure is an empty result — never an exception: these
/// databases belong to other tools and a reader must not fail over them.
/// </summary>
internal static class AntigravitySqlite
{
    public static IReadOnlyList<IReadOnlyList<string?>> Query(
        string path,
        string sql,
        IReadOnlyList<object?>? parameters = null)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        foreach (var connectionString in new[]
                 {
                     $"Data Source=file:{path}?mode=ro;Mode=ReadOnly",
                     $"Data Source=file:{path}?immutable=1",
                 })
        {
            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                for (var index = 0; index < (parameters?.Count ?? 0); index++)
                {
                    command.Parameters.AddWithValue($"@p{index}", parameters![index] ?? DBNull.Value);
                }

                using var reader = command.ExecuteReader();
                var rows = new List<IReadOnlyList<string?>>();
                while (reader.Read())
                {
                    var row = new string?[reader.FieldCount];
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        row[column] = reader.IsDBNull(column)
                            ? null
                            : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture);
                    }

                    rows.Add(row);
                }

                return rows;
            }
            catch
            {
                // Locked, corrupt, or not a database: try the next mode, then give up quietly.
            }
        }

        return [];
    }
}
