import type { Action, Peek } from './actions.ts';
import type { ApprovalView, Item, MediaView, Snapshot, TileView } from './delta.ts';
import { editsOf, diffLines } from './diff.ts';
import { h, replace, type Child } from './h.ts';
import { icon, type IconName } from './icons.ts';
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
/** The same fallback glyphs as the widget (AppIcons.GlyphFor). */
const glyph = (adapter: string): IconName => adapter === 'claude' ? 'terminal' : adapter === 'downloads' ? 'download' : 'window';

/** A button that runs an action. Content is text, an icon, or both; icon-only buttons need a label. */
function button(ui: Ui, content: Child | Child[], action: Action, opts: { cls?: string; label?: string; approval?: ApprovalView; then?: () => void } = {}): HTMLElement {
  const b = h('button', {
    class: opts.cls ?? '', type: 'button', 'aria-label': opts.label,
    onclick: async () => { if (await ui.run(b, action, opts.approval)) opts.then?.(); },
  }, ...(Array.isArray(content) ? content : [content]));
  return b;
}

const iconButton = (ui: Ui, name: IconName, label: string, action: Action, cls = 'icon-btn') => button(ui, icon(name), action, { cls, label });

function nav(label: string, name: IconName, onclick: () => void, cls = 'icon-btn'): HTMLElement {
  return h('button', { class: cls, type: 'button', 'aria-label': label, onclick }, icon(name));
}

export function render(root: HTMLElement, s: LiveState, route: string, ui: Ui): void {
  const snap = s.snapshot;
  const live = s.status === 'live';
  let body: HTMLElement;
  if (s.status === 'unpaired') body = h('main', {}, empty('phone', 'This phone was unpaired on the PC.', 'Pair it again from TaskDock → Phone (Remote).'));
  else if (!snap) body = h('main', {}, s.status === 'offline'
    ? empty('activity', 'Can’t reach your PC', 'Is Tailscale connected on this phone?')
    : empty('refresh', 'Connecting…', ''));
  else if (route.startsWith('approval=')) body = approvalDetail(snap, decodeURIComponent(route.slice(9)), ui);
  else if (route === 'menu') body = menu(snap, s, ui);
  else if (route === 'activity') body = activity(ui);
  else if (route.startsWith('peek=')) body = peekView(snap, decodeURIComponent(route.slice(5)), ui);
  else body = h('main', {},
    needsYou(snap, ui),
    section('Tiles', snap.tiles.map(t => tile(t, ui))),
    snap.detected.length ? h('details', { class: 'detected' },
      h('summary', {}, icon('chevronDown', 16), `${snap.detected.length} detected`), ...snap.detected.map(t => tile(t, ui))) : null);

  if (!live) { body.classList.add('stale'); body.setAttribute('aria-disabled', 'true'); }
  replace(root, header(s, route, ui), body, snap?.media && route === '' ? media(snap.media, ui) : null);
}

function empty(name: IconName, title: string, text: string): HTMLElement {
  return h('div', { class: 'empty' }, icon(name, 28), h('strong', {}, title), text ? h('p', { class: 'muted' }, text) : null);
}

function header(s: LiveState, route: string, ui: Ui): HTMLElement {
  const text = s.status === 'live' ? 'Live'
    : s.status === 'connecting' ? 'Reconnecting'
      : s.status === 'unpaired' ? 'Unpaired'
        : s.lastSeen ? `Last seen ${time(s.lastSeen)}` : 'Offline';
  const sub = route === '' ? null : route === 'menu' ? 'Settings' : route === 'activity' ? 'Activity' : route.startsWith('peek=') ? 'Peek' : 'Approval';
  const back = route === '' ? null : nav('Back', 'back', () => ui.go(route === 'activity' ? 'menu' : ''));
  return h('header', {},
    back,
    h('div', { class: 'title' },
      h('h1', {}, sub ?? pcName()),
      h('span', { class: `status ${s.status}`, role: 'status' }, h('i', {}), text),
      s.snapshot?.flags.away ? h('span', { class: 'chip away' }, 'Away') : null),
    ui.install ? h('button', { class: 'pill-btn', type: 'button', onclick: ui.install }, 'Install') : null,
    route === '' ? nav('Settings', 'settings', () => ui.go('menu')) : null);
}

