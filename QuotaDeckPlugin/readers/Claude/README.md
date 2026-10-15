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

The endpoint's 429 hold-off is the reader's own to apply: `ClaudeReader` takes
`HoldOffScope.ReaderManaged`, so a penalty stands for the usage endpoint alone and the Desktop cache
and `claude /usage` still get their turn — Codenotch checks `retryNoEarlierThan` inside the keychain
step (`ClaudeOAuthProvider.swift:303-309`), after both. The reader honours the contract that scope
comes with: while the penalty stands it throws `ReaderError.RateLimited(remaining)` *without*
calling `MarkRateLimitedSourceConsulted()`, and the base reads an unmarked throw as the replay it is
— no ladder rung, no deadline write, and the last reading degrades to Stale dated from when it was
actually taken. Immediately before a request it does make, it marks the source consulted, so a real
429 takes its rung and rewrites the deadline and a real success clears both. A cache or CLI answer
is never marked, so it leaves the endpoint's penalty and the ladder exactly where they were. The
token renewal is one atomic claim (`ClaudeTokenRefresher.TryClaim`): two fetches finishing together
cannot launch two `claude -p` processes for one expiry, and the renewal runs on its own task *after*
the reading, so a slow `claude` start never spends the fetch's deadline.
