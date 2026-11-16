// The device token lives in IndexedDB, which the service worker can read too (it sends Deny in Phase 4).

const DB = 'taskdock', STORE = 'kv';

function open(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function run<T>(mode: IDBTransactionMode, f: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await open();
  try {
    return await new Promise<T>((resolve, reject) => {
      const req = f(db.transaction(STORE, mode).objectStore(STORE));
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  } finally { db.close(); }
}

export interface Pairing { token: string; deviceId: string; deviceName: string }

export const loadPairing = () => run<Pairing | undefined>('readonly', s => s.get('pairing'));
export const savePairing = (p: Pairing) => run('readwrite', s => s.put(p, 'pairing'));
export const clearPairing = () => run('readwrite', s => s.delete('pairing'));
