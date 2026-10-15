# QuotaDeckPlugin

C# plugin for the Logi Actions SDK that renders QuotaDeck's provider quota faces on the nine 118 px
LCD keys of the Logitech MX Creative Keypad.

**Status: Phase 2c ("plugin host").** The state engine in `core/` is joined by the plugin's reader
host and seven provider keys: the host discovers provider modules, fetches on the schedule, keeps
the latest reading per provider, and raises notifications and the panel pulse. No provider reader is
implemented yet — every key says "not installed" unless a fixture stands in for it.

## Requirements

- macOS (Windows support comes later)
- .NET SDK 10 (`dotnet --version` → 10.0.x)
- Logi Plugin Service (`/Applications/Utilities/LogiPluginService.app`), installed with Logi Options+
- Logitech MX Creative Keypad for the on-device check

## Build

```bash
export PATH="/opt/homebrew/bin:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
export DOTNET_ROLL_FORWARD=Major

cd QuotaDeckPlugin/src
dotnet build -c Debug
```

The build does three extra things after compiling:

1. copies `src/package/**` next to `bin/`, so the output folder contains `metadata/` and `bin/`;
2. writes the plugin link file
   (`$HOME/Library/Application Support/Logi/LogiPluginService/Plugins/QuotaDeckPlugin.link`) with the
   single line `<repo>/QuotaDeckPlugin/bin/Debug`;
3. asks the running Logi Plugin Service to reload QuotaDeck (`open loupedeck:plugin/QuotaDeck/reload`).

A fourth post-build copy flattens native libraries next to the managed ones. NuGet lays SQLite's
`libe_sqlite3.dylib` (and any future native dependency) out under `runtimes/<rid>/native/`, but a
host that loads the plugin DLLs from `bin/Debug/bin/` does not probe that tree for `DllImport`
libraries — a plain .NET host there fails with `DllNotFoundException: e_sqlite3`, and the Logi
Plugin Service's own probing is unverified. Every native library for the current platform
(`osx-arm64` then `osx-x64`, arm64 winning when a file name exists in both; `win-x64` on Windows)
is copied flat next to `QuotaDeckPlugin.dll`; the `runtimes/` tree is left in place.

If the service does not pick the plugin up, restart it and wait ~6 s:

```bash
killall LogiPluginService
```

## Where things live on macOS

| What | Path |
| --- | --- |
| Plugin link | `$HOME/Library/Application Support/Logi/LogiPluginService/Plugins/QuotaDeckPlugin.link` |
| Plugin log | `$HOME/Library/Application Support/Logi/LogiPluginService/Logs/plugin_logs/QuotaDeck.log` |
| Config | `$HOME/Library/Application Support/QuotaDeck/config.json` |
| Snapshots | `$HOME/Library/Application Support/QuotaDeck/snapshots/` |
| Trigger file | `$HOME/Library/Application Support/QuotaDeck/trigger.txt` |

`config.json` carries `debugSnapshots` plus the state engine's settings (defaults shown):

```json
{
  "debugSnapshots": false,
  "showRemaining": true,
  "watchLimit": 0.5,
  "criticalLimit": 0.7,
  "countdownStyle": "Hours",
  "providers": {},
  "order": []
}
```

- `showRemaining` flips every face between what is left (the default) and what was used. Bands are
  always judged by the *used* fraction, whichever end the figures show.
- `watchLimit` / `criticalLimit` are the band boundaries. They are clamped on load: the watch limit
  stays below the critical limit, and the pair inside 0.01–1.0.
- `countdownStyle` is the corner countdown's unit. `"Hours"` (the default) is Codenotch's own form,
  "71h 00m"; `"Days"` shortens a multi-day window to "2d 23h" and leaves everything below a day
  unchanged.
- `providers` is per-reader enablement, schedule and rate-limit behaviour, keyed by reader id:
  `enabled`, `busyIntervalSeconds`, `idleIntervalSeconds`, `staleAfterMinutes`,
  `rateLimitBackoff`. A provider missing from the file gets the defaults (enabled, 30 s busy, 5 min
  idle, 15 min stale, `"Ladder"`).
- `order` is the face order (provider ids, first is leftmost). Stored ids no provider claims are
  ignored, and providers the order has never seen are appended at the end.

The 429 wait follows the provider, and each choice ports its Swift counterpart: `"Ladder"` (the
default) starts at 60 s, doubles for each 429 in a row and stops at 900 s with the server's
`Retry-After` only ever raising the floor — Codenotch's Claude
(`Sources/Providers/ClaudeOAuthProvider.swift`, `backoff(forAttempt:retryAfter:)`) and OpenCode
(`OpenCodeProvider.swift:206-210`); `"Flat"` waits `max(60 s, hint)` on every 429 with no doubling —
Codex (`CodexLocalProvider.swift:161-173`, pinned by its reader), Devin
(`DevinLocalProvider.swift:64`) and Muse (`MuseLocalProvider.swift:96`). Cursor has no backoff logic
of its own. Whichever the setting says, the deadline is persisted per reader, so a relaunch cannot
walk back into the limit.

## Keys and the host

Seven keys, one per provider — Claude, Codex, OpenCode, Antigravity, Cursor, Muse, Devin — under
**Quota Deck** in Logi Options+. Each draws the provider's latest reading: the headline (what is
left by default), the weekly figure, the band colour, the reset countdown, and a greyed face with an
overlay when the reading is stale or refused. A provider with no reader module and no fixture says
"not installed".

