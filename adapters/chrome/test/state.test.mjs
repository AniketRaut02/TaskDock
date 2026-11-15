// node --test "adapters/chrome/test/*.test.mjs"   (no dependencies)
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { summarize, toItems, rate, eta, reason } from "../state.js";

const now = Date.parse("2026-11-15T12:00:00Z");
const ago = (s) => new Date(now - s * 1000).toISOString();
const dl = (o) => ({ id: 1, filename: String.raw`C:\Users\me\Downloads\setup.exe`, url: "https://secret.example/token=abc", state: "in_progress",
  paused: false, danger: "safe", bytesReceived: 0, totalBytes: 100, ...o });
const run = (downloads, flash = null) => summarize(toItems(downloads, new Map([[1, 2_621_440]]), now), flash, now);

test("nothing at all is Idle", () => assert.deepEqual(run([]), { state: "Idle" }));

test("downloading shows count, percent, speed and ETA", () => {
  const s = run([dl({ bytesReceived: 45, estimatedEndTime: new Date(now + 125_000).toISOString() }), dl({ id: 2, filename: "b.zip", bytesReceived: 5, totalBytes: 100 })]);
  assert.equal(s.state, "Downloading (2, 25%)");
  assert.equal(s.progress, 0.25);
  assert.equal(s.detail, "setup.exe · 2.5 MB/s · 2 min left · +1 more");
});

test("unknown total size still downloads, without a percent", () =>
  assert.equal(run([dl({ totalBytes: -1 })]).state, "Downloading (1)"));

test("a dangerous file waiting in the browser needs you", () =>
  assert.equal(run([dl({ danger: "uncommon" })]).state, "Needs confirmation"));

test("a file that just finished flashes Complete even while another downloads", () => {
  const s = run([dl({ id: 2 })], { until: now + 1000, file: "a.zip" });
  assert.deepEqual([s.state, s.detail], ["Complete", "a.zip finished · 1 still downloading"]);
});

test("the last finished item decides once nothing is active", () => {
  assert.equal(run([dl({ state: "complete", endTime: ago(5) })]).state, "Complete");
  const f = run([dl({ state: "interrupted", error: "NETWORK_FAILED", endTime: ago(5) }), dl({ id: 2, state: "complete", endTime: ago(60) })]);
  assert.deepEqual([f.state, f.detail], ["Failed", "setup.exe: network failed"]);
  assert.equal(run([dl({ state: "interrupted", error: "USER_CANCELED", endTime: ago(5) })]).state, "Idle");
  assert.equal(run([dl({ state: "complete", endTime: ago(7200) })]).state, "Idle"); // older than an hour
});

test("all active items paused is Paused", () =>
  assert.equal(run([dl({ paused: true }), dl({ id: 2, paused: true })]).detail, "2 paused"));

test("items never carry URLs and keep at most 10 finished ones", () => {
  const many = Array.from({ length: 15 }, (_, i) => dl({ id: i + 10, state: "complete", endTime: ago(i) }));
  const items = toItems([dl({}), ...many], new Map(), now);
  assert.equal(items.length, 11);
  assert.ok(!JSON.stringify(items).includes("secret.example"));
});

test("formatting", () => {
  assert.equal(rate(512), "512 B/s");
  assert.equal(rate(12.34 * 1024 * 1024), "12 MB/s");
  assert.equal(eta(45), "45 s left");
  assert.equal(eta(7200), "2.0 h left");
  assert.equal(reason("FILE_NO_SPACE"), "file no space");
});

test("our state body has the sample's shape", () => {
  const sample = JSON.parse(readFileSync(new URL("../../../protocol/samples/state-downloads.json", import.meta.url)));
  const items = toItems([dl({ bytesReceived: 45 })], new Map(), now);
  for (const k of Object.keys(sample.body.items[0])) assert.ok(k in items[0], `items[].${k}`);
  const s = run([dl({ bytesReceived: 45 })]);
  for (const k of ["state", "detail", "progress"]) assert.ok(k in s, k);
});
