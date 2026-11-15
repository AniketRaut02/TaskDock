// Protocol v1 envelope and hub discovery (protocol/taskdock.schema.json). No vscode import.
import { readFileSync } from "node:fs";
import { join } from "node:path";

let seq = 0;

export function frame(type: string, instance: string, body: object): string {
  return JSON.stringify({ v: 1, type, seq: ++seq, ts: Date.now(), src: { adapter: "vscode", instance }, body });
}

/** Stable for this window session: VS Code's sessionId plus a hash of the workspace path. */
export function instanceId(sessionId: string, workspacePath: string | undefined): string {
  let h = 2166136261; // FNV-1a
  for (const ch of (workspacePath ?? "").replace(/\\/g, "/").replace(/\/+$/, "").toLowerCase()) {
    h ^= ch.charCodeAt(0);
    h = Math.imul(h, 16777619) >>> 0;
  }
  return `vscode:${sessionId.slice(0, 8)}:${(h & 0xffffff).toString(16).padStart(6, "0")}`;
}

/** %LOCALAPPDATA%\TaskDock\hub.json → port and token; null while the widget isn't running. */
export function readHub(): { port: number; token: string } | null {
  try {
    const j = JSON.parse(readFileSync(join(process.env.LOCALAPPDATA ?? "", "TaskDock", "hub.json"), "utf8"));
    return typeof j.port === "number" && typeof j.token === "string" ? { port: j.port, token: j.token } : null;
  } catch {
    return null; // missing or half-written: try again on the next reconnect
  }
}
