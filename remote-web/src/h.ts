// The only way the app builds DOM. Text goes in as text nodes and attributes as attributes, never as HTML:
// tile labels, window titles and file names come from adapters and are attacker-influenced.

type Attr = string | number | boolean | null | undefined | ((e: Event) => void);
export type Child = Node | string | number | null | undefined | false;

export function h(tag: string, attrs: Record<string, Attr> = {}, ...children: Child[]): HTMLElement {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === null || v === undefined || v === false) continue;
    if (typeof v === 'function') {
      if (!k.startsWith('on')) throw new Error(`function for ${k}`);
      el.addEventListener(k.slice(2), v);
    } else if (k.startsWith('on')) {
      throw new Error(`refusing inline handler ${k}`); // CSP blocks them anyway
    } else if (k === 'class') {
      el.className = String(v);
    } else {
      el.setAttribute(k, v === true ? '' : String(v));
    }
  }
  append(el, children);
  return el;
}

export function append(el: Node, children: Child[]): void {
  for (const c of children) {
    if (c === null || c === undefined || c === false) continue;
    el.appendChild(typeof c === 'string' || typeof c === 'number' ? document.createTextNode(String(c)) : c);
  }
}

export function replace(el: Element, ...children: Child[]): void {
  el.replaceChildren();
  append(el, children);
}
