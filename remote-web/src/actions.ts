import { fromB64, toB64 } from './b64.ts';
import type { ApprovalView } from './delta.ts';
import type { Live, Result } from './live.ts';

export type Action = { type: string; [arg: string]: unknown };

const messages: Record<string, string> = {
  stale: 'That changed on the PC. Showing the latest.',
  not_found: 'That’s gone on the PC.',
  already_resolved: 'Already handled on the PC.',
  stepup_required: 'This needs your fingerprint.',
  stepup_failed: 'Fingerprint check failed. Try again.',
  forbidden: 'Not allowed.',
  rate_limited: 'Slow down a little.',
  invalid: 'TaskDock didn’t understand that.',
  timeout: 'No answer from the PC.',
  offline: 'Not connected to your PC.',
};

export const describe = (r: Result): string => r.code === 'failed' ? r.error ?? 'It didn’t work on the PC.' : messages[r.code ?? ''] ?? r.error ?? 'Something went wrong.';

/**
 * Runs one action. No optimistic changes: the next delta shows what really happened. A short buzz on success,
 * three on failure. Allow on a risky tool and Always allow ask for the fingerprint first, bound to this exact request.
 */
const inFlight = new Map<string, Promise<Result>>();

/** The same tap twice within 1.5 s shares one request: a redraw between taps would otherwise send it twice. */
export function act(live: Live, action: Action, approval?: ApprovalView): Promise<Result> {
  const key = JSON.stringify(action);
  const same = inFlight.get(key);
  if (same) return same;
  const p = send(live, action, approval);
  inFlight.set(key, p);
  void p.then(() => setTimeout(() => inFlight.delete(key), 1500));
  return p;
}

async function send(live: Live, action: Action, approval?: ApprovalView): Promise<Result> {
  const id = crypto.randomUUID();
  const choice = action.type === 'approval.alwaysAllow' ? 'alwaysAllow' : action.type === 'approval.allow' ? 'allow' : null;
  let r: Result;
  try {
    if (choice && (choice === 'alwaysAllow' || approval?.risk !== 'read'))
      action = { ...action, assertion: await fingerprint(live.token, { purpose: 'approval', approvalId: action.approvalId, choice }) };
    r = await live.request(id, action);
  } catch (e) {
    r = { id, ok: false, code: e instanceof DOMException && e.name === 'NotAllowedError' ? 'stepup_failed' : 'failed', error: e instanceof Error ? e.message : String(e) };
  }
  navigator.vibrate?.(r.ok ? 15 : [40, 60, 40, 60, 40]);
  return r;
}

interface AssertionOptions { challenge: string; rpId?: string; timeout?: number; userVerification?: UserVerificationRequirement; allowCredentials?: { type: 'public-key'; id: string }[] }

async function fingerprint(token: string, purpose: object): Promise<unknown> {
  const res = await fetch('/api/stepup/options', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify(purpose),
  });
  if (res.status === 409) throw Object.assign(new Error(messages.already_resolved), { code: 'already_resolved' });
  if (!res.ok) throw new Error(res.status === 429 ? messages.rate_limited! : 'Couldn’t start the fingerprint check.');
  const o = await res.json() as AssertionOptions;
  const cred = await navigator.credentials.get({
    publicKey: { ...o, challenge: fromB64(o.challenge), allowCredentials: (o.allowCredentials ?? []).map(c => ({ ...c, id: fromB64(c.id) })) },
  }) as PublicKeyCredential | null;
  if (!cred) throw new DOMException('cancelled', 'NotAllowedError');
  const a = cred.response as AuthenticatorAssertionResponse;
  return {
    id: cred.id, rawId: toB64(cred.rawId), type: cred.type, clientExtensionResults: cred.getClientExtensionResults(),
    response: {
      authenticatorData: toB64(a.authenticatorData), clientDataJSON: toB64(a.clientDataJSON), signature: toB64(a.signature),
      userHandle: a.userHandle ? toB64(a.userHandle) : null,
    },
  };
}

export type Peek = { url?: string; error?: string; loading?: boolean };

const peekErrors: Record<string, string> = {
  minimized: 'That window is minimized on the PC.',
  blank: 'Nothing to show. The PC may be locked or asleep.',
  not_found: 'That window isn’t there any more.',
  stepup_failed: 'Fingerprint check failed. Try again.',
};

/** A JPEG of the tile's window as a blob URL. Asks for the fingerprint only when the 5-minute grant has run out. */
export async function peek(token: string, tileKey: string): Promise<Peek> {
  const auth = { Authorization: `Bearer ${token}` };
  const get = () => fetch(`/api/peek/${encodeURIComponent(tileKey)}`, { headers: auth });
  try {
    let res = await get();
    if (res.status === 403) {
      const assertion = await fingerprint(token, { purpose: 'peek' });
      const grant = await fetch('/api/peek/grant', { method: 'POST', headers: { ...auth, 'Content-Type': 'application/json' }, body: JSON.stringify({ assertion }) });
      if (!grant.ok) return { error: peekErrors.stepup_failed };
      res = await get();
    }
    if (res.ok) return { url: URL.createObjectURL(await res.blob()) };
    const code = (await res.json().catch(() => ({})) as { code?: string }).code ?? '';
    return { error: peekErrors[code] ?? (res.status === 429 ? 'One moment…' : 'Couldn’t capture that window.') };
  } catch (e) {
    return { error: e instanceof DOMException && e.name === 'NotAllowedError' ? peekErrors.stepup_failed : 'Couldn’t reach your PC.' };
  }
}
