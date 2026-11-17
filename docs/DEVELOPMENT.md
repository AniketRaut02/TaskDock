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
adapters/visualstudio/      Visual Studio 2022 / 2026 extension (VSSDK, .NET Framework 4.8)
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

## Visual Studio verification

Checked in Visual Studio Community 2026 (18.12), extension installed with `VSIXInstaller`, driven through DTE on a one-project console solution:

| Action | Tile |
|---|---|
| Open the solution | Idle (connected about 2 s after Visual Studio started) |
| Clean, then build | Building → Build succeeded |
| Build with a compile error | Building → Build failed (1 of 1 project) |
| Debug (F5) | Building → Build succeeded → Debugging |
| Breakpoint hit | Paused (Attention) |
| Continue to the end | Idle |

Hub tests cover Not responding (hung window) and Closed (process gone). Not yet checked in a real Visual Studio: Not responding on a real hang, Visual Studio 2022, Open Folder mode, and a solution switch while TaskDock is watching.

Two traps found while testing: an MSBuild worker left behind by a build blocks `VSIXInstaller` (the release script now passes `-nodeReuse:false`), and anything started from inside a packaged app (like the Claude desktop app) gets a private AppData. That's why the installer opens both TaskDock and the Visual Studio installer through Explorer.

## Unity verification

Checked against Unity 6000.5.8f1 running in batch mode (a test driver edited scripts and blocked the main thread; the widget pressed the buttons):

- The package compiles, and its 4 EditMode tests pass: frame shape matches the golden samples, the instance ID is stable, state priority is right.
- 20 consecutive script edits each showed Compiling → Reloading → Importing → Idle, with exactly one Complete sound per edit, 1.5 s after it settled.
- An 8 s main-thread block showed Main thread blocked after 5.1 s and cleared 0.4 s after it ended, with no sound.
- ▶ Play, Pause and ■ Stop on the tile drove play mode. The play-mode episode made no sound.
- Killing the Editor showed Closed after 0.6 s.
- Unity 6's asset-import worker processes are skipped, so each Editor shows up as one tile.
- In a real windowed Editor (6000.5.8f1, a project with other Editor packages, the package embedded): the tile followed Idle → Reloading (with elapsed seconds) → Importing → Playing, and ▶ Play / ■ Stop on the tile drove play mode. Killing and reinstalling TaskDock made Unity log the outage and reconnect by itself.
- Once, Unity re-entered play mode about a second after Stop. A second clean Play/Stop cycle didn't repeat it, and TaskDock never re-sends commands. Cause unknown.
- Found while testing: a TaskDock started from inside a packaged (MSIX) app writes `hub.json` into that app's private AppData, so a Unity started from Unity Hub never finds it. The installer now launches TaskDock through Explorer, and the package logs `[TaskDock]` connect and failure reasons to the Console (they were silent before).
- Not yet checked: Focus and Not responding on a real hang, Unity 2022.3, Building, and background tasks with progress.

Unity's update tick is throttled while the Editor is in the background. If "Main thread blocked" flickers while Unity is idle and unfocused, raise `UnityBlockedAfter` in `HubOptions` (default 5 s).

## Where TaskDock keeps things

