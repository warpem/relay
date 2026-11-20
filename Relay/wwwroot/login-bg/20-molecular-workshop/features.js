// features.js: the scenery pieces that slide in from the screen edges, each hosting working machines.
import { TAU, clamp, lerp, sm, hash, vnoise, fbm, mixc, css, rgb, lnOf, hiOf, blobPath, gouache, sprite, spr, layer, expo,
    resample, chaikin, normals, offset, linePath, bilayer, cumlen, at } from './lib.js';
import { vesicleSprite, membraneProtein, glycan, complexI } from './machines.js';

export const EDGE_ZONES = { T: ['TL', 'T', 'TR'], B: ['BL', 'B', 'BR'], L: ['TL', 'L', 'BL'], R: ['TR', 'R', 'BR'] };
export function edgeMap(G, e) {
    const { W, H } = G, f = G.h.focus;
    if (e === 'B') return { L: W, pt: (u, v) => [u, H - v], out: [0, 1], budget: H - (f.y + f.h) };
    if (e === 'T') return { L: W, pt: (u, v) => [u, v], out: [0, -1], budget: f.y };
    if (e === 'L') return { L: H, pt: (u, v) => [v, u], out: [-1, 0], budget: f.x };
    return { L: H, pt: (u, v) => [W - v, u], out: [1, 0], budget: W - (f.x + f.w) };
}
/** a roughly round free region for zone z: centre, radius and the outward (away from card) unit vector. */
export function spotOf(G, z) {
    const { W, H } = G, f = G.h.focus;
    const corners = { TL: [0, 0, f.x, f.y], TR: [W, 0, f.x + f.w, f.y], BL: [0, H, f.x, f.y + f.h], BR: [W, H, f.x + f.w, f.y + f.h] };
    let cx, cy, rad;
    if (corners[z]) {
        const [qx, qy, kx, ky] = corners[z];
        const dx = Math.abs(kx - qx), dy = Math.abs(ky - qy);
        cx = lerp(qx, kx, 0.46); cy = lerp(qy, ky, 0.46); rad = Math.min(Math.max(dx, dy) * 0.42, Math.min(dx, dy) * 0.75 + 40, 300);
    } else {
        const em = edgeMap(G, z), d = em.budget, L = em.L;
        [cx, cy] = em.pt(L / 2, d * 0.44); rad = Math.min(d * 0.42, L * 0.2, 300);
    }
    const ox = cx - f.cx, oy = cy - f.cy, ol = Math.hypot(ox, oy) || 1;
    return { cx, cy, rad, out: [ox / ol, oy / ol] };
}

class Feat {
    constructor(G, claims, out) {
        this.G = G; this.claims = claims; this.out = out; this.age = 0; this.dead = -1; this.alive = true;
        this.seed = (G.r() * 1e6) | 0;
    }
    retire() { if (this.dead < 0) this.dead = 0; }
    get alpha() { const fi = sm(clamp(this.age / 20, 0, 1)), fo = this.dead < 0 ? 1 : sm(clamp(1 - this.dead / 16, 0, 1)); return fi * fo; }
    get slide() { return (1 - sm(clamp(this.age / 20, 0, 1))) * 46 + (this.dead < 0 ? 0 : sm(clamp(this.dead / 16, 0, 1)) * 36); }
    tick(dt) {
        this.age += dt;
        if (this.dead >= 0) { this.dead += dt; if (this.dead > 16) this.alive = false; }
        this.step(dt);
    }
    draw() {
        const G = this.G, a = this.alpha;
        if (a <= 0.003) return;
        const ctx = G.ctx, s = this.slide;
        ctx.save(); G.ox = this.out[0] * s; G.oy = this.out[1] * s; ctx.translate(G.ox, G.oy);
        G.fa = a; ctx.globalAlpha = a;
        this.render();
        ctx.restore(); G.fa = 1; G.ox = G.oy = 0;
    }
}
// orient a local frame at (x,y) with x-axis at angle `ang`, and local +y along `want` (flip if needed)
function frame(ctx, x, y, ang, want) {
    ctx.translate(x, y); ctx.rotate(ang);
    if (want && (-Math.sin(ang) * want[0] + Math.cos(ang) * want[1]) < 0) ctx.scale(1, -1);
}
const flipOf = (ang, want) => (-Math.sin(ang) * want[0] + Math.cos(ang) * want[1]) < 0;
function bboxOf(P, pad) {
    let x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
    for (const [x, y] of P) { if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y; }
    return [Math.floor(x0 - pad), Math.floor(y0 - pad), Math.ceil(x1 - x0 + pad * 2), Math.ceil(y1 - y0 + pad * 2)];
}
function clipToScreen(G, b) {
    const x0 = Math.max(b[0], -40), y0 = Math.max(b[1], -40), x1 = Math.min(b[0] + b[2], G.W + 40), y1 = Math.min(b[1] + b[3], G.H + 40);
    return [x0, y0, Math.max(1, x1 - x0), Math.max(1, y1 - y0)];
}
// ===================================================================================== actin filament
// F-actin: rise 2.76 nm and twist -166.6 deg per subunit (left-handed 1-start helix), which makes two
// right-handed long-pitch strands crossing every ~13 subunits (~36 nm). Subunit centres sit ~1.5 nm off axis,
// so the filament is ~7-8 nm wide. Depth below uses a right-handed frame (x right, y down, z away from viewer).
const RISE = 2.76, TWIST = (-166.6 * Math.PI) / 180, RAD = 1.5;
// drawn extents (nm) used for exclusion: a subunit sprite reaches 2.8 nm from its centre, capping protein 3.4 nm,
// Arp2/3 7.7 nm (its centre sits 6.5 nm behind the daughter's first subunit), CapZ 0.7 subunits past the last one
export const ACT_R = { sub: 2.8, cap: 3.4, arp: 7.7, arpX: -6.5, capX: 0.7 };
export class Filament {
    // opt.clear(x, y, r): is a disc of radius r px at (x, y) free of the membrane? Growth stops (and CapZ caps the
    // barbed end) at the last subunit for which every subunit and the cap would still be clear, so no part of the
    // filament ever reaches into the bilayer. A filament that would start already too long is cut to that length.
    constructor(G, x, y, ang, n0, opt = {}) {
        this.G = G; this.x = x; this.y = y; this.ang = ang; this.k = G.k;
        this.i0 = 0; this.ph = G.r() * TAU;
        this.kOn = opt.kOn ?? 0.45; this.kOff = opt.kOff ?? 0.42;
        this.free = -1; this.drift = opt.drift ?? [0, 0]; this.lat = opt.lat ?? 1;
        this.iMax = this.maxLen(opt.maxPx ?? 900, opt.clear);
        if (opt.arp && !opt.clear(...this.localToScreen(ACT_R.arpX * this.k, 0), ACT_R.arp * this.k)) this.iMax = 0;
        n0 = Math.min(n0, this.iMax); this.i1 = n0; this.ok = n0 >= 3;
        this.born = new Float32Array(512); for (let i = 0; i < n0; i++) this.born[i & 511] = G.time - (n0 - i) * 2.2 - 60 * (opt.old ?? 1);
        this.capped = n0 >= this.iMax; this.leaving = [];
        // Arp2/3 daughters remember their mother and the mother subunit they are bound to
        this.arp = opt.arp ?? false; this.mother = opt.mother ?? null; this.mi = opt.mi ?? -1;
        this.target = opt.target ?? (n0 > 20 ? 0 : 45 + ((G.r() * 50) | 0)); // young filaments elongate before they start to treadmill
    }
    maxLen(maxPx, clear) {
        const G = this.G, k = this.k, inView = (px, py) => !(px < -60 || py < -60 || px > G.W + 60 || py > G.H + 60 || G.h.focusFalloff(px, py, 70) > 0);
        let best = 0;
        for (let n = 1; n * RISE * k < maxPx && n < 500; n++) {
            const [sx, sy] = this.toScreen(n - 1); // newest subunit, at its real off-axis position
            if (!inView(sx, sy) || (clear && !clear(sx, sy, ACT_R.sub * k))) break;
            const [cx, cy] = this.localToScreen((n - 1 + ACT_R.capX) * RISE * k, 0);
            if (!clear || clear(cx, cy, ACT_R.cap * k)) best = n;
        }
        return best;
    }
    get alive() { return this.free >= 0 ? this.free < 4 : (this.i1 - this.i0 > 1 || this.leaving.length > 0); }
    get n() { return this.i1 - this.i0; }
    debranch() { if (this.free < 0) { this.free = 0; this.mother = null; } }
    step(dt, rate = 1) {
        const G = this.G;
        if (this.free >= 0) { this.free += dt; return; } // a debranched daughter drifts off out of the section and fades
        if (!this.capped && G.r() < this.kOn * rate * dt * (this.target ? 1.6 : 1)) {
            this.i1++; this.born[this.i1 - 1 & 511] = G.time;
            if (this.i1 >= this.iMax) this.capped = true;
        }
        if (this.target && this.i1 - this.i0 >= this.target) this.target = 0;
        const off = this.target ? 0.08 : 1;
        // Arp2/3 caps the daughter's pointed end, so a branch loses no subunits until it debranches
        if (!this.arp && this.i1 - this.i0 > 1 && G.r() < this.kOff * off * rate * dt) {
            // a departing subunit drifts back along the axis and away from the membrane side
            if (this.leaving.length < 6) this.leaving.push({ i: this.i0, t: 0, a: 0.3 + G.r() * (Math.PI - 0.6) }); this.i0++;
        }
        for (const l of this.leaving) l.t += dt;
        if (this.leaving.length && this.leaving[0].t > 3) this.leaving.shift();
    }
    pos(i) { const k = this.k, phi = i * TWIST + this.ph; return [i * RISE * k, RAD * k * Math.cos(phi), Math.sin(phi)]; }
    localToScreen(lx, ly) { const c = Math.cos(this.ang), s = Math.sin(this.ang), o = this.free > 0 ? this.free : 0; return [this.x + lx * c - ly * s + this.drift[0] * o, this.y + lx * s + ly * c + this.drift[1] * o]; }
    toScreen(i) { const [lx, ly] = this.pos(i); return this.localToScreen(lx, ly); }
    leavePos(l) { const k = this.k, [lx, ly] = this.pos(l.i), e = l.t / 3; return [lx - e * 9 * k + Math.cos(l.a) * e * 8 * k, ly + this.lat * Math.sin(l.a) * e * 10 * k, 1 - e]; }
    /** every drawn part with its exclusion radius (px), in screen space: for offline checks. */
    parts() {
        const k = this.k, P = [];
        for (let i = this.i0; i < this.i1; i++) P.push([...this.toScreen(i), ACT_R.sub * k, 'sub']);
        for (const l of this.leaving) { const [lx, ly] = this.leavePos(l); P.push([...this.localToScreen(lx, ly), ACT_R.sub * k, 'leave']); }
        if (this.capped) P.push([...this.localToScreen((this.i1 - 1 + ACT_R.capX) * RISE * k, 0), ACT_R.cap * k, 'cap']);
        if (this.arp) P.push([...this.localToScreen(ACT_R.arpX * k, 0), ACT_R.arp * k, 'arp']);
        return P;
    }
    render() {
        const G = this.G, ctx = G.ctx, S = G.S, k = this.k, t = G.time, fa = G.fa;
        if (this.free >= 0) G.fa = fa * (1 - sm(clamp(this.free / 4, 0, 1)));
        const o = this.free > 0 ? this.free : 0;
        ctx.save(); ctx.translate(this.x + this.drift[0] * o, this.y + this.drift[1] * o); ctx.rotate(this.ang);
        const lo = this.i0, hi = this.i1;
        for (let pass = 0; pass < 3; pass++) {
            for (let i = lo; i < hi; i++) {
                const phi = i * TWIST + this.ph, z = -Math.sin(phi), d = z < -0.35 ? 0 : z < 0.35 ? 1 : 2; // z toward viewer
                if (d !== pass) continue;
                const age = t - this.born[i & 511], st = age < 6 ? 0 : age < 70 ? 1 : 2;
                const sc = i === hi - 1 ? clamp(age / 0.6, 0.2, 1) : 1;
                spr(G, S.act[st][d], i * RISE * k, RAD * k * Math.cos(phi) - (1 - sc) * 4 * k, 1, sc);
            }
        }
        for (const l of this.leaving) { const [lx, ly, a] = this.leavePos(l); spr(G, S.act[2][1], lx, ly, a); }
        if (this.arp) spr(G, S.arp, ACT_R.arpX * k, 0);
        if (this.capped) spr(G, S.capz, (hi - 1 + ACT_R.capX) * RISE * k, 0);
        ctx.restore(); G.fa = fa; ctx.globalAlpha = fa;
    }
}

