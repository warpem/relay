// 18 — Folding yarn: a pair of knitting needles knits protein folds from gradient-dyed wool, one after another.
import { TOPOLOGIES, sampleChain, matAxis, matMul, apply, STRAND_W } from './folds.js';
import * as Y from './yarn.js';
import { buildBackdrop, buildWindowLight } from './bg.js';

export const meta = {
    title: 'Folding Yarn',
    tagline: 'Knitting needles at work on a protein, one stitch at a time',
    description: 'A pair of wooden needles knits a protein chain from a ball of tonal hand-dyed wool: right-handed ' +
        'α-helix coils of cord, stockinette β-strand ribbons with knitted arrowheads that pair into twisted sheets, ' +
        'loops in between, coloured N to C along the skein. Each fold rests briefly, is frogged back onto its ball, and ' +
        'the needles move on to knit another topology in a new pose, place and colourway. By day it lies on sunlit ' +
        'linen under a drifting window patch; by night the same table glows in flickering hearth light.',
    renderer: 'p2d',
    fps: 30,
    pixelDensity: 1,
    maxStep: 0.1,
    post: { paper: 0.12, grain: 0.3, vignette: 0.42 },
};

const clamp = (v, a = 0, b = 1) => (v < a ? a : v > b ? b : v);
const lerp = (a, b, t) => a + (b - a) * t;
const smooth = (t) => t * t * (3 - 2 * t);
const hash = (i) => { let x = Math.imul(i ^ 0x9e3779b9, 0x85ebca6b); x ^= x >>> 13; x = Math.imul(x, 0xc2b2ae35); x ^= x >>> 16; return (x >>> 0) / 4294967296; };
const wrapPi = (a) => { a %= Math.PI; if (a > Math.PI / 2) a -= Math.PI; if (a < -Math.PI / 2) a += Math.PI; return a; };
const TAU = 6.283185307179586;
const Z = [0, 0, 1];
const HELIX_R = 2.3;
const THEMES = ['light', 'dark'];
const WOOD = { light: [150, 104, 68], dark: [150, 98, 60] };   // walnut
const SHADOW = { light: [9, 12], dark: [16, -10] };

