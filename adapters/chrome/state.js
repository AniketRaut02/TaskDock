// Pure download-state logic, shared by the service worker and the Node tests. No chrome.* here.

// A finished item shows "Complete" this long. Kept short so a second file finishing a moment later gets its own
// transition (and sound); the hub already merges sounds under 1 s apart.
export const FLASH_MS = 1200;
export const RECENT_MS = 60 * 60 * 1000;
const SAFE = new Set(["safe", "accepted", "allowlistedByPolicy"]);

const base = (p) => (p || "").split(/[\\/]/).pop() || "download";

/** Chrome DownloadItems → the items[] the hub gets: names, sizes, states, local paths. Never URLs. */
export function toItems(downloads, speeds, now) {
  const recent = downloads.filter((d) => d.state === "in_progress" || (d.endTime && now - Date.parse(d.endTime) < RECENT_MS));
  const active = recent.filter((d) => d.state === "in_progress");
  const done = recent.filter((d) => d.state !== "in_progress").sort((a, b) => Date.parse(b.endTime) - Date.parse(a.endTime)).slice(0, 10);
  return [...active, ...done].map((d) => ({
    id: d.id,
    file: base(d.filename),
    path: d.filename || null,
    state: itemState(d),
    bytes: d.bytesReceived,
    total: d.totalBytes > 0 ? d.totalBytes : null,
    speed: speeds.get(d.id) ?? null,
    etaSec: d.estimatedEndTime ? Math.max(0, Math.round((Date.parse(d.estimatedEndTime) - now) / 1000)) : null,
    error: d.error || null,
    endTime: d.endTime || null,
  }));
}

function itemState(d) {
  if (d.state === "complete") return "complete";
  if (d.state === "interrupted") return d.error === "USER_CANCELED" ? "canceled" : "failed";
  if (!SAFE.has(d.danger)) return "dangerous";
  return d.paused ? "paused" : "in_progress";
}

/** One tile state for the whole browser profile, from the items above. */
export function summarize(items, flash, now) {
  const dangerous = items.filter((i) => i.state === "dangerous");
  const running = items.filter((i) => i.state === "in_progress");
  const paused = items.filter((i) => i.state === "paused");

  if (dangerous.length) return { state: "Needs confirmation", detail: `${dangerous[0].file} is waiting for you in the browser` };
  if (flash && flash.until > now)
    return { state: "Complete", detail: flash.file + " finished" + (running.length ? ` · ${running.length} still downloading` : "") };
  if (running.length) {
    const known = running.filter((i) => i.total);
    const pct = known.length ? Math.floor((100 * known.reduce((s, i) => s + i.bytes, 0)) / known.reduce((s, i) => s + i.total, 0)) : null;
    const first = running[0];
    const bits = [first.file];
    if (first.speed) bits.push(rate(first.speed));
    if (first.etaSec != null) bits.push(eta(first.etaSec));
    if (running.length > 1) bits.push(`+${running.length - 1} more`);
    return {
      state: `Downloading (${running.length}${pct == null ? "" : `, ${pct}%`})`,
      detail: bits.join(" · "),
      progress: pct == null ? undefined : pct / 100,
    };
  }
  if (paused.length) return { state: "Paused", detail: paused.length === 1 ? `${paused[0].file} paused` : `${paused.length} paused` };

  // Nothing active: the most recent finished item decides, until the next download starts.
  const last = items.find((i) => i.endTime);
  if (last?.state === "complete") return { state: "Complete", detail: last.file };
  if (last?.state === "failed") return { state: "Failed", detail: `${last.file}: ${reason(last.error)}` };
  return { state: "Idle" };
}

export function rate(bytesPerSec) {
  const units = ["B/s", "KB/s", "MB/s", "GB/s"];
  let v = bytesPerSec, u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return `${v >= 10 || u === 0 ? Math.round(v) : v.toFixed(1)} ${units[u]}`;
}

export function eta(sec) {
  if (sec < 60) return `${sec} s left`;
  if (sec < 3600) return `${Math.round(sec / 60)} min left`;
  return `${(sec / 3600).toFixed(1)} h left`;
}

/** Chrome's InterruptReason codes → words. */
export function reason(code) {
  if (!code) return "failed";
  return code.toLowerCase().replace(/^(network|file|server|user|crash)_/, "$1 ").replace(/_/g, " ");
}
