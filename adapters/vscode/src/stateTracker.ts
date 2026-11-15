// Pure window-state logic: no vscode import, so Node can test it directly.

export interface Snapshot { state: string; detail?: string }

interface Result { ok: boolean; what: string; exitCode?: number }

/**
 * Several things can be active at once; the tile shows the highest priority and lists the rest in the detail:
 * Failed > Debugging > Task running > Command running > Succeeded > Idle.
 * A result (Failed / Succeeded) stays until a task or debug session starts.
 */
export class StateTracker {
  private readonly tasks = new Map<unknown, string>();
  private readonly debug = new Map<string, string>();
  private readonly commands = new Map<unknown, { cmd: string; start: number }>();
  private result: Result | null = null;

  longCommandMs: number;
  notifyOnDebugEnd: boolean;

  constructor(longCommandMs: number, notifyOnDebugEnd: boolean) {
    this.longCommandMs = longCommandMs;
    this.notifyOnDebugEnd = notifyOnDebugEnd;
  }

  taskStarted(key: unknown, name: string) { this.result = null; this.tasks.set(key, name); }

  /** exitCode is undefined for tasks VS Code can't track (custom executions): no result then. */
  taskEnded(key: unknown, exitCode: number | undefined) {
    const name = this.tasks.get(key);
    this.tasks.delete(key);
    if (name !== undefined && exitCode !== undefined) this.result = { ok: exitCode === 0, what: name, exitCode };
  }

  debugStarted(id: string, name: string) { this.result = null; this.debug.set(id, name); }

  debugEnded(id: string) {
    const name = this.debug.get(id);
    this.debug.delete(id);
    if (name !== undefined && this.notifyOnDebugEnd) this.result = { ok: true, what: `${name} (debug) ended` };
  }

  // A command doesn't clear an earlier result when it starts: most commands are short and must make no noise.
  commandStarted(key: unknown, cmd: string, now: number) { this.commands.set(key, { cmd: cmd.trim(), start: now }); }

  commandEnded(key: unknown, exitCode: number | undefined, now: number) {
    const c = this.commands.get(key);
    this.commands.delete(key);
    if (!c || now - c.start < this.longCommandMs || exitCode === undefined) return;
    this.result = { ok: exitCode === 0, what: short(c.cmd), exitCode };
  }

  /** When a running command will cross the "long" threshold, so the caller can re-snapshot then. */
  nextCommandDue(now: number): number | null {
    const due = [...this.commands.values()].map((c) => c.start + this.longCommandMs).filter((t) => t > now);
    return due.length ? Math.min(...due) : null;
  }

  snapshot(now: number): Snapshot {
    const longCmds = [...this.commands.values()].filter((c) => now - c.start >= this.longCommandMs).map((c) => short(c.cmd));
    const running = [
      ...[...this.debug.values()].map((n) => `${n} debugging`),
      ...[...this.tasks.values()].map((n) => `${n} running`),
      ...longCmds.map((c) => `${c} running`),
    ];
    const also = (skip: number) => (running.length > skip ? " · " + running.slice(skip).join(" · ") : "");

    const r = this.result;
    if (r && !r.ok) return { state: `Failed (exit ${r.exitCode})`, detail: `${r.what} exited with ${r.exitCode}` + also(0) };
    if (this.debug.size) return { state: "Debugging", detail: [...this.debug.values()].join(", ") + also(this.debug.size) };
    if (this.tasks.size) return { state: "Task running", detail: [...this.tasks.values()].join(", ") + also(this.tasks.size) };
    if (longCmds.length) return { state: "Command running", detail: longCmds.join(" · ") };
    if (r) return { state: "Succeeded", detail: r.what };
    return { state: "Idle" };
  }
}

export const short = (cmd: string, max = 60) => (cmd.length <= max ? cmd : cmd.slice(0, max - 1) + "…");