function section(title: string, items: HTMLElement[]): HTMLElement {
  return h('section', {}, h('h2', {}, title), ...(items.length ? items : [h('p', { class: 'muted pad' }, 'Nothing here yet.')]));
}

function needsYou(s: Snapshot, ui: Ui): HTMLElement | null {
  const failed = s.tiles.filter(t => t.class === 'error');
  if (!s.approvals.length && !failed.length) return null;
  return section('Needs you', [...s.approvals.map(a => approvalCard(a, ui)), ...failed.map(t => tile(t, ui))]);
}

const minutesLeft = (a: ApprovalView) => Math.max(0, Math.round((a.deadline - Date.now()) / 60000));
const riskChip = (a: ApprovalView) => a.risk === 'read'
  ? h('span', { class: 'chip' }, 'Read-only')
  : h('span', { class: 'chip risky' }, icon('lock', 12), 'Fingerprint');

function approvalCard(a: ApprovalView, ui: Ui): HTMLElement {
  return h('article', { class: 'card approval', role: 'button', tabindex: 0, onclick: () => ui.go(`approval=${encodeURIComponent(a.id)}`) },
    h('div', { class: 'card-head' },
      h('span', { class: 'glyph attention' }, icon('terminal')),
      h('div', { class: 'grow' }, h('strong', {}, a.tool), h('div', { class: 'muted ellipsis' }, a.project)),
      riskChip(a)),
    h('code', { class: 'preview' }, a.preview),
    h('div', { class: 'card-foot' }, h('span', { class: 'muted' }, `${minutesLeft(a)} min left`), h('span', { class: 'link' }, 'Review', icon('chevronDown', 14))));
}

function approvalDetail(s: Snapshot, id: string, ui: Ui): HTMLElement {
  const a = s.approvals.find(x => x.id === id);
  if (!a) return h('main', {}, empty('check', 'Already handled', 'This approval was answered or timed out.'));
  const done = () => ui.go('');
  const reason = h('input', { type: 'text', maxlength: 500, placeholder: 'Reason for Claude (optional)', 'aria-label': 'Deny reason', id: 'deny-reason' }) as HTMLInputElement;
  const edits = editsOf(a.tool, a.input);
  return h('main', { class: 'detail' },
    h('div', { class: 'card-head' },
      h('span', { class: 'glyph attention' }, icon('terminal')),
      h('div', { class: 'grow' }, h('strong', { class: 'big' }, a.tool), h('div', { class: 'muted' }, `${a.project} · ${minutesLeft(a)} min left`)),
      riskChip(a)),
    edits
      ? h('div', {}, ...edits.map(e => h('pre', { class: 'diff' }, ...diffLines(e.before, e.after).map(l => h('span', { class: `d${l.op === '+' ? 'add' : l.op === '-' ? 'del' : 'same'}` }, `${l.op} ${l.text}\n`)))))
      : null,
    h('pre', { class: 'input' }, a.input),
    h('div', { class: 'stack' },
      button(ui, [a.risk === 'read' ? icon('check') : icon('lock'), 'Allow'], { type: 'approval.allow', approvalId: a.id }, { cls: 'primary', approval: a, then: done }),
      a.canAlwaysAllow ? button(ui, [icon('lock'), 'Always allow'], { type: 'approval.alwaysAllow', approvalId: a.id }, { cls: 'secondary', approval: a, then: done }) : null,
      h('div', { class: 'deny-row' },
        reason,
        h('button', {
          class: 'danger', type: 'button',
          onclick: async (e: Event) => { if (await ui.run(e.currentTarget as HTMLElement, { type: 'approval.deny', approvalId: a.id, reason: reason.value.trim() || undefined })) done(); },
        }, icon('close'), 'Deny')),
      button(ui, [icon('terminal'), 'Answer in the terminal'], { type: 'approval.terminal', approvalId: a.id }, { cls: 'ghost', then: done })));
}