// ===================================================================================== plasma membrane + cortex
export class PlasmaMembrane extends Feat {
    constructor(G, e) {
        const em = edgeMap(G, e);
        super(G, EDGE_ZONES[e], em.out);
        const k = G.k, L = em.L, s = this.seed;
        this.kind = 'pm';
        const d0 = clamp(em.budget * 0.24, 40, 70);
        const P = [];
        this.memV = u => d0 + 14 * fbm(u / 520, s) + 5 * vnoise(u / 140, s + 3);
        for (let u = -60; u <= L + 60; u += 14) P.push(em.pt(u, this.memV(u)));
        // (u, depth) of a screen point, used to keep the cortex on the cytoplasmic side of the bilayer
        const W = G.W, H = G.H;
        this.uv = e === 'B' ? (x, y) => [x, H - y] : e === 'T' ? (x, y) => [x, y] : e === 'L' ? (x, y) => [y, x] : (x, y) => [y, W - x];
        // a disc of radius r is clear when it stays entirely on the cytoplasmic side of the drawn bilayer
        // (5.6 nm outline, so 2.8 nm half-thickness) plus a 0.5 nm gap
        this.clear = (x, y, r) => this.below(x, y) >= r + 3.3 * k && this.protD.every(q => Math.hypot(x - q[0], y - q[1]) >= r + q[2]);
        this.P = resample(P, 4); const N = normals(this.P);
        const mid = N[N.length >> 1], sg = mid[0] * em.out[0] + mid[1] * em.out[1] > 0 ? 1 : -1;
        this.N = N.map(n => [n[0] * sg, n[1] * sg]);
        this.C = cumlen(this.P);
        // membrane proteins along the bilayer
        this.prots = []; let a = 30 + G.r() * 60;
        while (a < this.C[this.C.length - 1] - 20) { this.prots.push({ s: a, kind: (G.r() * 3) | 0, seed: (G.r() * 99) | 0 }); a += 80 + G.r() * 150; }
        // integral proteins bulge up to ~5 nm into the cytoplasm: the cortex keeps clear of them too
        this.protD = this.prots.map(q => { const [px, py] = at(this.P, this.C, q.s); return [px, py, [5.0, 4.6, 5.6][q.kind] * k]; });
        this.glyc = []; for (let a2 = 20; a2 < this.C[this.C.length - 1]; a2 += 22 + G.r() * 40) this.glyc.push({ s: a2, seed: (G.r() * 999) | 0 });
        const outer = offset(this.P, this.N, 400), inner = offset(this.P, this.N, -(70 * k)); // the extracellular side up to the screen edge
        this.bb = clipToScreen(G, bboxOf([...outer, ...inner], 10));
        this.layer = null;
        // cortical actin just inside the membrane
        this.em = em; this.d0 = d0; this.fils = [];
        for (let i = 0; i < 3; i++) this.addFil(i, true);
        this.branchT = expo(G.r, 40);
    }
    /** perpendicular distance (px) of a point below the membrane midline; negative = outside the cell. */
    below(x, y) {
        const [u, v] = this.uv(x, y), sl = (this.memV(u + 2) - this.memV(u - 2)) / 4;
        return (v - this.memV(u)) / Math.sqrt(1 + sl * sl);
    }
    // the side of a filament's local frame that faces the cytoplasm (+1: local +y)
    latOf(ang) { const o = this.em.out; return (-Math.sin(ang) * -o[0] + Math.cos(ang) * -o[1]) >= 0 ? 1 : -1; }
    addFil(i, init) {
        const G = this.G, k = G.k, em = this.em, L = em.L;
        const dirSign = G.r() < 0.5 ? 1 : -1, u0 = dirSign > 0 ? -30 + G.r() * L * 0.4 : L + 30 - G.r() * L * 0.4;
        const depth = this.memV(u0) + (9 + i * 11 + G.r() * 5) * k;
        if (depth > em.budget - 60) return;
        const [x, y] = em.pt(u0, depth), [x2, y2] = em.pt(u0 + dirSign * 100, depth + (G.r() - 0.5) * 22);
        const ang = Math.atan2(y2 - y, x2 - x);
        const n0 = init ? 60 + ((G.r() * 80) | 0) : 3;
        const f = new Filament(G, x, y, ang, n0, { clear: this.clear, maxPx: L * 0.85, kOn: 0.5 + G.r() * 0.2, kOff: 0.42 + G.r() * 0.15, old: init ? 1 : 0, lat: this.latOf(ang) });
        if (f.ok) this.fils.push(f); // a filament with no room at all is not made
    }
    step(dt) {
        const G = this.G, k = G.k, rate = 1 + 0.35 * G.h.drift('pmActin' + this.seed, 120);
        for (const f of this.fils) {
            // a branch stays only while its mother (and the mother subunit it sits on) is still there; otherwise,
            // or spontaneously (cofilin, GMF; not drawn), it debranches
            if (f.arp && f.free < 0) {
                const m = f.mother;
                if (!m || m.free >= 0 || !this.fils.includes(m) || f.mi < m.i0 || f.mi >= m.i1 || G.r() < dt / 160) f.debranch();
            }
            f.step(dt, rate);
        }
        this.fils = this.fils.filter(f => f.alive);
        if (this.fils.filter(f => f.free < 0).length < 3 && G.r() < dt / 12) this.addFil((G.r() * 3) | 0, false);
        this.branchT -= dt;
        if (this.branchT < 0 && this.fils.length < 6) {
            this.branchT = expo(G.r, 50);
            const m = this.fils[(G.r() * this.fils.length) | 0];
            if (m && m.free < 0 && m.n > 20) {
                // Arp2/3 binds the side of a mother filament and nucleates a daughter at ~70 degrees; in the cortex the
                // daughters' barbed ends grow toward the membrane, where capping protein eventually stops them
                const i = Math.max(m.i0 + 4, m.i1 - 8 - ((G.r() * 20) | 0)), [mx, my] = m.localToScreen(i * RISE * k, 0), o = this.em.out;
                const a1 = m.ang + (70 * Math.PI) / 180, a2 = m.ang - (70 * Math.PI) / 180;
                const ang = Math.cos(a1) * o[0] + Math.sin(a1) * o[1] > Math.cos(a2) * o[0] + Math.sin(a2) * o[1] ? a1 : a2;
                // the daughter's first subunit sits beyond Arp2/3, which itself rests against the mother's side
                const D = 14 * k, x = mx + Math.cos(ang) * D, y = my + Math.sin(ang) * D;
                const f = new Filament(G, x, y, ang, 2, { arp: true, mother: m, mi: i, clear: this.clear, maxPx: 260, kOn: 0.55, kOff: 0.3, old: 0, lat: this.latOf(ang), drift: [-o[0] * 3 * k, -o[1] * 3 * k] });
                if (f.iMax >= 6) this.fils.push(f);
            }
        }
    }
    paint(x) {
        const G = this.G, k = G.k, P = this.P, N = this.N, C = this.C;
        // extracellular side
        const far = offset(P, N, 400);
        x.beginPath(); x.moveTo(P[0][0], P[0][1]); for (const q of P) x.lineTo(q[0], q[1]);
        for (let i = far.length - 1; i >= 0; i--) x.lineTo(far[i][0], far[i][1]); x.closePath();
        x.fillStyle = col2(G, 'outside'); x.fill();
        for (const g of this.glyc) { const [px, py, a] = at(P, C, g.s); x.save(); frame(x, px, py, a, this.Nat(g.s)); glycan(G, x, k, 0, 2.6 * k, (hash(g.seed) - 0.5) * 0.6, g.seed); x.restore(); }
        bilayer(G, x, P, k);
        for (const p of this.prots) { const [px, py, a] = at(P, C, p.s); x.save(); frame(x, px, py, a, this.Nat(p.s)); membraneProtein(G, x, k, p.kind, p.seed); x.restore(); }
    }
    Nat(s) { const i = clamp(Math.round(s / 4), 0, this.N.length - 1); return this.N[i]; }
    render() {
        if (!this.layer) this.layer = layer(this.G, ...this.bb, x => this.paint(x));
        this.layer.draw();
        for (const f of this.fils) f.render(); // drawn over the membrane layer: nothing may hide behind it
    }
}
const col2 = (G, n, a = 1) => css(rgb(G, n), a);
// radius (nm) of a folded globular domain of n residues: sphere at protein density (~0.066 M^1/3 nm) plus hydration
const protR = n => 0.37 * Math.cbrt(n);
const TUN = 35; // residues held in the ribosome exit tunnel

