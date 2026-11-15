# TaskDock

A small always-on-top Windows widget that shows the live state of running work. It snaps to a screen corner. The full design is in `TaskDock — Execution Plan.pdf`.

## Install

Build every deliverable into `artifacts/release` (runs the tests first; needs the .NET 10 SDK and Node 22+):

```bash
powershell -ExecutionPolicy Bypass -File tools/release.ps1
```

| File | What to do with it |
|---|---|
| `TaskDock.exe` | Run it. Self-contained (no .NET install needed), about 185 MB. Unsigned, so Windows SmartScreen warns on first run: **More info → Run anyway**. Tray icon → **Settings…** has **Start with Windows** |
| `com.darkpixelgd.taskdock-0.1.0.tgz` | Unity: **Window → Package Manager → + → Install package from tarball…** |
| `taskdock-0.1.0.vsix` | VS Code: Extensions view → **…** → **Install from VSIX…** |
| `taskdock-browser-extension.zip` | Unzip to a folder you keep. In Chrome / Edge / Brave: `chrome://extensions` → **Developer mode** → **Load unpacked** → that folder |
| Claude Code | Nothing to install: tray icon → **Connect Claude Code** |

## Status (v0.1: all seven phases built)

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

## Run

```bash
dotnet test TaskDock.sln
```

```bash
dotnet run --project src/TaskDock.App
```

Watch Claude Code: tray icon → **Connect Claude Code**. This adds HTTP hooks to `~/.claude/settings.json`, after saving a backup at `settings.json.taskdock.bak`. **Disconnect Claude Code** removes only TaskDock's entries. New Claude sessions show up under "+N detected". Pin a session to keep its folder (`cwd`). The bell on a pinned tile turns on Notify sounds.

Try it without Claude Code (with TaskDock running):

```bash
dotnet run --project tools/TaskDock.FakeAdapter -- claude 30
```

```bash
dotnet run --project tools/TaskDock.FakeAdapter -- demo 60
```

## Browser downloads

In Chrome, Edge or Brave: open `chrome://extensions` (Edge: `edge://extensions`), turn on **Developer mode**, click **Load unpacked**, and pick the `adapters/chrome` folder. The extension finds TaskDock on ports 47820–47829. Its options page shows the connection status and has a port override. The downloads tile shows up under "+N detected": pin it, and turn on the bell for a sound per finished file.

```bash
node --test "adapters/chrome/test/*.test.mjs"
```

## VS Code

```bash
cd adapters/vscode && npm install && npm run package
```

Then in VS Code: Extensions view → **…** → **Install from VSIX…** → `adapters/vscode/taskdock-0.1.0.vsix`. Each window shows up under "+N detected". Terminal commands are reported only with shell integration, which is on by default for PowerShell, bash and zsh. Tests: `npm test` in `adapters/vscode`.

## Unity

In your project, open **Window → Package Manager → +** and either **Install package from tarball…** (the release `.tgz`) or **Install package from disk…** (`adapters/unity/com.darkpixelgd.taskdock/package.json`). The package is Editor-only. Each Editor shows up under "+N detected" (one tile per Editor). Pin it to keep its project path, and turn on the bell for a sound when a compile → reload → import chain settles. Settings live under **Edit → Preferences → TaskDock**: enable, port override, background-task detail.

Checked against Unity 6000.5.8f1 running in batch mode (a test driver edited scripts and blocked the main thread; the widget pressed the buttons):

- The package compiles, and its 4 EditMode tests pass: frame shape matches the golden samples, the instance ID is stable, state priority is right.
- 20 consecutive script edits each showed Compiling → Reloading → Importing → Idle, with exactly one Complete sound per edit, 1.5 s after it settled.
- An 8 s main-thread block showed Main thread blocked after 5.1 s and cleared 0.4 s after it ended, with no sound.
- ▶ Play, Pause and ■ Stop on the tile drove play mode. The play-mode episode made no sound.
- Killing the Editor showed Closed after 0.6 s.
- Unity 6's asset-import worker processes are skipped, so each Editor shows up as one tile.
- Not yet checked: an Editor with a window (Focus, Not responding on a real hang), Unity 2022.3, Building, and background tasks with progress.

Unity's update tick is throttled while the Editor is in the background. If "Main thread blocked" flickers while Unity is idle and unfocused, raise `UnityBlockedAfter` in `HubOptions` (default 5 s).

## Files

- `%LOCALAPPDATA%\TaskDock\hub.json`: port, token, pid. Unity and VS Code adapters read this file.
- `%LOCALAPPDATA%\TaskDock\logs\`: rolling log, 5 × 1 MB. No prompt or tool-input text is logged.
- `%APPDATA%\TaskDock\settings.json`: what the Settings window edits, plus the media bar's pin and fold state.
- `%APPDATA%\TaskDock\presets\<name>.json`: pins, corner and mode, in the plan's preset format.

## Still unverified (Phase 0 spikes that need a real Claude Code session)

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

## Known limitations

These ship as-is in v1.

- Claude desktop app and claude.ai chats are not supported. Only Claude Code has hooks.
- File Explorer is not supported.
- Windows only, and Chromium browsers only (no Firefox).
- One media session per Chrome, not per tab. Spotify search opens the results page; it doesn't auto-play.
- VS Code Focus fails if `window.title` hides the folder name. Claude tiles have no Focus at all: a hook request can't be traced to its terminal yet (stretch goal).
- Unsigned builds show a SmartScreen warning on first run.
- The single-file `.exe` is about 185 MB, on purpose: the compressed build is 84 MB but uses 110 MB of private memory instead of 32 MB.
- A Claude session that needs approval surfaces even when it isn't pinned, so an approval never waits unseen. Other new sources stay in the detected tray, as the plan specifies.
- Not verified by hand: typing a Deny reason, file drop, the tray Presets menu, the Settings window, two monitors / 150% scaling, Spotify, a real VS Code, Chrome and Brave.