function tile(t: TileView, ui: Ui): HTMLElement {
  const c = t.caps;
  const pin = t.pinId;
  const main = [
    c.play ? button(ui, [icon('play', 16), 'Play'], { type: 'adapter.command', tileKey: t.key, command: 'enterPlay' }, { cls: 'pill-btn accent' }) : null,
    c.pause ? button(ui, [icon(c.pauseText === 'Resume' ? 'play' : 'pause', 16), c.pauseText], { type: 'adapter.command', tileKey: t.key, command: 'togglePause' }, { cls: 'pill-btn' }) : null,
    c.stop ? button(ui, [icon('stop', 16), 'Stop'], { type: 'adapter.command', tileKey: t.key, command: 'exitPlay' }, { cls: 'pill-btn' }) : null,
    c.launch && pin ? button(ui, [icon('power', 16), 'Launch'], { type: 'tile.launch', pinId: pin }, { cls: 'pill-btn accent' }) : null,
    c.ack ? button(ui, [icon('check', 16), 'Clear'], { type: 'tile.ack', tileKey: t.key }, { cls: 'pill-btn' }) : null,
  ].filter((b): b is HTMLElement => b !== null);
  const quiet = [
    c.peek ? nav('Peek', 'eye', () => ui.go(`peek=${encodeURIComponent(t.key)}`)) : null,
    c.pin ? iconButton(ui, 'pin', 'Pin', { type: 'tile.pin', tileKey: t.key }) : null,
    pin ? iconButton(ui, t.notify ? 'bell' : 'bellOff', t.notify ? 'Sounds on. Tap to mute' : 'Sounds off. Tap to turn on',
      { type: 'tile.notify', pinId: pin, on: !t.notify }, t.notify ? 'icon-btn on' : 'icon-btn') : null,
    pin ? h('button', {
      class: 'icon-btn', type: 'button', 'aria-label': `Rename ${t.label}`,
      onclick: (e: Event) => { const name = prompt('Name this tile', t.label); if (name !== null) void ui.run(e.currentTarget as HTMLElement, { type: 'tile.rename', pinId: pin, label: name.trim() || null }); },
    }, icon('pencil')) : null,
    c.unpin && pin ? iconButton(ui, 'close', 'Unpin', { type: 'tile.unpin', pinId: pin }) : null,
  ].filter((b): b is HTMLElement => b !== null);

  return h('article', { class: `card ${t.class}` },
    h('div', { class: 'card-head' },
      h('span', { class: `glyph ${t.class}` }, icon(glyph(t.adapter))),
      h('div', { class: 'grow' },
        h('strong', { class: 'ellipsis' }, t.label),
        t.detail ? h('div', { class: 'muted ellipsis' }, t.detail) : null),
      h('span', { class: `state ${t.class}` }, h('i', {}), t.state)),
    t.progress !== undefined ? progressBar(t.progress) : null,
    t.items?.length ? h('ul', { class: 'items' }, ...t.items.map(i => item(t, i, ui))) : null,
    main.length || quiet.length ? h('div', { class: 'card-foot' }, h('div', { class: 'row-wrap' }, ...main), h('div', { class: 'quiet' }, ...quiet)) : null);
}

/** The CSP blocks style attributes; setting the property from script is allowed. */
function progressBar(value: number): HTMLElement {
  const fill = h('span', {});
  fill.style.width = `${Math.round(Math.min(1, Math.max(0, value)) * 100)}%`;
  return h('div', { class: 'bar', role: 'progressbar', 'aria-valuenow': Math.round(value * 100), 'aria-valuemin': 0, 'aria-valuemax': 100 }, fill);
}

