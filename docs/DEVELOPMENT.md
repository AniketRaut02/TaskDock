# TaskDock: development notes

What was built against the [execution plan](execution-plan.pdf), how each part was verified, and what is still open. For installing and using TaskDock, see the [README](../README.md).

## Repository layout

```
protocol/                   wire format: JSON Schema + golden sample frames (contract tests)
src/TaskDock.Core/          pure logic: tiles, per-adapter rules, notify, presets, resolver
src/TaskDock.Hub/           local server: Kestrel on 127.0.0.1, auth, WebSocket adapters, Claude hooks
src/TaskDock.Platform/      Win32 + WinRT: windows, focus, hung check, media controls
src/TaskDock.App/           the WPF widget, tray, settings, Claude hooks installer
adapters/unity/             Unity Editor package (com.darkpixelgd.taskdock)
adapters/vscode/            VS Code extension (TypeScript, esbuild)
adapters/chrome/            browser downloads extension (Manifest V3)
tools/TaskDock.FakeAdapter/ replays adapter sessions for UI work, load and screenshots
tools/release.ps1           builds every release artifact
tests/                      xUnit: Core and Hub (in-process Kestrel, real sockets)
```

## Tests

```bash
dotnet test TaskDock.sln
```

```bash
node --test "adapters/chrome/test/*.test.mjs"
```

```bash
cd adapters/vscode && npm install && npm test
```

The Unity package's EditMode tests run in any project that lists `com.darkpixelgd.taskdock` under `testables` in `Packages/manifest.json`.

## FakeAdapter

With TaskDock running:

```bash
dotnet run --project tools/TaskDock.FakeAdapter -- claude 30
```

```bash
dotnet run --project tools/TaskDock.FakeAdapter -- showcase 120
```

Modes: `demo` (Unity, VS Code and downloads cycling through their states), `claude` (one Claude Code session with a held approval), `load` (8 sources at 10 updates/s), `showcase` (steady states, used for the README screenshots).

## Status by plan item

| Plan item | State |
|---|---|
| Core: protocol DTOs, status classes, per-adapter rules, TileRegistry (10 Hz coalescing), NotifyEngine (connect grace, 1 sound/s, mute, quiet hours), preset model + matcher | Done, unit-tested |
| Hub: Kestrel on 127.0.0.1:47820–47829, `hub.json`, token, Host/Origin checks, no CORS, `/health`, `/ws`, `/hooks/claude/*` | Done, integration-tested over real sockets |
| Claude Code adapter: session state machine, held PermissionRequest (Allow / Always allow / Deny + reason / Terminal / timeout), hooks installer (backup, idempotent, disconnect, rotate token, auto re-point on port change) | Done |
| Widget: borderless, topmost, no focus stealing, hidden from taskbar and Alt-Tab, rounded corners, corner snapping that grows toward the screen middle, re-snap on display/DPI/work-area change, expanded/compact/pill modes, `Ctrl+Alt+Shift+D`, tray with detected-count badge, fullscreen auto-hide, approval card, detected-sources tray, pins + Notify bell saved to the preset, Launch for pinned tiles | Done; checked by hand with FakeAdapter |
| Sounds: three generated tones, overridable in `%APPDATA%\TaskDock\sounds\{Complete,Attention,Error}.wav` | Done |
| `protocol/taskdock.schema.json` + golden samples + C# contract test | Done |
| FakeAdapter (`demo`, `claude`, `load`) | Done |
| Generic tile: 1 s shared check (Running / Not responding / Closed), binds by process + optional `titleRegex`, rebinds after relaunch, a sound when a hung window recovers | Done. A hung test window showed Not responding after 5.0 s |
| Crosshair picker (drag ⌖ onto a window; a click-through outline follows the cursor) + resolver (Unity, VS Code, browsers, terminals → choose a Claude session, else generic) | Done. Resolver is unit-tested; hit-test, shortcut reading and Focus were checked with a harness; the real mouse drag is untested |
| File drop: `.exe` or `.lnk` onto the widget → a generic tile that launches what you dropped | Done; real drag-and-drop untested |
| Focus button and right-click tile menu (Rename…, Focus, Launch, Remove); Notify stays silent when the source's window is already in front | Done |
| Named presets (tray → Presets): apply (Shift+click merges), "Save current as…" (fills in launch commands from live tiles), "Launch missing" (2 s apart). Edits save into the active preset | Done; tray menu not clicked through by hand |
| Settings window (tray → Settings…): mute, volume, quiet hours, silent when focused, fullscreen hide, auto-sort, Start with Windows (per-user Run key), Claude approval hold (rewrites the installed hooks' timeout), terminal launch template. Validated; applied live | Built and compiled; not clicked through by hand (the tray menu is out of reach for my UI automation) |
| Release (Phase 7): `tools/release.ps1` builds the four artifacts above. Memory and CPU measured on the release `.exe` | 32 MB private working set at idle, 37–46 MB with 8 tiles at 10 updates/s, 0.77% CPU under that load, 58–62 MB plateau in a 12-minute churn soak. See "Soak" below |
| Acrylic backdrop, snap animation, drag-to-reorder tiles | **Not started** (solid background) |
| Unity package `com.darkpixelgd.taskdock` (Phase 3): every state in the plan's Unity table, background Progress items, Play / Stop / Pause from the tile, Preferences → TaskDock. Hub-side silence rules: Reloading held through the domain reload with elapsed seconds, Main thread blocked, Not responding, Closed. Notify settle: one sound per compile → reload → import chain, silent for play mode | See "Unity" below for what was verified |
| Media bar (Phase 4): whatever Windows' media controls say is playing. Title, artist and thumbnail; previous / play-pause / next (greyed when the app doesn't offer them); a progress line; chips to switch sessions; a pin to stick to one app; Spotify / YouTube search (Enter searches where the music is playing). Using the controls makes the bar stick to that app | Checked with a silent test session: metadata 37–62 ms after a press, thumbnails from a real Brave/YouTube session. Search links are unit-tested but weren't opened; untested with Spotify |
| Browser downloads extension (Phase 5, `adapters/chrome`): MV3, loads unpacked in Chrome, Edge and Brave. One tile per browser profile: Downloading (n, %) with speed and time left, Needs confirmation, Complete, Failed, Paused. An expandable file list with Pause / Resume / Cancel (sent to the browser) and Open / Show in folder (run by the widget). Sends file names, sizes, states and local paths, never URLs. A fixed key pins the extension ID; the hub accepts only that Origin | Checked in headless Edge 157: progress every second, 3 concurrent downloads, Cancel, Pause → Resume, one Complete sound per file, recovery after a browser restart and after the worker was killed. Not checked: a real network failure (Edge kept retrying, then called the partial file complete), Chrome, Brave |
| VS Code extension (Phase 6, `adapters/vscode`): one tile per window. Failed (exit n) > Debugging > Task running > Command running > Succeeded > Idle, with the rest listed in the detail line. Terminal commands count only if they run at least `taskdock.longCommandSeconds` (10 s). `taskdock.notifyOnDebugEnd` (off) reports a finished debug session. Focus matches the window title against the workspace name | Unit-tested (11 Node tests). The bundled extension was run against the live hub with a stand-in for the VS Code API: task failure, ignored short command, long command → Succeeded, debugging, bye → Offline. **Not yet run inside a real VS Code** (none installed here) |

