// 12 — Thon rings: drops on still water, each ripple a true CTF.
// Light: a pale crackle-glazed basin at noon, seen through shallow water; ripples throw caustic rings
// on the floor and catch a soft sun sheen. Dark: the same basin at night, warm charcoal under a small moon,
// with two lanterns and a few fireflies throwing amber pools full of caustic rings.
import { VERT, FRAG, N_DROPS, N_LEAVES, N_PADS, N_FLIES } from './shader.js';

export const meta = {
    title: 'Thon Rings',
    tagline: 'Rain on a garden basin, where every ripple is a contrast transfer function',
    description: 'Drops fall into a still glazed garden basin, and every ripple is a real CTF with its own defocus, astigmatism, ' +
        'spherical aberration and B-factor fade; the rings spread, interfere softly and die away. At noon you look through the water ' +
        'at a crackle-glazed floor laced with caustic Thon rings; at night two lanterns and a few fireflies throw warm pools where the rings glow, ' +
        'a small moon breaks up on the ripples, and the water lily closes. Showers and lulls, a dripping spout whose defocus wanders ' +
        'like a collection session, seasonal petals and leaves, passing clouds, and lily pads that age and renew keep it from repeating.',
    renderer: 'webgl',
    fps: 30,
    pixelDensity: 0.6,
    maxStep: 1 / 15,
    post: { paper: 0.18, grain: 0.3, vignette: 0.4 },
};

const TAU = Math.PI * 2;
const lerp = (a, b, t) => a + (b - a) * t;
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const smooth = t => t * t * (3 - 2 * t);
const sstep = (a, b, x) => smooth(clamp((x - a) / (b - a), 0, 1));
const mixv = (a, b, t) => a.map((v, i) => lerp(v, b[i], t));

// electron optics
const LAMBDA = { 300: 0.019687, 200: 0.025079 };   // Å, relativistic
const PHI_A = Math.asin(0.07);                       // 7% amplitude contrast (vitreous ice)

