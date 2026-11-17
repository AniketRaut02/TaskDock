// WebAuthn options and responses travel as JSON with base64url byte strings; the browser API wants ArrayBuffers.
export const fromB64 = (s: string): ArrayBuffer =>
  Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0)).buffer;

export const toB64 = (b: ArrayBuffer): string =>
  btoa(String.fromCharCode(...new Uint8Array(b))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
