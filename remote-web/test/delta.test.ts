import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { applyDelta, type Delta, type Snapshot } from '../src/delta.ts';

const sample = <T>(f: string): T => JSON.parse(readFileSync(new URL(`../../protocol/remote/samples/${f}`, import.meta.url), 'utf8')) as T;

test('golden delta applies to the golden snapshot (same samples as DeltaTests.cs)', () => {
  assert.deepEqual(applyDelta(sample<Snapshot>('snapshot.json'), sample<Delta>('delta.json')), sample('snapshot-after-delta.json'));
});

test('a gap in rev or a new epoch asks for a resync', () => {
  const s = sample<Snapshot>('snapshot.json');
  assert.equal(applyDelta(s, { ...sample<Delta>('delta.json'), rev: 9 }), null);
  assert.equal(applyDelta(s, { ...sample<Delta>('delta.json'), epoch: 'e2' }), null);
});

test('media: null removes the player, absent keeps it', () => {
  const s = sample<Snapshot>('snapshot.json');
  assert.equal(applyDelta(s, { epoch: 'e1', rev: 8, media: null })!.media, undefined);
  assert.deepEqual(applyDelta(s, { epoch: 'e1', rev: 8 })!.media, s.media);
});

test('removal without a new order drops the tile from its list', () => {
  const s = sample<Snapshot>('snapshot.json');
  const next = applyDelta(s, { epoch: 'e1', rev: 8, remove: ['vscode|w1'] })!;
  assert.deepEqual(next.detected, []);
  assert.equal(next.tiles.length, s.tiles.length);
});
