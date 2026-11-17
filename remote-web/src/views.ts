import type { Action, Peek } from './actions.ts';
import type { ApprovalView, Item, MediaView, Snapshot, TileView } from './delta.ts';
import { editsOf, diffLines } from './diff.ts';
import { h, replace } from './h.ts';
import type { LiveState } from './live.ts';

export interface Ui {
  /** Runs an action from a button: pressed until the PC answers; resolves true on success. */
  run(button: HTMLElement, action: Action, approval?: ApprovalView): Promise<boolean>;
  go(route: string): void;
  install?: () => void;
  activity?: ActivityEntry[];
  loadOlderActivity(): void;
  unpair(): void;
  readonly alerts: 'on' | 'off' | 'blocked' | 'unsupported';
  enableAlerts(): void;
  peekState?: Peek;
  refreshPeek(): void;
}

export interface ActivityEntry { ts: string; deviceName: string; type: string; target: string; ok: boolean; code?: string; preview?: string }

const time = (ms: number) => new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
const pcName = () => location.hostname.split('.')[0] ?? 'PC';

const labelled = (el: HTMLElement, label: string) => { el.setAttribute('aria-label', label); return el; };

function button(ui: Ui, label: string, action: Action, opts: { cls?: string; approval?: ApprovalView; then?: () => void } = {}): HTMLElement {
  const b = h('button', {
    class: opts.cls ?? '', type: 'button',
    onclick: async () => { if (await ui.run(b, action, opts.approval)) opts.then?.(); },
  }, label);
  return b;
}

export function render(root: HTMLElement, s: LiveState, route: string, ui: Ui): void {
  const snap = s.snapshot;
  const live = s.status === 'live';
  let body: HTMLElement;
  if (s.status === 'unpaired') body = h('main', {}, h('p', { class: 'error' }, 'This phone was unpaired on the PC. Pair it again from TaskDock → Phone (Remote).'));
  else if (!snap) body = h('main', {}, h('p', { class: 'muted' }, s.status === 'offline' ? 'Can’t reach your PC. Is Tailscale connected on this phone?' : 'Connecting…'));
  else if (route.startsWith('approval=')) body = approvalDetail(snap, decodeURIComponent(route.slice(9)), ui);
  else if (route === 'menu') body = menu(snap, s, ui);
  else if (route === 'activity') body = activity(ui);
  else if (route.startsWith('peek=')) body = peekView(snap, decodeURIComponent(route.slice(5)), ui);
  else body = h('main', {},
    needsYou(snap, ui),
    section('Tiles', snap.tiles.map(t => tile(t, ui))),
    snap.detected.length ? h('details', {}, h('summary', {}, `${snap.detected.length} detected`), ...snap.detected.map(t => tile(t, ui))) : null);

  if (!live) { body.classList.add('stale'); body.setAttribute('aria-disabled', 'true'); }
  replace(root, header(s, ui), body, snap?.media && route === '' ? media(snap.media, ui) : null);
}

function header(s: LiveState, ui: Ui): HTMLElement {
  const text = s.status === 'live' ? 'Live'
    : s.status === 'connecting' ? 'Reconnecting…'
      : s.status === 'unpaired' ? 'Unpaired'
        : s.lastSeen ? `Last seen ${time(s.lastSeen)}` : 'Offline';
  return h('header', {},
    h('h1', {}, h('a', { href: '#', class: 'plain' }, pcName())),
    h('span', { class: `dot ${s.status}`, role: 'status' }, text),
    s.snapshot?.flags.away ? h('span', { class: 'badge' }, 'Away') : null,
    ui.install ? h('button', { class: 'ghost', type: 'button', onclick: ui.install }, 'Install') : null,
    h('button', { class: 'ghost menu', type: 'button', 'aria-label': 'Menu', onclick: () => ui.go('menu') }, '⋯'));
}

function section(title: string, items: HTMLElement[]): HTMLElement {
  return h('section', {}, h('h2', {}, title), ...(items.length ? items : [h('p', { class: 'muted' }, 'Nothing here.')]));
}

