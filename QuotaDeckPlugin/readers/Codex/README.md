# Codex reader

Port of Codenotch's Codex data path (`Sources/Providers/CodexLocalProvider.swift`,
`CodexUsage.swift`, `CodexCredentials.swift`, `CodexProfile.swift`) to the QuotaDeck reader
contract.

## What it reads

- **Credential** (`CodexCredentials.cs`): `~/.codex/auth.json`, re-read on every fetch so Codex's
  own token rotation is picked up. `CODEX_HOME` is deliberately not read. The access token's JWT
  payload is decoded without verifying the signature for the `exp` hint; the account label and plan
  come from the `id_token`'s claims. The `account_id` is sent only as a header, never displayed or
  logged.
- **Usage** (`CodexUsage.cs`): `GET https://chatgpt.com/backend-api/wham/usage` — the main
  primary/secondary windows, the Spark and code-review extras (grouped, deduplicated, switchable
  with `ShowExtraLimits`, default on), and the credit seat (`spend_control.individual_limit`) when
  an account has no rolling windows. Labels derive from the reported window length, so a free
  plan's 30-day primary is a "Monthly limit" rather than nothing.
- **Reset credits** (`CodexResetCredits.cs`): `GET .../wham/rate-limit-reset-credits` is fetched
  concurrently and failures are ignored, exactly like Codenotch. The parsed value is currently
  dropped because `ProviderSnapshot` has no field for it yet; the parser and its fixtures are here
  for the follow-up.
- Not read: `/wham/profiles/me` (the token chart — there is no face for it).

## HTTP mapping

401/403 → `NeedsAuth`; 429 → `RateLimited` with `max(60 s, Retry-After)` (seconds or HTTP date);
other non-2xx → `BadResponse(status)`; undecodable JSON → `BadResponse(0)`; no windows at all →
`NothingMetered`. Every request gets a 15 s budget and no caching.

## Profiles

`CodexProfile.cs` discovers `~/.codex` plus each `~/.codex-<slug>` sibling that carries a profile
marker (auth.json, config.toml, sessions/, history.jsonl, state_5.sqlite, sqlite/codex-dev.db).
This PR exposes **only the default profile** as a key; extra profiles are discovered, logged once
through the module's `LogSink`, and become keys in a follow-up after Checkpoint 1. Profiles are
strictly isolated — a reader never falls back to another account's credential.

## Tests

`tests-readers/Codex/` mirrors Codenotch's fixtures (`CodexUsageTests.swift`,
`CodexResetCreditsTests.swift`, `CodexProfileTests.swift`) on `FakeTransport` + `FakeClock` and a
temp home directory. The URLProtocol stub of `CodexProfileTests.swift:243-272` is mirrored by
`CodexEndpointStub`; `CodexScriptedTransport` adds the `Retry-After` header seam the shared
`FakeTransport` does not have.

The live test is gated on `QUOTADECK_LIVE_CODEX=1` and is a no-op otherwise:

```bash
QUOTADECK_LIVE_CODEX=1 dotnet test QuotaDeckPlugin/tests-readers/QuotaDeck.Readers.Tests.csproj \
  --filter FullyQualifiedName~CodexLiveTests
```

It asserts only that a snapshot with a `primary` window came back; it prints nothing about the
account.

## Known gaps

- `ShowExtraLimits` is a reader/module property, not a `config.json` setting — core's
  `ReaderConfig` has no per-provider options bag.
- Reset credits are parsed but not attached to the snapshot (no core field).
- The reader host on `main` discovers `CodexModule` and builds the default-profile reader with the
  defaults; nothing extra is needed to wire it up.
