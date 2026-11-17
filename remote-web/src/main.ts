import { act, describe, peek } from './actions.ts';
import { newAlerts } from './banner.ts';
import type { Snapshot } from './delta.ts';
import { h, replace } from './h.ts';
import { clearPairing, loadPairing } from './idb.ts';
import { Live } from './live.ts';
import { pairPage } from './pair.ts';
import { alertsState, closeHandled, enableAlerts, subscribe } from './push.ts';
import { render, toast, type ActivityEntry, type Ui } from './views.ts';

interface InstallPrompt extends Event { prompt(): Promise<void> }

const root = document.getElementById('app')!;
if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => { /* works without, just not offline */ });

async function boot(): Promise<void> {
  if (location.pathname === '/pair') { pairPage(root); return; }

  const pairing = await loadPairing();
  if (!pairing) {
    replace(root, h('main', { class: 'pair' },
      h('h1', {}, 'TaskDock'),
      h('p', {}, 'This phone isn’t paired yet. On your PC, open TaskDock → Phone (Remote) → Pair a phone, then scan the QR code.')));
    return;
  }

  const token = pairing.token;
  let install: InstallPrompt | undefined;
  let waiting = false;
  const route = () => location.hash.slice(1);

  // Redrawing replaces the DOM; while someone types (deny reason, search) wait, or their text and focus would vanish.
  const draw = () => {
    const el = document.activeElement;
    if (el instanceof HTMLInputElement && root.contains(el)) { waiting = true; return; }
    waiting = false;
    render(root, live.state, route(), ui);
  };
  root.addEventListener('focusout', () => setTimeout(() => { if (waiting) draw(); }));

  const ui: Ui = {
    async run(button, action, approval) {
      button.setAttribute('disabled', '');
      button.setAttribute('aria-busy', 'true');
      const r = await act(live, action, approval);
      button.removeAttribute('disabled');
      button.removeAttribute('aria-busy');
      if (!r.ok) toast(describe(r));
      return r.ok;
    },
    go(r) { location.hash = r; },
    get install() { return install ? () => { void install?.prompt(); install = undefined; draw(); } : undefined; },
    activity: undefined as ActivityEntry[] | undefined,
    loadOlderActivity() { void loadActivity(true); },
    get alerts() { return alertsState(); },
    peekState: undefined,
    refreshPeek() { void loadPeek(); },
    async enableAlerts() { toast(await enableAlerts(token).catch((e: unknown) => e instanceof Error ? e.message : String(e))); draw(); },
    async unpair() {
      await fetch('/api/devices/self', { method: 'DELETE', headers: { Authorization: `Bearer ${token}` } }).catch(() => undefined);
      await clearPairing();
      location.replace('/');
    },
  };

  async function loadPeek(): Promise<void> {
    const key = route().startsWith('peek=') ? decodeURIComponent(route().slice(5)) : null;
    if (!key) return;
    if (ui.peekState?.url) URL.revokeObjectURL(ui.peekState.url);
    ui.peekState = { loading: true };
    draw();
    const r = await peek(token, key);
    if (route() !== `peek=${encodeURIComponent(key)}`) { if (r.url) URL.revokeObjectURL(r.url); return; } // left meanwhile
    ui.peekState = r;
    draw();
  }

  async function loadActivity(older: boolean): Promise<void> {
    const last = ui.activity?.at(-1);
    const before = older && last ? `?before=${Date.parse(last.ts)}` : '';
    const res = await fetch(`/api/activity${before}`, { headers: { Authorization: `Bearer ${token}` } }).catch(() => undefined);
    const page = res?.ok ? await res.json() as ActivityEntry[] : [];
    ui.activity = older ? [...(ui.activity ?? []), ...page] : page;
    draw();
  }

  let approvalIds = '';
  let seen: Snapshot | undefined;
  const live = new Live(token, s => {
    draw();
    if (s.status === 'live' && s.snapshot && s.snapshot !== seen) {
      const alerts = newAlerts(seen, s.snapshot);
      seen = s.snapshot;
      if (alerts.length) { toast(alerts.join('\n')); navigator.vibrate?.([30, 60, 30]); }
    } else if (s.status !== 'live') seen = undefined; // after a reconnect, the first snapshot is a fresh start
    const ids = s.snapshot?.approvals.map(a => a.id) ?? [];
    if (s.status === 'live' && ids.join() !== approvalIds) { approvalIds = ids.join(); void closeHandled(new Set(ids)).catch(() => undefined); }
  });
  if (alertsState() === 'on') void subscribe(token).catch(() => undefined); // keep the PC's copy current
  addEventListener('hashchange', () => {
    if (route() === 'activity') void loadActivity(false);
    if (route().startsWith('peek=')) void loadPeek();
    else if (ui.peekState) { if (ui.peekState.url) URL.revokeObjectURL(ui.peekState.url); ui.peekState = undefined; }
    draw();
    scrollTo(0, 0);
  });
  addEventListener('beforeinstallprompt', e => { e.preventDefault(); install = e as InstallPrompt; draw(); });
  setInterval(draw, 30_000); // approval countdowns and "Last seen" age
  if (route() === 'activity') void loadActivity(false);
  live.connect();
}

void boot();