function needsYou(s: Snapshot, ui: Ui): HTMLElement | null {
  const failed = s.tiles.filter(t => t.class === 'error');
  if (!s.approvals.length && !failed.length) return null;
  return section('Needs you', [...s.approvals.map(a => approvalCard(a, ui)), ...failed.map(t => tile(t, ui))]);
}

const minutesLeft = (a: ApprovalView) => Math.max(0, Math.round((a.deadline - Date.now()) / 60000));
const riskBadge = (a: ApprovalView) => h('span', { class: `badge ${a.risk}` }, a.risk === 'read' ? 'Read-only' : 'Fingerprint');

function approvalCard(a: ApprovalView, ui: Ui): HTMLElement {
  return h('article', { class: 'card attention tap', role: 'button', tabindex: 0, onclick: () => ui.go(`approval=${encodeURIComponent(a.id)}`) },
    h('div', { class: 'row' }, h('strong', {}, a.tool), riskBadge(a), h('span', { class: 'muted right' }, `${minutesLeft(a)} min left`)),
    h('div', { class: 'muted' }, a.project),
    h('code', { class: 'preview' }, a.preview));
}

function approvalDetail(s: Snapshot, id: string, ui: Ui): HTMLElement {
  const a = s.approvals.find(x => x.id === id);
  const back = h('button', { class: 'ghost', type: 'button', onclick: () => ui.go('') }, '← Back');
  if (!a) return h('main', {}, back, h('p', { class: 'muted' }, 'Already handled.'));
  const done = () => ui.go('');
  const reason = h('input', { type: 'text', maxlength: 500, placeholder: 'Reason for Claude (optional)', 'aria-label': 'Deny reason', id: 'deny-reason' }) as HTMLInputElement;
  const edits = editsOf(a.tool, a.input);
  return h('main', { class: 'detail' }, back,
    h('div', { class: 'row' }, h('h2', { class: 'tool' }, a.tool), riskBadge(a), h('span', { class: 'muted right' }, `${minutesLeft(a)} min left`)),
    h('div', { class: 'muted' }, a.project),
    edits
      ? h('div', {}, ...edits.map(e => h('pre', { class: 'diff' }, ...diffLines(e.before, e.after).map(l => h('span', { class: `d${l.op === '+' ? 'add' : l.op === '-' ? 'del' : 'same'}` }, `${l.op} ${l.text}\n`)))))
      : null,
    h('pre', { class: 'input' }, a.input),
    h('div', { class: 'actions' },
      button(ui, a.risk === 'read' ? 'Allow' : 'Allow 🔒', { type: 'approval.allow', approvalId: a.id }, { cls: 'primary', approval: a, then: done }),
      a.canAlwaysAllow ? button(ui, 'Always allow 🔒', { type: 'approval.alwaysAllow', approvalId: a.id }, { approval: a, then: done }) : null,
      reason,
      h('button', {
        class: 'danger', type: 'button',
        onclick: async (e: Event) => { if (await ui.run(e.currentTarget as HTMLElement, { type: 'approval.deny', approvalId: a.id, reason: reason.value.trim() || undefined })) done(); },
      }, 'Deny'),
      button(ui, 'Answer in the terminal', { type: 'approval.terminal', approvalId: a.id }, { then: done })));
}

