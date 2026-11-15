# TaskDock

A small always-on-top Windows widget that shows the live state of running work. It snaps to a screen corner. The full design is in `TaskDock — Execution Plan.pdf`.

## Status (v0.1: Phases 1 and 2)

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
| Acrylic backdrop, snap animation, settings window, Start with Windows | **Not started** (solid background for now) |
| Unity package `com.darkpixelgd.taskdock` (Phase 3): every state in the plan's Unity table, background Progress items, Play / Stop / Pause from the tile, Preferences → TaskDock. Hub-side silence rules: Reloading held through the domain reload with elapsed seconds, Main thread blocked, Not responding, Closed. Notify settle: one sound per compile → reload → import chain, silent for play mode | See "Unity" below for what was verified |
| Media bar, Chrome extension, VS Code extension | **Not started** (Phases 4–6) |

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

## Unity

In your project, open **Window → Package Manager → + → Install package from disk…** and pick `adapters/unity/com.darkpixelgd.taskdock/package.json`. The package is Editor-only. Each Editor shows up under "+N detected" (one tile per Editor). Pin it to keep its project path, and turn on the bell for a sound when a compile → reload → import chain settles. Settings live under **Edit → Preferences → TaskDock**: enable, port override, background-task detail.

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
- `%APPDATA%\TaskDock\settings.json`: hold window, mute, quiet hours (`"quietFrom": "22:00"`), volume, terminal launch template.
- `%APPDATA%\TaskDock\presets\<name>.json`: pins, corner and mode, in the plan's preset format.

## Still unverified (Phase 0 spikes that need a real Claude Code session)

The hub follows the hooks docs: an empty 200 means no decision, an HTTP failure is non-blocking, and the PermissionRequest hook timeout is set to the hold window + 10 s. Four things are still open and need a real interactive session to settle:

1. Does the terminal prompt show while the PermissionRequest hook is pending? If not, set `holdSeconds` to about 20 (see Risks in the plan).
2. Does an empty 200 really leave the normal prompt in place?
3. What happens if the hook timeout fires mid-hold?
4. Which hooks fire inside the VS Code extension, compared with the CLI?

## Known gaps in this slice

- Memory: about 56 MB private and 158 MB working set (Debug build). The plan's under-80 MB target hasn't been worked on yet (Phase 7 soak).
- The Deny-reason text box takes focus by briefly dropping `WS_EX_NOACTIVATE`. This hasn't been tested with real typing.
- A Claude session that needs approval surfaces even when it isn't pinned, so an approval never waits unseen. Other new sources stay in the detected tray, as the plan specifies.