`ReaderHost`, created at plugin load, owns the loop: a 1 s tick asks the refresh coordinator which
providers are due (a window rollover first, then the busy/idle intervals), fetches in the
background, redraws that provider's key, and feeds the threshold, limit and reset watchers.
Pressing a key asks for a from-source fetch, spaced 15 s per provider.

## Fixture mode

With `debugSnapshots: true` and a `fixtures/` directory next to `config.json`, every
`<providerId>.json` in it drives that provider's key — no reader, credential or network needed, and
it works for providers with no module yet. Fixtures are re-read on every fetch, so editing one shows
up on the next tick; they are kept in memory and never overwrite the real reading archive. The
`fixtures/` folder in this repo has examples (Claude, Codex, OpenCode, a `needsAuth` face and a
`stale` face) and the format — see `fixtures/README.md`.

## Alerts

Crossings are announced once each: the 80% crossing sends a notification; 100% and a session or
weekly limit-reached event send a notification and play the red panel pulse; a window reset sends a
notification. At most one pulse every 10 s, whatever asked for it. Notifications go out through
`osascript` (macOS only) and are logged either way.

## Dev harness

Set `debugSnapshots` to `true` and reload the plugin. Every rendered key face is then also saved to
the snapshots folder as `<KeyClassName>_<size>.png`, and a trigger watcher starts.

Write one line into the trigger file (it is deleted after reading, and each trigger is logged):

```bash
DATA="$HOME/Library/Application Support/QuotaDeck"
echo snapshot > "$DATA/trigger.txt"          # re-render every face at Width116 and Width90
echo pulse > "$DATA/trigger.txt"             # play the 3-pulse alert scene on the whole panel
echo fetch > "$DATA/trigger.txt"             # fetch every reader from source, then redraw
echo "fetch claude" > "$DATA/trigger.txt"    # fetch one reader from source
echo SampleFaceCommand > "$DATA/trigger.txt" # run one key's action, then re-render it
```

While a scene plays, key faces are replaced by each key's window of one shared 480x480 frame
(`panel_NN.png`; `panelview_NN.png` shows it with the bezels blacked out).

## Seeing it on the keypad

The on-device check needs a human with Logi Options+:

1. Open Logi Options+, select the MX Creative Keypad.
2. **All actions > Quota Deck > Sample face** (or any of the seven provider keys).
3. Drag it onto any key.

The teal face appears on that key, showing what is left by default for the sample's 73%-used reading
(`27%`, `wk 59%`, a 27% ring); with `"showRemaining": false` it shows the used figures (`73%`,
`wk 41%`, 73%). Pressing it plays the red panel pulse across all nine keys. The provider keys render
the host's latest reading — with fixtures copied in, that works without any reader.

## Tests

```bash
dotnet test QuotaDeckPlugin/tests/QuotaDeck.Core.Tests.csproj
dotnet test QuotaDeckPlugin/tests-readers/QuotaDeck.Readers.Tests.csproj
```

## Layout

```
core/                     the provider-agnostic state engine
  Alerts/                 notification copy (threshold, limit, reset)
  Faces/                  FaceModel, FaceComposer, FaceColors, fixture format
  Model/                  snapshots, statuses, bands, watchers, face figures
  Readers/                reader contract, degradation, stores, modules, fixture reader
  Policy/                 backoff, refresh schedule, refresh coordinator, freshness
  Text/                   percent and countdown copy
  Settings/               settings, per-reader config, provider order
fixtures/                 synthetic readings for fixture mode + README
readers/                  provider reader implementations (one folder per provider) + Shared/
tests/                    xunit suite for core/ (and the only project that runs without the SDK)
tests-readers/            xunit suite for readers/, with scripted HTTP/process/clock fakes
src/
  QuotaDeckPlugin.cs        plugin entry point: reader host, 1 s ticker, dev harness
  QuotaDeckApplication.cs   required empty ClientApplication subclass (universal plugin host quirk)
  Hosting/ReaderHost.cs     reader loop: discovery, schedule, keys, watchers, alerts, pulse
  Hosting/MacNotifier.cs    osascript notifications (macOS; no-op elsewhere)
  Actions/QuotaKey.cs       base key: press-only + 350 ms debounce, face rendering, snapshots
  Actions/ProviderKey.cs    base for the seven provider keys (palette, band colours, dimming)
  Actions/*QuotaCommand.cs  the seven provider keys
  Actions/SampleFaceCommand.cs  the Phase 1 key
  Faces/Face.cs             one face's data (accent, ring, texts, overlay)
  Faces/FaceRenderer.cs     draws a face at Width116 / Width90
  Panel/Panel.cs            nine keys as one 480x480 screen (ported from neon-deck, MIT)
  Panel/Layout.cs           which key is in which grid cell, from the Logi profile files
  Panel/PulseScene.cs       the 100 % alert pulse
  Helpers/DeckConfig.cs     data directory + config.json
  Helpers/PluginLog.cs      template logging boilerplate
  Helpers/PluginResources.cs  template resource boilerplate
  package/                  manifest, icon, full-face icon template, action symbols
```

## License

MIT. Portions of the keypad behaviour and panel geometry are ported from
[neon-deck](https://github.com/k12club/neon-deck) (MIT); see `THIRD_PARTY_NOTICES.md` at the repo root.
