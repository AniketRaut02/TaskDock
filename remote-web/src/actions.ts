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
export async function act(live: Live, action: Action, approval?: ApprovalView): Promise<Result> {
  const id = crypto.randomUUID();
  const choice = action.type === 'approval.alwaysAllow' ? 'alwaysAllow' : action.type === 'approval.allow' ? 'allow' : null;
  let r: Result;
  try {
    if (choice && (choice === 'alwaysAllow' || approval?.risk !== 'read'))
      action = { ...action, assertion: await fingerprint(live.token, action.approvalId as string, choice) };
    r = await live.request(id, action);
  } catch (e) {
    r = { id, ok: false, code: e instanceof DOMException && e.name === 'NotAllowedError' ? 'stepup_failed' : 'failed', error: e instanceof Error ? e.message : String(e) };
  }
  navigator.vibrate?.(r.ok ? 15 : [40, 60, 40, 60, 40]);
  return r;
}

interface AssertionOptions { challenge: string; rpId?: string; timeout?: number; userVerification?: UserVerificationRequirement; allowCredentials?: { type: 'public-key'; id: string }[] }

async function fingerprint(token: string, approvalId: string, choice: string): Promise<unknown> {
  const res = await fetch('/api/stepup/options', {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify({ purpose: 'approval', approvalId, choice }),
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