export default function sketch(p, h) {
    let sh;
    const S = () => clamp(Math.sqrt(p.width * p.height) / 1140, 0.7, 1.5);

    const drops = [];
    const leaves = [];
    const pads = [];
    const flies = [];
    const lily = { phase: 'gap', t: 0, dur: 1, x: 0, y: 0, len: 0, rot: 0, open: 0, rise: 0, tint: 0.5 };
    let cluster = { x: 0, y: 0 };
    const st = {
        spout: { x: 0, y: 0, on: true, wait: 3 },
        shower: null,
        cloud: { off: [0, 0] },
        sunCloud: null,
        glazeOff: [0, 0],
    };

    // ---------- geometry around the card ----------
    const rectDist = (x, y) => {
        const f = h.focus;
        const dx = Math.max(Math.abs(x - f.cx) - f.w / 2, 0), dy = Math.max(Math.abs(y - f.cy) - f.h / 2, 0);
        return Math.hypot(dx, dy);
    };
    const calmAt = (x, y) => lerp(0.05, 1, sstep(10 * S(), 230 * S(), rectDist(x, y)));

    // ---------- one drop = one micrograph's CTF ----------
    function makeDrop(x, y, kind) {
        const s = S();
        const kv = p.random() < 0.82 ? 300 : 200;
        const lam = LAMBDA[kv];
        const Cs = 2.7e7;                                                  // 2.7 mm in Å
        const dfMean = 1.6 + 0.9 * h.drift('session-df', 900);             // µm, the session's target range wanders
        let df;
        if (kind === 'spout') df = clamp(st.spout.df + p.random(-0.12, 0.12), 0.5, 3.6);
        else df = clamp(dfMean + p.randomGaussian(0, 0.55), 0.45, 3.8);
        df *= 1e4;                                                          // Å
        const astR = p.random() < 0.4 ? p.random(0.08, 0.25) : p.random(0, 0.04);
        const ast = df * astR, astA = p.random(Math.PI);
        // how many rings out to the front, and the spatial frequency there
        const big = kind === 'spout' ? 1 : kind === 'rain' ? p.random(0.45, 0.85) : p.random(0.25, 0.5);
        const nf = kind === 'shower' ? p.random(6, 10) : p.random(9, 17);
        const kF = Math.sqrt(nf / (lam * df));
        // B factor chosen so the rings fade naturally toward the front (kept within a sane 20–250 Å²)
        const ef = p.random(0.12, 0.45);
        const B = clamp(-4 * Math.log(ef) / (kF * kF), 20, 250);
        return {
            x, y, age: 0, R0: 6 * s, v: (kind === 'spout' ? p.random(17, 22) : p.random(13, 21)) * s,
            tau: (kind === 'shower' ? p.random(5, 8) : kind === 'spout' ? p.random(13, 18) : p.random(9, 15)),
            S0: (kind === 'spout' ? p.random(0.008, 0.011) : kind === 'rain' ? 0.010 * big : 0.009 * big),
            kF, nf, df, c2: ast * Math.cos(2 * astA), s2: ast * Math.sin(2 * astA), piLam: Math.PI * lam,
            csT: 0.5 * Math.PI * Cs * lam * lam * lam, B, amp: 0, R: 0, s: 0,
        };
    }
    function updateDrop(d) {
        d.R = d.R0 + d.v * d.age;
        d.s = d.kF / d.R;
        const S0 = d.S0 * (1 - Math.exp(-d.age / 0.35)) * Math.exp(-d.age / d.tau);
        // curvature amplitude (1/px): spreading rings flatten
        d.amp = S0 * Math.sqrt(160 * S() / (d.R + 40 * S()));
        const g = TAU * Math.sqrt(d.nf) / d.R * 0.45;    // |∇χ| about the first ring
        d.g2 = g * g;
    }
    function addDrop(d) {
        if (drops.length >= N_DROPS) {
            // the faintest ripple makes room
            let wi = 0, wv = Infinity;
            for (let i = 0; i < drops.length; i++) { const v = drops[i].S0 * Math.exp(-drops[i].age / drops[i].tau); if (v < wv) { wv = v; wi = i; } }
            drops.splice(wi, 1);
        }
        updateDrop(d);
        drops.push(d);
    }
    // somewhere on open water, preferring the edges and never right by the card
    function pickSpot() {
        const W = p.width, H = p.height, s = S();
        for (let k = 0; k < 30; k++) {
            const x = p.random(-0.04, 1.04) * W, y = p.random(-0.04, 1.04) * H;
            const d = rectDist(x, y);
            if (d < 120 * s) continue;
            const w = 0.25 + 0.75 * sstep(120 * s, 520 * s, d);
            if (onPad(x, y)) continue;
            if (p.random() < w) return [x, y];
        }
        return [p.random() < 0.5 ? 0.08 * W : 0.92 * W, p.random(0.1, 0.9) * H];
    }

    // the same field on the CPU, for things floating on it
    function slopeAt(x, y) {
        let gx = 0, gy = 0;
        for (const d of drops) {
            const ux = x - d.x, uy = y - d.y, r2 = ux * ux + uy * uy;
            if (r2 >= d.R * d.R) continue;
            const s2 = d.s * d.s, k2 = s2 * r2;
            const q = d.df * r2 + d.c2 * (ux * ux - uy * uy) + d.s2 * 2 * ux * uy;
            const chi = d.piLam * s2 * q - d.csT * k2 * k2;
            const gqx = 2 * d.df * ux + 2 * (d.c2 * ux + d.s2 * uy), gqy = 2 * d.df * uy + 2 * (-d.c2 * uy + d.s2 * ux);
            const gcx = d.piLam * s2 * gqx - 4 * d.csT * s2 * k2 * ux, gcy = d.piLam * s2 * gqy - 4 * d.csT * s2 * k2 * uy;
            const env = Math.exp(-0.25 * d.B * k2);
            const xx = Math.sqrt(r2) / d.R, win = 1 - sstep(0.72, 1, xx);
            const g2 = gcx * gcx + gcy * gcy;
            const c = -d.amp * env * win * Math.cos(chi + PHI_A) / (g2 + d.g2);
            gx += c * gcx; gy += c * gcy;
        }
        const k = calmAt(x, y);
        return [gx * k, gy * k];
    }

    // ---------- floating leaves and petals ----------
    function season() { return (h.drift('season', 1400) + 1) / 2; }          // 0 spring blossom … 1 autumn
    function makeLeaf(onScreen) {
        const W = p.width, H = p.height, s = S();
        const se = season();
        const petal = p.random() < lerp(0.85, 0.05, sstep(0.25, 0.75, se));
        let x, y, hx, hy;
        // enter from an edge, heading across the water but bending away from the card
        const edge = (p.random() * 4) | 0;
        const m = 40 * s;
        if (edge === 0) { x = -m; y = p.random(0.05, 0.95) * H; }
        else if (edge === 1) { x = W + m; y = p.random(0.05, 0.95) * H; }
        else if (edge === 2) { x = p.random(0.05, 0.95) * W; y = -m; }
        else { x = p.random(0.05, 0.95) * W; y = H + m; }
        const tx = p.random(0.1, 0.9) * W, ty = p.random(0.1, 0.9) * H;
        const a = Math.atan2(ty - y, tx - x) + p.random(-0.5, 0.5);
        const spd = (petal ? p.random(5, 9) : p.random(4, 7)) * s;
        hx = Math.cos(a) * spd; hy = Math.sin(a) * spd;
        if (onScreen) {
            const sp = pickSpot(); x = sp[0]; y = sp[1];
        }
        let col, rib, L, Wd;
        if (petal) {
            const pinkness = p.random(0.3, 1);
            col = mixv([0.99, 0.95, 0.94], [0.99, 0.86, 0.87], pinkness);
            rib = mixv([0.95, 0.74, 0.78], [0.90, 0.58, 0.66], pinkness);
            L = p.random(8.5, 12) * s; Wd = L * p.random(0.62, 0.75);
        } else {
            const autumn = clamp(se + p.random(-0.3, 0.3), 0, 1);
            const green = mixv([0.55, 0.66, 0.36], [0.68, 0.72, 0.38], p.random());
            const gold = mixv([0.93, 0.70, 0.30], [0.86, 0.48, 0.26], p.random());
            col = mixv(green, gold, sstep(0.35, 0.8, autumn));
            rib = mixv(col, [0.98, 0.9, 0.7], 0.35);
            L = p.random(17, 26) * s; Wd = L * p.random(0.36, 0.48);
        }
        return {
            x, y, hx, hy, ang: p.random(TAU), spin: p.random(-0.05, 0.05), petal, col, rib, L, W: Wd,
            curl: p.random(-0.12, 0.12), tilt: [0, 0], tv: [0, 0], age: onScreen ? 10 : 0, o: p.random(1000), alpha: onScreen ? 1 : 0,
        };
    }

    // ---------- lily pads and a water lily in one corner ----------
    function makePad(x, y, R, young) {
        return { ax: x, ay: y, x, y, vx: 0, vy: 0, R, g: young ? 0.25 : 1, grow: 1 / p.random(300, 600), ang: p.random(TAU),
            spin: p.random(-0.004, 0.004), hue: 0, age: 0, life: p.random(1500, 4000), alpha: young ? 0 : 1, tilt: [0, 0], tv: [0, 0], o: p.random(1000) };
    }
    function padSpot(R) {
        const s = S();
        for (let k = 0; k < 40; k++) {
            const a = p.random(TAU), r = p.random(0.3, 1.6) * 120 * s;
            const x = cluster.x + Math.cos(a) * r, y = cluster.y + Math.sin(a) * r;
            if (rectDist(x, y) < R + 140 * s) continue;
            if (pads.some(q => q.alpha > 0 && Math.hypot(q.x - x, q.y - y) < (q.R * q.g + R) * 0.92)) continue;
            return [x, y];
        }
        return null;
    }
    function makeCluster() {
        const W = p.width, H = p.height, s = S();
        const br = p.random() < 0.5;
        cluster = br ? { x: W * p.random(0.86, 0.95), y: H * p.random(0.8, 0.93) } : { x: W * p.random(0.04, 0.13), y: H * p.random(0.06, 0.18) };
        const n = 3 + (p.random() < 0.5 ? 1 : 0);
        for (let i = 0; i < n; i++) {
            const R = (i === 0 ? p.random(80, 100) : p.random(48, 80)) * s;
            const sp = i === 0 ? [cluster.x, cluster.y] : padSpot(R);
            if (sp) { const q = makePad(sp[0], sp[1], R, false); q.age = p.random(0, 0.75) * q.life; pads.push(q); }
        }
    }
    function startLily() {
        const s = S();
        // a bud comes up in the gap beside a pad
        const host = pads[(p.random() * pads.length) | 0];
        if (!host) return;
        for (let k = 0; k < 20; k++) {
            const a = p.random(TAU), r = host.R * host.g + p.random(14, 30) * s;
            const x = host.x + Math.cos(a) * r, y = host.y + Math.sin(a) * r;
            if (x < 30 * s || y < 30 * s || x > p.width - 30 * s || y > p.height - 30 * s) continue;
            if (pads.some(q => q !== host && Math.hypot(q.x - x, q.y - y) < q.R * q.g + 10 * s)) continue;
            Object.assign(lily, { phase: 'rise', t: 0, dur: p.random(60, 120), x, y, len: p.random(34, 44) * s, rot: p.random(TAU),
                open: 0, rise: 0, tint: p.random() });
            return;
        }
    }
    function stepLily(dt) {
        lily.t += dt;
        const u = clamp(lily.t / lily.dur, 0, 1);
        const next = (ph, dur) => { lily.phase = ph; lily.t = 0; lily.dur = dur; };
        if (lily.phase === 'gap') { if (u >= 1) startLily(); if (lily.phase === 'gap') { lily.t = 0; lily.dur = p.random(200, 700); } }
        else if (lily.phase === 'rise') { lily.rise = smooth(u); lily.open = 0; if (u >= 1) next('opening', p.random(180, 320)); }
        else if (lily.phase === 'opening') { lily.open = smooth(u); if (u >= 1) next('bloom', p.random(600, 1300)); }
        else if (lily.phase === 'bloom') { lily.open = 1 - 0.06 * (1 + h.drift('lilybreath', 120)) / 2; if (u >= 1) next('fade', p.random(150, 260)); }
        else if (lily.phase === 'fade') { lily.open = 1 - 0.6 * smooth(u); lily.rise = 1 - smooth(u); if (u >= 1) { lily.rise = 0; next('gap', p.random(200, 700)); } }
        lily.rot += dt * 0.003;
    }
    function stepPads(dt) {
        const s = S();
        for (const q of pads) {
            q.age += dt;
            q.g = Math.min(1, q.g + dt * q.grow);
            q.hue = sstep(0.55, 1, q.age / q.life);   // old pads bronze before they sink
            if (q.age > q.life) q.alpha = Math.max(0, q.alpha - dt / 120); else q.alpha = Math.min(1, q.alpha + dt / 60);
            const g = slopeAt(q.x, q.y);
            q.tv[0] += ((g[0] - q.tilt[0]) * 12 - q.tv[0] * 4) * dt;
            q.tv[1] += ((g[1] - q.tilt[1]) * 12 - q.tv[1] * 4) * dt;
            q.tilt[0] += q.tv[0] * dt; q.tilt[1] += q.tv[1] * dt;
            // tethered by its stem: wanders a little around its anchor
            const tx = q.ax + 10 * s * h.drift('padx' + q.o, 400), ty = q.ay + 10 * s * h.drift('pady' + q.o, 450);
            q.vx += ((tx - q.x) * 0.02 - q.vx * 0.3 - g[0] * 30 * s) * dt;
            q.vy += ((ty - q.y) * 0.02 - q.vy * 0.3 - g[1] * 30 * s) * dt;
            q.x += q.vx * dt; q.y += q.vy * dt;
            q.ang += (q.spin + 0.01 * h.drift('pada' + q.o, 300)) * dt;
        }
        for (let i = pads.length - 1; i >= 0; i--) if (pads[i].age > pads[i].life && pads[i].alpha <= 0) pads.splice(i, 1);
        // a new leaf unrolls now and then
        if (pads.length < N_PADS && p.random() < dt / 500) {
            const R = p.random(48, 90) * s, sp = padSpot(R);
            if (sp) pads.push(makePad(sp[0], sp[1], R, true));
        }
    }
    const onPad = (x, y) => pads.some(q => Math.hypot(q.x - x, q.y - y) < q.R * q.g + 6 * S());

    // ---------- fireflies: a few slow warm lights that keep to the edges ----------
    function makeFly() {
        const s = S();
        const [x, y] = pickSpot();
        return { x, y, vx: 0, vy: 0, age: 0, life: p.random(60, 240), o: p.random(1000), I: 0, peak: p.random(0.5, 1) };
    }
    function stepFlies(dt) {
        const s = S(), W = p.width, H = p.height;
        const evening = (h.drift('fireflies', 600) + 1) / 2;           // some nights are busier than others
        const want = Math.round(1 + 5 * evening);
        if (flies.length < Math.min(want, N_FLIES) && p.random() < dt / 8) flies.push(makeFly());
        for (const f of flies) {
            f.age += dt;
            const ax = (p.noise(f.o, h.time * 0.12) - 0.5) * 30 * s, ay = (p.noise(f.o + 50, h.time * 0.12) - 0.5) * 30 * s;
            f.vx += (ax - f.vx * 0.6) * dt; f.vy += (ay - f.vy * 0.6) * dt;
            // keep away from the card and inside the basin
            const d = rectDist(f.x, f.y);
            if (d < 220 * s) { const ex = f.x - h.focus.cx, ey = f.y - h.focus.cy, el = Math.hypot(ex, ey) || 1; f.vx += ex / el * 20 * s * dt; f.vy += ey / el * 20 * s * dt; }
            if (f.x < 20 * s) f.vx += 10 * s * dt; if (f.x > W - 20 * s) f.vx -= 10 * s * dt;
            if (f.y < 20 * s) f.vy += 10 * s * dt; if (f.y > H - 20 * s) f.vy -= 10 * s * dt;
            f.x += f.vx * dt; f.y += f.vy * dt;
            // a slow glow, never a blink: smooth swell in and out over seconds
            const life = sstep(0, 6, f.age) * (1 - sstep(f.life - 8, f.life, f.age));
            f.I = f.peak * life * (0.35 + 0.65 * sstep(0.3, 0.7, p.noise(f.o + 100, h.time * 0.18)));
        }
        for (let i = flies.length - 1; i >= 0; i--) if (flies[i].age > flies[i].life) flies.splice(i, 1);
    }

    // ---------- events ----------
    h.every(30, () => { if (leaves.length < N_LEAVES - 1) leaves.push(makeLeaf(false)); });
    h.every(320, () => {   // a shower rolls through
        if (st.shower) return;
        st.shower = { t: 0, dur: p.random(40, 130), peak: p.random(0.5, 1.3) };
    });
    h.every(700, () => {   // the spout is nudged (a bamboo pipe settles), or runs dry for a while
        if (p.random() < 0.3) { st.spout.on = false; st.spout.dry = p.random(40, 150); }
        else placeSpout();
    });
    h.every(260, () => {   // a cloud passes over the sun
        if (st.sunCloud) return;
        st.sunCloud = { t: 0, dur: p.random(20, 60), ramp: p.random(6, 14), depth: p.random(0.25, 0.5) };
    });

    function placeSpout() {
        const W = p.width, H = p.height;
        const right = p.random() < 0.5, top = p.random() < 0.5;
        st.spout.x = (right ? p.random(0.8, 0.93) : p.random(0.07, 0.2)) * W;
        st.spout.y = (top ? p.random(0.12, 0.3) : p.random(0.7, 0.88)) * H;
        st.spout.df = 1.0 + 1.6 * p.random();
    }

    // ---------- uniform buffers ----------
    const DA = new Float32Array(N_DROPS * 4), DB = new Float32Array(N_DROPS * 4), DC = new Float32Array(N_DROPS * 4);
    const PA = new Float32Array(N_PADS * 4), PB = new Float32Array(N_PADS * 4), FF = new Float32Array(N_FLIES * 4);
    const LA = new Float32Array(N_LEAVES * 4), LB = new Float32Array(N_LEAVES * 4), LC = new Float32Array(N_LEAVES * 4), LD = new Float32Array(N_LEAVES * 4);

    function stepLeaves(dt) {
        const W = p.width, H = p.height, s = S();
        const ca = 0.6 * h.drift('current', 1200) * Math.PI;
        const cur = (2 + 1.5 * h.drift('curspd', 500)) * s;
        for (const l of leaves) {
            l.age += dt;
            const g = slopeAt(l.x, l.y);
            // rocking: the tilt follows the passing slope through a soft spring
            l.tv[0] += ((g[0] - l.tilt[0]) * 30 - l.tv[0] * 5) * dt;
            l.tv[1] += ((g[1] - l.tilt[1]) * 30 - l.tv[1] * 5) * dt;
            l.tilt[0] += l.tv[0] * dt; l.tilt[1] += l.tv[1] * dt;
            // steer around the still patch by the card
            const d = rectDist(l.x, l.y);
            let rx = 0, ry = 0;
            if (d < 200 * s) {
                const f = h.focus, k = (1 - d / (200 * s)) * 9 * s;
                const ex = l.x - f.cx, ey = l.y - f.cy, el = Math.hypot(ex, ey) || 1;
                rx = ex / el * k; ry = ey / el * k;
            }
            const vx = l.hx + Math.cos(ca) * cur + rx - g[0] * 40 * s;
            const vy = l.hy + Math.sin(ca) * cur + ry - g[1] * 40 * s;
            l.x += vx * dt; l.y += vy * dt;
            l.ang += (l.spin + (g[0] * Math.sin(l.ang) - g[1] * Math.cos(l.ang)) * 1.5) * dt;
            const out = l.x < -80 * s || l.x > W + 80 * s || l.y < -80 * s || l.y > H + 80 * s;
            l.alpha = Math.min(1, l.alpha + dt / 3);
            if (out && l.age > 20) l.dead = true;
        }
        for (let i = leaves.length - 1; i >= 0; i--) if (leaves[i].dead) leaves.splice(i, 1);
    }

    return {
        setup() {
            sh = p.createShader(VERT, FRAG);
            makeCluster();
            // the lily is somewhere in its life when we arrive
            startLily();
            const r0 = p.random();
            if (r0 < 0.6) { lily.phase = 'bloom'; lily.rise = 1; lily.open = 1; lily.t = p.random(0, 400); lily.dur = p.random(700, 1300); }
            else if (r0 < 0.8) { lily.phase = 'opening'; lily.rise = 1; lily.t = p.random(0, 100); lily.dur = p.random(180, 320); }
            placeSpout();
            st.spout.wait = p.random(1, 4);
            st.glazeOff = [p.random(-5000, 5000), p.random(-5000, 5000)];
            st.cloud.off = [p.random(100), p.random(100)];
            // the scene opens mid-drizzle: a few ripples already spreading
            for (let i = 0; i < 4; i++) {
                const [x, y] = pickSpot();
                const d = makeDrop(x, y, 'rain');
                d.age = p.random(1, 9);
                addDrop(d);
            }
            const d = makeDrop(st.spout.x, st.spout.y, 'spout'); d.age = 2.5; addDrop(d);
            for (let i = 0; i < 4; i++) leaves.push(makeLeaf(true));
            for (let i = 0; i < 3; i++) { const f = makeFly(); f.age = p.random(8, 40); flies.push(f); }
        },

        step(dt) {
            const s = S();
            // ripples spread and fade
            for (const d of drops) { d.age += dt; updateDrop(d); }
            for (let i = drops.length - 1; i >= 0; i--) {
                const d = drops[i];
                if (d.age > d.tau * 4.5 || d.R > 1400 * s) drops.splice(i, 1);
            }

            // rain: slow wet/dry weather plus the odd shower
            const wet = sstep(-0.3, 0.8, h.drift('wet', 520));
            let rate = 0.07 + 0.3 * wet;
            if (st.shower) {
                const sh = st.shower; sh.t += dt;
                const u = sh.t / sh.dur;
                rate += sh.peak * Math.sin(Math.PI * clamp(u, 0, 1)) ** 1.5;
                if (u >= 1) st.shower = null;
            }
            if (p.random() < rate * dt) {
                const [x, y] = pickSpot();
                addDrop(makeDrop(x, y, st.shower && p.random() < 0.7 ? 'shower' : 'rain'));
            }

            // the spout drips irregularly; its defocus walks slowly like a collection session
            st.spout.df = clamp(st.spout.df + dt * 0.004 * h.drift('spout-walk', 200), 0.6, 3.4);
            if (!st.spout.on) { st.spout.dry -= dt; if (st.spout.dry <= 0) st.spout.on = true; }
            if (st.spout.on) {
                st.spout.wait -= dt;
                if (st.spout.wait <= 0) {
                    const j = 2.5 * s;
                    addDrop(makeDrop(st.spout.x + p.random(-j, j), st.spout.y + p.random(-j, j), 'spout'));
                    const mean = 6 + 4 * h.drift('drip', 300);
                    st.spout.wait = mean * p.random(0.6, 1.5);
                }
            }

            if (st.sunCloud) { st.sunCloud.t += dt; if (st.sunCloud.t > st.sunCloud.dur + 2 * st.sunCloud.ramp) st.sunCloud = null; }
            st.cloud.off[0] += dt * 0.006 * (1 + 0.5 * h.drift('cdx', 300));
            st.cloud.off[1] += dt * 0.002 * h.drift('cdy', 400);

            stepLeaves(dt);
            stepPads(dt);
            stepFlies(dt);
            stepLily(dt);
        },

        render() {
            const W = p.width, H = p.height, s = S(), m = h.byTheme(0, 1);

            DA.fill(0); DB.fill(0); DC.fill(0);
            drops.forEach((d, i) => {
                DA.set([d.x, d.y, d.R, d.amp], i * 4);
                DB.set([d.s, d.df, d.c2, d.s2], i * 4);
                DC.set([d.piLam, d.csT, d.B, d.g2], i * 4);
            });
            LA.fill(0); LB.fill(0); LC.fill(0); LD.fill(0);
            leaves.forEach((l, i) => {
                if (i >= N_LEAVES) return;
                // a floating thing also bobs sideways a little on the orbital motion of the waves
                LA.set([l.x - l.tilt[0] * 30 * s, l.y - l.tilt[1] * 30 * s, l.ang, l.L], i * 4);
                LB.set([l.W, l.petal ? 1 : 0, l.tilt[0], l.tilt[1]], i * 4);
                LC.set([...l.col, l.alpha], i * 4);
                LD.set([...l.rib, l.curl], i * 4);
            });

            PA.fill(0); PB.fill(0);
            pads.forEach((q, i) => {
                if (i >= N_PADS) return;
                PA.set([q.x - q.tilt[0] * 20 * s, q.y - q.tilt[1] * 20 * s, q.R * (0.3 + 0.7 * smooth(q.g)), q.ang], i * 4);
                PB.set([q.tilt[0], q.tilt[1], q.alpha, q.hue], i * 4);
            });
            const nightShut = 1 - 0.78 * m;   // water lilies close at night
            const lopen = lily.open * nightShut;

            // day
            const sunA = 0.6 + 0.5 * h.drift('sun-az', 1500);
            const sunSpot = [W * (0.95 + 0.04 * h.drift('sunx', 900)), H * (0.04 + 0.04 * h.drift('suny', 1100))];
            let sunI = 0.95 + 0.05 * h.drift('haze', 300);
            if (st.sunCloud) {
                const c = st.sunCloud;
                sunI *= 1 - c.depth * sstep(0, c.ramp, c.t) * (1 - sstep(c.dur + c.ramp, c.dur + 2 * c.ramp, c.t));
            }
            const shOff = [-Math.cos(sunA) * 16 * s, Math.sin(sunA) * 20 * s];

            // night
            const moon = [W * (0.8 + 0.1 * h.drift('moonx', 1700)), H * (0.17 + 0.07 * h.drift('moony', 1900)), 13 * s];
            const moonK = clamp(0.8 + 0.35 * h.drift('phase', 3000), 0.55, 1);
            const flick = 1 + 0.035 * (p.noise(h.time * 0.9, 77) - 0.5) * 2 + 0.015 * (p.noise(h.time * 3.1, 79) - 0.5) * 2;
            const lantern = [W * 0.075, H * 0.86];
            const lanCol = mixv([1.0, 0.62, 0.28], [1.0, 0.70, 0.36], (h.drift('lamphue', 500) + 1) / 2);

            // perspective: an eye above the basin; each light is placed so its mirror image lands on its spot
            const E = 1.3 * Math.max(W, H), eye = [W / 2, H / 2, E];
            const mirrorDir = (P) => { const v = [eye[0] - P[0], eye[1] - P[1], E], l = Math.hypot(...v); return [-v[0] / l, -v[1] / l, v[2] / l]; };
            const lz = 170 * s, tt = (E + lz) / E;
            const lampP = [eye[0] + (lantern[0] - eye[0]) * tt, eye[1] + (lantern[1] - eye[1]) * tt, lz];
            const lantern2 = [W * (0.955 + 0.015 * h.drift('lan2x', 800)), H * (0.56 + 0.03 * h.drift('lan2y', 900))];
            const lz2 = 120 * s, tt2 = (E + lz2) / E;
            const lamp2P = [eye[0] + (lantern2[0] - eye[0]) * tt2, eye[1] + (lantern2[1] - eye[1]) * tt2, lz2];
            const flick2 = 1 + 0.04 * (p.noise(h.time * 0.8, 91) - 0.5) * 2;
            FF.fill(0);
            flies.forEach((f, i) => { if (i < N_FLIES) FF.set([f.x, f.y, f.I * m, 0], i * 4); });

            p.shader(sh);
            sh.setUniform('uEye', eye);
            sh.setUniform('uSunL', mirrorDir(sunSpot));
            sh.setUniform('uMoonL', mirrorDir(moon));
            sh.setUniform('uLampP', lampP);
            sh.setUniform('uLantern2', lantern2);
            sh.setUniform('uLamp2P', lamp2P);
            sh.setUniform('uLantern2I', 0.7 * flick2);
            sh.setUniform('uFF', FF);
            sh.setUniform('uSize', [W, H]);
            sh.setUniform('uDensity', p.pixelDensity());
            sh.setUniform('uMix', m);
            sh.setUniform('uFocus', [h.focus.cx, h.focus.cy, h.focus.w / 2, h.focus.h / 2]);
            sh.setUniform('uS', s);
            sh.setUniform('uDA', DA); sh.setUniform('uDB', DB); sh.setUniform('uDC', DC);
            sh.setUniform('uLA', LA); sh.setUniform('uLB', LB); sh.setUniform('uLC', LC); sh.setUniform('uLD', LD);
            sh.setUniform('uPA', PA); sh.setUniform('uPB', PB);
            sh.setUniform('uFA', [lily.x, lily.y, lily.len * (0.5 + 0.5 * lily.rise), lopen]);
            sh.setUniform('uFB', [lily.rot, lily.phase === 'gap' ? 0 : smooth(clamp(lily.rise * 1.5, 0, 1)), lily.rise, lily.tint]);
            sh.setUniform('uSunSpot', sunSpot);
            sh.setUniform('uSunI', sunI);
            sh.setUniform('uShadowOff', shOff);
            sh.setUniform('uGlaze', 0.55 + 0.35 * h.drift('glaze', 2000));
            sh.setUniform('uGlazeOff', st.glazeOff);
            sh.setUniform('uMoon', moon);
            sh.setUniform('uMoonI', 0.85 + 0.15 * h.drift('moonI', 400));
            sh.setUniform('uMoonPhase', 2.6 + 0.3 * h.drift('tilt', 2000));
            sh.setUniform('uMoonK', moonK);
            sh.setUniform('uLantern', lantern);
            sh.setUniform('uLanternI', 0.95 * flick);
            sh.setUniform('uLanternCol', lanCol);
            sh.setUniform('uCloudOff', st.cloud.off);
            sh.setUniform('uCloudAmt', clamp(0.6 + 0.5 * h.drift('cloudy', 700), 0.2, 1));
            sh.setUniform('uSeedF', (h.seed % 1000) / 1000);
            p.noStroke();
            p.plane(W, H);
        },
    };
}
