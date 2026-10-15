# Fixtures

Synthetic provider readings so every key can be rendered — on the device or as debug snapshots —
without a provider, a credential or a network.

Fixture mode is on when `debugSnapshots` is `true` in `config.json` and a `fixtures/` directory
exists next to it:

| Platform | Fixtures directory |
| --- | --- |
| macOS | `$HOME/Library/Application Support/QuotaDeck/fixtures/` |
| Windows | `%LOCALAPPDATA%\QuotaDeck\fixtures\` |
| other | `$XDG_DATA_HOME/QuotaDeck/fixtures/` |

Copy any of the files here into that directory and reload the plugin (or write `fetch` into
`trigger.txt`). The file name is the provider id, so `claude.json` drives the Claude key. A fixture
works even when no reader module for that provider exists yet.

A fixture is re-read on every fetch, so editing one shows up on the next tick — no reload needed.
Fixture readings are kept in memory only; they never overwrite the real reading archive.

## The files

| File | Reading |
| --- | --- |
| `claude.json` | 73% used session, 41% used week — a healthy Max-plan reading |
| `codex.json` | 21% used primary window, 8% used week |
| `opencode.json` | 55% used rolling window, 30% weekly, 12% monthly |
| `muse.json` | `needsAuth`: no windows, a sign-in prompt |
| `devin.json` | `stale`: a 34% reading marked old |

With the default `showRemaining: true`, the three healthy readings render **27%**, **79%** and
**45%**; `devin.json` renders greyed with its 66%, and `muse.json` renders as an empty face.

## Dates

`resetsAt` values are absolute. These examples use March 2027, so the corner countdown
(`↻ 2h 05m`) shows only while that moment is in the future — once it passes, the corner is empty
and the numbers do not change (fixtures do not roll over like a real provider would). `since` on the
stale example is an October 2026 date, in the past.

## Format

```json
{
  "id": "claude",
  "displayName": "Claude",
  "fidelity": "official",
  "status": { "kind": "ok" },
  "windows": [
    { "id": "session", "label": "5-hour limit", "usedFraction": 0.73, "resetsAt": "2027-03-13T15:30:00+00:00" }
  ],
  "headlineId": "session",
  "weeklyId": "weekly_all",
  "plan": "Max"
}
```

- `fidelity`: `official`, `derived` or `manual`.
- `status.kind`: `ok`, `stale` (with `since`), `needsAuth`, `signedOutByOwner`, `accessDenied`,
  `unsupported` (with `message`) or `error` (with `message`).
- windows: `id`, optional `group`, `label`, optional `usedFraction`, optional `resetsAt`, optional
  `durationSeconds`.
- A malformed fixture reads as no fixture: the key says `not installed` rather than failing.
