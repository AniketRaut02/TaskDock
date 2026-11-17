import { fromB64, toB64 } from './b64.ts';

export type Alerts = 'on' | 'off' | 'blocked' | 'unsupported';

export const alertsState = (): Alerts =>
  !('PushManager' in window) || !('Notification' in window) ? 'unsupported'
    : Notification.permission === 'granted' ? 'on'
      : Notification.permission === 'denied' ? 'blocked' : 'off';

/** Asks once (from a tap), then subscribes. Returns what to tell the user. */
export async function enableAlerts(token: string): Promise<string> {
  if (alertsState() === 'unsupported') return 'This browser can’t show TaskDock alerts.';
  // Chrome answers "denied" without asking when Android has notifications off for Chrome (spike S4).
  if (await Notification.requestPermission() !== 'granted')
    return 'Notifications are blocked. Turn them on for Chrome in Android settings (Apps → Chrome → Notifications), then try again.';
  await subscribe(token);
  return 'Alerts are on. You’ll get approvals and failures when you’re away from the PC.';
}

/** Registers this phone's push subscription with the PC. Safe to repeat; fixes things if TaskDock's key changed. */
export async function subscribe(token: string): Promise<void> {
  const auth = { Authorization: `Bearer ${token}` };
  const reg = await navigator.serviceWorker.ready;
  const { key } = await (await fetch('/api/push/vapid', { headers: auth })).json() as { key: string };
  let sub = await reg.pushManager.getSubscription();
  const had = sub?.options.applicationServerKey;
  if (sub && (!had || toB64(had) !== key)) { await sub.unsubscribe(); sub = null; }
  sub ??= await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: fromB64(key) }); // takes a few seconds (S4)
  const res = await fetch('/api/push/subscribe', { method: 'POST', headers: { ...auth, 'Content-Type': 'application/json' }, body: JSON.stringify(sub) });
  if (!res.ok) throw new Error('Your PC didn’t accept this phone’s alerts.');
}

/** Closes approval notifications that were answered elsewhere, so the shade never offers a dead Deny. */
export async function closeHandled(pendingIds: Set<string>): Promise<void> {
  const reg = await navigator.serviceWorker.getRegistration();
  for (const n of await reg?.getNotifications() ?? []) {
    const id = (n.data as { approvalId?: string } | null)?.approvalId;
    if (id && !pendingIds.has(id)) n.close();
  }
}
