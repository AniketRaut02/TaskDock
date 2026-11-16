// Remote protocol v1, phone side. protocol/remote/samples is the source of truth; DeltaTests.cs runs the same rules in C#.

export type StatusClass = 'attention' | 'error' | 'busy' | 'done' | 'idle' | 'offline';

export interface Caps { play: boolean; stop: boolean; pause: boolean; pauseText: string; launch: boolean; peek: boolean; pin: boolean; unpin: boolean; ack: boolean }
export interface Item { id: number; file: string; state: string; progress?: number; speedBps?: number }
export interface TileView {
  key: string; pinId?: string; adapter: string; label: string; state: string; class: StatusClass; detail?: string; progress?: number;
  placeholder: boolean; notify: boolean; caps: Caps; items?: Item[];
}
export interface ApprovalView {
  id: string; tileKey: string; tool: string; project: string; preview: string; input: string; risk: 'read' | 'risky';
  canAlwaysAllow: boolean; heldSince: number; deadline: number;
}
export interface MediaView {
  appId: string; appName: string; title: string; artist: string; playing: boolean; canPrev: boolean; canToggle: boolean; canNext: boolean;
  position?: number; duration?: number; timelineAt?: number; artHash?: string; apps: { appId: string; name: string; selected: boolean }[]; pinned: boolean;
}
export interface Snapshot {
  epoch: string; rev: number; pill: { text: string; worst: StatusClass }; tiles: TileView[]; detected: TileView[];
  approvals: ApprovalView[]; media?: MediaView; presets: { active: string; names: string[] }; flags: { away: boolean; keepAwake: boolean };
}
export interface Delta {
  epoch: string; rev: number; upsert?: TileView[]; remove?: string[]; order?: string[]; detectedOrder?: string[];
  approvals?: ApprovalView[]; media?: MediaView | null; pill?: Snapshot['pill']; presets?: Snapshot['presets']; flags?: Snapshot['flags'];
}

/** The next state, or null when the phone must ask for a full snapshot (new epoch, or a missed frame). */
export function applyDelta(s: Snapshot, d: Delta): Snapshot | null {
  if (d.epoch !== s.epoch || d.rev !== s.rev + 1) return null;

  const byKey = new Map<string, TileView>();
  for (const t of [...s.tiles, ...s.detected]) byKey.set(t.key, t);
  for (const t of d.upsert ?? []) byKey.set(t.key, t);
  for (const k of d.remove ?? []) byKey.delete(k);

  const pick = (keys: string[]) => keys.map(k => byKey.get(k)).filter((t): t is TileView => t !== undefined);
  const next: Snapshot = {
    ...s,
    rev: d.rev,
    tiles: pick(d.order ?? s.tiles.map(t => t.key)),
    detected: pick(d.detectedOrder ?? s.detected.map(t => t.key)),
  };
  if (d.approvals !== undefined) next.approvals = d.approvals;
  if (d.media !== undefined) { if (d.media === null) delete next.media; else next.media = d.media; }
  if (d.pill !== undefined) next.pill = d.pill;
  if (d.presets !== undefined) next.presets = d.presets;
  if (d.flags !== undefined) next.flags = d.flags;
  return next;
}
