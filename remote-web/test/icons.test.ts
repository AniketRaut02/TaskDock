import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { ICONS } from '../src/icons.ts';

// The widget and the phone draw the same icons: Icons.xaml must hold exactly the paths in icons.ts.
test('widget icons match the phone icons', () => {
  const xaml = readFileSync(new URL('../../src/TaskDock.App/Icons.xaml', import.meta.url), 'utf8');
  const widget = Object.fromEntries([...xaml.matchAll(/<StreamGeometry x:Key="I\.(\w+)">([^<]+)<\/StreamGeometry>/g)].map(m => [m[1], m[2]]));
  assert.deepEqual(widget, { ...ICONS });
});