function item(t: TileView, i: Item, ui: Ui): HTMLElement {
  const cmd = (command: string, name: IconName, label: string) => iconButton(ui, name, `${label} ${i.file}`, { type: 'download.command', tileKey: t.key, downloadId: i.id, command }, 'icon-btn small');
  return h('li', {},
    h('div', { class: 'grow' },
      h('div', { class: 'ellipsis' }, i.file),
      h('div', { class: 'muted' }, i.progress !== undefined ? `${Math.round(i.progress * 100)}%` : i.state.replace('_', ' '))),
    i.state === 'in_progress' ? cmd('pause', 'pause', 'Pause') : null,
    i.state === 'paused' ? cmd('resume', 'play', 'Resume') : null,
    i.state === 'in_progress' || i.state === 'paused' ? cmd('cancel', 'close', 'Cancel') : null);
}

function media(m: MediaView, ui: Ui): HTMLElement {
  const query = h('input', { type: 'search', maxlength: 100, placeholder: 'Search music on the PC', 'aria-label': 'Search music', id: 'media-query' }) as HTMLInputElement;
  const search = (target: string) => (e: Event) => {
    if (query.value.trim()) void ui.run(e.currentTarget as HTMLElement, { type: 'media.search', target, query: query.value.trim() }).then(ok => { if (ok) query.value = ''; });
  };
  const ctl = (name: IconName, aria: string, type: string, enabled: boolean, cls = 'icon-btn') => {
    const b = iconButton(ui, name, aria, { type }, cls);
    if (!enabled) b.setAttribute('disabled', '');
    return b;
  };
  return h('footer', { class: 'media' },
    h('div', { class: 'row' },
      h('div', { class: 'grow ellipsis' }, h('strong', { class: 'ellipsis' }, m.title), h('div', { class: 'muted ellipsis' }, m.artist || m.appName)),
      ctl('prev', 'Previous', 'media.prev', m.canPrev),
      ctl(m.playing ? 'pause' : 'play', m.playing ? 'Pause' : 'Play', 'media.toggle', m.canToggle, 'icon-btn play'),
      ctl('next', 'Next', 'media.next', m.canNext)),
    m.apps.length > 1 ? h('div', { class: 'row-wrap' },
      ...m.apps.map(a => button(ui, a.name, { type: 'media.select', appId: a.appId }, { cls: a.selected ? 'chip-btn on' : 'chip-btn' }))) : null,
    h('div', { class: 'row' },
      h('div', { class: 'search grow' }, icon('search', 16), query,
        h('button', { class: 'chip-btn', type: 'button', onclick: search('spotify') }, 'Spotify'),
        h('button', { class: 'chip-btn', type: 'button', onclick: search('youtube') }, 'YouTube')),
      iconButton(ui, 'pin', m.pinned ? 'Unpin player' : 'Pin player', { type: 'media.pin', on: !m.pinned }, m.pinned ? 'icon-btn on' : 'icon-btn')));
}

/** One row of a settings list: icon, title and subtitle, then a trailing control. */
function row(name: IconName, title: string, sub: Child, trailing: Child, opts: { cls?: string; onclick?: () => void } = {}): HTMLElement {
  return h(opts.onclick ? 'button' : 'div', { class: `list-row ${opts.cls ?? ''}`, type: opts.onclick ? 'button' : null, onclick: opts.onclick },
    h('span', { class: 'row-icon' }, icon(name)),
    h('div', { class: 'grow' }, h('div', { class: 'row-title' }, title), sub ? h('div', { class: 'muted' }, sub) : null),
    trailing);
}

function group(title: string, rows: Child[]): HTMLElement {
  return h('section', {}, h('h2', {}, title), h('div', { class: 'list' }, ...rows));
}