## Unity verification

Checked against Unity 6000.5.8f1 running in batch mode (a test driver edited scripts and blocked the main thread; the widget pressed the buttons):

- The package compiles, and its 4 EditMode tests pass: frame shape matches the golden samples, the instance ID is stable, state priority is right.
- 20 consecutive script edits each showed Compiling → Reloading → Importing → Idle, with exactly one Complete sound per edit, 1.5 s after it settled.
- An 8 s main-thread block showed Main thread blocked after 5.1 s and cleared 0.4 s after it ended, with no sound.
- ▶ Play, Pause and ■ Stop on the tile drove play mode. The play-mode episode made no sound.
- Killing the Editor showed Closed after 0.6 s.
- Unity 6's asset-import worker processes are skipped, so each Editor shows up as one tile.
- Not yet checked: an Editor with a window (Focus, Not responding on a real hang), Unity 2022.3, Building, and background tasks with progress.

Unity's update tick is throttled while the Editor is in the background. If "Main thread blocked" flickers while Unity is idle and unfocused, raise `UnityBlockedAfter` in `HubOptions` (default 5 s).

## Where TaskDock keeps things

- `%LOCALAPPDATA%\TaskDock\hub.json`: port, token, pid. Unity and VS Code adapters read this file.
- `%LOCALAPPDATA%\TaskDock\logs\`: rolling log, 5 × 1 MB. No prompt or tool-input text is logged.
- `%APPDATA%\TaskDock\settings.json`: what the Settings window edits, plus the media bar's pin and fold state.
- `%APPDATA%\TaskDock\presets\<name>.json`: pins, corner and mode, in the plan's preset format.

## Open questions (Phase 0 spikes that need a real Claude Code session)

The hub follows the hooks docs: an empty 200 means no decision, an HTTP failure is non-blocking, and the PermissionRequest hook timeout is set to the hold window + 10 s. Four things are still open and need a real interactive session to settle:

1. Does the terminal prompt show while the PermissionRequest hook is pending? If not, set `holdSeconds` to about 20 (see Risks in the plan).
2. Does an empty 200 really leave the normal prompt in place?
3. What happens if the hook timeout fires mid-hold?
4. Which hooks fire inside the VS Code extension, compared with the CLI?

## Soak

Run against the release `.exe` with `TaskDock.FakeAdapter` (private working set, the number Task Manager shows):

- **Reconnect torture, 15 min:** fake Unity, VS Code, downloads and Claude adapters connected at random and were killed mid-stream (43 launches, 15 kills), and the widget itself was killed and restarted 6 times. It came back healthy within 500 ms every time.
- **The soak found a real leak.** The tray badge was regenerated on every change, and each regeneration leaked GDI objects. After 20 minutes of churn the widget hit Windows' limit (9,996 of 10,000), logged 406 drawing errors, and memory plateaued at 76–80 MB. Fixed: the ten badges are drawn once and reused. Under the same churn GDI now stays at about 30.
- **Soak after the fix, 12 min:** 8 load adapters at 10 updates/s plus Unity, VS Code and Claude sessions, all reconnecting every 30 s. Memory plateaued at 58–62 MB; handles stayed flat around 800; 0 errors.
- **Not done:** the plan's 8-hour soak and the "clean machine in under 15 minutes" setup check.