- `%LOCALAPPDATA%\TaskDock\hub.json`: port, token, pid. Unity and VS Code adapters read this file.
- `%LOCALAPPDATA%\TaskDock\logs\`: rolling log, 5 × 1 MB. No prompt or tool-input text is logged.
- `%APPDATA%\TaskDock\settings.json`: what the Settings window edits, plus the media bar's pin and fold state.
- `%APPDATA%\TaskDock\presets\<name>.json`: pins, corner and mode, in the plan's preset format.

## Open questions (Phase 0 spikes that need a real Claude Code session)

The hub follows the hooks docs: an empty 200 means no decision, an HTTP failure is non-blocking, and the PermissionRequest hook timeout is set to the hold window + 10 s. Four things are still open and need a real interactive session to settle:

1. ~~Does the terminal prompt show while the PermissionRequest hook is pending?~~ Yes (S1 below).
2. ~~Does an empty 200 really leave the normal prompt in place?~~ Yes (S1).
3. ~~What happens if the hook timeout fires mid-hold?~~ The request closes silently; the prompt stays (S1).
4. Which hooks fire inside the VS Code extension, compared with the CLI?

### Remote (0.2.0) Phase 0 spikes

| # | Status | Result |
| --- | --- | --- |
| S1 | **Pass**, with one plan change | Claude Code 2.1.325 CLI, HTTP hook, 300 s hold. (1) The normal Allow/Deny prompt shows in the terminal at the same time as the held hook. (2) An empty 200 after 300 s leaves that prompt in place; nothing runs. (3) A hook timeout mid-hold (60 s) closes the request with no visible error; the prompt stays. (4) **Answering in the terminal mid-hold runs the tool at once but does not close the hook request**: it stays open until the hub answers. So the hub cannot learn from the socket that an approval was answered; it must clear the pending approval on the session's next hook event (PreToolUse, Stop, or a PostToolUse hook added for this), or the widget and phone keep showing it until the hold ends. Not tested: what a late Allow from the hub does after the terminal answer |
| S2 | Skipped | Not tested; the plan follows the hooks docs (hooks snapshotted at session start). Settings tells users to restart running sessions after the hold changes, which is right either way |
| S3 | **Pass** (reboot check open) | Tailscale 1.104.7, non-admin shell. `serve --bg --https=443 http://127.0.0.1:47830` and `serve --https=443 off` both exit 0 without elevation; `serve status --json` gives `Web["<name>:443"].Handlers["/"].Proxy`. `status --json` fields: `BackendState` (`Running`; `NeedsLogin` with `Health` "Tailscale is stopped." when signed out), `Self.DNSName` (trailing dot), `CertDomains` (empty until HTTPS certs are on), `MagicDNSSuffix`, `CurrentTailnet.MagicDNSEnabled`, login at `User[Self.UserID].LoginName`. Proxied requests carry `Tailscale-User-Login`, `-User-Name`, `-User-Profile-Pic`, `X-Forwarded-For/Host/Proto`, and `Host` = the bare ts.net name (no port). A client-sent `Tailscale-User-Login` is replaced with the real one. Identity headers are added even for requests from the PC itself. Fixtures need personal data scrubbed before going into tests |
| S4 | **Pass** | Android, Chrome, installed PWA over the tailnet via `tailscale serve`. (1) Installs as a standalone app. (2) Passkey create and get with RP ID = the full `ts.net` name, platform authenticator, user verified flag set; `signCount` is always 0 (Google Password Manager), so the counter check must stay optional. (3) Web Push via `Lib.Net.Http.WebPush` 3.3.1 with VAPID reaches the locked phone in 0.2–0.4 s (PC clock, send to service-worker report). (4) The notification shows Deny and Open, but only after the user expands it on the lock screen. (5) Deny's `fetch` from `notificationclick` reaches the PC while the phone is locked. Setup gotchas for the wizard and docs: notifications must be on for Chrome in Android settings and for the site, otherwise `requestPermission` returns `denied` with no prompt; subscribe takes about 3 s, so the app must wait for it |
| S5 | **Pass** | `PrintWindow(PW_RENDERFULLCONTENT)` gives a real image for Unity 6.5 (DX12, Game view included), Visual Studio 2026, Chrome and Electron (Claude desktop; VS Code is Electron too but was not installed), including windows fully covered by others, and **also with the PC locked** (captured 9 s after `LockWorkStation`). Minimized windows give a 159×27 title-bar stub: keep the 409 `minimized` check, and keep the `blank` check for cases not seen here (sleep, a disconnected display). The capturing process must be per-monitor DPI aware, or `GetWindowRect` is scaled and the image is cropped (1550×830 instead of 1938×1038 at 125%). Windows.Graphics.Capture fallback not needed |
| S6 | **Pass** | A second idle `CreateSlimBuilder` Kestrel app adds 1.3–2.3 MB private memory (3 runs, 1 app 14.8–15.0 MB vs 2 apps 16.1–17.3 MB, framework-dependent build). Budget 10 MB: keep `RemoteServer` as its own app |

## Remote (0.2.0) layout

The phone is a second view of the same state, not a second brain: every rule and action lives once, in Core.

