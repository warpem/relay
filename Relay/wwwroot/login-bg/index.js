// Picks one of the cryo-EM-themed login backgrounds at random and runs it behind the login card.

import { mount } from './stage.js';

export const BACKGROUNDS = [
    '11-grid-atlas',
    '12-thon-rings',
    '13-darkroom',
    '14-snow-globe',
    '17-capsid-lanterns',
    '18-folding-yarn',
    '19-ice-frost',
    '20-molecular-workshop',
];

const LAST_KEY = 'relay.loginBackground.last';
const PIN_KEY = 'relay.loginBackground.pin';
const P5_VERSION = '2.4.0';

// p5.js ships with Relay (p5/, unmodified, LGPL-2.1; see p5/license.txt) so the login page works without
// internet access.
let p5Loading = null;

function loadP5() {
    if (window.p5) return Promise.resolve();
    if (p5Loading) return p5Loading;
    // p5 2.x fetches friendly-error translations from a CDN on every load unless a cached copy is in
    // localStorage. Seed an empty one so the login page makes no third-party requests.
    try {
        const key = `${navigator.language}-${P5_VERSION}`;
        if (!localStorage.getItem(key)) localStorage.setItem(key, '{}');
    } catch { /* storage unavailable: p5 falls back to built-in English */ }
    p5Loading = new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = new URL('./p5/p5.min.js', import.meta.url).href;
        s.onload = () => resolve();
        s.onerror = () => { s.remove(); p5Loading = null; reject(new Error('Could not load p5.js')); };
        document.head.appendChild(s);
    });
    return p5Loading;
}

/** Chooses a background: the one pinned in localStorage (relay.loginBackground.pin = '<id>'), if any,
 *  otherwise a random one that differs from the last shown. */
function pick() {
    let last = null, pinned = null;
    try { pinned = localStorage.getItem(PIN_KEY); last = localStorage.getItem(LAST_KEY); } catch { }
    if (pinned && BACKGROUNDS.includes(pinned)) return pinned;
    const pool = BACKGROUNDS.filter(id => id !== last);
    const id = pool[Math.floor(Math.random() * pool.length)];
    try { localStorage.setItem(LAST_KEY, id); } catch { }
    return id;
}

/** Starts a background in `container`. Resolves to a handle with dispose(), or null if it couldn't start. */
export async function start(container, focusEl) {
    const id = pick();
    try {
        const [module] = await Promise.all([import(`./${id}/sketch.js`), loadP5()]);
        if (!container.isConnected) return null;
        const stage = await mount({ container, module, focusEl });
        container.dataset.background = id;
        container.classList.add('ready');
        return stage;
    } catch (e) {
        console.warn(`Login background ${id} failed to start:`, e);
        return null;
    }
}