// ===================================================================================== microtubule + kinesins
export class Microtubule extends Feat {
    constructor(G, e, side) {
        const em = edgeMap(G, e), Z = EDGE_ZONES[e];
        super(G, side ? [Z[2], Z[1]] : [Z[0], Z[1]], em.out);
        this.kind = 'mt';
        const k = G.k, L = em.L, R = 12.5 * k; this.R = R; this.k = k;
        const v = clamp(em.budget - 62 - R, 50, 290);
        const uA = side ? L + 90 : -90, uMax = side ? L * 0.24 : L * 0.76, uMin = side ? L * 0.6 : L * 0.4;
        const A = em.pt(uA, v + (G.r() - 0.5) * 36), Bp = em.pt(uMax, v + (G.r() - 0.5) * 36);
        this.O = A; this.ang = Math.atan2(Bp[1] - A[1], Bp[0] - A[0]);
        this.flip = flipOf(this.ang, em.out);
        const c = Math.cos(this.ang), s = Math.sin(this.ang);
        this.dir = [c, s]; this.nrm = this.flip ? [s, -c] : [-s, c];
        this.lenMax = Math.hypot(Bp[0] - A[0], Bp[1] - A[1]); this.lenMin = Math.abs(uMin - uA);
        this.len = lerp(this.lenMin, this.lenMax, 0.45 + G.r() * 0.5); this.grow = true;
        // protofilament rows facing us: 13 pf, B-lattice (0.92 nm rise per pf), seam hidden at the back
        this.rows = [];
        // 13 protofilaments (centres ~10 nm off axis), each shifted 0.92 nm from its neighbour (3 x 4 nm / 13): a
        // left-handed 3-start B-lattice. Going once round leaves a 12 nm (1.5-dimer) mismatch, the seam, placed at the
        // back. The sign of the shift depends on whether this frame is mirrored, so the lattice is left-handed on screen.
        const hand = this.flip ? 1 : -1;
        this.pfs = [];
        for (let j = 0; j < 13; j++) {
            const th = (j / 13) * TAU + Math.PI + 0.24, cc = Math.cos(th);
            const pf = { j, sn: Math.sin(th), y: 10.2 * Math.sin(th) * k, h: Math.max(1.4, 4.9 * Math.abs(cc) * k), c: cc, off: hand * j * 0.923 * k, rag: G.r() };
            this.pfs.push(pf); if (cc >= 0.06) this.rows.push(pf);
        }
        this.rows.sort((a, b) => a.c - b.c); this.pfs.sort((a, b) => a.c - b.c);
        this.flakes = [];
        this.strip = null; this.kins = []; this.spawnT = 2;
        { // footprint for the director's overlap test: the tube plus the side where cargo hangs
            const B = [A, Bp].flatMap(p0 => [-R - 6 * k, R + 110 * k].map(d => [p0[0] + this.nrm[0] * d, p0[1] + this.nrm[1] * d]));
            this.box = clipToScreen(G, bboxOf(B, 0));
        }
        this.curl = 0;
        for (let i = 0; i < 2; i++) this.addKin(Math.floor((this.lenMin * (0.25 + i * 0.4)) / (8 * k)));
    }
    toScreen(x, y) { return [this.O[0] + x * this.dir[0] + y * this.nrm[0], this.O[1] + x * this.dir[1] + y * this.nrm[1]]; }
    extras() { const k = this.k; return this.kins.map(q => [...this.toScreen(q.x, q.y), (q.Rv + 3) * k]); }
    siteX(n) { return (n * 8 + 6) * this.k; }
    addKin(n) {
        const G = this.G, k = this.k, Rv = 20 + G.r() * 10; // 40-60 nm transport vesicles
        const kin = { b: 0.5 + G.r() * 0.35, n, ph: 0, t: 0.5 + G.r(), s: 0, Rv, v: (G.r() * 4) | 0, seed: (G.r() * 999) | 0, det: -1, spd: 0.8 + G.r() * 0.5, spr: null };
        const [tx, ty] = this.cargoTarget(kin); kin.x = tx; kin.y = ty;
        this.kins.push(kin);
    }
    // Head and neck positions for a motor: the bound head sits on site n+1; the other head is on site n, or swings
    // over its partner to site n+2 during a step. Each head's neck linker (~14 residues, ~5 nm contour) runs to the
    // junction where the coiled coil starts, which stays midway between the two linker ends, so neither linker is
    // ever stretched beyond about 4.2 nm.
    kinGeom(q) {
        const k = this.k, hy = this.R + 2.3 * k, xa = this.siteX(q.n), xb = this.siteX(q.n + 1);
        let sx = xa, sy = hy, bx = xb, by = hy;
        if (q.ph === 1) { const e = sm(clamp(q.s, 0, 1)); sx = lerp(xa, this.siteX(q.n + 2), e); sy = hy + Math.sin(e * Math.PI) * 6 * k; }
        if (q.det >= 0) { sx = xa + q.det * 1.5 * k; sy = hy + q.det * 5 * k + 3 * k; bx = xb + q.det * 1.5 * k; by = hy + q.det * 5 * k + 1 * k; }
        const ax = sx, ay = sy + 1.5 * k, cx = bx, cy = by + 1.5 * k;
        return { sx, sy, bx, by, ax, ay, cx, cy, nx: (ax + cx) / 2, ny: (ay + cy) / 2 + 0.8 * k };
    }
    cargoTarget(kin) {
        const k = this.k, b = kin.b, Ls = 46 * k, nx = this.kinGeom(kin).nx, ny = this.R + 4.5 * k;
        const d = Ls + kin.Rv * k;
        return [nx - Math.cos(b) * d, ny + Math.sin(b) * d];
    }
    step(dt) {
        const G = this.G, k = this.k;
        // dynamic instability at the plus end: slow growth, rare catastrophe, faster shrinkage, rescue
        // (shrinkage ~2x growth, as in cells; both ~100x slower than real)
        if (this.grow) { this.len += 1.5 * k * dt; if (this.len >= this.lenMax || G.r() < dt / 160) this.grow = false; }
        else {
            this.len -= 3.4 * k * dt;
            if (this.len <= this.lenMin || G.r() < dt / 22) { // rescue: the peeled protofilament curls break off as oligomers
                this.grow = true;
                if (this.curl > 0.05 && this.flakes.length < 3) this.flakes.push({ len: this.len, curl: this.curl, t: 0 });
                this.curl = 0;
            }
        }
        if (!this.grow) this.curl = clamp(this.curl + dt / 1.5, 0, 1);
        for (const fl of this.flakes) fl.t += dt;
        this.flakes = this.flakes.filter(fl => fl.t < 3);
        this.spawnT -= dt;
        const attached = this.kins.filter(q => q.det < 0);
        if (this.spawnT < 0) {
            this.spawnT = expo(G.r, 20);
            if (attached.length < 3 && !attached.some(q => q.n < 12)) this.addKin(1);
        }
        const rate = 1 + 0.3 * G.h.drift('kin' + this.seed, 70);
        for (const q of this.kins) {
            if (q.det >= 0) { q.det += dt; q.y += 5 * k * dt; q.x += 1.5 * k * dt; continue; }
            if (q.ph === 0) {
                q.t -= dt * rate * q.spd;
                if (q.t <= 0) {
                    const [tx, ty] = this.cargoTarget({ ...q, n: q.n + 1, ph: 0 }), gap = o => (q.Rv + o.Rv + 5) * k;
                    // cargoes cannot pass through each other: wait while the next step would bring this vesicle up against one ahead
                    const ahead = this.kins.some(o => o !== q && o.n >= q.n && o.det < 3 && (Math.hypot(tx - o.x, ty - o.y) < gap(o) || Math.hypot(tx - this.cargoTarget(o)[0], ty - this.cargoTarget(o)[1]) < gap(o)));
                    if (ahead) q.t = 0.4; else { q.ph = 1; q.s = 0; }
                }
            } else {
                q.s += dt / 0.5;
                if (q.s >= 1) { q.n++; q.ph = 0; q.s = 0; q.t = 0.35 + expo(G.r, 1.0); }
            }
            if (this.siteX(q.n + 1) > this.len - 3 * k) q.det = 0; // runs off the plus end
            const [tx, ty] = this.cargoTarget(q), f = 1 - Math.exp(-dt / 1.5);
            q.x += (tx - q.x) * f; q.y += (ty - q.y) * f;
        }
        this.kins = this.kins.filter(q => q.det < 5);
    }
    paintStrip(x, w) {
        const G = this.G, k = this.k, R = this.R, A = rgb(G, 'tubA'), Bc = rgb(G, 'tubB');
        x.fillStyle = css(rgb(G, 'mtCore')); x.beginPath(); x.roundRect(0, -R, w, 2 * R, 3 * k); x.fill();
        for (const r of this.rows) {
            const f = 0.66 + 0.34 * r.c;
            const ca = css(mixc(A, rgb(G, 'mtCore'), 1 - f)), cb = css(mixc(Bc, rgb(G, 'mtCore'), 1 - f));
            const ln = css(lnOf(G, A, 0.45), 0.55), rr = Math.min(1.4 * k, r.h / 2);
            const n = Math.ceil(w / (4 * k)) + 2;
            for (let m = -1; m < n; m++) {
                const xx = m * 4 * k + (((r.off % (8 * k)) + 8 * k) % (8 * k));
                x.beginPath(); x.roundRect(xx + 0.2 * k, r.y - r.h / 2, 3.6 * k, r.h, rr);
                x.fillStyle = (m & 1) ? cb : ca; x.fill();
                if (r.c > 0.3) { x.strokeStyle = ln; x.lineWidth = 0.6; x.stroke(); }
            }
        }
        // soft cylinder shading: darker toward the silhouette edges
        const g = x.createLinearGradient(0, -R, 0, R);
        g.addColorStop(0, css(rgb(G, 'mtCore'), 0.5)); g.addColorStop(0.22, css(rgb(G, 'mtCore'), 0)); g.addColorStop(0.7, css(rgb(G, 'cream'), 0.08)); g.addColorStop(1, css(rgb(G, 'mtCore'), 0.55));
        x.fillStyle = g; x.fillRect(0, -R, w, 2 * R);
    }
    render() {
        const G = this.G, ctx = G.ctx, k = this.k, R = this.R, S = G.S;
        if (!this.strip) { const w = this.lenMax + 30; this.strip = sprite(G, w, 2 * R + 6, x => { x.translate(-w / 2, 0); this.paintStrip(x, w); }, 0.3); this.stripW = w; }
        ctx.save(); frame(ctx, this.O[0], this.O[1], this.ang, null); if (this.flip) ctx.scale(1, -1);
        if (this.curl > 0.02) this.renderCurls(this.len, this.curl, false);
        for (const fl of this.flakes) this.renderCurls(fl.len, fl.curl, false, fl.t);
        if (this.grow) this.renderCurls(this.len, 1, false, -1, true);
        // lattice clipped to a ragged plus end (protofilaments end at slightly different lengths)
        const len = this.len, t = G.time;
        ctx.save();
        ctx.beginPath(); ctx.moveTo(-2, -R - 3);
        const rowsY = [...this.rows].sort((a, b) => a.y - b.y);
        for (const r of rowsY) { const ex = len + (r.rag - 0.5) * 7 * k * (this.grow ? 1 : 0.4); ctx.lineTo(ex, r.y - r.h / 2 - 0.5); ctx.lineTo(ex, r.y + r.h / 2 + 0.5); }
        ctx.lineTo(-2, R + 3); ctx.closePath(); ctx.clip();
        spr(G, this.strip, this.stripW / 2, 0);
        if (this.grow) { // GTP cap: freshly added tubulin near the plus end
            // (GTP / GDP-Pi tubulin, the region EB proteins track: tens to hundreds of nm in cells)
            const g = ctx.createLinearGradient(len - 60 * k, 0, len, 0);
            g.addColorStop(0, css(rgb(G, 'tubGTP'), 0)); g.addColorStop(0.6, css(rgb(G, 'tubGTP'), 0.3)); g.addColorStop(1, css(rgb(G, 'tubGTP'), 0.55));
            ctx.fillStyle = g; ctx.fillRect(len - 60 * k, -R, 64 * k, 2 * R);
        }
        ctx.restore();
        if (this.curl > 0.02) this.renderCurls(len, this.curl, true);
        for (const fl of this.flakes) this.renderCurls(fl.len, fl.curl, true, fl.t);
        if (this.grow) this.renderCurls(len, 1, true, -1, true);
        // kinesins: stalk, cargo, light chains, heads
        for (const q of this.kins) this.renderKin(q);
        ctx.restore();
    }
    // Ram's horns: each protofilament peels outward (radially) from its own ragged end, 0.225 rad (~13 deg) per
    // monomer, like GDP-tubulin rings (~14 dimers per turn). Seen from the side, a protofilament at angle th bends
    // in the image plane by sin(th): the silhouette ones curl fully in view, the front and back ones are
    // foreshortened stubs bending toward or away from us. front: draw the near half (after the lattice) or the far half.
    // Broken-off oligomers (age tb) drift outward and fade. flare: a growing end, where the protofilaments only splay
    // gently (1-3 monomers bending ~4 deg each beyond the ragged lattice end; McIntosh et al. 2018).
    renderCurls(len, curl, front, tb = -1, flare = false) {
        const G = this.G, ctx = G.ctx, k = this.k, A = rgb(G, 'tubA'), Bc = rgb(G, 'tubB');
        const fade = tb < 0 ? 1 : 1 - sm(clamp(tb / 3, 0, 1));
        for (const r of this.pfs) {
            if ((r.c >= 0) !== front) continue;
            const nb = flare ? 1 + Math.round(r.rag * 2) : Math.round((4 + r.rag * 5) * curl), da = flare ? 0.07 : 0.225;
            let X = len + (r.rag - 0.5) * 7 * k * (flare ? 1 : 0.4), Y = r.y, a = 0;
            if (tb >= 0) { X += tb * 2.5 * k; Y += r.sn * tb * 4 * k; }
            const h = Math.max(1.6 * k, r.h), col0 = 0.3 * (1 - Math.abs(r.c)) + (r.c < 0 ? 0.25 : 0);
            for (let m = 0; m < nb; m++) {
                const a1 = a + da, dX = Math.cos(a + da / 2) * 4 * k, dY = Math.sin(a + da / 2) * 4 * k * r.sn;
                const cx = X + dX / 2, cy = Y + dY / 2; X += dX; Y += dY; a = a1;
                const c = (m & 1) ? A : Bc;
                ctx.save(); ctx.translate(cx, cy); ctx.rotate(Math.atan2(dY, dX));
                ctx.globalAlpha = G.fa * fade * clamp(curl * 1.5 - m * 0.08, 0, 1);
                const w = Math.hypot(dX, dY);
                ctx.beginPath(); ctx.roundRect(-w / 2 + 0.1 * k, -h / 2, Math.max(0.8 * k, w - 0.2 * k), h, Math.min(1.3 * k, h / 2));
                ctx.fillStyle = css(mixc(c, rgb(G, 'mtCore'), col0)); ctx.fill(); ctx.strokeStyle = css(lnOf(G, c, 0.45), 0.55); ctx.lineWidth = 0.6; ctx.stroke();
                ctx.restore();
            }
        }
        ctx.globalAlpha = G.fa;
    }
    renderKin(q) {
        const G = this.G, ctx = G.ctx, k = this.k, R = this.R, S = G.S;
        const fade = q.det < 0 ? 1 : clamp(1 - q.det / 5, 0, 1);
        if (!q.spr) q.spr = vesicleSprite(G, q.Rv, q.v, q.seed);
        const bob = vnoise(G.time * 0.35, q.seed) * 2.2 * k, bob2 = vnoise(G.time * 0.3, q.seed + 7) * 2.2 * k;
        const vx = q.x + bob, vy = q.y + bob2;
        const g = this.kinGeom(q), nx = g.nx, ny = g.ny;
        // coiled-coil stalk from the neck to the cargo, sagging a little at the hinge
        const dx = vx - nx, dy = vy - ny, dl = Math.hypot(dx, dy) || 1, ex = vx - (dx / dl) * (q.Rv + 2.5) * k, ey = vy - (dy / dl) * (q.Rv + 2.5) * k;
        const kc = rgb(G, 'kin');
        ctx.globalAlpha = G.fa * fade; ctx.lineCap = 'round';
        ctx.beginPath(); ctx.moveTo(nx, ny); ctx.quadraticCurveTo((nx + ex) / 2 + 3 * k, (ny + ey) / 2 - 2 * k, ex, ey);
        ctx.strokeStyle = css(lnOf(G, kc, 0.45)); ctx.lineWidth = 2.1 * k; ctx.stroke();
        ctx.strokeStyle = css(kc); ctx.lineWidth = 1.3 * k; ctx.stroke();
        spr(G, q.spr, vx, vy, fade);
        spr(G, S.kLC, ex + (dx / dl) * 1.2 * k - (dy / dl) * 2.2 * k, ey + (dy / dl) * 1.2 * k + (dx / dl) * 2.2 * k, fade);
        spr(G, S.kLC, ex - (dy / dl) * -2.4 * k, ey + (dx / dl) * -2.4 * k, fade);
        // heads: one bound, the other bound (dwell) or swinging forward 16 nm over its partner
        const { sx, sy, bx, by } = g;
        ctx.globalAlpha = G.fa * fade; ctx.strokeStyle = css(lnOf(G, kc, 0.3)); ctx.lineWidth = 0.9 * k;
        ctx.beginPath(); ctx.moveTo(g.ax, g.ay); ctx.lineTo(nx, ny); ctx.lineTo(g.cx, g.cy); ctx.stroke();
        spr(G, S.kHead, sx, sy, fade * (q.ph === 1 ? 0.92 : 1));
        spr(G, S.kHead, bx, by, fade);
        const [gx, gy] = this.toScreen(vx, vy);
        G.glow(gx, gy, q.Rv * k * 3.2, 0.5 * fade);
        ctx.globalAlpha = G.fa;
    }
}

