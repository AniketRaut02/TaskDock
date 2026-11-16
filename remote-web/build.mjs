// Builds dist/: hashed app.js and app.css (cached forever), index.html pointing at them, and sw.js listing the shell.
import * as esbuild from 'esbuild';
import { createHash } from 'node:crypto';
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';

rmSync('dist', { recursive: true, force: true });
mkdirSync('dist/icons', { recursive: true });

const app = await esbuild.build({
  entryPoints: { app: 'src/main.ts', style: 'src/app.css' },
  bundle: true, format: 'esm', target: 'chrome120', minify: true, outdir: 'dist', entryNames: '[name].[hash]', metafile: true,
});
const out = Object.keys(app.metafile.outputs).map(p => p.replace(/^dist\//, ''));
const js = out.find(f => f.endsWith('.js')), css = out.find(f => f.endsWith('.css'));
writeFileSync('dist/index.html', readFileSync('index.html', 'utf8').replace('{{js}}', js).replace('{{css}}', css));
cpSync('manifest.webmanifest', 'dist/manifest.webmanifest');
cpSync('icons', 'dist/icons', { recursive: true });

const shell = ['/', `/${js}`, `/${css}`, '/manifest.webmanifest', '/icons/icon-192.png', '/icons/icon-512.png'];
const version = createHash('sha256').update(shell.join()).update(readFileSync('dist/index.html')).digest('hex').slice(0, 12);
await esbuild.build({
  entryPoints: ['src/sw.ts'], bundle: true, format: 'iife', target: 'chrome120', minify: true, outfile: 'dist/sw.js',
  define: { SHELL: JSON.stringify(shell), SHELL_VERSION: JSON.stringify(version) },
});
console.log(`built ${js}, ${css}, sw.js (shell ${version})`);
