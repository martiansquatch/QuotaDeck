Reader for Claude; port of Codenotch `Sources/Providers/Claude*.swift` (`ClaudeOAuthProvider`, `ClaudeUsageCLI`, `ClaudeProfile`).

What is here:

- `ClaudeModule` / `ClaudeReader` — the provider module and the three-source chain (Claude Desktop's
  cache, then `claude /usage`, then the keychain token and `GET /api/oauth/usage`), with each
  source's freshness allowance.
- `ClaudeUsageResponse` — the endpoint's decoder, shared with the Desktop cache because they carry
  the same document.
- `ClaudeDesktopUsageCache` — Chromium Simple Cache entries, with the zstd frame walk.
- `ClaudeUsageCli` / `ClaudeStandaloneCli` — where `claude` is found, how `/usage` is run, and what
  its text means.
- `ClaudeProfile` — `~/.claude` and `~/.claude-<slug>` discovery, naming and the account file.
- `ClaudeCredentials` / `CredentialCache` — the keychain item and how rarely it is read;
  `ClaudeTokenRefresher` — the `claude -p` renewal, driven from the plugin's tick.
- `../Shared/MacKeychain.cs` — the keychain seam (`IKeychain`) Cursor, Muse and Antigravity will
  reuse: attributes-only enumeration, then one data read with interaction off.

Only the default profile (`~/.claude`) is drawn in this build; discovery finds the other logins and
logs them. Reset credits (`cedar_ember`) have no field in `QuotaDeck.Core`'s snapshot and are not
carried.
