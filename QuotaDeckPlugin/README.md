# QuotaDeckPlugin

C# plugin for the Logi Actions SDK that renders QuotaDeck's provider quota faces on the nine 118 px
LCD keys of the Logitech MX Creative Keypad.

**Status: Phase 1 ("hello deck").** One static sample face, a headless snapshot harness, and the
full-panel pulse — no provider readers yet.

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

`config.json` currently has one key:

```json
{
  "debugSnapshots": false
}
```

## Dev harness

Set `debugSnapshots` to `true` and reload the plugin. Every rendered key face is then also saved to
the snapshots folder as `<KeyClassName>_<size>.png`, and a trigger watcher starts.

Write one line into the trigger file (it is deleted after reading, and each trigger is logged):

```bash
DATA="$HOME/Library/Application Support/QuotaDeck"
echo snapshot > "$DATA/trigger.txt"          # re-render every face at Width116 and Width90
echo pulse > "$DATA/trigger.txt"             # play the 3-pulse alert scene on the whole panel
echo SampleFaceCommand > "$DATA/trigger.txt" # run one key's action, then re-render it
```

While a scene plays, key faces are replaced by each key's window of one shared 480x480 frame
(`panel_NN.png`; `panelview_NN.png` shows it with the bezels blacked out).

## Seeing it on the keypad

The on-device check needs a human with Logi Options+:

1. Open Logi Options+, select the MX Creative Keypad.
2. **All actions > Quota Deck > Sample face**.
3. Drag it onto any key.

The teal "73%" face appears on that key. Pressing it plays the red panel pulse across all nine keys.

## Layout

```
src/
  QuotaDeckPlugin.cs        plugin entry point: 1 s ticker, dev harness
  QuotaDeckApplication.cs   required empty ClientApplication subclass (universal plugin host quirk)
  Actions/QuotaKey.cs       base key: press-only + 350 ms debounce, face rendering, snapshots
  Actions/SampleFaceCommand.cs  the Phase 1 key
  Faces/Face.cs             one face's data (accent, ring, texts)
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