function menu(s: Snapshot, live: LiveState, ui: Ui): HTMLElement {
  const sleep = (e: Event) => {
    if (confirm('Put the PC to sleep?\n\nYour phone can’t wake the PC again. Someone has to press a key or the power button.'))
      void ui.run(e.currentTarget as HTMLElement, { type: 'power.sleep', confirm: true });
  };
  return h('main', { class: 'settings' },
    group('This PC', [
      row('lock', 'Lock PC', 'Locks Windows at once', button(ui, 'Lock', { type: 'power.lock' }, { cls: 'chip-btn' })),
      row('moon', 'Sleep PC', 'Your phone can’t wake it again', h('button', { class: 'chip-btn danger-text', type: 'button', onclick: sleep }, 'Sleep')),
    ]),
    group('Presets', [
      ...s.presets.names.map(n => row('presets', n, n === s.presets.active ? 'Active' : '', h('div', { class: 'row-wrap' },
        button(ui, 'Apply', { type: 'preset.apply', name: n, merge: false }, { cls: 'chip-btn', label: `Apply ${n}` }),
        button(ui, 'Merge', { type: 'preset.apply', name: n, merge: true }, { cls: 'chip-btn', label: `Merge ${n}` })))),
      row('power', 'Launch missing apps', 'Starts every pinned app that isn’t running', button(ui, 'Launch', { type: 'preset.launchMissing' }, { cls: 'chip-btn' })),
    ]),
    group('Alerts', [
      ui.alerts === 'on'
        ? row('bell', 'Alerts are on', 'Approvals and failures reach this phone while you’re away from the PC', h('span', { class: 'chip ok' }, 'On'))
        : ui.alerts === 'unsupported'
          ? row('bellOff', 'Alerts unavailable', 'This browser can’t show TaskDock alerts', null)
          : row('bellOff', 'Alerts are off', ui.alerts === 'blocked' ? 'Turn on notifications for Chrome in Android settings first' : 'Get approvals and failures on this phone',
            h('button', { class: 'chip-btn accent', type: 'button', onclick: ui.enableAlerts }, 'Turn on')),
    ]),
    group('This phone', [
      row('phone', live.deviceName ?? 'This phone', 'Paired with this PC', null),
      row('activity', 'Activity', 'Everything a phone did on this PC', icon('chevronDown', 16), { cls: 'nav', onclick: () => ui.go('activity') }),
      row('close', 'Unpair', 'Remove this phone; you can pair it again from the PC', null,
        { cls: 'nav danger-text', onclick: () => { if (confirm('Unpair this phone? You can pair it again from the PC.')) ui.unpair(); } }),
    ]));
}

function activity(ui: Ui): HTMLElement {
  const list = ui.activity;
  return h('main', { class: 'settings' },
    !list ? empty('refresh', 'Loading…', '')
      : !list.length ? empty('activity', 'Nothing yet', 'Actions from your phones show up here.')
        : h('div', { class: 'list' }, ...list.map(e => h('div', { class: 'list-row activity-row' },
          h('span', { class: `row-icon ${e.ok ? '' : 'bad'}` }, icon(e.ok ? 'check' : 'close')),
          h('div', { class: 'grow' },
            h('div', { class: 'row' }, h('span', { class: 'row-title' }, e.type), h('span', { class: 'muted right' }, new Date(e.ts).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' }))),
            h('div', { class: 'muted ellipsis' }, `${e.target} · ${e.deviceName}${e.ok ? '' : ` · ${e.code ?? 'failed'}`}`),
            e.preview ? h('code', { class: 'preview' }, e.preview) : null)))),
    list?.length ? h('button', { class: 'ghost wide', type: 'button', onclick: ui.loadOlderActivity }, 'Older') : null);
}

function peekView(s: Snapshot, key: string, ui: Ui): HTMLElement {
  const t = [...s.tiles, ...s.detected].find(x => x.key === key);
  const p = ui.peekState;
  return h('main', { class: 'detail' },
    h('div', { class: 'card-head' },
      h('span', { class: `glyph ${t?.class ?? 'idle'}` }, icon(glyph(t?.adapter ?? ''))),
      h('div', { class: 'grow' }, h('strong', { class: 'big ellipsis' }, t?.label ?? 'Window'), h('div', { class: 'muted' }, t?.state ?? '')),
      h('button', { class: 'pill-btn', type: 'button', onclick: ui.refreshPeek, disabled: p?.loading ?? false }, icon('refresh', 16), p?.loading ? 'Capturing…' : 'Refresh')),
    p?.error ? empty('eye', p.error, '') : null,
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