// ===================================================================================== mitochondrion edge
export class Mito extends Feat {
    constructor(G, e) {
        const em = edgeMap(G, e);
        super(G, [EDGE_ZONES[e][1]], em.out);
        this.kind = 'mito';
        const k = G.k, L = em.L, s = this.seed;
        const uc = L / 2 + (G.r() - 0.5) * L * 0.08, au = clamp(L * 0.27, 240, 420), Dp = clamp(em.budget - 58, 105, 80 * k);
        const OM = [];
        for (let u = uc - au - 40; u <= uc + au + 40; u += 5) {
            const q = (u - uc) / au, aq = Math.abs(q);
            const v = aq < 1 ? Dp * Math.pow(1 - q * q, 0.5) * (1 + 0.06 * vnoise(u / 90, s)) : -(aq - 1) * au * 1.2;
            OM.push(em.pt(u, v));
        }
        this.OM = resample(OM, 4);
        let N = normals(this.OM); const mid = N[N.length >> 1];
        if (mid[0] * em.out[0] + mid[1] * em.out[1] < 0) N = N.map(n => [-n[0], -n[1]]);
        this.N = N;
        const base = offset(this.OM, N, 12 * k), BC = cumlen(base), BN = N;
        // cristae: hairpin folds of the inner membrane reaching into the matrix
        const cr = [], Ltot = BC[BC.length - 1], hw = 10 * k;
        let sc = Ltot * 0.12 + G.r() * 30;
        while (sc < Ltot * 0.88) {
            const [px, py] = at(base, BC, sc), depth = this.depthOf(em, px, py);
            if (depth > 30 * k) {
                // cristae point along the dome normals and converge inward: keep every ridge (with its dimer, which
                // reaches ~22 nm from the tip) clear of its neighbours, shortening a crista or dropping it if needed
                const tilt = (G.r() - 0.5) * 0.35, ni = BN[Math.min(BN.length - 1, Math.round(sc / 4))];
                const n = [ni[0] * Math.cos(tilt) - ni[1] * Math.sin(tilt), ni[0] * Math.sin(tilt) + ni[1] * Math.cos(tilt)];
                const segD = (P, B, nn, L) => { const dx = nn[0] * L, dy = nn[1] * L, u = clamp(((P[0] - B[0]) * dx + (P[1] - B[1]) * dy) / (dx * dx + dy * dy), 0, 1); return Math.hypot(P[0] - B[0] - dx * u, P[1] - B[1] - dy * u); };
                const cross = (A, An, AL, B, Bn, BL) => { // do two crista axes intersect?
                    const d = An[0] * Bn[1] - An[1] * Bn[0]; if (Math.abs(d) < 1e-9) return false;
                    const wx = B[0] - A[0], wy = B[1] - A[1], ta = (wx * Bn[1] - wy * Bn[0]) / d, tb = (wx * An[1] - wy * An[0]) / d;
                    return ta >= 0 && ta <= AL && tb >= 0 && tb <= BL;
                };
                const clear = (T, Lc) => cr.every(o => Math.hypot(T[0] - o.T0[0], T[1] - o.T0[1]) > 62 * k
                    && segD(T, o.B0, o.n0, o.Lc) > 46 * k && segD(o.T0, [px, py], n, Lc) > 46 * k
                    && !cross([px, py], n, Lc, o.B0, o.n0, o.Lc) && segD([px, py], o.B0, o.n0, o.Lc) > 30 * k);
                // most cristae are drawn short so their tips and dimer rows stay in view; about a third run on deeper
                // into the matrix and off the screen edge, as lamellar cristae do
                const deep = G.r() < 0.35, L0 = deep ? depth + (8 + G.r() * 30) * k : Math.min((22 + G.r() * 18) * k, depth - 14 * k);
                for (let Lc = L0; Lc >= 18 * k; Lc -= 3 * k) {
                    const T = [px + n[0] * Lc, py + n[1] * Lc];
                    if (clear(T, Lc)) { cr.push({ s: sc, Lc, tilt, T0: T, B0: [px, py], n0: n }); break; }
                }
            }
            sc += (52 + G.r() * 26) * k;
        }
        const IM = []; let ci = 0;
        for (let i = 0; i < base.length; i++) {
            const s2 = BC[i], c = cr[ci];
            if (c && s2 >= c.s - hw) {
                const [cx, cy, ta] = at(base, BC, c.s), ni = BN[Math.min(BN.length - 1, Math.round(c.s / 4))];
                const ca = Math.cos(c.tilt), sa = Math.sin(c.tilt), n = [ni[0] * ca - ni[1] * sa, ni[0] * sa + ni[1] * ca], t0 = [Math.cos(ta), Math.sin(ta)];
                const t = (-n[1] * t0[0] + n[0] * t0[1]) >= 0 ? [-n[1], n[0]] : [n[1], -n[0]]; // across the crista, square to its axis (a tilted crista is not sheared)
                const sgn = (t[0] * n[1] - t[1] * n[0]) > 0 ? 1 : -1; // so the hairpin turns the right way round
                const A = [cx - t[0] * hw, cy - t[1] * hw], Bq = [cx + t[0] * hw, cy + t[1] * hw];
                for (let d = 0; d <= c.Lc; d += 4) IM.push([A[0] + n[0] * d, A[1] + n[1] * d]);
                const T = [cx + n[0] * c.Lc, cy + n[1] * c.Lc];
                for (let j = 1; j < 12; j++) { const a = Math.PI * (j / 12); IM.push([T[0] - t[0] * hw * Math.cos(a) + n[0] * hw * Math.sin(a), T[1] - t[1] * hw * Math.cos(a) + n[1] * hw * Math.sin(a)]); }
                for (let d = c.Lc; d >= 0; d -= 4) IM.push([Bq[0] + n[0] * d, Bq[1] + n[1] * d]);
                c.T = T; c.n = n; c.t = t; c.sgn = sgn;
                while (i < base.length && BC[i] <= c.s + hw) i++;
                i--; ci++;
                continue;
            }
            IM.push(base[i]);
        }
        this.IM = chaikin(IM, 2); this.cr = cr;
        // ATP synthase dimers at each crista rim, heads splayed ~86 degrees apart into the matrix
        this.atp = [];
        for (const c of cr) {
            const axA = Math.atan2(c.n[1], c.n[0]);
            // dimer rows run along the crista ridge; seen end-on each dimer is a V with rotor axes 86 degrees apart,
            // F1 heads ~28 nm apart, both peripheral stalks on the inner side next to the dimer interface. The c-rings
            // sit 0.65 rad round the 10 nm tip radius (~12 nm apart), so the two a-subunits stay apart in the membrane
            // with a gap at the apex for subunits e/g; each rotor axis tips a further 0.1 rad outward.
            for (const sgn of [-1, 1]) {
                const pa = axA + sgn * 0.65, a = axA + sgn * 0.75, px = c.T[0] + Math.cos(pa) * hw, py = c.T[1] + Math.sin(pa) * hw;
                this.atp.push({ x: px, y: py, a, mir: sgn > 0, phi: G.r() * TAU, tgt: 0, dw: G.r() * 2, made: 0 });
            }
            for (const q of this.atp.slice(-2)) q.tgt = q.phi;
        }
        // sit each c-ring exactly on the drawn (smoothed) inner membrane
        for (const q of this.atp) { let b = null, bd = 1e9; for (const P of this.IM) { const d = Math.hypot(P[0] - q.x, P[1] - q.y); if (d < bd) { bd = d; b = P; } } if (b) { q.x = b[0]; q.y = b[1]; } }
        this.sparks = [];
        this.em = em;
        this.bb = clipToScreen(G, bboxOf(this.OM.filter(q => q[0] > -50 && q[1] > -50 && q[0] < G.W + 50 && q[1] < G.H + 50).concat([em.pt(uc - au - 40, -20), em.pt(uc + au + 40, -20)]), 30));
        this.layer = null;
    }
    depthOf(em, x, y) { return em.out[1] ? (em.out[1] > 0 ? this.G.H - y : y) : (em.out[0] > 0 ? this.G.W - x : x); }
    step(dt) {
        const G = this.G, k = G.k, mean = 0.85 - 0.35 * G.h.drift('atp' + this.seed, 90);
        for (const q of this.atp) {
            q.dw -= dt;
            if (q.dw <= 0) { q.tgt += TAU / 8; q.dw = 0.22 + expo(G.r, mean); } // one proton through the a-subunit: one c-subunit step (c8)
            q.phi += (q.tgt - q.phi) * (1 - Math.exp(-dt / 0.09));
            const made = Math.floor(q.phi / (TAU / 3)); // 120 degrees of gamma per ATP: 8/3 protons each
            if (made > q.made) {
                if (q.made && this.sparks.length < 40) {
                    const d = 12 * k, c = Math.cos(q.a), s = Math.sin(q.a), side = (G.r() - 0.5) * 9 * k;
                    this.sparks.push({ x: q.x + c * d - s * side, y: q.y + s * d + c * side, vx: c * 4 * k + (G.r() - 0.5) * 3 * k, vy: s * 4 * k + (G.r() - 0.5) * 3 * k, t: 0, r: G.r() * TAU });
                }
                q.made = made;
            }
            if (q.phi > 1e4) { q.phi -= TAU * 600; q.tgt -= TAU * 600; q.made = Math.floor(q.phi / (TAU / 3)); }
        }
        for (const sp of this.sparks) { sp.t += dt; sp.x += sp.vx * dt; sp.y += sp.vy * dt; sp.vx *= Math.exp(-dt * 0.6); sp.vy *= Math.exp(-dt * 0.6); sp.r += dt; }
        this.sparks = this.sparks.filter(sp => sp.t < 5);
    }
    paint(x) {
        const G = this.G, k = G.k, em = this.em;
        // both membranes start and end beyond the screen edge: close each through the off-screen side of the organelle
        // (an inward offset would fold over itself on a narrow dome)
        const o = em.out, closeOut = P => { x.beginPath(); x.moveTo(P[0][0], P[0][1]); for (const q of P) x.lineTo(q[0], q[1]); const a = P[P.length - 1], b = P[0]; x.lineTo(a[0] + o[0] * 500, a[1] + o[1] * 500); x.lineTo(b[0] + o[0] * 500, b[1] + o[1] * 500); x.closePath(); };
        closeOut(this.OM); x.fillStyle = col2(G, 'ims'); x.fill();
        // matrix: inside the inner membrane
        x.save();
        closeOut(this.IM);
        x.fillStyle = col2(G, 'matrix'); x.fill(); x.clip();
        // matrix crowd: dense soluble enzymes
        const [bx, by, bw, bh] = this.bb;
        for (let i = 0; i < (bw * bh) / 900; i++) {
            const px = bx + hash(i, this.seed) * bw, py = by + hash(i, this.seed + 1) * bh, r = (1.6 + hash(i, this.seed + 2) * 2.4) * k;
            gouache(G, x, () => blobPath(x, px, py, r, r * 0.85, i, i, 0.12, 8), rgb(G, 'mcrowd', i), { lw: 0.6, hi: 0.3, hd: 0.4 * k, a: 0.8 });
        }
        x.restore();
        // cytochrome c in the intermembrane space
        const mid = offset(this.OM, this.N, 6 * k);
        for (let i = 8; i < mid.length; i += 9 + (hash(i, 5) * 14 | 0)) { x.fillStyle = col2(G, 'cytc'); x.beginPath(); x.arc(mid[i][0], mid[i][1], 1.7 * k, 0, TAU); x.fill(); }
        // ... and in the crista lumens, which are continuous with it through the crista junctions
        for (const c of this.cr) for (let j = 0; j < 3; j++) {
            const d = c.Lc * (0.2 + 0.3 * j + 0.1 * hash(j, c.s | 0)), off = (hash(j, (c.s | 0) + 1) - 0.5) * 6 * k;
            const px = c.T[0] - c.n[0] * d + c.t[0] * off, py = c.T[1] - c.n[1] * d + c.t[1] * off;
            x.fillStyle = col2(G, 'cytc'); x.beginPath(); x.arc(px, py, 1.7 * k, 0, TAU); x.fill();
        }
        bilayer(G, x, this.OM, k); bilayer(G, x, this.IM, k);
        // porins (VDAC) in the outer membrane
        const OC = cumlen(this.OM);
        for (let s = 20 + hash(this.seed) * 20; s < OC[OC.length - 1]; s += (8 + hash(s | 0, 3) * 10) * k) { // VDAC is the most abundant OM protein
            const [px, py, a] = at(this.OM, OC, s); x.save(); frame(x, px, py, a, null); membraneProtein(G, x, k, 3, (s | 0) % 4); x.restore();
        }
        // complex I on the flat crista membranes, arm into the matrix
        for (const c of this.cr) {
            if (c.Lc < 26 * k) continue;
            const side = hash(c.s | 0, 9) < 0.5 ? -1 : 1, hw = 10 * k;
            const px = c.T[0] - c.n[0] * c.Lc * 0.45 + c.t[0] * hw * side, py = c.T[1] - c.n[1] * c.Lc * 0.45 + c.t[1] * hw * side;
            x.save(); frame(x, px, py, Math.atan2(c.n[1], c.n[0]), [-side * c.t[0], -side * c.t[1]]); complexI(G, x, k, c.s | 0); x.restore();
        }
    }
    render() {
        const G = this.G, ctx = G.ctx, k = G.k, S = G.S;
        if (!this.layer) this.layer = layer(G, ...this.bb, x => this.paint(x));
        this.layer.draw();
        const cr = rgb(G, 'cring'), gm = rgb(G, 'gamma');
        for (const q of this.atp) {
            ctx.save(); ctx.translate(q.x, q.y); ctx.rotate(q.a + Math.PI / 2); if (q.mir) ctx.scale(-1, 1);
            // the C2 partner is a rotated (not mirrored) copy: mirror the stator, but turn the rotor with phase pi - phi
            // so both rings run the synthesis way (clockwise seen from the membrane/F0 side)
            const ph = q.mir ? Math.PI - q.phi : q.phi;
            // c8 ring turning in the membrane: stripes are the c-subunits
            ctx.fillStyle = css(mixc(cr, rgb(G, 'ink'), 0.35)); ctx.beginPath(); ctx.roundRect(-2.9 * k, -3.3 * k, 5.8 * k, 6.6 * k, 1.5 * k); ctx.fill();
            for (let i = 0; i < 8; i++) {
                const th = ph + (i * TAU) / 8, c = Math.cos(th);
                if (c <= 0.02) continue;
                const xs = 2.7 * Math.sin(th) * k, w = 1.95 * c * k;
                ctx.fillStyle = css(mixc(mixc(cr, rgb(G, 'ink'), 0.3), hiOf(G, cr, 0.3), c));
                ctx.beginPath(); ctx.roundRect(xs - w / 2, -3.1 * k, w, 6.2 * k, Math.min(w / 2, 1 * k)); ctx.fill();
            }
            // asymmetric central stalk (gamma with the delta/epsilon foot; mitochondrial delta = bacterial epsilon)
            const fx = 1.5 * Math.sin(ph) * k, fc = Math.cos(ph);
            const foot = () => { ctx.fillStyle = css(mixc(gm, hiOf(G, gm, 0.4), fc * 0.5 + 0.5)); ctx.beginPath(); ctx.ellipse(fx, -4.4 * k, 1.5 * k, 1.1 * k, 0, 0, TAU); ctx.fill(); };
            if (fc < 0) foot();
            ctx.strokeStyle = css(gm); ctx.lineWidth = 1.7 * k; ctx.lineCap = 'round';
            ctx.beginPath(); ctx.moveTo(0, -3.4 * k); ctx.quadraticCurveTo(0.9 * Math.sin(ph + 1) * k, -6.8 * k, 0.2 * k, -9.4 * k); ctx.stroke();
            if (fc >= 0) foot();
            spr(G, S.atp, 0, -8.5 * k);
            ctx.restore();
        }
        for (const sp of this.sparks) {
            const a = sm(clamp(sp.t / 0.4, 0, 1)) * (1 - sm(clamp((sp.t - 3) / 2, 0, 1)));
            ctx.save(); ctx.translate(sp.x, sp.y); ctx.rotate(sp.r); spr(G, S.atpMol, 0, 0, a); ctx.restore();
            G.glow(sp.x, sp.y, 8 * k, 0.6 * a);
        }
    }
}

