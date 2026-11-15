// TaskDock downloads adapter (MV3 service worker). Streams one snapshot per browser profile to the
// local TaskDock hub: file names, sizes, states and local paths only, never download URLs.
import { summarize, toItems, FLASH_MS } from "./state.js";

const BASE_PORT = 47820, PORT_SPAN = 10;
let ws = null, connecting = false, backoff = 1000, beat = null, poll = null, seq = 0;
let lastSent = "", flash = null;
const lastBytes = new Map(), speeds = new Map();

const browserName = () => {
  const brands = (navigator.userAgentData?.brands || []).map((b) => b.brand);
  if (navigator.brave || brands.includes("Brave")) return "brave";
  if (brands.includes("Microsoft Edge")) return "edge";
  return "chrome";
};

async function identity() {
  const s = await chrome.storage.local.get(["instance", "port"]);
  if (!s.instance) {
    s.instance = crypto.randomUUID(); // one per browser profile, stable across restarts
    await chrome.storage.local.set({ instance: s.instance });
  }
  const browser = browserName();
  return { instance: `${browser}:${s.instance}`, browser, port: s.port | 0 };
}

const status = (text) => chrome.storage.local.set({ status: text });

function send(type, body, instance) {
  if (ws?.readyState !== WebSocket.OPEN) return;
  ws.send(JSON.stringify({ v: 1, type, seq: ++seq, ts: Date.now(), src: { adapter: "downloads", instance }, body }));
}

/** An extension can't read hub.json: probe /health on 47820–47829 (or the port set in options). */
async function findHub(override) {
  const ports = override ? [override] : Array.from({ length: PORT_SPAN }, (_, i) => BASE_PORT + i);
  for (const p of ports) {
    try {
      const r = await fetch(`http://127.0.0.1:${p}/health`, { signal: AbortSignal.timeout(600) });
      if (r.ok && (await r.json()).protocol === 1) return p;
    } catch { /* not this port */ }
  }
  return null;
}

async function connect() {
  if (connecting || ws) return;
  connecting = true;
  try {
    const id = await identity();
    const port = await findHub(id.port);
    if (!port) { status("Not connected: is TaskDock running?"); return retry(); }
    const sock = new WebSocket(`ws://127.0.0.1:${port}/ws`);
    ws = sock;
    sock.onopen = () => send("hello", {
      adapter: "downloads", instance: id.instance, version: chrome.runtime.getManifest().version,
      match: { browser: id.browser }, actions: ["pause", "resume", "cancel"],
    }, id.instance);
    sock.onmessage = (m) => onMessage(JSON.parse(m.data), id.instance);
    sock.onclose = () => {
      if (ws === sock) ws = null;
      clearInterval(beat);
      status("Not connected: is TaskDock running?");
      retry();
    };
  } finally {
    connecting = false;
  }
}

function retry() {
  setTimeout(connect, backoff);
  backoff = Math.min(backoff * 2, 5000);
}

async function onMessage(msg, instance) {
  if (msg.type === "welcome") {
    backoff = 1000;
    status(`Connected to TaskDock (${new URL(ws.url).port})`);
    clearInterval(beat);
    // Heartbeats also keep this service worker alive while the socket is open (Chrome 116+).
    beat = setInterval(() => send("heartbeat", {}, instance), Math.min(msg.body.heartbeatMs || 5000, 20000));
    lastSent = "";
    return push(); // full snapshot after every (re)connect
  }
  if (msg.type !== "command") return;
  const { id, action, args } = msg.body;
  const target = Number(args?.downloadId);
  try {
    if (!Number.isInteger(target)) throw new Error("downloadId missing");
    if (action === "pause") await chrome.downloads.pause(target);
    else if (action === "resume") await chrome.downloads.resume(target);
    else if (action === "cancel") await chrome.downloads.cancel(target);
    else throw new Error(`unknown action ${action}`);
    send("result", { id, ok: true }, instance);
  } catch (e) {
    send("result", { id, ok: false, error: String(e.message || e) }, instance);
  }
  push();
}

/** Rebuild the snapshot; send it if it changed. Polls every 1 s while anything downloads (onChanged has no byte progress). */
async function push() {
  const now = Date.now();
  const downloads = await chrome.downloads.search({ orderBy: ["-startTime"], limit: 100 });
  for (const d of downloads.filter((d) => d.state === "in_progress")) {
    const prev = lastBytes.get(d.id);
    if (prev) speeds.set(d.id, Math.max(0, (d.bytesReceived - prev.bytes) / ((now - prev.at) / 1000 || 1)));
    lastBytes.set(d.id, { bytes: d.bytesReceived, at: now });
  }
  const items = toItems(downloads, speeds, now);
  const anyRunning = items.some((i) => i.state === "in_progress");
  if (anyRunning && !poll) poll = setInterval(push, 1000);
  if (!anyRunning && poll) { clearInterval(poll); poll = null; lastBytes.clear(); speeds.clear(); }

  const s = summarize(items, flash, now);
  const id = await identity();
  const body = { state: s.state, label: { chrome: "Chrome", edge: "Edge", brave: "Brave" }[id.browser] + " downloads", items };
  if (s.detail) body.detail = s.detail;
  if (s.progress != null) body.progress = s.progress;
  const json = JSON.stringify(body);
  if (json === lastSent) return;
  lastSent = json;
  send("state", body, id.instance);
}

chrome.downloads.onCreated.addListener(() => push());
chrome.downloads.onErased.addListener(() => push());
chrome.downloads.onChanged.addListener((delta) => {
  if (delta.state?.current === "complete") {
    chrome.downloads.search({ id: delta.id }).then(([d]) => {
      flash = { until: Date.now() + FLASH_MS, file: (d?.filename || "").split(/[\\/]/).pop() };
      setTimeout(push, FLASH_MS + 100); // end the flash
      push();
    });
  } else push();
});

// Woken by browser start, install, or the alarm after Chrome stopped an idle worker.
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
chrome.alarms.create("taskdock-reconnect", { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener(connect);
chrome.storage.onChanged.addListener((c) => { if (c.port) { ws?.close(); } }); // port override changed: reconnect
connect();
