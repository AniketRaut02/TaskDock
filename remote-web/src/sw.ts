/// <reference lib="webworker" />
// Service worker: caches the app shell so the app opens instantly, even before the PC answers (live data is never cached),
// shows pushes, and sends Deny straight from an approval's notification.
import { loadPairing } from './idb.ts';

declare const self: ServiceWorkerGlobalScope; // the import makes this a module, so this self shadows the lib's DedicatedWorkerGlobalScope
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

interface Push { title: string; body: string; tag: string; approvalId?: string }

const icon = '/icons/icon-192.png';

self.addEventListener('push', (e: PushEvent) => {
  const p = e.data?.json() as Push | undefined;
  if (!p) return;
  e.waitUntil(self.registration.showNotification(p.title, {
    body: p.body, tag: p.tag, icon, data: { approvalId: p.approvalId },
    requireInteraction: !!p.approvalId,
    // Deny works from the lock screen; Allow always opens the app first (it may need a fingerprint).
    actions: p.approvalId ? [{ action: 'deny', title: 'Deny' }, { action: 'open', title: 'Open' }] : [],
  } as NotificationOptions));
});

self.addEventListener('notificationclick', (e: NotificationEvent) => {
  const approvalId = (e.notification.data as { approvalId?: string } | null)?.approvalId;
  e.notification.close();
  e.waitUntil(e.action === 'deny' && approvalId ? deny(approvalId) : open(approvalId ? `/#approval=${encodeURIComponent(approvalId)}` : '/'));
});

async function deny(approvalId: string): Promise<void> {
  const pairing = await loadPairing();
  if (!pairing) return open('/');
  let text: string | null;
  try {
    const res = await fetch('/api/action', {
      method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${pairing.token}` },
      body: JSON.stringify({ id: crypto.randomUUID(), action: { type: 'approval.deny', approvalId, reason: 'Denied from phone' } }),
    });
    const r = res.ok ? await res.json() as { ok: boolean; code?: string } : { ok: false };
    text = r.ok ? null : r.code === 'already_resolved' ? 'Already handled on the PC' : 'Couldn’t deny it. Open TaskDock.';
  } catch { text = 'Couldn’t reach your PC. Is Tailscale connected?'; }
  if (text) await self.registration.showNotification(text, { tag: `approval-${approvalId}`, icon });
}

async function open(url: string): Promise<void> {
  const [win] = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
  if (win) {
    await win.focus();
    await win.navigate(url).catch(() => undefined);
    return;
  }
  await self.clients.openWindow(url);
}
