import { h, replace } from './h.ts';
import { loadPairing } from './idb.ts';
import { Live } from './live.ts';
import { pairPage } from './pair.ts';
import { render } from './views.ts';

interface InstallPrompt extends Event { prompt(): Promise<void> }

const root = document.getElementById('app')!;
if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => { /* works without, just not offline */ });

let install: InstallPrompt | undefined;

async function boot(): Promise<void> {
  if (location.pathname === '/pair') { pairPage(root); return; }

  const pairing = await loadPairing();
  if (!pairing) {
    replace(root, h('main', { class: 'pair' },
      h('h1', {}, 'TaskDock'),
      h('p', {}, 'This phone isn’t paired yet. On your PC, open TaskDock → Settings → Remote → Pair a phone, then scan the QR code.')));
    return;
  }

  const live = new Live(pairing.token, s => draw());
  const draw = () => render(root, live.state, install ? () => { void install?.prompt(); install = undefined; draw(); } : undefined);
  addEventListener('beforeinstallprompt', e => { e.preventDefault(); install = e as InstallPrompt; draw(); });
  setInterval(draw, 30_000); // approval countdowns and "Last seen" age
  live.connect();
}

void boot();
