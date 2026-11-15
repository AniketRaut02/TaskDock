// TaskDock adapter for VS Code: one tile per window with running tasks, debug sessions and long terminal
// commands (with exit codes). Claude Code in VS Code's terminal is covered by its own hooks, not here.
import * as vscode from "vscode";
import WebSocket from "ws";
import { StateTracker, type Snapshot } from "./stateTracker.ts";
import { frame, instanceId, readHub } from "./wire.ts";

export function activate(ctx: vscode.ExtensionContext) {
  const cfg = () => vscode.workspace.getConfiguration("taskdock");
  const tracker = new StateTracker(cfg().get("longCommandSeconds", 10) * 1000, cfg().get("notifyOnDebugEnd", false));

  // Folder or .code-workspace file: the preset matcher's workspacePath.
  const ws = vscode.workspace.workspaceFile?.scheme === "file" ? vscode.workspace.workspaceFile : vscode.workspace.workspaceFolders?.[0]?.uri;
  const workspacePath = ws?.scheme === "file" ? ws.fsPath : undefined;
  const label = vscode.workspace.name ?? "VS Code";
  const instance = instanceId(vscode.env.sessionId, workspacePath);
  const hub = new HubConnection(instance, {
    adapter: "vscode", instance, version: ctx.extension.packageJSON.version, pid: process.pid,
    match: workspacePath ? { workspacePath } : {}, actions: [],
  });

  let dueTimer: NodeJS.Timeout | undefined;
  const push = () => {
    const now = Date.now();
    hub.sendState(tracker.snapshot(now), label);
    clearTimeout(dueTimer);
    const due = tracker.nextCommandDue(now); // a command is about to count as "long"
    if (due) dueTimer = setTimeout(push, due - now + 50);
  };

  ctx.subscriptions.push(
    vscode.tasks.onDidStartTaskProcess((e) => { tracker.taskStarted(e.execution, e.execution.task.name); push(); }),
    vscode.tasks.onDidEndTaskProcess((e) => { tracker.taskEnded(e.execution, e.exitCode); push(); }),
    vscode.debug.onDidStartDebugSession((s) => { tracker.debugStarted(s.id, s.name); push(); }),
    vscode.debug.onDidTerminateDebugSession((s) => { tracker.debugEnded(s.id); push(); }),
    // Needs shell integration (on by default for PowerShell, bash and zsh).
    vscode.window.onDidStartTerminalShellExecution((e) => { tracker.commandStarted(e.execution, e.execution.commandLine.value, Date.now()); push(); }),
    vscode.window.onDidEndTerminalShellExecution((e) => { tracker.commandEnded(e.execution, e.exitCode, Date.now()); push(); }),
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (!e.affectsConfiguration("taskdock")) return;
      tracker.longCommandMs = cfg().get("longCommandSeconds", 10) * 1000;
      tracker.notifyOnDebugEnd = cfg().get("notifyOnDebugEnd", false);
      push();
    }),
    { dispose: () => { clearTimeout(dueTimer); hub.close("window closing"); } },
  );
  push();
}

export function deactivate() {}

/** WebSocket to the hub: hello with the token from hub.json, heartbeats, reconnect with 1 s → 5 s backoff. */
class HubConnection {
  private sock: WebSocket | null = null;
  private last: string | null = null; // the latest state frame, resent after every reconnect
  private beat: NodeJS.Timeout | undefined;
  private retry: NodeJS.Timeout | undefined;
  private backoff = 1000;
  private closed = false;
  private readonly instance: string;
  private readonly hello: Record<string, unknown>;

  constructor(instance: string, hello: Record<string, unknown>) {
    this.instance = instance;
    this.hello = hello;
    this.connect();
  }

  sendState(s: Snapshot, label: string) {
    const body: Record<string, unknown> = { state: s.state, label };
    if (s.detail) body.detail = s.detail;
    const f = frame("state", this.instance, body);
    if (f.slice(f.indexOf('"body"')) === this.last?.slice(this.last.indexOf('"body"'))) return; // unchanged
    this.last = f;
    this.send(f);
  }

  close(reason: string) {
    this.closed = true;
    clearTimeout(this.retry);
    clearInterval(this.beat);
    if (this.sock?.readyState === WebSocket.OPEN) this.sock.send(frame("bye", this.instance, { reason }));
    this.sock?.close();
  }

  private send(f: string) {
    if (this.sock?.readyState === WebSocket.OPEN) this.sock.send(f);
  }

  private connect() {
    if (this.closed) return;
    const hub = readHub();
    if (!hub) return this.later();
    const sock = new WebSocket(`ws://127.0.0.1:${hub.port}/ws`);
    this.sock = sock;
    sock.on("open", () => sock.send(frame("hello", this.instance, { ...this.hello, token: hub.token })));
    sock.on("message", (data) => {
      const msg = JSON.parse(String(data));
      if (msg.type !== "welcome") return; // this adapter takes no commands
      this.backoff = 1000;
      clearInterval(this.beat);
      this.beat = setInterval(() => this.send(frame("heartbeat", this.instance, {})), msg.body?.heartbeatMs ?? 5000);
      if (this.last) this.send(this.last); // full snapshot after every (re)connect
    });
    sock.on("error", () => { /* "close" follows */ });
    sock.on("close", () => {
      clearInterval(this.beat);
      if (this.sock === sock) this.sock = null;
      this.later();
    });
  }

  private later() {
    if (this.closed) return;
    clearTimeout(this.retry);
    this.retry = setTimeout(() => this.connect(), this.backoff);
    this.backoff = Math.min(this.backoff * 2, 5000);
  }
}
