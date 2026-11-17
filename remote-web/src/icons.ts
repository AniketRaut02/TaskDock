// TaskDock's icon set: minimal 24×24 line icons (2 px stroke, round caps and joins, no fill).
// The widget draws the same path data from src/TaskDock.App/Icons.xaml; test/icons.test.ts keeps the two identical.

export const ICONS = {
  play: 'M7 4.5v15l12-7.5z',
  pause: 'M8 5v14M16 5v14',
  stop: 'M6.5 6.5h11v11h-11z',
  prev: 'M18 19l-9-7 9-7zM6 5v14',
  next: 'M6 5l9 7-9 7zM18 5v14',
  bell: 'M6 8.5a6 6 0 0 1 12 0c0 6.5 2.5 8.5 2.5 8.5h-17S6 15 6 8.5M10 20.5a2.2 2.2 0 0 0 4 0',
  bellOff: 'M8.6 3.5A6 6 0 0 1 18 8.5c0 2.2.3 3.9.7 5.2M16.5 17H3.5S6 15 6 8.5c0-.6.1-1.1.2-1.6M10 20.5a2.2 2.2 0 0 0 4 0M3 3l18 18',
  pin: 'M12 16.5V21M8.5 3.5h7M9.5 3.5v6.2L6.5 13v3.5h11V13l-3-3.3V3.5',
  close: 'M18 6L6 18M6 6l12 12',
  check: 'M20 6.5L9.5 17 4 11.5',
  focus: 'M7 17L17 7M8 7h9v9',
  target: 'M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18M12 1v5M12 18v5M1 12h5M18 12h5',
  layout: 'M4 4.5h16v6H4zM4 13.5h16v6H4z',
  chevronDown: 'M6 9l6 6 6-6',
  chevronUp: 'M18 15l-6-6-6 6',
  back: 'M19 12H5M11 18l-6-6 6-6',
  more: 'M5 12h.01M12 12h.01M19 12h.01',
  download: 'M12 3.5v11M7 10l5 5 5-5M4.5 20.5h15',
  terminal: 'M4.5 16.5l5-5-5-5M12 18.5h7.5',
  window: 'M3.5 4.5h17v15h-17zM3.5 9h17',
  folder: 'M3.5 6.5a2 2 0 0 1 2-2h4l2 2.5h7a2 2 0 0 1 2 2v8.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z',
  open: 'M14 4h6v6M10.5 13.5L20 4M18 13.5v4.5a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4.5',
  lock: 'M5.5 11h13v9.5h-13zM8.5 11V7.5a3.5 3.5 0 0 1 7 0V11',
  moon: 'M20.5 14A8.5 8.5 0 1 1 10 3.5a6.5 6.5 0 0 0 10.5 10.5z',
  eye: 'M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12zM12 9.5a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5',
  pencil: 'M16.5 4.5a2.1 2.1 0 0 1 3 3L8 19l-4 1 1-4z',
  power: 'M12 3v8M17.7 6.8a8 8 0 1 1-11.4 0',
  search: 'M10.5 4a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13M20 20l-4.8-4.8',
  refresh: 'M20 12a8 8 0 1 1-2.3-5.6L20 8.5M20 3.5v5h-5',
  bolt: 'M13 3L5 13.5h6.5L10.5 21 19 10.5h-6.5z',
  activity: 'M3 12h4l3-7 4 14 3-7h4',
  phone: 'M7.5 2.5h9a1.5 1.5 0 0 1 1.5 1.5v16a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 6 20V4a1.5 1.5 0 0 1 1.5-1.5zM11 18.5h2',
  settings: 'M4 7h9M17 7h3M4 17h3M11 17h9M15 5v4M9 15v4',
  presets: 'M4 6h16M4 12h16M4 18h10',
  quit: 'M10 4.5H5.5v15H10M14 8l4 4-4 4M18 12H9',
  volume: 'M4 9.5h3.5L12 5.5v13l-4.5-4H4zM15.5 9a4 4 0 0 1 0 6M18 6.5a7.5 7.5 0 0 1 0 11',
  mute: 'M4 9.5h3.5L12 5.5v13l-4.5-4H4zM16 9.5l5 5M21 9.5l-5 5',
} as const;

export type IconName = keyof typeof ICONS;

const NS = 'http://www.w3.org/2000/svg';

/** An inline SVG icon (built with DOM calls, never markup). Decorative: the button around it carries the label. */
export function icon(name: IconName, size = 18): SVGSVGElement {
  const svg = document.createElementNS(NS, 'svg');
  for (const [k, v] of Object.entries({
    viewBox: '0 0 24 24', width: size, height: size, fill: 'none', stroke: 'currentColor', 'stroke-width': 2,
    'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true', class: 'icon',
  })) svg.setAttribute(k, String(v));
  const path = document.createElementNS(NS, 'path');
  path.setAttribute('d', ICONS[name]);
  svg.appendChild(path);
  return svg;
}
