import assert from 'node:assert/strict';
import { test } from 'node:test';
import { diffLines, editsOf } from '../src/diff.ts';

const show = (before: string, after: string) => diffLines(before, after).map(l => l.op + l.text).join('|');

test('unchanged lines stay, changed ones show as - then +', () => {
  assert.equal(show('a\nb\nc', 'a\nB\nc'), ' a|-b|+B| c');
  assert.equal(show('', 'x'), '-|+x');
  assert.equal(show('same', 'same'), ' same');
  assert.equal(show('a\nb', 'b'), '-a| b');
});

test('Edit and MultiEdit inputs give their edits; other tools give none', () => {
  assert.deepEqual(editsOf('Edit', JSON.stringify({ file_path: 'x', old_string: 'a', new_string: 'b' })), [{ before: 'a', after: 'b' }]);
  assert.deepEqual(editsOf('MultiEdit', JSON.stringify({ edits: [{ old_string: '1', new_string: '2' }, { old_string: '3', new_string: '4' }] })),
    [{ before: '1', after: '2' }, { before: '3', after: '4' }]);
  assert.equal(editsOf('Bash', '{"command":"ls"}'), null);
  assert.equal(editsOf('Edit', 'not json'), null);
});
