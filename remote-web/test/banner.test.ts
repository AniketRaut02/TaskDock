import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { newAlerts } from '../src/banner.ts';
import type { Snapshot } from '../src/delta.ts';

const base = JSON.parse(readFileSync(new URL('../../protocol/remote/samples/snapshot.json', import.meta.url), 'utf8')) as Snapshot;

test('nothing on the first snapshot or when nothing new happened', () => {
  assert.deepEqual(newAlerts(null, base), []);
  assert.deepEqual(newAlerts(base, base), []);
});

test('a new approval and a tile that just failed are announced once', () => {
  const next: Snapshot = {
    ...base,
    approvals: [...base.approvals, { ...base.approvals[0]!, id: 'ap2', tool: 'Edit' }],
    tiles: base.tiles.map(t => t.key === 'unity|g1' ? { ...t, class: 'error', state: 'Compile errors (2)' } : t),
  };
  assert.deepEqual(newAlerts(base, next), ['Claude needs approval: Edit · api', 'MyGame: Compile errors (2)']);
  assert.deepEqual(newAlerts(next, next), []);
});
