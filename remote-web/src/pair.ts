import { h, replace } from './h.ts';
import { savePairing } from './idb.ts';

import { fromB64, toB64 } from './b64.ts';

interface CreationOptions {
  rp: { id?: string; name: string }; user: { id: string; name: string; displayName: string }; challenge: string;
  pubKeyCredParams: PublicKeyCredentialParameters[]; timeout?: number; attestation?: AttestationConveyancePreference;
  authenticatorSelection?: AuthenticatorSelectionCriteria; excludeCredentials?: { type: 'public-key'; id: string }[];
}

/** /pair#c=<code>: the code stays in the fragment, so it never reaches a server log. */
export function pairPage(root: HTMLElement): void {
  const code = new URLSearchParams(location.hash.slice(1)).get('c') ?? '';
  const name = h('input', { type: 'text', maxlength: 40, value: 'Android phone', 'aria-label': 'Phone name' }) as HTMLInputElement;
  const status = h('p', { class: 'muted', role: 'status' });
  const go = h('button', { class: 'primary', type: 'submit' }, 'Pair this phone');

  const form = h('form', {
    onsubmit: async (e: Event) => {
      e.preventDefault();
      go.setAttribute('disabled', '');
      try { await pair(code, name.value.trim(), m => replace(status, m)); }
      catch (err) { replace(status, err instanceof Error ? err.message : String(err)); go.removeAttribute('disabled'); }
    },
  },
    h('label', {}, 'Name this phone', name),
    go,
  );

  replace(root, h('main', { class: 'pair' },
    h('h1', {}, 'Pair with TaskDock'),
    code ? null : h('p', { class: 'error' }, 'This link has no pairing code. Scan the QR code in TaskDock → Settings → Remote again.'),
    h('ol', { class: 'steps' },
      h('li', {}, 'Tailscale is installed on this phone and connected.'),
      h('li', {}, 'Notifications are on for Chrome in Android settings (Settings → Apps → Chrome → Notifications).'),
      h('li', {}, 'Your phone has a screen lock with a fingerprint.')),
    form, status));
}

async function pair(code: string, deviceName: string, say: (m: string) => void): Promise<void> {
  if (!deviceName) throw new Error('Give this phone a name.');
  say('Contacting your PC…');
  const start = await fetch('/api/pair/start', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code, deviceName }),
  });
  if (!start.ok) throw new Error(start.status === 403 ? 'That pairing code expired or was already used. Make a new one on the PC.' : `Pairing failed (${start.status}).`);
  const { deviceId, token, creationOptions: o } = await start.json() as { deviceId: string; token: string; creationOptions: CreationOptions };

  say('Confirm with your fingerprint…');
  const cred = await navigator.credentials.create({
    publicKey: {
      ...o,
      challenge: fromB64(o.challenge),
      user: { ...o.user, id: fromB64(o.user.id) },
      excludeCredentials: (o.excludeCredentials ?? []).map(c => ({ ...c, id: fromB64(c.id) })),
    },
  }) as PublicKeyCredential | null;
  if (!cred) throw new Error('No passkey was created.');
  const r = cred.response as AuthenticatorAttestationResponse;

  say('Now allow this phone on your PC.');
  const finish = await fetch('/api/pair/finish', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify({
      id: cred.id, rawId: toB64(cred.rawId), type: cred.type, clientExtensionResults: cred.getClientExtensionResults(),
      response: { attestationObject: toB64(r.attestationObject), clientDataJSON: toB64(r.clientDataJSON) },
    }),
  });
  if (!finish.ok) throw new Error(finish.status === 403 ? 'The PC said no, or nobody answered in time.' : 'Your PC did not accept the passkey.');

  await savePairing({ token, deviceId, deviceName });
  location.replace('/');
}