export default function sketch(p, h) {
    let W = 0, H = 0, pd = 1, ctx, frame = 0, flickNow = 0;
    let K = 11.5, ROW = 17, NEEDLE_L = 205, NEEDLE_W = 8.5, KAPPA = 40;
    const neutral = {}, neutralBall = {};
    let backdrop = {}, winLight, shadowSpr;
    const slots = [{ phase: 'empty' }, { phase: 'empty' }];
    const recent = [], recentWay = [];
    let cloud = null, flare = 0;
    const rnd = () => p.random();
    const N = { free: false, snap: null, ld: 1, mode: 'hidden', slot: -1, x: 0, y: 0, ang: 0, angT: 0, lean: 0, h: 3, a: 0, phi: 0, cyc: 0, period: 0.9, gpc: 1.5, t: 0, dur: 0, wait: 1.2, fx: 0, fy: 0, tries: 0 };

    // ------------------------------------------------------------------ fold construction
    function makeFold(type, way) {
        const top = TOPOLOGIES[type](rnd);
        const R = matMul(matAxis(Z, rnd() * TAU), top.base);
        const ch = sampleChain(top, rnd, Y.SPACING / K / 2, ROW / K);
        const nres = ch.nres, SP = Y.SPACING, NC = Y.NC, NL = Y.NL;
        let zmin = 1e9, zmax = -1e9;
        for (const s of ch.spine) { const z = apply(R, s.p)[2]; zmin = Math.min(zmin, z); zmax = Math.max(zmax, z); }
        const far = (z) => (zmax - z) / Math.max(zmax - zmin, 1);
        const items = [];
        const bump = (x, y, ang, ply) => {
            if (ang < 0) ang += TAU;
            const oi = Math.round(ang / TAU * Y.NO) % Y.NO, r = ang - oi * TAU / Y.NO;
            return { x, y, oi, c: Math.cos(r), s: Math.sin(r), ply };
        };
        // the cord: loops and helices, one ply bump every SPACING of true (3D) yarn length
        let acc = SP, prev = null, ti = 0;
        for (const s of ch.tube) {
            const q = apply(R, s.p);
            if (prev) acc += K * Math.hypot(q[0] - prev[0], q[1] - prev[1], q[2] - prev[2]);
            prev = q;
            if (acc < SP) continue;
            acc = Math.min(acc - SP, SP * 0.5);
            const t = apply(R, s.tan);
            // depth: the back half of each helical turn sits in its own shade, so the coil reads as a coil
            const back = s.ax ? clamp(-apply(R, [s.p[0] - s.ax[0], s.p[1] - s.ax[1], s.p[2] - s.ax[2]])[2] / HELIX_R) : 0;
            const d = clamp(0.95 * smooth(back) + 0.45 * far(q[2]));
            const b = bump(K * q[0], -K * q[1], Math.atan2(-t[1], t[0]), ti & 1);
            b.ci = Math.min(NC - 1, Math.floor(s.res / nres * (NC - 1) + hash(ti * 7 + 3)));
            b.lvl = Math.min(NL - 1, Math.floor(d * (NL - 1) + hash(ti * 7 + 1) * 0.9 + 0.05));
            items.push({ k: 0, z: q[2] + (back > 0 ? -0.6 : 0.6) * (s.ax ? 1 : 0), res: s.res, b });
            ti++;
        }
        // knitted rows: stockinette Vs (or purl bars on the back face), built from the same bumps
        const STW = STRAND_W / 2;
        ch.rows.forEach((r, ri) => {
            const c = apply(R, r.c), a = apply(R, r.a), w = apply(R, r.w), n = apply(R, r.n);
            const O = [K * c[0], -K * c[1]], Pa = [K * a[0], -K * a[1]], Pw = [K * w[0], -K * w[1]];
            const map = (u, s) => [O[0] + u * Pw[0] + s * Pa[0], O[1] + u * Pw[1] + s * Pa[1]];
            const purl = n[2] < 0, rh = r.rowH, hw = r.hw;
            const nSt = Math.max(1, Math.round(2 * hw / STW)), sw = 2 * hw / nSt;
            const bumps = [];
            let pl = ri;
            const leg = (u0, s0, u1, s1) => {
                const L3 = K * Math.hypot((u1 - u0), (s1 - s0)), nb = Math.max(2, Math.round(L3 / SP));
                const A = map(u0, s0), B = map(u1, s1), ang = Math.atan2(B[1] - A[1], B[0] - A[0]);
                for (let i = 0; i < nb; i++) { const f = (i + 0.5) / nb; bumps.push(bump(lerp(A[0], B[0], f), lerp(A[1], B[1], f), ang, pl++ & 1)); }
            };
            for (let j = 0; j < nSt; j++) {
                const uc = -hw + (j + 0.5) * sw;
                if (!purl) {
                    // each V points back toward the N-terminal (cast-on) end, as stockinette does
                    leg(uc, -0.5 * rh, uc - 0.44 * sw, 0.68 * rh);
                    leg(uc, -0.5 * rh, uc + 0.44 * sw, 0.68 * rh);
                } else {
                    const so = ((r.i + j) & 1 ? 0.16 : -0.16) * rh;
                    leg(uc - 0.46 * sw, so, uc + 0.46 * sw, so);
                }
            }
            // selvedges along both edges (and the cast-on edge) give each ribbon a clean outline of the same cord
            const prevRow = ri > 0 && ch.rows[ri - 1].strand === r.strand ? ch.rows[ri - 1] : null;
            for (const sd of [-1, 1]) {
                if (r.arrow && prevRow && !prevRow.arrow) leg(sd * STRAND_W / 2, -0.55 * rh, sd * r.hw0, -0.55 * rh);
                leg(sd * r.hw0, -0.55 * rh, sd * r.hw1, 0.6 * rh);
            }
            if (!prevRow) leg(-r.hw0, -0.55 * rh, r.hw0, -0.55 * rh);
            const q = [...map(-r.hw0, -0.55 * rh), ...map(r.hw0, -0.55 * rh), ...map(r.hw1, 0.6 * rh), ...map(-r.hw1, 0.6 * rh)];
            const nl = clamp((purl ? -1 : 1) * (n[0] * Y.LIGHT.light[0] - n[1] * Y.LIGHT.light[1] + n[2] * Y.LIGHT.light[2]));
            const d = clamp(0.45 * far(c[2]) + 0.35 * (1 - nl));
            items.push({
                k: 1, z: c[2], res: r.res, quad: q, bumps,
                ci: Math.min(NC - 1, Math.floor(r.res / nres * (NC - 1) + hash(ri * 13 + 5))),
                lvl: Math.min(NL - 1, Math.floor(d * (NL - 1) + hash(ri * 13 + 2) * 0.9 + 0.05)),
            });
        });
        for (const it of items) it.zq = Math.round(it.z / 0.7);
        items.sort((a, b) => (a.zq - b.zq) || (a.res - b.res));
        let bx0 = 1e9, bx1 = -1e9, by0 = 1e9, by1 = -1e9;
        const grow = (x, y, m) => { bx0 = Math.min(bx0, x - m); bx1 = Math.max(bx1, x + m); by0 = Math.min(by0, y - m); by1 = Math.max(by1, y + m); };
        for (const it of items) { if (it.k === 0) grow(it.b.x, it.b.y, Y.SB / 2 + 2); else for (const b of it.bumps) grow(b.x, b.y, Y.SB / 2 + 2); }
        bx0 = Math.floor(bx0); by0 = Math.floor(by0); bx1 = Math.ceil(bx1); by1 = Math.ceil(by1);
        const spine = ch.spine.map(s => { const q = apply(R, s.p); return { x: K * q[0], y: -K * q[1], res: s.res }; });
        return { type, way, items, spine, nres, bx0, bx1, by0, by1, r: Math.hypot(bx1 - bx0, by1 - by0) / 2 };
    }

    function tipAt(f, g) {
        const S = f.spine;
        if (g <= S[0].res) return { x: S[0].x, y: S[0].y, tx: S[1].x - S[0].x, ty: S[1].y - S[0].y };
        let lo = 0, hi = S.length - 1;
        if (g >= S[hi].res) return { x: S[hi].x, y: S[hi].y, tx: S[hi].x - S[hi - 1].x, ty: S[hi].y - S[hi - 1].y };
        while (hi - lo > 1) { const mid = (lo + hi) >> 1; if (S[mid].res <= g) lo = mid; else hi = mid; }
        const a = S[lo], b = S[hi], t = (g - a.res) / Math.max(b.res - a.res, 1e-6);
        return { x: lerp(a.x, b.x, t), y: lerp(a.y, b.y, t), tx: b.x - a.x, ty: b.y - a.y };
    }

    // ------------------------------------------------------------------ placement
    const cardRect = (pad) => { const f = h.focus; return [f.x - pad, f.y - pad, f.x + f.w + pad, f.y + f.h + pad]; };
    function rectsOverlap(a, b, pad) { return !(a[2] + pad < b[0] || b[2] + pad < a[0] || a[3] + pad < b[1] || b[3] + pad < a[1]); }
    function rectCircleDist(r, cx, cy) { const dx = Math.max(r[0] - cx, 0, cx - r[2]), dy = Math.max(r[1] - cy, 0, cy - r[3]); return Math.hypot(dx, dy); }
    function place(f, other) {
        const card = cardRect(40), ow = other && other.fold ? other : null;
        const m = 34;
        let best = null;
        for (let tries = 0; tries < 120; tries++) {
            const cx = lerp(-f.bx0 + m, W - f.bx1 - m, rnd()), cy = lerp(-f.by0 + m, H - f.by1 - m, rnd());
            const r = [cx + f.bx0, cy + f.by0, cx + f.bx1, cy + f.by1];
            if (r[0] < 0 || r[1] < 0 || r[2] > W || r[3] > H) continue;
            if (rectsOverlap(r, card, 0)) continue;
            if (ow && (rectsOverlap(r, ow.rect, 70) || rectCircleDist(r, ow.bx, ow.by) < Y.BALL_R + 50)) continue;
            // the ball sits on the cloth just beyond the C-terminal end, so the finished strand runs away from the fold
            let ball = null, bs = 1e9;
            const ct = f.spine[f.spine.length - 1], ctx0 = cx + ct.x, cty = cy + ct.y;
            const fcx = (r[0] + r[2]) / 2, fcy = (r[1] + r[3]) / 2, oa = Math.atan2(cty - fcy, ctx0 - fcx);
            for (let a = 0; a < 33; a++) {
                const ai = a % 11, ang = oa + (ai % 2 ? 1 : -1) * Math.ceil(ai / 2) * 0.28, dist = Y.BALL_R + 36 + Math.floor(a / 11) * 50 + rnd() * 20;
                const x = ctx0 + Math.cos(ang) * dist, y = cty + Math.sin(ang) * dist;
                if (x < Y.BALL_R + 12 || x > W - Y.BALL_R - 12 || y < Y.BALL_R + 12 || y > H - Y.BALL_R - 12) continue;
                if (rectCircleDist(card, x, y) < Y.BALL_R + 20 || rectCircleDist(r, x, y) < Y.BALL_R * 0.5) continue;
                if (ow && (rectCircleDist(ow.rect, x, y) < Y.BALL_R + 30 || Math.hypot(ow.bx - x, ow.by - y) < 3 * Y.BALL_R)) continue;
                const s = Math.ceil(ai / 2) * 12 + Math.floor(a / 11) * 20 + rnd() * 10;
                if (s < bs) { bs = s; ball = [x, y]; }
            }
            if (!ball) continue;
            const edge = Math.min(r[0], W - r[2], r[1], H - r[3]);
            const sc = edge * 0.8 + bs + rnd() * 80 + (ow ? -Math.min(500, Math.hypot(ow.cx - cx, ow.cy - cy)) * 0.2 : 0);
            if (!best || sc < best.sc) best = { sc, cx, cy, r, ball };
        }
        return best;
    }

    // ------------------------------------------------------------------ lifecycle
    function pickFrom(list, recentList, keep) {
        const all = list.filter(t => !recentList.includes(t));
        const t = all[Math.floor(rnd() * all.length)];
        recentList.push(t); while (recentList.length > keep) recentList.shift();
        return t;
    }
    function spawn(si) {
        const slot = slots[si], other = slots[1 - si];
        for (let attempt = 0; attempt < 4; attempt++) {
            const type = pickFrom(Object.keys(TOPOLOGIES), recent, 3), way = pickFrom([0, 1, 2, 3, 4], recentWay, 2);
            const f = makeFold(type, way), pl = place(f, other);
            if (!pl) continue;
            for (const k in slot) delete slot[k];
            Object.assign(slot, {
                fold: f, cx: pl.cx, cy: pl.cy, rect: pl.r, bx: pl.ball[0], by: pl.ball[1], g: 0, phase: 'appear', t: 0, dur: 1.6,
                att: 0, cache: {}, drawnG: {}, lastDraw: {}, atl: {}, ballAtl: {}, quad: {}, shadow: null, shadowG: -1,
                sag: rnd() < 0.5 ? -1 : 1,
            });
            return true;
        }
        return false;
    }
    function stepSlot(s, dt) {
        if (s.phase === 'empty') return;
        s.t += dt;
        switch (s.phase) {
            case 'appear': break;                       // waits for the needles to arrive
            case 'rest': if (s.t >= s.dur) { s.phase = 'unravel'; s.t = 0; } break;
            case 'unravel': {
                const ease = clamp(s.t / 2.5) * 0.85 + 0.15;
                s.g -= (7 + s.fold.nres / 12) * ease * dt;
                if (s.g <= 0) { s.g = 0; s.phase = 'vanish'; s.t = 0; s.dur = 1.8; }
                break;
            }
            case 'vanish': if (s.t >= s.dur) { for (const k in s) delete s[k]; s.phase = 'empty'; } break;
        }
        if (s.phase !== 'knit' && s.phase !== 'appear') s.att = Math.max(0, s.att - dt / 0.9);
    }

    // knitting rhythm, per stitch: insert (0–.3), wrap (.3–.55), pull through (.55–.82), slip (.82–1)
    const slipEase = (ph) => smooth(clamp((ph - 0.55) / 0.45));

    function needleTarget(s) { const t = tipAt(s.fold, s.g); return [s.cx + t.x, s.cy + t.y]; }
    function chooseAxis(s, px, py) {
        const f = s.fold, t1 = tipAt(f, s.g), t0 = tipAt(f, s.g - 3);
        let dx = t1.x - t0.x, dy = t1.y - t0.y; if (Math.hypot(dx, dy) < 1) { dx = t1.tx; dy = t1.ty; }
        const pref = Math.atan2(dy, dx) + Math.PI / 2;
        const card = cardRect(34), other = slots.find(o => o !== s && o.fold);
        const ox = px - (s.cx + (f.bx0 + f.bx1) / 2), oy = py - (s.cy + (f.by0 + f.by1) / 2);
        const outLean = (th) => Math.sign(-Math.sin(th) * ox + Math.cos(th) * oy) || 1;
        let hits = 0;
        const score = (th, lean) => {
            let sc = 0; hits = 0;
            const cx = Math.cos(th), cy = Math.sin(th), px0 = -cy * lean, py0 = cx * lean;
            for (const [sg, e] of [[1, 0.2], [-1, 0.42]]) {
                const ux = cx * sg * Math.cos(e) + px0 * Math.sin(e), uy = cy * sg * Math.cos(e) + py0 * Math.sin(e);
                for (let i = 1; i <= 10; i++) {
                    const x = px + ux * (NEEDLE_L + NEEDLE_W) * i / 10, y = py + uy * (NEEDLE_L + NEEDLE_W) * i / 10;
                    if (x > card[0] && x < card[2] && y > card[1] && y < card[3]) { sc += 3; hits++; }
                    if (x < -30 || x > W + 30 || y < -30 || y > H + 30) sc += 0.15;
                    if (other && x > other.rect[0] && x < other.rect[2] && y > other.rect[1] && y < other.rect[3]) sc += 0.09;
                    if (Math.hypot(x - s.bx, y - s.by) < Y.BALL_R + 14) sc += 0.5;
                }
            }
            // prefer the butts leaning away from the fold, and a hold close to the live row
            return sc + (lean !== outLean(th) ? 0.4 : 0) + Math.abs(wrapPi(th - pref)) * 0.5;
        };
        // the lean is fixed for the whole fold (re-chosen while the needles are lifted between folds)
        const ln = N.free ? null : N.lean;
        let best = pref, bl = ln ?? 1, bs = 1e9;
        for (const dl of [0, 0.25, -0.25, 0.5, -0.5, 0.75, -0.75, 1.0, -1.0, 1.25, -1.25, Math.PI / 2]) for (const l of (ln === null ? [1, -1] : [ln])) {
            const sc = score(pref + dl, l);
            if (sc < bs) { bs = sc; best = pref + dl; bl = l; }
        }
        if (ln === null) { N.nextLean = bl; N.nextAng = N.ang + wrapPi(best - N.ang); return; }
        // hysteresis: keep the current hold unless the new one is clearly better
        if (score(N.angT, ln) > bs + 0.35) N.angT = N.ang + wrapPi(best - N.ang);
        // hard rule: the needles as they are now must never pass under the card; turn away at once if they would
        score(N.ang, ln);
        if (hits > 0) {
            let found = false;
            for (const l of [ln, -ln]) {
                for (let k = 1; k <= 32 && !found; k++) for (const sg of [1, -1]) {
                    const th = N.ang + sg * k * 0.05; score(th, l);
                    if (hits === 0) { N.angT = th; N.snap = th; N.lean = l; found = true; break; }
                }
                if (found) break;
            }
        }
    }

    function stepNeedles(dt) {
        N.t += dt;
        const s = N.slot >= 0 ? slots[N.slot] : null;
        const follow = (k) => {
            if (!s || !s.fold) return;
            const [tx, ty] = needleTarget(s), e = 1 - Math.exp(-dt / k);
            N.x += (tx - N.x) * e; N.y += (ty - N.y) * e;
            N.snap = null;
            chooseAxis(s, N.x, N.y);
            if (N.snap !== null) N.ang = N.snap;          // rare: a sharp turn of the chain right beside the card
            else N.ang += wrapPi(N.angT - N.ang) * (1 - Math.exp(-dt / 1.1));
        };
        const tryStart = () => {
            const si = slots.findIndex(o => o.phase === 'empty');
            if (si < 0 || !spawn(si)) return false;
            const sl = slots[si];
            N.slot = si; N.fx = N.x; N.fy = N.y;
            const [tx, ty] = needleTarget(sl);
            N.period = 0.82 + rnd() * 0.18; N.gpc = 1.4 + rnd() * 0.3; N.cyc = 0; N.phi = 0;
            if (N.mode === 'hidden') { N.x = tx; N.y = ty; N.h = 3.2; N.mode = 'descend'; N.t = 0; N.dur = 1.5; N.angT = N.ang = Math.atan2(sl.cy - h.focus.cy, sl.cx - h.focus.cx); N.free = true; chooseAxis(sl, tx, ty); N.free = false; N.ang = N.angT = N.nextAng; N.lean = N.ld = N.nextLean; }
            else {
                N.free = true; const a0 = N.ang; chooseAxis(sl, tx, ty); N.free = false;
                // glide over the cloth unless the way passes the card; then lift away and come down at the new place
                const c = cardRect(NEEDLE_L * 0.9); let clear = true;
                for (let i = 0; i <= 12; i++) { const x = lerp(N.x, tx, i / 12), y = lerp(N.y, ty, i / 12); if (x > c[0] && x < c[2] && y > c[1] && y < c[3]) clear = false; }
                if (clear) { N.mode = 'travel'; N.t = 0; N.dur = clamp(Math.hypot(tx - N.x, ty - N.y) / 420, 1.4, 3.2); N.ang = a0; N.angT = N.nextAng; N.lean = N.nextLean; }
                else { N.mode = 'hop'; N.t = 0; N.dur = 0.9; N.ang = a0; N.tx = tx; N.ty = ty; }
            }
            return true;
        };
        N.ld = N.a <= 0 ? N.lean : N.ld + (N.lean - N.ld) * (1 - Math.exp(-dt / 0.35));
        switch (N.mode) {
            case 'hidden':
                N.a = 0; N.wait -= dt;
                if (N.wait <= 0) { N.wait = 3; tryStart(); }
                break;
            case 'descend': {
                const e = smooth(clamp(N.t / N.dur));
                N.h = lerp(3.2, 1, e); N.a = Math.max(N.a, e);
                if (s) s.att = Math.max(s.att, e);
                follow(0.15);
                if (N.t >= N.dur) { N.mode = 'knit'; N.t = 0; if (s) { s.phase = 'knit'; s.t = 0; s.att = 1; } }
                break;
            }
            case 'knit': {
                N.phi += dt / N.period;
                while (N.phi >= 1) { N.phi -= 1; N.cyc++; }
                s.g = Math.min(s.fold.nres, N.gpc * (N.cyc + slipEase(N.phi)));
                follow(0.12);
                if (s.g >= s.fold.nres) { s.phase = 'rest'; s.t = 0; s.dur = 15 + rnd() * 15; N.mode = 'lift'; N.t = 0; N.dur = 1.3; }
                break;
            }
            case 'lift': {
                N.h = lerp(1, 3.2, smooth(clamp(N.t / N.dur)));
                N.phi = Math.min(1, N.phi + dt / N.period);
                if (N.t >= N.dur) { N.slot = -1; N.tries = 0; if (!tryStart()) { N.mode = 'hover'; N.t = 0; } }
                break;
            }
            case 'hover':
                if (N.t > 2) { N.t = 0; if (!tryStart() && ++N.tries > 2) { N.mode = 'leave'; N.t = 0; } }
                break;
            case 'leave':
                N.a = Math.max(0, N.a - dt / 1.5);
                if (N.a <= 0) { N.mode = 'hidden'; N.wait = 2; N.slot = -1; }
                break;
            case 'hop':
                N.a = Math.max(0, 1 - N.t / N.dur);
                if (N.t >= N.dur) { N.a = 0; N.x = N.tx; N.y = N.ty; N.ang = N.angT = N.nextAng; N.lean = N.ld = N.nextLean; N.h = 3.2; N.mode = 'descend'; N.t = 0; N.dur = 1.4; }
                break;
            case 'travel': {
                const e = smooth(clamp(N.t / N.dur)), [tx, ty] = needleTarget(s);
                N.x = lerp(N.fx, tx, e); N.y = lerp(N.fy, ty, e);
                N.ang += wrapPi(N.angT - N.ang) * (1 - Math.exp(-dt / 0.6));
                if (N.t >= N.dur) { N.mode = 'descend'; N.t = 0; N.dur = 1.1; }
                break;
            }
        }
    }

    // ------------------------------------------------------------------ drawing
    function ensureAtlases(s, theme) {
        if (s.atl[theme]) return;
        const f = s.fold;
        s.atl[theme] = Y.tintBumps(neutral[theme], f.way);
        s.ballAtl[theme] = Y.tintBall(neutralBall[theme], f.way);
        const lv = theme === 'light' ? [1, 0.86, 0.72] : [1, 0.78, 0.6];
        s.quad[theme] = Array.from({ length: Y.NC }, (_, ci) => {
            const b = Y.dye(f.way, ci / (Y.NC - 1));
            return lv.map(l => theme === 'light' ? Y.css(b.map(v => v * 0.5 * l)) : Y.css([b[0] * 0.4 * l, b[1] * 0.27 * l, b[2] * 0.2 * l]));
        });
    }

    function drawBumpAt(x, atlas, b, row, ox, oy) {
        const SB = Y.SB;
        x.setTransform(pd * b.c, pd * b.s, -pd * b.s, pd * b.c, pd * (b.x + ox), pd * (b.y + oy));
        x.drawImage(atlas, b.oi * SB, row * SB, SB, SB, -SB / 2, -SB / 2, SB, SB);
    }

    function redrawCache(s, theme) {
        ensureAtlases(s, theme);
        const f = s.fold, cw = f.bx1 - f.bx0, chh = f.by1 - f.by0, NL = Y.NL;
        let c = s.cache[theme];
        if (!c) c = s.cache[theme] = Y.makeCanvas(cw * pd, chh * pd);
        const x = c.getContext('2d'), ox = -f.bx0, oy = -f.by0, g = s.g + 1e-6;
        x.setTransform(1, 0, 0, 1, 0, 0); x.clearRect(0, 0, c.width, c.height);
        const atlas = s.atl[theme], QC = s.quad[theme];
        for (const it of f.items) {
            if (it.res > g) continue;
            if (it.k === 0) { const b = it.b; drawBumpAt(x, atlas, b, (b.ci * 2 + b.ply) * NL + b.lvl, ox, oy); continue; }
            const q = it.quad;
            x.setTransform(pd, 0, 0, pd, 0, 0);
            x.fillStyle = QC[it.ci][it.lvl];
            x.beginPath(); x.moveTo(q[0] + ox, q[1] + oy); x.lineTo(q[2] + ox, q[3] + oy); x.lineTo(q[4] + ox, q[5] + oy); x.lineTo(q[6] + ox, q[7] + oy); x.closePath(); x.fill();
            const r0 = it.ci * 2 * NL + it.lvl;
            for (const b of it.bumps) drawBumpAt(x, atlas, b, r0 + b.ply * NL, ox, oy);
        }
        x.setTransform(1, 0, 0, 1, 0, 0);
        s.drawnG[theme] = s.g;
        if (s.shadowG !== s.g) {
            const sc = 1 / 6;
            if (!s.shadow) s.shadow = Y.makeCanvas(cw * sc + 2, chh * sc + 2);
            const sx = s.shadow.getContext('2d');
            sx.globalCompositeOperation = 'source-over'; sx.clearRect(0, 0, s.shadow.width, s.shadow.height);
            sx.drawImage(c, 1, 1, cw * sc, chh * sc);
            sx.globalCompositeOperation = 'source-in'; sx.fillStyle = 'rgb(34,18,10)'; sx.fillRect(0, 0, s.shadow.width, s.shadow.height);
            s.shadowG = s.g;
        }
    }

    // needle geometry for the current rhythm phase
    function needlePose() {
        const ux = Math.cos(N.ang), uy = Math.sin(N.ang), px = -uy * N.ld, py = ux * N.ld;
        const eA = 0.2, eB = 0.42, ph = N.phi;
        let dAx = ux * Math.cos(eA) + px * Math.sin(eA), dAy = uy * Math.cos(eA) + py * Math.sin(eA);
        let dBx = -ux * Math.cos(eB) + px * Math.sin(eB), dBy = -uy * Math.cos(eB) + py * Math.sin(eB);
        const knitting = N.mode === 'knit' || N.mode === 'descend';
        let off = -14, slide = 0, lift = 0, wob = 0;
        if (knitting) {
            if (ph < 0.3) off = lerp(-14, 9, smooth(ph / 0.3));
            else if (ph < 0.55) off = 9 + 1.2 * Math.sin((ph - 0.3) / 0.25 * Math.PI);
            else if (ph < 0.82) { const e = smooth((ph - 0.55) / 0.27); off = lerp(9, -10, e); lift = Math.sin(e * Math.PI) * 0.45; }
            else { const e = smooth((ph - 0.82) / 0.18); off = lerp(-10, -14, e); slide = Math.sin(e * Math.PI) * 5; wob = Math.sin(e * Math.PI) * 0.05; }
            off *= Y.D / 12;
        }
        if (wob) { const c = Math.cos(wob * N.ld), sn = Math.sin(wob * N.ld), x = dAx * c - dAy * sn; dAy = dAx * sn + dAy * c; dAx = x; }
        const A = { tx: N.x - dAx * (0.6 * Y.D + slide), ty: N.y - dAy * (0.6 * Y.D + slide), dx: dAx, dy: dAy, h: N.h };
        const B = { tx: N.x - dBx * off, ty: N.y - dBy * off, dx: dBx, dy: dBy, h: N.h + 0.25 + lift };
        // where the working yarn leaves the right needle (it wraps around the tip during the wrap phase)
        const pbx = -dBy * N.ld, pby = dBx * N.ld;
        let wx = B.tx + dBx * Y.D * 1.1 + pbx * Y.D * 0.55, wy = B.ty + dBy * Y.D * 1.1 + pby * Y.D * 0.55;
        if (knitting && ph >= 0.3 && ph < 0.55) {
            const ps = (ph - 0.3) / 0.25 * TAU, r = Y.D * 0.62;
            wx = B.tx + dBx * Y.D * (0.55 - 0.45 * Math.cos(ps)) + pbx * r * Math.cos(ps) * 0.9 - dBx * r * 0.2 * Math.sin(ps);
            wy = B.ty + dBy * Y.D * (0.55 - 0.45 * Math.cos(ps)) + pby * r * Math.cos(ps) * 0.9 - dBy * r * 0.2 * Math.sin(ps);
        }
        return { A, B, wx, wy };
    }

    function drawNeedle(nd, m, alpha, shadowPass) {
        const L = NEEDLE_L, w = NEEDLE_W, dx = nd.dx, dy = nd.dy;
        if (shadowPass) {
            const sv = [lerp(SHADOW.light[0], SHADOW.dark[0], m) + flickNow * m * 1.5, lerp(SHADOW.light[1], SHADOW.dark[1], m) - flickNow * m];
            const k = 0.55 * nd.h, sx = nd.tx + sv[0] * k, sy = nd.ty + sv[1] * k, a = alpha * 0.26 / Math.sqrt(nd.h);
            ctx.lineCap = 'round';
            for (const [lw, aa] of [[w * 2.2, 0.35], [w * 1.2, 0.6]]) {
                ctx.strokeStyle = `rgba(34,18,10,${(a * aa).toFixed(3)})`; ctx.lineWidth = lw;
                ctx.beginPath(); ctx.moveTo(sx + dx * w, sy + dy * w); ctx.lineTo(sx + dx * (L + w * 0.6), sy + dy * (L + w * 0.6)); ctx.stroke();
            }
            return;
        }
        ctx.save();
        ctx.setTransform(pd * dx, pd * dy, -pd * dy, pd * dx, pd * nd.tx, pd * nd.ty);
        ctx.globalAlpha = alpha;
        // wood lit across its round section (local +y in screen space is (−dy, dx))
        const g = ctx.createLinearGradient(0, -w / 2, 0, w / 2);
        for (let i = 0; i <= 4; i++) {
            const sv = -1 + i / 2, z = Math.sqrt(Math.max(0, 1 - sv * sv)), n = [-dy * sv, dx * sv, z];
            const col = [0, 1, 2].map(ch => {
                const one = (th) => { const Lt = Y.LIGHT[th], df = Math.max(0, n[0] * Lt[0] + n[1] * Lt[1] + n[2] * Lt[2]);
                    return th === 'light' ? WOOD.light[ch] * (0.6 + 0.5 * df) + 30 * df ** 6 : WOOD.dark[ch] * (0.22 + 0.85 * df) * [1, 0.78, 0.6][ch]; };
                return lerp(one('light'), one('dark'), m);
            });
            g.addColorStop(i / 4, Y.css(col));
        }
        const tp = w * 2.6;
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.moveTo(0, 0);
        ctx.quadraticCurveTo(tp * 0.45, -w * 0.42, tp, -w / 2); ctx.lineTo(L, -w / 2); ctx.lineTo(L, w / 2); ctx.lineTo(tp, w / 2);
        ctx.quadraticCurveTo(tp * 0.45, w * 0.42, 0, 0); ctx.fill();
        // grain
        ctx.strokeStyle = m < 0.5 ? 'rgba(120,80,45,0.22)' : 'rgba(40,20,10,0.3)'; ctx.lineWidth = 0.6;
        ctx.beginPath(); ctx.moveTo(tp * 1.2, -w * 0.12); ctx.lineTo(L * 0.62, -w * 0.18); ctx.moveTo(L * 0.3, w * 0.16); ctx.lineTo(L * 0.95, w * 0.1); ctx.stroke();
        // knob at the butt
        const kr = w * 0.9, kg = ctx.createRadialGradient(L + kr * 0.6 - dx * 0, -kr * 0.3, kr * 0.1, L + kr * 0.6, 0, kr);
        kg.addColorStop(0, Y.css(Y.mixc([176, 128, 86], [150, 96, 60], m))); kg.addColorStop(1, Y.css(Y.mixc([96, 62, 38], [42, 24, 16], m)));
        ctx.fillStyle = kg; ctx.beginPath(); ctx.arc(L + kr * 0.6, 0, kr, 0, TAU); ctx.fill();
        ctx.restore();
    }

    function yarnPoints(s, pose) {
        const f = s.fold, tip = tipAt(f, s.g), tl = Math.hypot(tip.tx, tip.ty) || 1;
        const tx = s.cx + tip.x, ty = s.cy + tip.y, ux = tip.tx / tl, uy = tip.ty / tl;
        const n = 40, free = [], att = [];
        const toBall = (x0, y0) => { const dx = s.bx - x0, dy = s.by - y0, dl = Math.hypot(dx, dy) || 1; return [s.bx - dx / dl * Y.BALL_R * 0.7, s.by - dy / dl * Y.BALL_R * 0.7, dl, dx / dl, dy / dl]; };
        const cubic = (out, P0, P1, P2, P3, i0, i1) => { for (let i = i0; i <= i1; i++) { const t = (i - i0) / (i1 - i0), u = 1 - t; out.push([u * u * u * P0[0] + 3 * u * u * t * P1[0] + 3 * u * t * t * P2[0] + t * t * t * P3[0], u * u * u * P0[1] + 3 * u * u * t * P1[1] + 3 * u * t * t * P2[1] + t * t * t * P3[1]]); } };
        if (s.att < 1) {
            const [bx, by, dl, ex, ey] = toBall(tx, ty), sg = s.sag * Math.min(40, dl * 0.12);
            cubic(free, [tx, ty], [tx + ux * Math.min(60, dl * 0.35), ty + uy * Math.min(60, dl * 0.35)], [bx - ex * dl * 0.3 - ey * sg, by - ey * dl * 0.3 + ex * sg], [bx, by], 0, n);
        }
        if (s.att > 0 && pose) {
            const k = 6, W0 = [pose.wx, pose.wy];
            for (let i = 0; i <= k; i++) { const t = i / k; att.push([lerp(tx, W0[0], t), lerp(ty, W0[1], t)]); }
            att.pop();
            const [bx, by, dl, ex, ey] = toBall(W0[0], W0[1]), sg = s.sag * Math.min(40, dl * 0.12), r = Math.min(110, dl * 0.35);
            cubic(att, W0, [W0[0] + pose.B.dx * r * 0.3 + ex * r * 0.7, W0[1] + pose.B.dy * r * 0.3 + ey * r * 0.7], [bx - ex * dl * 0.3 - ey * sg, by - ey * dl * 0.3 + ex * sg], [bx, by], 0, n - k);
        }
        if (s.att <= 0 || !pose) return free;
        if (s.att >= 1) return att;
        const e = smooth(s.att);
        return free.map((q, i) => [lerp(q[0], att[i][0], e), lerp(q[1], att[i][1], e)]);
    }

    function drawYarn(s, pts, theme, alpha) {
        const atlas = s.atl[theme]; if (!atlas) return;
        const SP = Y.SPACING, NL = Y.NL, NC = Y.NC, f = s.fold;
        const mat = s.g * KAPPA, ph = mat % SP, base = Math.floor(mat / SP);
        ctx.globalAlpha = alpha;
        let sAcc = 0, j = 1, next = SP - ph;
        for (let i = 1; i < pts.length; i++) {
            const [x0, y0] = pts[i - 1], [x1, y1] = pts[i], sl = Math.hypot(x1 - x0, y1 - y0);
            while (next <= sAcc + sl && sl > 0) {
                const t = (next - sAcc) / sl, x = lerp(x0, x1, t), y = lerp(y0, y1, t);
                let ang = Math.atan2(y1 - y0, x1 - x0); if (ang < 0) ang += TAU;
                const oi = Math.round(ang / TAU * Y.NO) % Y.NO, r = ang - oi * TAU / Y.NO, id = base + j;
                const ci = Math.min(NC - 1, Math.floor(clamp((s.g + next / KAPPA) / f.nres) * (NC - 1) + hash(id * 5 + 1)));
                drawBumpAt(ctx, atlas, { x, y, oi, c: Math.cos(r), s: Math.sin(r) }, (ci * 2 + (id & 1)) * NL, 0, 0);
                next += SP; j++;
            }
            sAcc += sl;
        }
        ctx.setTransform(pd, 0, 0, pd, 0, 0);
        ctx.globalAlpha = 1;
    }

    function drawSlotBase(s, m, pose) {
        const f = s.fold; if (!f) return;
        const ap = s.phase === 'appear' ? smooth(clamp(s.t / s.dur)) : s.phase === 'vanish' ? 1 - smooth(clamp(s.t / s.dur)) : 1;
        const ws = [1 - m, m];
        for (let ti = 0; ti < 2; ti++) {
            if (ws[ti] <= 0) continue;
            const th = THEMES[ti];
            ensureAtlases(s, th);
            const due = s.drawnG[th] === undefined || (s.drawnG[th] !== s.g && ((s.lastDraw[th] ?? -9) + 3 <= frame || s.phase === 'rest'));
            if (due && f.items.length) { redrawCache(s, th); s.lastDraw[th] = frame; }
        }
        const dg = s.drawnG[m < 0.5 ? 'light' : 'dark'] ?? 0;
        const sox = lerp(SHADOW.light[0], SHADOW.dark[0], m) + flickNow * m * 1.6, soy = lerp(SHADOW.light[1], SHADOW.dark[1], m) - flickNow * m * 1.2, sa = lerp(0.26, 0.34, m);
        if (s.shadow && dg > 0) { ctx.globalAlpha = sa; ctx.drawImage(s.shadow, s.cx + f.bx0 + sox * 0.7 - 6, s.cy + f.by0 + soy * 0.7 - 6, f.bx1 - f.bx0 + 12, f.by1 - f.by0 + 12); ctx.globalAlpha = 1; }
        const br = Y.BALL_R;
        // ball nudges toward the needles as each loop is pulled through
        let jx = 0, jy = 0;
        if (s.phase === 'knit' && N.slot === slots.indexOf(s)) { const e = Math.sin(Math.PI * clamp((N.phi - 0.55) / 0.3)), dx = N.x - s.bx, dy = N.y - s.by, dl = Math.hypot(dx, dy) || 1; jx = dx / dl * e * 1.6; jy = dy / dl * e * 1.6; }
        ctx.globalAlpha = sa * ap;
        ctx.drawImage(shadowSpr, s.bx + jx - br * 1.2 + sox * 0.9, s.by + jy - br * 1.2 + soy * 0.9, br * 2.4, br * 2.4);
        ctx.globalAlpha = 1;
        for (let ti = 0; ti < 2; ti++) {
            const th = THEMES[ti], c = s.cache[th];
            if (ws[ti] <= 0 || !c || dg <= 0) continue;
            ctx.globalAlpha = ti === 0 ? 1 : ws[1];
            ctx.drawImage(c, s.cx + f.bx0, s.cy + f.by0, f.bx1 - f.bx0, f.by1 - f.by0);
        }
        ctx.globalAlpha = 1;
        // the working strand from the knitted end (or the needles) back to the ball
        const pts = yarnPoints(s, pose);
        ctx.strokeStyle = `rgba(34,18,10,${(sa * 0.4 * ap).toFixed(3)})`; ctx.lineWidth = Y.D * 0.8; ctx.lineCap = 'round'; ctx.lineJoin = 'round';
        ctx.beginPath(); ctx.moveTo(pts[0][0] + sox * 0.35, pts[0][1] + soy * 0.35); for (const q of pts) ctx.lineTo(q[0] + sox * 0.35, q[1] + soy * 0.35); ctx.stroke();
        for (let ti = 0; ti < 2; ti++) if (ws[ti] > 0) drawYarn(s, pts, THEMES[ti], (ti === 0 ? 1 : ws[1]) * ap);
        // the ball, showing the colour about to be knitted
        const cpos = clamp(s.g / f.nres + 0.04) * (Y.NB - 1), c0 = Math.floor(cpos), c1 = Math.min(Y.NB - 1, c0 + 1), fr = cpos - c0;
        for (let ti = 0; ti < 2; ti++) {
            if (ws[ti] <= 0) continue;
            const a = (ti === 0 ? 1 : ws[1]) * ap, B = Y.BALL, atl = s.ballAtl[THEMES[ti]];
            ctx.globalAlpha = a; ctx.drawImage(atl, c0 * B, 0, B, B, s.bx + jx - B / 2, s.by + jy - B / 2, B, B);
            if (fr > 0.02) { ctx.globalAlpha = a * fr; ctx.drawImage(atl, c1 * B, 0, B, B, s.bx + jx - B / 2, s.by + jy - B / 2, B, B); }
        }
        ctx.globalAlpha = 1;
    }

    function drawLight(m) {
        if (m < 1) {
            const cl = cloud ? cloud.depth * Math.sin(Math.PI * clamp((h.time - cloud.t0) / cloud.dur)) ** 2 : 0;
            const sun = clamp(0.8 + 0.2 * h.drift('sun', 500) - cl);
            const cx = W * (0.2 + 0.07 * h.drift('sunx', 1100)), cy = H * (0.3 + 0.1 * h.drift('suny', 1500));
            ctx.globalCompositeOperation = 'multiply';
            ctx.globalAlpha = 0.45 * sun * (1 - m);
            ctx.drawImage(winLight, cx - W, cy - H, W * 2, H * 2);
            ctx.globalCompositeOperation = 'source-over';
            ctx.globalAlpha = 1;
        }
        if (m > 0) {
            const fire = clamp(0.78 + 0.16 * h.drift('fire', 260) + flare + 0.07 * flickNow, 0, 1.3);
            const fx = -W * 0.06, fy = H * 1.08, R = Math.hypot(W, H);
            const reach = 0.32 + 0.12 * fire;
            const g = ctx.createRadialGradient(fx, fy, 0, fx, fy, R * 1.05);
            g.addColorStop(0, 'rgb(255,250,240)'); g.addColorStop(reach, 'rgb(240,208,184)'); g.addColorStop(reach + 0.3, 'rgb(176,146,160)'); g.addColorStop(1, 'rgb(124,116,160)');
            ctx.globalCompositeOperation = 'multiply'; ctx.globalAlpha = m;
            ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
            const g2 = ctx.createRadialGradient(fx, fy, 0, fx, fy, R * 0.75);
            g2.addColorStop(0, `rgba(255,150,70,${(0.3 * fire).toFixed(3)})`); g2.addColorStop(0.5, `rgba(230,110,50,${(0.09 * fire).toFixed(3)})`); g2.addColorStop(1, 'rgba(0,0,0,0)');
            ctx.globalCompositeOperation = 'screen';
            ctx.fillStyle = g2; ctx.fillRect(0, 0, W, H);
            ctx.globalCompositeOperation = 'source-over'; ctx.globalAlpha = 1;
        }
    }

    function buildScene() {
        W = p.width; H = p.height;
        backdrop.light = buildBackdrop(W, H, 'light', h.seed, h.focus);
        backdrop.dark = buildBackdrop(W, H, 'dark', h.seed, h.focus);
        winLight = buildWindowLight(W, H);
    }

    return {
        setup() {
            pd = p.pixelDensity(); ctx = p.drawingContext;
            W = p.width; H = p.height;
            const sc = clamp(Math.min(W, H) / 900, 0.8, 1.12);
            Y.setGauge(Math.round(11 * sc));
            K = 11.5 * sc; ROW = 0.72 * (STRAND_W / 2) * K; NEEDLE_L = 235 * sc; NEEDLE_W = Y.D * 1.0; KAPPA = 3.8 * K;
            for (const th of THEMES) { neutral[th] = Y.buildNeutralBumps(th, h.seed + 1); neutralBall[th] = Y.buildNeutralBall(th, neutral[th], h.seed + 3); }
            shadowSpr = Y.buildShadowSprite();
            buildScene();
            h.every(260, () => { if (!cloud || h.time > cloud.t0 + cloud.dur) cloud = { t0: h.time, dur: 25 + p.random(40), depth: 0.25 + p.random(0.35) }; });
            h.every(200, () => { flare = Math.min(0.45, flare + 0.22 + p.random(0.15)); });
        },
        step(dt) {
            flare *= Math.exp(-dt / 14);
            stepNeedles(dt);
            for (const s of slots) stepSlot(s, dt);
        },
        render() {
            frame++;
            pd = p.pixelDensity();
            const m = smooth(h.mix);
            flickNow = 0.6 * h.drift('fl1', 0.9) + 0.4 * h.drift('fl2', 0.33);
            ctx.save();
            ctx.setTransform(pd, 0, 0, pd, 0, 0);
            if (m < 1) ctx.drawImage(backdrop.light, 0, 0, W, H);
            if (m > 0) { ctx.globalAlpha = m; ctx.drawImage(backdrop.dark, 0, 0, W, H); ctx.globalAlpha = 1; }
            const pose = N.a > 0 ? needlePose() : null;
            slots.forEach((s, i) => drawSlotBase(s, m, N.slot === i ? pose : null));
            if (pose) {
                drawNeedle(pose.A, m, N.a, true); drawNeedle(pose.B, m, N.a, true);
                drawNeedle(pose.A, m, N.a, false); drawNeedle(pose.B, m, N.a, false);
            }
            drawLight(m);
            ctx.restore();
        },
        resize() {
            buildScene();
            for (const s of slots) if (s.fold && (s.rect[2] > W + 20 || s.rect[3] > H + 20 || rectsOverlap(s.rect, cardRect(0), 0))) {
                if (s.phase === 'rest') s.dur = 0;
            }
        },
    };
}
