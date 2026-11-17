// Line diff for Edit and MultiEdit approvals: old_string against new_string. LCS, fine for the sizes Claude edits.

export type DiffLine = { op: ' ' | '-' | '+'; text: string };

export function diffLines(before: string, after: string): DiffLine[] {
  const a = before.split('\n'), b = after.split('\n');
  // shortcut: O(n·m) table; edits over ~2,000 lines each fall back to "all removed, all added".
  if (a.length * b.length > 4_000_000) return [...a.map(text => ({ op: '-' as const, text })), ...b.map(text => ({ op: '+' as const, text }))];
  const lcs = Array.from({ length: a.length + 1 }, () => new Uint32Array(b.length + 1));
  for (let i = a.length - 1; i >= 0; i--)
    for (let j = b.length - 1; j >= 0; j--)
      lcs[i]![j] = a[i] === b[j] ? lcs[i + 1]![j + 1]! + 1 : Math.max(lcs[i + 1]![j]!, lcs[i]![j + 1]!);
  const out: DiffLine[] = [];
  let i = 0, j = 0;
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) { out.push({ op: ' ', text: a[i]! }); i++; j++; }
    else if (lcs[i + 1]![j]! >= lcs[i]![j + 1]!) out.push({ op: '-', text: a[i++]! });
    else out.push({ op: '+', text: b[j++]! });
  }
  while (i < a.length) out.push({ op: '-', text: a[i++]! });
  while (j < b.length) out.push({ op: '+', text: b[j++]! });
  return out;
}

/** The edits an Edit or MultiEdit approval would make, or null for any other tool. */
export function editsOf(tool: string, input: string): { before: string; after: string }[] | null {
  if (tool !== 'Edit' && tool !== 'MultiEdit') return null;
  try {
    const o = JSON.parse(input) as { old_string?: unknown; new_string?: unknown; edits?: { old_string?: unknown; new_string?: unknown }[] };
    const list = tool === 'Edit' ? [o] : o.edits ?? [];
    return list.map(e => ({ before: String(e.old_string ?? ''), after: String(e.new_string ?? '') }));
  } catch { return null; }
}
