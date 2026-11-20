// lib.js: maths, colour, and the little gouache painting kit shared by every machine.
export const TAU = Math.PI * 2;
export const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
export const lerp = (a, b, t) => a + (b - a) * t;
export const sm = t => t * t * (3 - 2 * t);
export const sstep = (a, b, x) => sm(clamp((x - a) / (b - a), 0, 1));
export function hash(a, b = 0) {
    let h = Math.imul((a | 0) ^ 0x9e3779b9, 0x85ebca6b) ^ Math.imul((b | 0) + 0x632be5ab, 0xc2b2ae35);
    h ^= h >>> 16; h = Math.imul(h, 0x7feb352d); h ^= h >>> 15; h = Math.imul(h, 0x846ca68b); h ^= h >>> 16;
    return (h >>> 0) / 4294967296;
}
export function vnoise(x, s = 0) { const i = Math.floor(x), f = x - i, u = f * f * (3 - 2 * f); return lerp(hash(i, s), hash(i + 1, s), u) * 2 - 1; }
export const fbm = (x, s = 0) => vnoise(x, s) * 0.62 + vnoise(x * 2.13 + 5.1, s + 1) * 0.38;
export const hex = s => [parseInt(s.slice(1, 3), 16), parseInt(s.slice(3, 5), 16), parseInt(s.slice(5, 7), 16)];
export const mixc = (a, b, t) => [lerp(a[0], b[0], t), lerp(a[1], b[1], t), lerp(a[2], b[2], t)];
export const css = (c, a = 1) => `rgba(${c[0] | 0},${c[1] | 0},${c[2] | 0},${a})`;
export function mulberry(seed) {
    let a = seed >>> 0;
    return () => { a = (a + 0x6d2b79f5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
export const expo = (r, mean) => -Math.log(1 - r()) * mean;

// ---------- palette: one light value per name, night derived (or overridden) ----------
const LIGHT = {
    bg0: '#fcf3e2', bg1: '#f1dfc3', sun: '#fff6dc',
    crowd: ['#ecd6b8', '#ead0c0', '#e1d6b8', '#eecfb0', '#e3d0c6', '#dcd8c0'],
    ink: '#6b4a3a', cream: '#fffaf0',
    head: '#e1a160', tail: '#f6e0b6', outside: '#ece4cf', glycan: '#b4c493',
    mprot: ['#a9bf92', '#d99f7c', '#c4aac8', '#e2c070'],
    ims: '#f6dfbd', matrix: '#f0cfaa', mcrowd: ['#e6bf97', '#e9c6a6', '#dfc3a0'],
    atpA: '#f2c0a0', atpB: '#dc7a64', cring: '#c46a5d', gamma: '#94605a', stator: '#8f7398', cplx: '#c9b47c', cytc: '#cf5f56',
    tubA: '#9dbfae', tubB: '#b8d3bd', tubGTP: '#e4f0cf', mtCore: '#7c9f8f',
    kin: '#d7838f', kinLC: '#e7b39f', ves: ['#fbe5bd', '#f7d6c3', '#eee2bb', '#f8dcae'],
    rib60: '#c8899f', rib40: '#e1acb6', mrna: '#c4664f', cap: '#946a8f', pabp: '#e6b75f', trna: '#d98b5e',
    chain: '#7a9ec0', fold: ['#86a8c8', '#9fb789', '#d4986b', '#b394c4', '#d7b25d'],
    tric: ['#97b3c9', '#a5b7d6', '#b0c3cd', '#94aac2', '#a8bdd0', '#b6bfda', '#9fbaca', '#acb1cf'], pfd: '#c7a7cf',
    actATP: '#f8dc88', actPi: '#f1c47a', actADP: '#e8b083', arp: '#c97b6c',
    nucleo: '#f0d6c4', lumen: '#f5e2c1', histone: '#efc3ae', dna: '#cf8f80', npc: '#a5b788', fg: '#cbd5a2', lamina: '#cfae8c',
    importin: '#7fa3c3', mrnp: '#d8a35c', glow: '#ffc778', spark: '#ffb85a',
};
const DARK = {
    bg0: '#2b2230', bg1: '#18131b', sun: '#3a2c33',
    crowd: ['#2e2632', '#32282e', '#2b2a33', '#352a2c', '#2e2834', '#2b2b30'],
    ink: '#120c12', cream: '#4a3a40',
    outside: '#1c1820', ims: '#3a2a2c', matrix: '#3a2726', mcrowd: ['#4a3230', '#45302e', '#4b3634'],
    nucleo: '#33242e', lumen: '#3c2d2c', glow: '#ffc778', spark: '#ffbe66',
};
// night: dim, slightly plum-shifted, keeping hue identity so the cast is recognisable by lamplight
const night = c => {
    const lum = c[0] * 0.3 + c[1] * 0.59 + c[2] * 0.11, s = c.map(v => lum + (v - lum) * 1.7);
    return [s[0] * 0.36 + 20, s[1] * 0.31 + 15, s[2] * 0.33 + 27].map(v => Math.max(12, v));
};
function build(src, over) {
    const L = {}, D = {};
    for (const k in src) {
        const v = src[k];
        L[k] = Array.isArray(v) ? v.map(hex) : hex(v);
        const o = over[k];
        D[k] = o ? (Array.isArray(o) ? o.map(hex) : hex(o)) : Array.isArray(v) ? L[k].map(night) : night(L[k]);
    }
    return { L, D };
}
export const PAL = build(LIGHT, DARK);
// the page passes through a warm dusk on its way to night (and back), rather than a grey midpoint
const DUSK = { bg0: '#d9b096', bg1: '#9a7468', sun: '#efc69a', outside: '#a89488', crowd: ['#b49488', '#b28e86', '#a8988a', '#b89182', '#a99089', '#a69a8a'] };
PAL.M = {}; for (const k in DUSK) PAL.M[k] = Array.isArray(DUSK[k]) ? DUSK[k].map(hex) : hex(DUSK[k]);

// G.t is the theme blend used for colours right now (0 light .. 1 dark); caches force 0 or 1.
export function rgb(G, name, i) {
    let a = PAL.L[name], b = PAL.D[name];
    let m = PAL.M[name];
    if (i !== undefined) { a = a[i % a.length]; b = b[i % b.length]; if (m) m = m[i % m.length]; }
    const t = G.t;
    if (t <= 0) return a; if (t >= 1) return b;
    if (!m) return mixc(a, b, t);
    const u = 1 - t; return [u * u * a[0] + 2 * u * t * m[0] + t * t * b[0], u * u * a[1] + 2 * u * t * m[1] + t * t * b[1], u * u * a[2] + 2 * u * t * m[2] + t * t * b[2]];
}
export const col = (G, name, a = 1, i) => css(rgb(G, name, i), a);
export const lnOf = (G, c, k = 0.34) => mixc(c, rgb(G, 'ink'), k);
export const hiOf = (G, c, k = 0.4) => (G.t < 0.5 ? mixc(c, PAL.L.cream, k) : mixc(c, [c[0] * 1.35 + 18, c[1] * 1.3 + 14, c[2] * 1.25 + 12], k));

// ---------- shapes ----------
/** wobbly closed blob through n radial points, smoothed with quadratic midpoints. */
export function blobPath(x, cx, cy, rx, ry, rot = 0, seed = 0, wob = 0.08, n = 11) {
    const cr = Math.cos(rot), sr = Math.sin(rot), P = [];
    for (let i = 0; i < n; i++) {
        const a = (i / n) * TAU, r = 1 + wob * (Math.sin(a * 2 + seed * 3.1) * 0.55 + Math.sin(a * 3 + seed * 1.7) * 0.45);
        const lx = Math.cos(a) * rx * r, ly = Math.sin(a) * ry * r;
        P.push([cx + lx * cr - ly * sr, cy + lx * sr + ly * cr]);
    }
    smoothClosed(x, P);
}
export function smoothClosed(x, P) {
    const n = P.length;
    x.beginPath();
    x.moveTo((P[n - 1][0] + P[0][0]) / 2, (P[n - 1][1] + P[0][1]) / 2);
    for (let i = 0; i < n; i++) { const a = P[i], b = P[(i + 1) % n]; x.quadraticCurveTo(a[0], a[1], (a[0] + b[0]) / 2, (a[1] + b[1]) / 2); }
    x.closePath();
}
export function smoothOpen(x, P, move = true) {
    if (move) { x.beginPath(); x.moveTo(P[0][0], P[0][1]); }
    for (let i = 1; i < P.length - 1; i++) x.quadraticCurveTo(P[i][0], P[i][1], (P[i][0] + P[i + 1][0]) / 2, (P[i][1] + P[i + 1][1]) / 2);
    const l = P[P.length - 1]; x.lineTo(l[0], l[1]);
}
/** gouache fill: flat pigment with a pale wet-edge crescent toward the upper left and a soft darker outline.
 *  path() must (re)build the path. */
export function gouache(G, x, path, c, opt = {}) {
    const a = opt.a ?? 1, lw = opt.lw ?? 1.1, hi = opt.hi ?? 0.5, d = opt.hd ?? 1.7;
    path();
    if (hi > 0) {
        x.fillStyle = css(mixc(c, hiOf(G, c, 0.7), hi), a); x.fill();
        x.save(); x.clip(); x.translate(d * 0.8, d); path(); x.fillStyle = css(c, a); x.fill(); x.restore();
        path();
    } else { x.fillStyle = css(c, a); x.fill(); }
    if (lw > 0) { x.strokeStyle = css(lnOf(G, c, opt.ln ?? 0.34), a); x.lineWidth = lw; x.lineJoin = 'round'; x.stroke(); }
}

// ---------- canvases & sprites ----------
export function canvas(w, h) { const c = document.createElement('canvas'); c.width = Math.max(1, Math.ceil(w)); c.height = Math.max(1, Math.ceil(h)); return c; }
/** pigment mottling pattern (rgba speckle) applied with source-atop to give flat shapes a painted body. */
export function makeTexture(r) {
    const N = 192, c = canvas(N, N), x = c.getContext('2d'), img = x.createImageData(N, N);
    const lat = Array.from({ length: 144 }, () => r());
    for (let i = 0; i < N * N; i++) {
        const px = i % N, py = (i / N) | 0, gx = (px / N) * 12, gy = (py / N) * 12, ix = gx | 0, iy = gy | 0, fx = sm(gx - ix), fy = sm(gy - iy);
        const g = (a, b) => lat[((iy + b) % 12) * 12 + ((ix + a) % 12)];
        const v = lerp(lerp(g(0, 0), g(1, 0), fx), lerp(g(0, 1), g(1, 1), fx), fy) - 0.5 + (r() - 0.5) * 0.5;
        const o = i * 4;
        if (v > 0) { img.data[o] = 255; img.data[o + 1] = 250; img.data[o + 2] = 235; img.data[o + 3] = v * 90; }
        else { img.data[o] = 80; img.data[o + 1] = 45; img.data[o + 2] = 35; img.data[o + 3] = -v * 70; }
    }
    x.putImageData(img, 0, 0);
    return c;
}
export function texturize(G, x, w, h, a = 0.5) {
    x.save(); x.globalCompositeOperation = 'source-atop'; x.globalAlpha = a;
    x.fillStyle = x.createPattern(G.texCanvas, 'repeat'); x.fillRect(-w, -h, w * 2, h * 2); x.restore();
}
/** a theme-pair sprite drawn around its centre; dark copy rendered lazily. */
export function sprite(G, w, h, draw, tex = 0.45) {
    const s = { w, h, L: null, D: null };
    (G.reg ??= []).push(s);
    s.get = th => {
        const k = th ? 'D' : 'L';
        if (!s[k]) {
            const pd = G.spd, c = canvas(w * pd, h * pd), x = c.getContext('2d');
            x.scale(pd, pd); x.translate(w / 2, h / 2);
            const t0 = G.t; G.t = th; draw(x); if (tex) texturize(G, x, w, h, tex * (th ? 0.6 : 1)); G.t = t0;
            s[k] = c;
        }
        return s[k];
    };
    return s;
}
/** draw a sprite centred at (x,y) in the current transform, crossfading light→dark by G.m. */
export function spr(G, s, x, y, a = 1, sc = 1) {
    const ctx = G.ctx, m = G.m, w = s.w * sc, h = s.h * sc, fa = G.fa;
    if (m < 0.999) { ctx.globalAlpha = a * fa; ctx.drawImage(s.get(0), x - w / 2, y - h / 2, w, h); }
    if (m > 0.001) { ctx.globalAlpha = a * fa * (m < 0.999 ? m : 1); ctx.drawImage(s.get(1), x - w / 2, y - h / 2, w, h); }
    ctx.globalAlpha = fa;
}
/** a large theme-pair layer (feature backdrop) in screen space. */
export function layer(G, bx, by, bw, bh, draw) {
    const s = { bx, by, bw, bh, L: null, D: null };
    (G.reg ??= []).push(s);
    s.get = th => {
        const k = th ? 'D' : 'L';
        if (!s[k]) {
            const pd = G.pd, c = canvas(bw * pd, bh * pd), x = c.getContext('2d');
            x.scale(pd, pd); x.translate(-bx, -by);
            const t0 = G.t; G.t = th; draw(x); G.t = t0;
            s[k] = c;
        }
        return s[k];
    };
    s.draw = (a = 1) => {
        const ctx = G.ctx, m = G.m, fa = G.fa;
        if (s.soft) { // independent fades (the background crowd), so the dusk midpoint stays clean
            if (m < 0.999) { ctx.globalAlpha = a * fa * Math.pow(1 - m, 0.7); ctx.drawImage(s.get(0), bx, by, bw, bh); }
            if (m > 0.001) { ctx.globalAlpha = a * fa * Math.pow(m, 2.5); ctx.drawImage(s.get(1), bx, by, bw, bh); }
            ctx.globalAlpha = fa; return;
        }
        if (m < 0.999) { ctx.globalAlpha = a * fa; ctx.drawImage(s.get(0), bx, by, bw, bh); }
        if (m > 0.001) { ctx.globalAlpha = a * fa * (m < 0.999 ? m : 1); ctx.drawImage(s.get(1), bx, by, bw, bh); }
        ctx.globalAlpha = fa;
    };
    return s;
}

// ---------- polylines ----------
export function resample(P, step) {
    const out = [P[0]]; let acc = 0;
    for (let i = 1; i < P.length; i++) {
        let [ax, ay] = P[i - 1]; const [bx, by] = P[i];
        let seg = Math.hypot(bx - ax, by - ay);
        while (acc + seg >= step) {
            const t = (step - acc) / seg; ax = ax + (bx - ax) * t; ay = ay + (by - ay) * t;
            out.push([ax, ay]); seg = Math.hypot(bx - ax, by - ay); acc = 0;
        }
        acc += seg;
    }
    return out;
}
export function chaikin(P, it = 2, closed = false) {
    for (let k = 0; k < it; k++) {
        const Q = closed ? [] : [P[0]], n = P.length;
        for (let i = 0; i < (closed ? n : n - 1); i++) {
            const a = P[i], b = P[(i + 1) % n];
            Q.push([a[0] * 0.75 + b[0] * 0.25, a[1] * 0.75 + b[1] * 0.25], [a[0] * 0.25 + b[0] * 0.75, a[1] * 0.25 + b[1] * 0.75]);
        }
        if (!closed) Q.push(P[n - 1]);
        P = Q;
    }
    return P;
}
/** per-point unit normals (left of travel, i.e. (dy,-dx) rotated so that it agrees with `toward`). */
export function normals(P, closed = false) {
    const n = P.length, N = new Array(n);
    for (let i = 0; i < n; i++) {
        const a = P[closed ? (i - 1 + n) % n : Math.max(0, i - 1)], b = P[closed ? (i + 1) % n : Math.min(n - 1, i + 1)];
        const dx = b[0] - a[0], dy = b[1] - a[1], l = Math.hypot(dx, dy) || 1;
        N[i] = [-dy / l, dx / l];
    }
    return N;
}
export const offset = (P, N, d) => P.map((p, i) => [p[0] + N[i][0] * d, p[1] + N[i][1] * d]);
export function linePath(x, P, closed = false) {
    x.beginPath(); x.moveTo(P[0][0], P[0][1]);
    for (let i = 1; i < P.length; i++) x.lineTo(P[i][0], P[i][1]);
    if (closed) x.closePath();
}
/** a lipid bilayer along polyline P (k px per nm): tail band, two rows of head beads, soft outline. */
export function bilayer(G, x, P, k, closed = false, opt = {}) {
    const Q = resample(P, 1.05 * k), N = normals(Q, closed), head = rgb(G, 'head'), tail = rgb(G, 'tail');
    x.lineJoin = 'round'; x.lineCap = 'round';
    linePath(x, Q, closed); x.strokeStyle = css(lnOf(G, head, 0.5), 0.9); x.lineWidth = 5.6 * k; x.stroke();
    x.strokeStyle = css(head); x.lineWidth = 5.0 * k; x.stroke();
    x.strokeStyle = css(tail); x.lineWidth = 2.9 * k; x.stroke();
    // a hairline between leaflets
    x.strokeStyle = css(mixc(tail, head, 0.35), 0.8); x.lineWidth = Math.max(0.6, 0.18 * k); x.stroke();
    const r = 0.62 * k, hc = css(hiOf(G, head, 0.25));
    x.fillStyle = hc; x.beginPath();
    for (const s of [-1, 1]) {
        const d = 1.9 * k * s;
        for (let i = 0; i < Q.length; i++) { const px = Q[i][0] + N[i][0] * d, py = Q[i][1] + N[i][1] * d; x.moveTo(px + r, py); x.arc(px, py, r, 0, TAU); }
    }
    x.fill();
}
/** point & tangent angle at arclength s along a polyline with cumulative lengths C. */
export function cumlen(P) { const C = [0]; for (let i = 1; i < P.length; i++) C.push(C[i - 1] + Math.hypot(P[i][0] - P[i - 1][0], P[i][1] - P[i - 1][1])); return C; }
export function at(P, C, s) {
    const n = P.length; s = clamp(s, 0, C[n - 1]);
    let lo = 0, hi = n - 1;
    while (hi - lo > 1) { const mid = (lo + hi) >> 1; if (C[mid] <= s) lo = mid; else hi = mid; }
    const t = (s - C[lo]) / ((C[hi] - C[lo]) || 1), a = P[lo], b = P[hi];
    return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, Math.atan2(b[1] - a[1], b[0] - a[0])];
}
