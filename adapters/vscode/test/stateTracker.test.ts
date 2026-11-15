// npm test (Node 22.6+ runs these .ts files directly)
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { StateTracker } from "../src/stateTracker.ts";
import { frame, instanceId } from "../src/wire.ts";

const t0 = 1_000_000;
const make = () => new StateTracker(10_000, false);

test("idle by default", () => assert.deepEqual(make().snapshot(t0), { state: "Idle" }));

test("a task exiting with 1 is Failed and wins over things still running", () => {
  const s = make();
  s.taskStarted("a", "build");
  s.taskStarted("b", "watch");
  s.taskEnded("a", 1);
  assert.deepEqual(s.snapshot(t0), { state: "Failed (exit 1)", detail: "build exited with 1 · watch running" });
});

test("starting a task clears the old result", () => {
  const s = make();
  s.taskStarted("a", "build"); s.taskEnded("a", 1);
  s.taskStarted("a2", "build");
  assert.equal(s.snapshot(t0).state, "Task running");
  s.taskEnded("a2", 0);
  assert.deepEqual(s.snapshot(t0), { state: "Succeeded", detail: "build" });
});

test("tasks VS Code can't track (no exit code) leave no result", () => {
  const s = make();
  s.taskStarted("a", "custom"); s.taskEnded("a", undefined);
  assert.equal(s.snapshot(t0).state, "Idle");
});

test("long command: shown only after the threshold, then Succeeded", () => {
  const s = make();
  s.commandStarted("c", "npm install", t0);
  assert.equal(s.snapshot(t0 + 9_000).state, "Idle");
  assert.equal(s.nextCommandDue(t0), t0 + 10_000);
  assert.deepEqual(s.snapshot(t0 + 10_000), { state: "Command running", detail: "npm install" });
  s.commandEnded("c", 0, t0 + 42_000);
  assert.deepEqual(s.snapshot(t0 + 42_000), { state: "Succeeded", detail: "npm install" });
});

test("short commands are ignored and don't clear a failure", () => {
  const s = make();
  s.taskStarted("a", "test"); s.taskEnded("a", 2);
  s.commandStarted("c", "ls", t0);
  s.commandEnded("c", 0, t0 + 300);
  assert.equal(s.snapshot(t0 + 300).state, "Failed (exit 2)");
});

test("a long command failing reports its exit code", () => {
  const s = make();
  s.commandStarted("c", "cargo build --release", t0);
  s.commandEnded("c", 101, t0 + 15_000);
  assert.equal(s.snapshot(t0 + 15_000).state, "Failed (exit 101)");
});

test("debugging beats tasks; ending it reports only when asked to", () => {
  const s = make();
  s.taskStarted("a", "watch");
  s.debugStarted("d", "Launch app");
  assert.deepEqual(s.snapshot(t0), { state: "Debugging", detail: "Launch app · watch running" });
  s.debugEnded("d");
  assert.equal(s.snapshot(t0).state, "Task running");
  s.notifyOnDebugEnd = true;
  s.debugStarted("d2", "Launch app"); s.debugEnded("d2");
  s.taskEnded("a", undefined);
  assert.deepEqual(s.snapshot(t0), { state: "Succeeded", detail: "Launch app (debug) ended" });
});

test("long command lines are truncated", () => {
  const s = make();
  s.commandStarted("c", "x".repeat(100), t0);
  assert.equal(s.snapshot(t0 + 10_000).detail!.length, 60);
});

test("instance id is stable and ignores slash style and case", () => {
  assert.equal(instanceId("abcdef12-xyz", "D:\\Projects\\Fatebound\\"), instanceId("abcdef12-other", "d:/projects/fatebound"));
  assert.match(instanceId("abcdef12", undefined), /^vscode:abcdef12:[0-9a-f]{6}$/);
});

test("frames have the golden sample's envelope shape", () => {
  const sample = JSON.parse(readFileSync(new URL("../../../protocol/samples/state-unity.json", import.meta.url), "utf8"));
  const ours = JSON.parse(frame("state", "vscode:1:abc", { state: "Idle", label: "Fatebound" }));
  assert.deepEqual(Object.keys(ours).sort(), Object.keys(sample).sort());
  assert.equal(ours.src.adapter, "vscode");
});
