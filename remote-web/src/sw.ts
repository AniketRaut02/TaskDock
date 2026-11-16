/// <reference lib="webworker" />
// Service worker: caches the app shell so the app opens instantly, even before the PC answers. Live data is never cached.
export {}; // a module, so this self shadows the lib's DedicatedWorkerGlobalScope
declare const self: ServiceWorkerGlobalScope;
declare const SHELL: string[];      // filled in by build.mjs
declare const SHELL_VERSION: string;

const CACHE = `shell-${SHELL_VERSION}`;

self.addEventListener('install', e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', e => {
  e.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (e: FetchEvent) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== location.origin || url.pathname.startsWith('/api/')) return;
  // The page itself: network first so a new TaskDock version shows at once; the cached shell when the PC is unreachable.
  if (e.request.mode === 'navigate') {
    e.respondWith(fetch(e.request).catch(async () => (await caches.match('/')) ?? Response.error()));
    return;
  }
  e.respondWith(caches.match(e.request).then(hit => hit ?? fetch(e.request)));
});