function tile(t: TileView, ui: Ui): HTMLElement {
  const c = t.caps;
  const pin = t.pinId;
  const buttons = [
    c.play ? button(ui, 'Play', { type: 'adapter.command', tileKey: t.key, command: 'enterPlay' }) : null,
    c.pause ? button(ui, c.pauseText, { type: 'adapter.command', tileKey: t.key, command: 'togglePause' }) : null,
    c.stop ? button(ui, 'Stop', { type: 'adapter.command', tileKey: t.key, command: 'exitPlay' }) : null,
    c.launch && pin ? button(ui, 'Launch', { type: 'tile.launch', pinId: pin }) : null,
    c.peek ? h('button', { class: 'ghost', type: 'button', onclick: () => ui.go(`peek=${encodeURIComponent(t.key)}`) }, 'Peek') : null,
    c.ack ? button(ui, 'Clear', { type: 'tile.ack', tileKey: t.key }) : null,
    c.pin ? button(ui, 'Pin', { type: 'tile.pin', tileKey: t.key }, { cls: 'ghost' }) : null,
    pin ? labelled(button(ui, t.notify ? '🔔' : '🔕', { type: 'tile.notify', pinId: pin, on: !t.notify }, { cls: 'ghost' }),
      t.notify ? 'Sounds on. Tap to mute' : 'Sounds off. Tap to turn on') : null,
    pin ? h('button', {
      class: 'ghost', type: 'button', 'aria-label': `Rename ${t.label}`,
      onclick: (e: Event) => { const name = prompt('Name this tile', t.label); if (name !== null) void ui.run(e.currentTarget as HTMLElement, { type: 'tile.rename', pinId: pin, label: name.trim() || null }); },
    }, '✎') : null,
    c.unpin && pin ? button(ui, 'Unpin', { type: 'tile.unpin', pinId: pin }, { cls: 'ghost' }) : null,
  ].filter((b): b is HTMLElement => b !== null);

  return h('article', { class: `card ${t.class}` },
    h('div', { class: 'row' }, h('strong', { class: 'ellipsis' }, t.label), h('span', { class: 'state right' }, t.state)),
    t.detail ? h('div', { class: 'muted ellipsis' }, t.detail) : null,
    t.progress !== undefined ? h('progress', { max: 1, value: t.progress }) : null,
    t.items?.length ? h('ul', { class: 'items' }, ...t.items.map(i => item(t, i, ui))) : null,
    buttons.length ? h('div', { class: 'actions row-wrap' }, ...buttons) : null);
}

function item(t: TileView, i: Item, ui: Ui): HTMLElement {
  const cmd = (command: string, label: string) => button(ui, label, { type: 'download.command', tileKey: t.key, downloadId: i.id, command }, { cls: 'ghost small' });
  return h('li', {},
    h('span', { class: 'ellipsis' }, i.file),
    h('span', { class: 'muted' }, i.progress !== undefined ? `${Math.round(i.progress * 100)}%` : i.state.replace('_', ' ')),
    i.state === 'in_progress' ? cmd('pause', 'Pause') : null,
    i.state === 'paused' ? cmd('resume', 'Resume') : null,
    i.state === 'in_progress' || i.state === 'paused' ? cmd('cancel', 'Cancel') : null);
}

function media(m: MediaView, ui: Ui): HTMLElement {
  const query = h('input', { type: 'search', maxlength: 100, placeholder: 'Search on the PC', 'aria-label': 'Search music', id: 'media-query' }) as HTMLInputElement;
  const search = (target: string) => (e: Event) => {
    if (query.value.trim()) void ui.run(e.currentTarget as HTMLElement, { type: 'media.search', target, query: query.value.trim() }).then(ok => { if (ok) query.value = ''; });
  };
  const ctl = (label: string, aria: string, type: string, enabled: boolean) => {
    const b = button(ui, label, { type }, { cls: 'ghost' });
    b.setAttribute('aria-label', aria);
    if (!enabled) b.setAttribute('disabled', '');
    return b;
  };
  return h('footer', { class: 'media' },
    h('div', { class: 'row' },
      h('div', { class: 'ellipsis grow' }, h('strong', {}, m.title), h('div', { class: 'muted' }, m.artist || m.appName)),
      ctl('⏮', 'Previous', 'media.prev', m.canPrev),
      ctl(m.playing ? '⏸' : '▶', m.playing ? 'Pause' : 'Play', 'media.toggle', m.canToggle),
      ctl('⏭', 'Next', 'media.next', m.canNext)),
    h('div', { class: 'row-wrap' },
      ...(m.apps.length > 1 ? m.apps.map(a => button(ui, a.name, { type: 'media.select', appId: a.appId }, { cls: a.selected ? 'chip on' : 'chip' })) : []),
      button(ui, m.pinned ? 'Unpin player' : 'Pin player', { type: 'media.pin', on: !m.pinned }, { cls: 'chip' })),
    h('div', { class: 'row' }, query,
      h('button', { class: 'ghost', type: 'button', onclick: search('spotify') }, 'Spotify'),
      h('button', { class: 'ghost', type: 'button', onclick: search('youtube') }, 'YouTube')));
}