| Where | What |
| --- | --- |
| `src/TaskDock.Core/Dock/` | `TileCaps`, `DockSnapshot` + `SnapshotBuilder`, the action catalog (`DockAction`) and `ActionRouter` (re-checks each action against live state), `HoldRule`, `PushPolicy` |
| `src/TaskDock.Core/Claude/ToolRisk.cs` | Read-only allowlist; any other tool needs a fingerprint |
| `src/TaskDock.Remote/` | `RemoteServer` (Kestrel on 127.0.0.1:47830–47839, the request checks, pairing, routes), `LiveSession` + `DeltaEncoder` (the socket), `RemoteActions` (rate limit, dedup, step-up, Peek grant, log, hints), `StepUp` (WebAuthn bound to one request), `DeviceStore`, `ActivityLog`, `PushSender`, `TailscaleStatus`. References Core only; reaches the app through `IDockHost` |
| `src/TaskDock.App/` | `DockHost` (the `IDockHost`; actions run on the UI thread through the widget's own methods), `RemoteControl` (on/off, Away mode, hold hand-back, keep awake, pushes), `RemoteWindow` (setup checklist), `TailscaleCli`, `WindowCapture` |
| `remote-web/` | The phone app: plain TypeScript, esbuild, served from embedded resources. `delta.ts` (protocol), `live.ts` (socket), `actions.ts` (fingerprints, Peek), `push.ts`, `sw.ts` (service worker), `views.ts` (all DOM through `h()`, text only) |
| `protocol/remote/samples/` | Golden snapshot and delta, read by C# and `node --test` |

Data lives in `%LOCALAPPDATA%\TaskDock\remote\`: `devices.json` (token hashes, passkeys, push subscriptions), `remote.json` (owner login, VAPID keys) and `activity-YYYY-MM.jsonl`.

## Remote (0.2.0) gates

- **Phase 1, 16 Nov 2026: pass.** 204 tests. Release builds of 0.1.4 and this branch render the widget identically under FakeAdapter `showcase` and `claude`; idle memory 53.4 vs 52.3 MB (3 runs each).
- **Phase 2, 16 Nov 2026: pass**, on an Android phone in Chrome over the tailnet. Pairing from the QR code (passkey, then Allow on the PC); tiles follow the widget within about a second under FakeAdapter `demo`; airplane mode on and off reconnects by itself; a TaskDock restart brings Remote back and the phone resyncs; Revoke empties `devices.json` and the phone shows "unpaired" at once. Memory 150 s after start: 53.8 MB with Remote on and one phone connected, 57.4 MB with Remote off (noise is about ±4 MB). Shell 5.4 KB gzipped.
  - Seen: after the restart the phone took about 2 minutes to reconnect (backoff at 30 s, or the app was in the background). Phase 3: reconnect on the browser's `online` event and when TaskDock's port answers again.
  - Not checked: whether `tailscale serve` survives a reboot (Remote re-points it at startup either way).

- **Phase 3, 17 Nov 2026: pass**, same phone. Every catalog action run from the phone and confirmed in the activity log on the PC and on the phone: approvals (Allow and Always allow with fingerprint, one-tap Grep, Deny with a reason, Terminal, answered on the PC first, Edit diff shown), Play, Pause, Resume and Stop on a real Unity Editor (UnityMCPTest), download pause and cancel, pin, unpin, rename, sounds, Clear, Launch (relaunched Unity from the phone), preset apply, merge and launch missing, media play/pause, pin and search. Media previous/next and player switching weren't available (Brave's YouTube offers neither; one player open). Holds reached Claude Code's hook format: allow, allow + rules, deny + message, no decision for Terminal.
  - Found and fixed during the gate: the preset list never loaded (an overflow in the 5 s cache check, from Phase 1); a double tap could send two requests when a redraw replaced the button (identical taps within 1.5 s now share one); a half-open socket swallowed taps silently (server `KeepAliveTimeout` 30 s, and the app reconnects when a tap gets no answer in 10 s).
  - Not done by hand: Play on a tile that is already Playing → `stale` (the phone redraws within 250 ms, so the race can't be hit by tapping); covered by ActionRouterTests and the server path.

- **Phases 4 and 5: not yet run on a phone.** Code complete (0.2.0); automated tests pass. To check by hand, in one sitting:
  - Phone locked, PC untouched 3 minutes: a new approval notifies within 5 s; **Deny** from the notification resolves it; a second approval answered on the PC first gives "Already handled on the PC".
  - At the PC: no notification. With the app open: no notification, but the in-app banner shows the new approval.
  - Away: an approval is still answerable at minute 9 and goes back to the terminal after 10. Coming back to the PC hands it back after `HoldSeconds`, counted from your return.
  - Answered in the terminal: the card clears when Claude finishes the turn.
  - Keep awake (quick check chosen): during an Away run with a Busy tile, the log shows `Keep awake: True`, then `False` once you're back or the work ends.
  - Peek on Unity, Visual Studio and VS Code; a minimized window says so. **Lock** locks the PC; **Sleep** sleeps it and the phone shows it offline.
  - Setup wizard: each step's fix; rename and revoke a phone; Away-after minutes; alerts off stops pushes.
  - Still open from the plan: setup from a clean Windows user account in under 15 minutes; the 30-minute soak with two phones; whether `tailscale serve` survives a reboot.

Run the remote tests with `dotnet test` (TaskDock.Remote.Tests) and `npm test` in `remote-web`. `npm run build` there writes `remote-web/dist`, which TaskDock.Remote embeds; without it the server serves a page saying the app wasn't built.

## Soak

Run against the release `.exe` with `TaskDock.FakeAdapter` (private working set, the number Task Manager shows):

- **Reconnect torture, 15 min:** fake Unity, VS Code, downloads and Claude adapters connected at random and were killed mid-stream (43 launches, 15 kills), and the widget itself was killed and restarted 6 times. It came back healthy within 500 ms every time.
- **The soak found a real leak.** The tray badge was regenerated on every change, and each regeneration leaked GDI objects. After 20 minutes of churn the widget hit Windows' limit (9,996 of 10,000), logged 406 drawing errors, and memory plateaued at 76–80 MB. Fixed: the ten badges are drawn once and reused. Under the same churn GDI now stays at about 30.
- **Soak after the fix, 12 min:** 8 load adapters at 10 updates/s plus Unity, VS Code and Claude sessions, all reconnecting every 30 s. Memory plateaued at 58–62 MB; handles stayed flat around 800; 0 errors.
- **Not done:** the plan's 8-hour soak and the "clean machine in under 15 minutes" setup check.
