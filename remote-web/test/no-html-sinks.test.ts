import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { test } from 'node:test';

// Adapter text (labels, window titles, file names) is attacker-influenced: it may only reach the page through h().
test('no source file writes HTML', () => {
  const dir = new URL('../src/', import.meta.url);
  for (const f of readdirSync(dir).filter(f => f.endsWith('.ts'))) {
    const src = readFileSync(new URL(f, dir), 'utf8');
    for (const sink of ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'createContextualFragment', 'eval(', 'new Function'])
      assert.ok(!src.includes(sink), `${f} uses ${sink}`);
  }
});
