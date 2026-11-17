import type { Snapshot } from './delta.ts';

/**
 * What became urgent between two states: new approvals and tiles that just failed. The push policy sends no push while the
 * app is open, so this is the in-app banner instead. A first snapshot (prev null) announces nothing.
 */
export function newAlerts(prev: Snapshot | null | undefined, next: Snapshot): string[] {
  if (!prev) return [];
  const had = new Set(prev.approvals.map(a => a.id));
  const failed = new Set([...prev.tiles, ...prev.detected].filter(t => t.class === 'error').map(t => t.key));
  return [
    ...next.approvals.filter(a => !had.has(a.id)).map(a => `Claude needs approval: ${a.tool} · ${a.project}`),
    ...[...next.tiles, ...next.detected].filter(t => t.class === 'error' && !failed.has(t.key)).map(t => `${t.label}: ${t.state}`),
  ];
}