// ===================================================================================== polysome
export class Polysome extends Feat {
    constructor(G, zone) {
        const sp = spotOf(G, zone);
        super(G, [zone], sp.out);
        this.kind = 'poly'; this.sp = sp;
        const k = G.k, s = this.seed;
        const rho = sp.rad * 0.6, a0 = G.r() * TAU, sweep = 3.4 + G.r() * 1.1, dirn = G.r() < 0.5 ? 1 : -1;
        const pts = [];
        for (let i = 0; i <= 60; i++) {
            const t = i / 60, a = a0 + dirn * sweep * t, r = rho * (0.82 + 0.36 * t) * (1 + 0.1 * vnoise(t * 5, s));
            pts.push([sp.cx + Math.cos(a) * r, sp.cy + Math.sin(a) * r]);
        }
        this.base = resample(pts, 6); this.BN = normals(this.base);
        this.Ltot = cumlen(this.base).at(-1);
        // ribosomes sit on the convex side so their exit tunnels (and new chains) face out
        const mi = this.base.length >> 1, mpt = this.base[mi];
        this.outN = (this.BN[mi][0] * (mpt[0] - sp.cx) + this.BN[mi][1] * (mpt[1] - sp.cy)) > 0 ? 1 : -1;
        this.cod = 1.3 * k; this.sStart = 18 * k; // ~0.43 nm/nt for a compact mRNA
        // One ORF, one protein: every ribosome on this mRNA makes the same chain of N residues, with the same domains.
        // The stop codon is exactly N codons after the start; the 3' UTR and poly(A) follow it.
        this.N = Math.max(8, Math.floor((this.Ltot - 50 * k - this.sStart) / this.cod));
        this.sStop = this.sStart + this.N * this.cod;
        this.spec = this.makeSpec(this.N);
        this.ribs = []; this.prods = []; this.initT = 4; this.nTerm = 0;
        // pre-populate elongating ribosomes at staggered codons; each holds exactly as many residues as it has decoded
        for (let s2 = this.sStop - 20 * k - G.r() * 30 * k; s2 > this.sStart + 10 * k; s2 -= (38 + G.r() * 40) * k) {
            const c = Math.floor((s2 - this.sStart) / this.cod), r = this.newRib(this.sStart + c * this.cod, 'elong');
            r.codons = c; this.updFold(r.fold, c - TUN, 0.9);
            this.ribs.push(r);
        }
        this.P = this.base; this.C = cumlen(this.base);
    }
    /** the protein this ORF encodes: an N-terminal tail, then folding domains (55-150 aa) joined by short linkers,
     *  then a C-terminal tail. A short ORF (under ~60 codons) makes a peptide with no domain at all. */
    makeSpec(N) {
        const G = this.G, doms = [];
        let pos = Math.min(N, 4 + ((G.r() * 16) | 0));
        while (N - pos >= 60) {
            const n = Math.min(N - pos, 55 + ((G.r() * 95) | 0));
            doms.push({ a: pos, b: pos + n, n, f: (G.r() * 6) | 0 }); pos += n + 3 + ((G.r() * 12) | 0);
        }
        return { N, doms };
    }
    // folding kinetics differ from ribosome to ribosome (the protein does not): a domain folds a random 3-40 s after
    // its last residue has left the tunnel, whether or not translation has finished by then
    newFold() { return this.spec.doms.map(() => ({ tEm: -1, lag: 3 + expo(this.G.r, 14), done: false })); }
    updFold(F, emerged, back = 0) {
        const t = this.G.time;
        this.spec.doms.forEach((d, j) => {
            const f = F[j];
            if (f.tEm < 0 && emerged >= d.b) f.tEm = t - (emerged - d.b) * back;
            if (!f.done && f.tEm >= 0 && t - f.tEm >= f.lag) f.done = true;
        });
    }
    extras() {
        const k = this.G.k, C = [];
        for (const r of this.ribs) { const F = this.ribFrame(r); C.push([F.x, F.y, 22 * k]); }
        // the nascent and released chains: every few backbone points, and every folded domain
        const add = g => { for (let i = 0; i < g.pts.length; i += 3) C.push([g.pts[i][0], g.pts[i][1], 1.6 * k]); for (const d of g.dom) C.push([d[0], d[1], d[2] + 0.5 * k]); };
        for (const r of this.ribs) if (r.st === 'elong') add(this.ribChain(r));
        for (const p of this.prods) add(this.prodChain(p));
        const [cx, cy] = this.P[0], [ex, ey] = this.P[this.P.length - 1]; C.push([cx, cy, 8 * k], [ex, ey, 8 * k]);
        return C;
    }
    newRib(s, st) { return { s, st, codons: 0, dw: 0.5, tr: -1, fold: this.newFold(), split: 0, join: st === 'elong' ? 1 : 0, seed: this.G.r() * 999 | 0 }; }
    step(dt) {
        const G = this.G, k = G.k, rate = 1 + 0.3 * G.h.drift('tl' + this.seed, 80);
        this.initT -= dt;
        if (this.initT < 0) {
            this.initT = expo(G.r, 16);
            if (!this.ribs.some(r => r.s < this.sStart + 34 * k) && this.ribs.length < 7) this.ribs.push(this.newRib(2 * k, 'scan'));
        }
        this.ribs.sort((a, b) => b.s - a.s);
        for (let i = 0; i < this.ribs.length; i++) {
            const r = this.ribs[i], ahead = this.ribs.slice(0, i).filter(o => o.st !== 'split').at(-1);
            if (r.st === 'scan') { // 43S scans 5'->3' for the start codon, then the 60S joins
                r.s += 3 * k * dt;
                if (r.s >= this.sStart) { r.s = this.sStart; r.st = 'elong'; r.join = 0; }
            } else if (r.st === 'elong') {
                r.join = Math.min(1, r.join + dt / 1.5);
                // one codon decoded per translocation: one residue added, one codon (1.3 nm) moved
                if (r.tr >= 0) { r.tr += dt / 0.2; if (r.tr >= 1) { r.tr = -1; r.codons++; r.s = this.sStart + r.codons * this.cod; r.dw = 0.2 + expo(G.r, 0.5); } }
                else {
                    r.dw -= dt * rate;
                    if (r.dw <= 0 && r.join >= 1) {
                        if (ahead && ahead.s - r.s < 32 * k) r.dw = 0.3; // queued behind the ribosome in front
                        else r.tr = 0;
                    }
                }
                this.updFold(r.fold, r.codons - TUN);
                if (r.codons >= this.N) { r.st = 'split'; r.split = 0; this.release(r); }
            } else r.split += dt;
        }
        this.ribs = this.ribs.filter(r => r.st !== 'split' || r.split < 5);
        for (const p of this.prods) {
            p.t += dt; p.x += p.vx * dt; p.y += p.vy * dt; p.vx += (G.r() - 0.5) * 3 * dt; p.vy += (G.r() - 0.5) * 3 * dt; p.rot += p.vr * dt;
            this.updFold(p.fold, this.N); // domains still unfolded at release fold afterwards, off the ribosome
        }
        this.prods = this.prods.filter(p => p.t < 16);
    }
    // termination: the whole polypeptide (all N residues, folded or not, including the ~35 that were in the tunnel)
    // leaves the ribosome, which then splits
    release(r) {
        const G = this.G, k = G.k, F = this.ribFrame(r), ex = this.exitOf(F);
        this.updFold(r.fold, this.N);
        this.prods.push({ x: ex[0], y: ex[1], ang: ex[2], n: this.N, fold: r.fold, seed: r.seed, vx: this.out[0] * 1.2 * k, vy: this.out[1] * 1.2 * k, t: 0, rot: 0, vr: (G.r() - 0.5) * 0.3 });
        r.fold = null; this.nTerm++;
    }
    curve() {
        const G = this.G, t = G.time, s = this.seed;
        return this.base.map((q, i) => { const w = 4 * vnoise(t / 16 + i * 0.09, s) ; return [q[0] + this.BN[i][0] * w, q[1] + this.BN[i][1] * w]; });
    }
    ribFrame(r) {
        const [x, y, a] = at(this.P, this.C, r.s + (r.tr >= 0 ? sm(r.tr) * this.cod : 0)); // sliding one codon during translocation
        const c = Math.cos(a), s = Math.sin(a), ny = this.outN > 0 ? [s, -c] : [-s, c]; // local +y points into the mRNA side; -y outward
        return { x, y, a, c, s, ny, flip: this.outN > 0 };
    }
    // the tunnel exit on the 60S solvent face (local (4.5, -17.4) nm) and the outward direction there
    exitOf(F) {
        const k = this.G.k, lx = 4.5 * k, ly = -17.4 * k;
        const x = F.x + lx * F.c + ly * F.ny[0], y = F.y + lx * F.s + ly * F.ny[1];
        return [x, y, Math.atan2(-F.ny[1], -F.ny[0])];
    }
    /** backbone and folded domains of a chain whose C-terminal end is at (x, y) heading along ang. Residues run from
     *  the anchor back to the N-terminus: unfolded stretches as a coil (~0.35 nm per residue of contour), folded
     *  domains as globules (radius 0.37 n^(1/3) nm) with the backbone running into and out of them. */
    chain(x, y, ang, n, fold, seed) {
        const k = this.G.k, t = this.G.time, doms = this.spec.doms, pts = [[x, y]], dom = [];
        const a0 = ang; let i = n;
        const go = d => { x += Math.cos(ang) * d; y += Math.sin(ang) * d; pts.push([x, y]); };
        const coil = lo => { // residues [lo, i): 3 per 1.05 nm segment, turning, with a weak drift outward
            for (let m = i; m > lo; m -= 3) {
                ang += vnoise(t * 0.25 + m * 0.18, seed) * 0.95 + Math.atan2(Math.sin(a0 - ang), Math.cos(a0 - ang)) * 0.08;
                go((1.05 * k * Math.min(3, m - lo)) / 3);
            }
        };
        for (let j = doms.length - 1; j >= 0; j--) {
            const d = doms[j];
            if (!fold || !fold[j].done || d.b > n) continue; // an unfolded domain is just more coil
            coil(d.b); i = d.a;
            const rr = protR(d.n) * k;
            ang += vnoise(t * 0.2 + j * 2.1, seed + 3) * 0.6;
            go(rr); dom.push([x, y, rr, d.f]); go(rr); // into the domain, through its centre, out the far side
        }
        coil(0);
        return { pts, dom };
    }
    ribChain(r) { const [x, y, a] = this.exitOf(this.ribFrame(r)); return this.chain(x, y, a, Math.max(0, r.codons - TUN), r.fold, r.seed); }
    prodChain(p) { return this.chain(p.x, p.y, p.ang + p.rot, p.n, p.fold, p.seed); }
    drawChain(g, a = 1) {
        const G = this.G, ctx = G.ctx, k = G.k, S = G.S;
        if (g.pts.length > 1) { ctx.globalAlpha = G.fa * a; linePath(ctx, g.pts); ctx.strokeStyle = col2(G, 'chain'); ctx.lineWidth = 0.95 * k; ctx.stroke(); ctx.globalAlpha = G.fa; }
        for (const d of g.dom) spr(G, S.fold[d[3]], d[0], d[1], a, d[2] / (2.7 * k));
    }
    render() {
        const G = this.G, ctx = G.ctx, k = G.k, S = G.S;
        this.P = this.curve(); this.C = cumlen(this.P);
        // mRNA: 5' cap ... coding sequence ... poly(A) with PABPs
        const mr = rgb(G, 'mrna');
        ctx.lineCap = 'round'; ctx.lineJoin = 'round';
        linePath(ctx, this.P); ctx.strokeStyle = css(lnOf(G, mr, 0.35)); ctx.lineWidth = 1.9 * k; ctx.stroke();
        ctx.strokeStyle = css(mr); ctx.lineWidth = 1.2 * k; ctx.stroke();
        ctx.setLineDash([0.45 * k, 0.75 * k]); ctx.strokeStyle = css(hiOf(G, mr, 0.45)); ctx.lineWidth = 0.6 * k; ctx.stroke(); ctx.setLineDash([]);
        const [cx, cy] = this.P[0]; { const [qx, qy] = this.P[1], a = Math.atan2(qy - cy, qx - cx); ctx.save(); ctx.translate(cx, cy); ctx.rotate(a); if (this.outN > 0) ctx.scale(1, -1); spr(G, S.eif4f, 1 * k, 2.5 * k); ctx.restore(); } // cap + eIF4F
        for (let s = this.Ltot - 38 * k; s < this.Ltot; s += 11.5 * k) { const [px, py] = at(this.P, this.C, s + 4 * k); gouache(G, ctx, () => blobPath(ctx, px, py, 2.7 * k, 2.3 * k, s, s | 0, 0.1, 8), rgb(G, 'pabp'), { lw: 0.6, hi: 0.4, hd: 0.4 * k }); } // PABP, one per ~27 nt of poly(A)
        for (const r of this.ribs) {
            const F = this.ribFrame(r);
            const st = r.st, sp = st === 'split' ? r.split : 0, fade = st === 'split' ? clamp(1 - sp / 5, 0, 1) : 1;
            if (st === 'elong') {
                const ch = this.ribChain(r);
                this.drawChain(ch);
                if (ch.pts.length > 2) { const e = ch.pts[1]; G.glow(e[0], e[1], 7 * k, 0.35); } // a glint where the new chain leaves the tunnel
            }
            ctx.save(); ctx.translate(F.x, F.y); ctx.rotate(F.a); if (F.flip) ctx.scale(1, -1);
            if (st === 'scan') { spr(G, S.r40, 0, 2.5 * k); spr(G, S.eif3, -2 * k, 13 * k); } // eIF3 (~800 kDa, ~15-20 nm, five-lobed) on the solvent face of the 40S
            else {
                const j = sm(r.join);
                spr(G, S.r60, -sp * 1.5 * k, -10 * k - (1 - j) * 10 * k - sp * 6 * k, fade * j);
                ctx.save(); ctx.translate(sp * 4 * k, sp * 2 * k); spr(G, S.r40, 0, 2.5 * k, fade); ctx.restore(); // (the ~9 deg ratchet turns about an axis normal to the interface, out of this plane: not drawn)
            }
            ctx.restore();
        }
        for (const p of this.prods) {
            const a = 1 - sm(clamp((p.t - 10) / 6, 0, 1)); // the chain was already in view on the ribosome
            G.glow(p.x, p.y, 9 * k, 0.5 * a * (1 - clamp(p.t / 12, 0, 1)));
            this.drawChain(this.prodChain(p), a);
        }
    }
}