function menu(s: Snapshot, live: LiveState, ui: Ui): HTMLElement {
  return h('main', {},
    h('button', { class: 'ghost', type: 'button', onclick: () => ui.go('') }, '← Back'),
    section('Presets', [
      ...s.presets.names.map(n => h('div', { class: 'row card' },
        h('strong', { class: 'grow' }, n, n === s.presets.active ? h('span', { class: 'muted' }, ' · active') : null),
        button(ui, 'Apply', { type: 'preset.apply', name: n, merge: false }, { cls: 'ghost' }),
        button(ui, 'Merge', { type: 'preset.apply', name: n, merge: true }, { cls: 'ghost' }))),
      button(ui, 'Launch missing apps', { type: 'preset.launchMissing' }),
    ]),
    section('This PC', [
      button(ui, 'Lock PC', { type: 'power.lock' }),
      h('button', {
        class: 'danger', type: 'button',
        onclick: (e: Event) => {
          if (confirm('Put the PC to sleep?\n\nYour phone can’t wake the PC again. Someone has to press a key or the power button.'))
            void ui.run(e.currentTarget as HTMLElement, { type: 'power.sleep', confirm: true });
        },
      }, 'Sleep PC'),
    ]),
    section('This phone', [
      h('p', {}, live.deviceName ?? 'This phone'),
      ui.alerts === 'on' ? h('p', { class: 'muted' }, 'Alerts are on: approvals and failures reach this phone when you’re away from the PC.')
        : ui.alerts === 'unsupported' ? h('p', { class: 'muted' }, 'This browser can’t show alerts.')
          : h('button', { class: 'primary', type: 'button', onclick: ui.enableAlerts }, 'Turn on alerts'),
      h('button', { type: 'button', onclick: () => ui.go('activity') }, 'Activity'),
      h('button', { class: 'danger', type: 'button', onclick: () => { if (confirm('Unpair this phone? You can pair it again from the PC.')) ui.unpair(); } }, 'Unpair'),
    ]));
}

function activity(ui: Ui): HTMLElement {
  const list = ui.activity;
  return h('main', {},
    h('button', { class: 'ghost', type: 'button', onclick: () => ui.go('menu') }, '← Back'),
    h('h2', {}, 'Activity'),
    !list ? h('p', { class: 'muted' }, 'Loading…')
      : !list.length ? h('p', { class: 'muted' }, 'Nothing yet.')
        : h('ul', { class: 'activity' }, ...list.map(e => h('li', {},
          h('div', { class: 'row' }, h('strong', {}, e.type), h('span', { class: 'muted right' }, new Date(e.ts).toLocaleString())),
          h('div', { class: 'muted' }, `${e.target} · ${e.deviceName} · ${e.ok ? 'ok' : e.code ?? 'failed'}`),
          e.preview ? h('code', { class: 'preview' }, e.preview) : null))),
    list?.length ? h('button', { class: 'ghost', type: 'button', onclick: ui.loadOlderActivity }, 'Older') : null);
}

function peekView(s: Snapshot, key: string, ui: Ui): HTMLElement {
  const t = [...s.tiles, ...s.detected].find(x => x.key === key);
  const p = ui.peekState;
  return h('main', { class: 'detail' },
    h('div', { class: 'row' },
      h('button', { class: 'ghost', type: 'button', onclick: () => ui.go('') }, '← Back'),
      h('button', { class: 'ghost', type: 'button', onclick: ui.refreshPeek, disabled: p?.loading ?? false }, p?.loading ? 'Capturing…' : 'Refresh')),
    h('h2', { class: 'tool' }, t?.label ?? 'Window'),
    p?.error ? h('p', { class: 'muted' }, p.error) : null,
    p?.url ? h('img', { class: 'peek', src: p.url, alt: `Screenshot of ${t?.label ?? 'the window'}` }) : null);
}

/** Lives outside the redrawn root: every update replaces the screen, and would wipe a banner straight away. */
export function toast(text: string): void {
  let t = document.getElementById('toast');
  if (!t) document.body.appendChild(t = h('div', { id: 'toast', role: 'alert' }));
  replace(t, text);
  t.classList.add('show');
  setTimeout(() => t.classList.remove('show'), 4000);
}
