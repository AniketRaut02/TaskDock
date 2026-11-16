import { applyDelta, type Delta, type Snapshot } from './delta.ts';

export type Status = 'connecting' | 'live' | 'offline' | 'unpaired';

export interface LiveState {
  status: Status;
  snapshot?: Snapshot;
  /** When the last frame arrived: shown as "Last seen 14:02" while not live. */
  lastSeen?: number;
  deviceName?: string;
}

interface Frame { t: string; id?: string; body?: unknown }

/** The socket to the PC. Old state is never shown as live: status says whether it is current. */
export class Live {
  state: LiveState = { status: 'connecting' };
  #ws?: WebSocket;
  #backoff = 1000;
  #timer?: ReturnType<typeof setTimeout>;
  #stopped = false;
  readonly token: string;
  readonly onChange: (s: LiveState) => void;

  constructor(token: string, onChange: (s: LiveState) => void) {
    this.token = token;
    this.onChange = onChange;
    document.addEventListener('visibilitychange', () => {
      // Closed while hidden, so "app open" on the PC means someone is looking (push decisions use it in Phase 4).
      if (document.hidden) this.#close();
      else { this.#backoff = 1000; this.connect(); }
    });
  }

  connect(): void {
    if (this.#stopped || document.hidden || this.#ws) return;
    clearTimeout(this.#timer);
    this.#set({ status: 'connecting' });
    const ws = new WebSocket(`wss://${location.host}/api/live`);
    this.#ws = ws;
    ws.onopen = () => ws.send(JSON.stringify({ t: 'auth', body: { token: this.token } }));
    ws.onmessage = e => this.#frame(JSON.parse(String(e.data)) as Frame);
    ws.onclose = e => {
      if (this.#ws !== ws) return;
      this.#ws = undefined;
      if (e.code === 4401) { this.#stopped = true; this.#set({ status: 'unpaired' }); return; } // revoked on the PC
      this.#set({ status: 'offline' });
      if (!document.hidden) this.#timer = setTimeout(() => this.connect(), this.#backoff);
      this.#backoff = Math.min(this.#backoff * 2, 30_000);
    };
  }

  #frame(f: Frame): void {
    const now = Date.now();
    switch (f.t) {
      case 'hello': {
        const b = f.body as { device: { name: string } };
        this.#set({ deviceName: b.device.name, lastSeen: now });
        break;
      }
      case 'snapshot':
        this.#backoff = 1000;
        this.#set({ status: 'live', snapshot: f.body as Snapshot, lastSeen: now });
        break;
      case 'delta': {
        const next = this.state.snapshot && applyDelta(this.state.snapshot, f.body as Delta);
        if (!next) { this.#send({ t: 'resync', body: {} }); return; } // missed a frame or TaskDock restarted
        this.#set({ snapshot: next, lastSeen: now });
        break;
      }
    }
  }

  #send(f: Frame): void { this.#ws?.send(JSON.stringify(f)); }

  #close(): void {
    clearTimeout(this.#timer);
    const ws = this.#ws;
    this.#ws = undefined;
    ws?.close();
    if (this.state.status === 'live' || this.state.status === 'connecting') this.#set({ status: 'offline' });
  }

  #set(patch: Partial<LiveState>): void {
    this.state = { ...this.state, ...patch };
    this.onChange(this.state);
  }
}