// ===================================================================================== chaperonin corner (TRiC, prefoldin)
// TRiC/CCT: two back-to-back rings that open and close together (both lids shut in the same ATP-driven cycle), while
// each ring holds its own client or none, so occupancy can be asymmetric. A cycle starts once a client is bound; a
// cycle with both chambers empty is rare. Prefoldin reserves the open ring it heads for, so that ring cannot close
// before the hand-over.
export class Chaperonins extends Feat {
    constructor(G, zone) {
        const sp = spotOf(G, zone);
        super(G, [zone], sp.out);
        this.kind = 'tric';
        const k = G.k; this.sp = sp;
        const n = sp.rad > 150 ? 3 : 2, pos = [];
        for (let tries = 0; pos.length < n && tries < 80; tries++) {
            const a = G.r() * TAU, d = Math.sqrt(G.r()) * sp.rad * 0.62, x = sp.cx + Math.cos(a) * d, y = sp.cy + Math.sin(a) * d;
            if (pos.every(q => Math.hypot(q.x - x, q.y - y) > 30 * k) && G.h.focusFalloff(x, y, 90) === 0) pos.push({ x, y });
        }
        this.tr = pos.map(q => ({ ...q, ang: G.r() * TAU, o: 1, st: 'open', tm: 2 + G.r() * 14, res: -1, rings: [0, 1].map(() => ({ load: false })) }));
        this.pfd = { x: sp.cx + sp.out[0] * sp.rad * 0.4, y: sp.cy + sp.out[1] * sp.rad * 0.4, tgt: null, hold: 0, ang: 0, loads: 0, bad: 0 };
        this.prods = [];
    }
    extras() {
        const k = this.G.k, C = this.tr.map(t => [t.x, t.y, 11 * k]);
        C.push([this.pfd.x, this.pfd.y, 9 * k]);
        for (const q of this.prods) C.push([q.x, q.y, 5 * k]);
        return C;
    }
    step(dt) {
        const G = this.G, k = G.k;
        for (const t of this.tr) {
            t.tm -= dt;
            if (t.st === 'open') {
                if (t.tm <= 0 && t.res < 0) {
                    // clients also come from Hsp70 and straight from ribosomes (not drawn): usually into one ring,
                    // sometimes both; only ~5% of cycles run with both chambers empty
                    if (!t.rings.some(r => r.load) && G.r() > 0.05) { t.rings[(G.r() * 2) | 0].load = true; if (G.r() < 0.2) t.rings.forEach(r => (r.load = true)); }
                    t.st = 'closing';
                }
            } else if (t.st === 'closing') { t.o -= dt / 1.8; if (t.o <= 0) { t.o = 0; t.st = 'closed'; t.tm = 6 + G.r() * 6; } }
            else if (t.st === 'closed') { if (t.tm <= 0) t.st = 'opening'; }
            else {
                t.o += dt / 1.8;
                if (t.o >= 1) {
                    t.o = 1; t.st = 'open'; t.tm = 7 + G.r() * 14;
                    t.rings.forEach((r, ri) => {
                        if (r.load && this.prods.length < 8) {
                            const sg = ri ? 1 : -1, c = Math.cos(t.ang), s = Math.sin(t.ang), ly = sg * 9 * k;
                            this.prods.push({ x: t.x - ly * s, y: t.y + ly * c, vx: -s * sg * 2 * k, vy: c * sg * 2 * k, t: 0, rot: G.r() * TAU, kind: G.r() < 0.55 ? 0 : 1 }); // a folded actin or tubulin monomer
                        }
                        r.load = false;
                    });
                }
            }
        }
        // prefoldin wanders over to an empty open ring (reserving it), docks, hands over its client, and drifts off
        const p = this.pfd;
        if (!p.tgt && G.r() < dt / 8) {
            const t = this.tr[(G.r() * this.tr.length) | 0], free = t ? [0, 1].filter(ri => !t.rings[ri].load) : [];
            if (t && t.st === 'open' && t.res < 0 && free.length) { const ri = free[(G.r() * free.length) | 0]; p.tgt = { t, ri }; t.res = ri; }
        }
        if (p.tgt) {
            const { t, ri } = p.tgt, sg = ri ? 1 : -1, c = Math.cos(t.ang), s = Math.sin(t.ang), ly = sg * 16 * k;
            const tx = t.x - ly * s, ty = t.y + ly * c, f = 1 - Math.exp(-dt / 3);
            p.x += (tx - p.x) * f; p.y += (ty - p.y) * f;
            { let da = t.ang + (ri ? Math.PI : 0) - p.ang; da = Math.atan2(Math.sin(da), Math.cos(da)); p.ang += da * f; } // tentacles toward the open ring
            if (Math.hypot(tx - p.x, ty - p.y) < 2 * k) {
                p.hold += dt;
                if (p.hold > 2.5) {
                    if (t.st !== 'open' || t.o < 1) p.bad++; // (cannot happen while the ring is reserved; counted for offline checks)
                    t.rings[ri].load = true; t.res = -1; t.tm = Math.min(t.tm, 1 + G.r() * 2); p.loads++;
                    p.tgt = null; p.hold = 0; p.vx = this.sp.out[0] * 4 * k; p.vy = this.sp.out[1] * 4 * k;
                }
            }
        } else {
            const hx = this.sp.cx + this.sp.out[0] * this.sp.rad * 0.5, hy = this.sp.cy + this.sp.out[1] * this.sp.rad * 0.5;
            p.x += ((hx - p.x) * 0.05 + vnoise(G.time * 0.1, this.seed) * 2 * k) * dt; p.y += ((hy - p.y) * 0.05 + vnoise(G.time * 0.1, this.seed + 5) * 2 * k) * dt;
        }
        for (const q of this.prods) { q.t += dt; q.x += q.vx * dt; q.y += q.vy * dt; q.vx *= Math.exp(-dt * 0.3); q.vy *= Math.exp(-dt * 0.3); q.rot += dt * 0.2; }
        this.prods = this.prods.filter(q => q.t < 16);
    }
    render() {
        const G = this.G, ctx = G.ctx, k = G.k, S = G.S;
        for (const t of this.tr) {
            ctx.save(); ctx.translate(t.x, t.y); ctx.rotate(t.ang);
            const f = t.o * 5, i0 = Math.floor(f), fr = f - i0;
            for (let ri = 0; ri < 2; ri++) {
                if (t.o < 0.6 && t.rings[ri].load) { const sg = ri ? 1 : -1; G.glow(t.x - Math.sin(t.ang) * sg * 5 * k, t.y + Math.cos(t.ang) * sg * 5 * k, 14 * k, 0.4 * (1 - t.o / 0.6)); }
                ctx.save(); if (ri) ctx.scale(1, -1);
                spr(G, S.tric[i0], 0, -6 * k);
                if (fr > 0.02 && i0 < 5) spr(G, S.tric[i0 + 1], 0, -6 * k, fr);
                ctx.restore();
            }
            ctx.restore();
        }
        const p = this.pfd;
        ctx.save(); ctx.translate(p.x, p.y); ctx.rotate(p.ang); spr(G, S.pfd, 0, -2 * k); ctx.restore();
        for (const q of this.prods) {
            const a = sm(clamp(q.t / 0.8, 0, 1)) * (1 - sm(clamp((q.t - 10) / 6, 0, 1)));
            ctx.save(); ctx.translate(q.x, q.y); ctx.rotate(q.rot); spr(G, q.kind ? S.tubMono : S.actMono, 0, 0, a); ctx.restore();
        }
    }
}
