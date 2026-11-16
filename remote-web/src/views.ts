import type { ApprovalView, MediaView, Snapshot, TileView } from './delta.ts';
import { h, replace } from './h.ts';
import type { LiveState } from './live.ts';

const time = (ms: number) => new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
const pcName = () => location.hostname.split('.')[0] ?? 'PC';

export function render(root: HTMLElement, s: LiveState, install?: () => void): void {
  const snap = s.snapshot;
  const live = s.status === 'live';
  replace(root,
    header(s, install),
    s.status === 'unpaired'
      ? h('main', {}, h('p', { class: 'error' }, 'This phone was unpaired on the PC. Pair it again from TaskDock → Settings → Remote.'))
      : !snap
        ? h('main', {}, h('p', { class: 'muted' }, s.status === 'offline' ? 'Can’t reach your PC. Is Tailscale connected on this phone?' : 'Connecting…'))
        : h('main', { class: live ? '' : 'stale', 'aria-disabled': live ? null : 'true' },
          needsYou(snap),
          section('Tiles', snap.tiles.map(tile)),
          snap.detected.length ? h('details', {}, h('summary', {}, `${snap.detected.length} detected`), ...snap.detected.map(tile)) : null),
    snap?.media ? media(snap.media) : null);
}

function header(s: LiveState, install?: () => void): HTMLElement {
  const text = s.status === 'live' ? 'Live'
    : s.status === 'connecting' ? 'Reconnecting…'
      : s.status === 'unpaired' ? 'Unpaired'
        : s.lastSeen ? `Last seen ${time(s.lastSeen)}` : 'Offline';
  return h('header', {},
    h('h1', {}, pcName()),
    h('span', { class: `dot ${s.status}`, role: 'status' }, text),
    s.snapshot?.flags.away ? h('span', { class: 'badge' }, 'Away') : null,
    install ? h('button', { class: 'ghost', onclick: install }, 'Install') : null);
}

function needsYou(s: Snapshot): HTMLElement | null {
  const failed = s.tiles.filter(t => t.class === 'error');
  if (!s.approvals.length && !failed.length) return null;
  return section('Needs you', [...s.approvals.map(approval), ...failed.map(tile)]);
}

function section(title: string, items: HTMLElement[]): HTMLElement {
  return h('section', {}, h('h2', {}, title), ...(items.length ? items : [h('p', { class: 'muted' }, 'Nothing here.')]));
}

function approval(a: ApprovalView): HTMLElement {
  const left = Math.max(0, Math.round((a.deadline - Date.now()) / 60000));
  return h('article', { class: 'card attention' },
    h('div', { class: 'row' },
      h('strong', {}, a.tool),
      h('span', { class: `badge ${a.risk}` }, a.risk === 'read' ? 'Read-only' : 'Needs fingerprint'),
      h('span', { class: 'muted right' }, `${left} min left`)),
    h('div', { class: 'muted' }, a.project),
    h('code', { class: 'preview' }, a.preview));
}

function tile(t: TileView): HTMLElement {
  return h('article', { class: `card ${t.class}` },
    h('div', { class: 'row' },
      h('strong', {}, t.label),
      h('span', { class: 'state right' }, t.state)),
    t.detail ? h('div', { class: 'muted ellipsis' }, t.detail) : null,
    t.progress !== undefined ? h('progress', { max: 1, value: t.progress }) : null,
    t.items?.length ? h('ul', { class: 'items' }, ...t.items.map(i =>
      h('li', {}, h('span', { class: 'ellipsis' }, i.file), h('span', { class: 'muted right' },
        i.progress !== undefined ? `${Math.round(i.progress * 100)}%` : i.state.replace('_', ' '))))) : null);
}

function media(m: MediaView): HTMLElement {
  return h('footer', { class: 'media' },
    h('div', { class: 'ellipsis' }, h('strong', {}, m.title), h('div', { class: 'muted' }, m.artist || m.appName)),
    h('span', { class: 'muted right' }, m.playing ? 'Playing' : 'Paused'));
}
