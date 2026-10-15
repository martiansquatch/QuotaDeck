Reader for OpenCode; port of Codenotch `Sources/Providers/OpenCode*.swift`
(`OpenCodeProvider`, `OpenCodeUsage`, `OpenCodeCredentials`, `OpenCodeSchema`) plus the read-only
store open from `Sources/Providers/SQLiteStore.swift`, shared as
`readers/Shared/SqliteReadOnly.cs`.

- ProviderId `opencode`, display name OpenCode, palette `F5A524`. Present when OpenCode's data
  directory (`$XDG_DATA_HOME/opencode`, else `~/.local/share/opencode`) holds `auth.json` or
  `opencode.db`.
- Credential lookup, first hit wins, re-read every fetch: `auth.json` → `opencode-go`, then the
  database's `credential` table (Go key row, then OAuth row), then `auth.json` → `opencode` when it
  is an OAuth entry.
- Routes: the Go key reads `https://opencode.ai/zen/go/v1/usage`; OAuth reads
  `https://opencode.ai/inference/go/v1/usage`; a 401 on OAuth is checked against the console's
  `/api/user` before it is called a sign-out.
- SQLite comes from Microsoft.Data.Sqlite's native folder
  (`runtimes/<rid>/native/libe_sqlite3.dylib`). Where the host cannot load it, the reader logs
  through `OpenCodeCredentials.Warning` and keeps reading `auth.json` only.
