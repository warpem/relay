// folds.js — protein topologies in model space (Å) and sampling of the chain into yarn.
//
// Geometry conventions (all right-handed, never mirrored, so handedness survives to the screen):
//   model: x right, y up, z toward the viewer. Screen = (k·x, −k·y), depth = z (bigger = nearer).
//   α-helix: Cα radius 2.3 Å, rise 1.5 Å/residue, 3.6 residues/turn (pitch 5.4 Å), right-handed.
//   β-strand: rise 3.3 Å/residue, neighbouring strands 4.8 Å apart, sheets twisted left-handed
//   about the inter-strand axis (= the usual right-handed twist seen along the strands).
//   β-α-β crossovers are right-handed (helix on the side e_advance × e_strand).

export const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
export const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
export const mul = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
export const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
export const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
export const len = (a) => Math.hypot(a[0], a[1], a[2]);
export const norm = (a) => { const l = len(a) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };
const X = [1, 0, 0], Y = [0, 1, 0], Z = [0, 0, 1];
const DEG = Math.PI / 180;

/** Rodrigues rotation of v about unit axis k by angle a (right-hand rule). */
export function rot(v, k, a) {
    const c = Math.cos(a), s = Math.sin(a), d = dot(k, v), cr = cross(k, v);
    return [v[0] * c + cr[0] * s + k[0] * d * (1 - c), v[1] * c + cr[1] * s + k[1] * d * (1 - c), v[2] * c + cr[2] * s + k[2] * d * (1 - c)];
}
/** 3×3 rotation matrices as row arrays */
export function matAxis(k, a) { return [rot(X, k, a), rot(Y, k, a), rot(Z, k, a)].reduce((m, col, j) => { for (let i = 0; i < 3; i++) m[i][j] = col[i]; return m; }, [[0, 0, 0], [0, 0, 0], [0, 0, 0]]); }
export function matMul(A, B) { const C = [[0, 0, 0], [0, 0, 0], [0, 0, 0]]; for (let i = 0; i < 3; i++) for (let j = 0; j < 3; j++) C[i][j] = A[i][0] * B[0][j] + A[i][1] * B[1][j] + A[i][2] * B[2][j]; return C; }
export const apply = (M, v) => [M[0][0] * v[0] + M[0][1] * v[1] + M[0][2] * v[2], M[1][0] * v[0] + M[1][1] * v[1] + M[1][2] * v[2], M[2][0] * v[0] + M[2][1] * v[1] + M[2][2] * v[2]];

const HELIX_R = 2.3, HELIX_RISE = 1.5, HELIX_TURN = 2 * Math.PI / 3.6, STRAND_RISE = 3.3, SHEET_GAP = 4.8;
export const STRAND_W = 3.6;   // knitted ribbon width, Å

const ri = (rnd, a, b) => a + Math.floor(rnd() * (b - a + 1));
const rf = (rnd, a, b) => a + rnd() * (b - a);
const helix = (A, B) => ({ t: 'H', A, B });
const strand = (S, E, n) => ({ t: 'E', S, E, n });
const helixAlong = (c, d, nres) => { const h = d.map(v => v * nres * HELIX_RISE / 2); return helix(sub(c, h), add(c, h)); };
const strandAlong = (c, d, nres, n) => { const h = d.map(v => v * nres * STRAND_RISE / 2); return strand(sub(c, h), add(c, h), n); };

// ------------------------------------------------------------------ topologies
// Each returns { els, base }: base is a proper rotation giving a readable view (sheets within ~40° of face-on so
// the knitted ribbons keep their width; helix axes tilted 23–32° toward the viewer so the coils show their loops).
// The sketch adds a random spin about the view axis.

const tiltSheet = (rnd) => matMul(matAxis(Y, (rnd() < 0.5 ? 1 : -1) * rf(rnd, 0.12, 0.32)), matAxis(X, (rnd() < 0.5 ? 1 : -1) * rf(rnd, 0.42, 0.55)));
// α/β folds: seen from the sheet's far side, so the helices sit behind the knitted sheet and peek out past its edges
const tiltAB = (rnd) => matMul(matAxis(Y, Math.PI + (rnd() < 0.5 ? 1 : -1) * rf(rnd, 0.6, 0.7)), matAxis(X, (rnd() < 0.5 ? 1 : -1) * rf(rnd, 0.3, 0.4)));
// the knit (right) side of the fabric faces away from the helices; which face is 'right' is just a convention
const knitAway = (els) => { for (const el of els) if (el.t === 'E') el.n = mul(el.n, -1); return els; };

/** up-down helical bundle (3 or 4 helices) with a left-handed supercoil */
function bundle(rnd) {
    const nH = rnd() < 0.4 ? 3 : 4, rb = nH === 3 ? 6.1 : 7.2, sense = rnd() < 0.5 ? 1 : -1, phi0 = rnd() * 6.283;
    const els = [];
    for (let i = 0; i < nH; i++) {
        const phi = phi0 + sense * i * 2 * Math.PI / nH, er = [Math.cos(phi), 0, Math.sin(phi)];
        const line = norm(sub(Y, mul(cross(Y, er), 0.18)));        // left-handed supercoil tilt (~10°)
        const dir = i % 2 === 0 ? line : mul(line, -1);
        const c = add(mul(er, rb), [0, rf(rnd, -1.2, 1.2), 0]);
        els.push(helixAlong(c, dir, ri(rnd, 11, 14)));
    }
    return { els, base: matMul(matAxis(X, (rnd() < 0.5 ? 1 : -1) * rf(rnd, 0.42, 0.55)), matAxis(Y, rnd() * 6.283)) };
}

/** antiparallel sheet strand at sheet position j (twisted left-handed about the inter-strand axis) */
const sheetStrand = (rnd, xs, mid, tw, j, up, nres, dy = 0) => {
    const ang = tw * (j - mid);
    return strandAlong([xs(j), dy + rf(rnd, -0.8, 0.8), 0], rot(mul(Y, up), X, ang), nres, rot(Z, X, ang));
};

/** β-meander: 3–5 antiparallel strands joined by tight hairpins */
function meander(rnd) {
    const n = ri(rnd, 3, 5), tw = -rf(rnd, 12, 18) * DEG, xs = (j) => SHEET_GAP * (j - (n - 1) / 2), L = ri(rnd, 5, 6);
    const els = [];
    for (let j = 0; j < n; j++) els.push(sheetStrand(rnd, xs, (n - 1) / 2, tw, j, j % 2 ? -1 : 1, L - (j === 0 || j === n - 1 ? ri(rnd, 0, 1) : 0)));
    return { els, base: tiltSheet(rnd) };
}

/** Greek key: strands 1-2-3 meander, strand 4 returns beside strand 1 over a long connection */
function greekKey(rnd) {
    const tw = -rf(rnd, 12, 18) * DEG, xs = (j) => SHEET_GAP * (j - 1.5), L = ri(rnd, 5, 6);
    const els = [sheetStrand(rnd, xs, 1.5, tw, 1, 1, L), sheetStrand(rnd, xs, 1.5, tw, 2, -1, L), sheetStrand(rnd, xs, 1.5, tw, 3, 1, L), sheetStrand(rnd, xs, 1.5, tw, 0, -1, L)];
    els[3].loopK = 0.5; els[3].loopLift = 3;          // the Greek-key connection arches over the 1–2 hairpin
    return { els, base: tiltSheet(rnd) };
}

/** ββα zinc-finger-like motif: a hairpin with a helix packed on one face */
function zincFinger(rnd) {
    const tw = -rf(rnd, 10, 16) * DEG, xs = (j) => SHEET_GAP * (j - 0.5);
    const els = [sheetStrand(rnd, xs, 0.5, tw, 0, 1, 4), sheetStrand(rnd, xs, 0.5, tw, 1, -1, 4),
        helixAlong([rf(rnd, -0.8, 0.8), 1.5, 8], rot(Y, Z, rf(rnd, -0.25, 0.25)), ri(rnd, 11, 13))];
    return { els, base: matMul(matAxis(Y, (rnd() < 0.5 ? 1 : -1) * rf(rnd, 0.5, 0.62)), matAxis(X, (rnd() < 0.5 ? 1 : -1) * rf(rnd, 0.4, 0.5))) };
}

/** ferredoxin-like βαββαβ: sheet order 4132 antiparallel, both helices on one face (right-handed crossovers) */
function ferredoxin(rnd) {
    const tw = -rf(rnd, 12, 18) * DEG, xs = (j) => SHEET_GAP * (j - 1.5), st = (j, up) => sheetStrand(rnd, xs, 1.5, tw, j, up, ri(rnd, 5, 6));
    const els = [
        st(1, 1),
        helixAlong([xs(2.5), 0, 10], rot([0, -1, 0], Z, rf(rnd, -0.2, 0.2)), ri(rnd, 9, 11)),
        st(3, 1), st(2, -1),
        helixAlong([xs(0.5), 0, 10], rot([0, 1, 0], Z, rf(rnd, -0.2, 0.2)), ri(rnd, 9, 11)),
        st(0, -1),
    ];
    return { els: knitAway(els), base: tiltAB(rnd) };
}

/** βαβαβ(αβ): parallel strands in sequence order, every crossover right-handed so all helices sit on one face */
function babab(rnd) {
    const n = rnd() < 0.6 ? 3 : 4, tw = -rf(rnd, 12, 18) * DEG, xs = (j) => SHEET_GAP * (j - (n - 1) / 2);
    const hx = Array.from({ length: n - 1 }, (_, i) => (xs(i) + xs(i + 1)) / 2);
    for (let it = 0; it < 8; it++) for (let k = 1; k < hx.length; k++) {
        const gap = hx[k] - hx[k - 1];
        if (gap < 9.6) { hx[k] += (9.6 - gap) / 2; hx[k - 1] -= (9.6 - gap) / 2; }
    }
    const els = [];
    for (let j = 0; j < n; j++) {
        const ang = tw * (j - (n - 1) / 2);
        els.push(strandAlong([xs(j), rf(rnd, -0.8, 0.8), 0], rot(Y, X, ang), ri(rnd, 4, 5), rot(Z, X, ang)));
        // advance +x, strands +y → e_adv × e_strand = +z: helix on the +z face
        if (j < n - 1) els.push(helixAlong([hx[j], rf(rnd, -0.8, 0.8), 10], rot([0, -1, 0], Z, rf(rnd, -0.2, 0.2)), ri(rnd, 9, 11)));
    }
    return { els: knitAway(els), base: tiltAB(rnd) };
}

export const TOPOLOGIES = { bundle, meander, greekKey, zincFinger, ferredoxin, babab };

// ------------------------------------------------------------------ chain sampling
function helixPoint(el, t) {
    const a = norm(sub(el.B, el.A));
    if (!el.u) { el.u = norm(cross(a, Math.abs(a[0]) < 0.9 ? X : Z)); el.v = cross(a, el.u); el.th0 = el.th0 ?? 0; }
    // u × v = a  →  r(t) = R(cos θ u + sin θ v) + rise·t·a is right-handed
    const th = el.th0 + t * HELIX_TURN, c = Math.cos(th), s = Math.sin(th);
    return add(add(el.A, mul(a, t * HELIX_RISE)), add(mul(el.u, HELIX_R * c), mul(el.v, HELIX_R * s)));
}
function helixTan(el, t) { return norm(sub(helixPoint(el, t + 0.02), helixPoint(el, t - 0.02))); }
const nresOf = (el) => el.t === 'H' ? Math.max(4, Math.round(len(sub(el.B, el.A)) / HELIX_RISE)) : Math.max(3, Math.round(len(sub(el.E, el.S)) / STRAND_RISE));
const entry = (el) => el.t === 'H' ? [helixPoint(el, 0), helixTan(el, 0)] : [el.S, norm(sub(el.E, el.S))];
const exit = (el) => el.t === 'H' ? [helixPoint(el, el.nr), helixTan(el, el.nr)] : [el.E, norm(sub(el.E, el.S))];
const bez = (P, t) => { const u = 1 - t; return add(add(mul(P[0], u * u * u), mul(P[1], 3 * u * u * t)), add(mul(P[2], 3 * u * t * t), mul(P[3], t * t * t))); };

/**
 * Sample the chain. ds = tube sample spacing (Å), rowH = knitted row height (Å).
 * Returns { tube:[{p,tan,res}], rows:[{c,a,w,n,hw,rowH,res,strand}], spine:[{p,res}], nres }
 */
export function sampleChain(top, rnd, ds, rowH) {
    const els = top.els;
    for (const el of els) { el.nr = nresOf(el); if (el.t === 'H') { el.th0 = rnd() * 6.283; el.u = null; } }
    const mid = els.reduce((m, el) => add(m, mul(add(el.t === 'H' ? el.A : el.S, el.t === 'H' ? el.B : el.E), 0.5 / els.length)), [0, 0, 0]);
    const tube = [], rows = [], spine = [];
    let res = 0;
    const pushTube = (p, tan, ax = null) => { tube.push({ p, tan, res, ax }); spine.push({ p, res }); };
    const loop = (P0, T0, P3, T3, minRes = 2, bulgeK = 0.2, lift = 0) => {
        const L = len(sub(P3, P0)) + 3;
        const m = mul(add(P0, P3), 0.5), out = norm(sub(m, mid)), b = add(mul(out, bulgeK * L), [0, 0, lift]);
        const P = [P0, add(add(P0, mul(T0, L * 0.42)), b), add(sub(P3, mul(T3, L * 0.42)), b), P3];
        let arc = 0, prev = P0; const pts = [P0], acc = [0];
        for (let i = 1; i <= 48; i++) { const q = bez(P, i / 48); arc += len(sub(q, prev)); pts.push(q); acc.push(arc); prev = q; }
        const nres = Math.max(minRes, Math.round(arc / 3.4)), r0 = res, n = Math.max(2, Math.ceil(arc / ds));
        for (let i = 1; i < n; i++) {
            const target = arc * i / n; let k = 1; while (acc[k] < target) k++;
            const f = (target - acc[k - 1]) / Math.max(acc[k] - acc[k - 1], 1e-6);
            const p = add(pts[k - 1], mul(sub(pts[k], pts[k - 1]), f));
            res = r0 + nres * i / n; pushTube(p, norm(sub(pts[k], pts[k - 1])));
        }
        res = r0 + nres;
    };
    // N-terminal tail: the loose cast-on end
    const [s0, t0] = entry(els[0]);
    const tailStart = add(sub(s0, mul(t0, 7)), mul(norm(sub(s0, mid)), 5));
    pushTube(tailStart, t0);
    loop(tailStart, norm(sub(s0, tailStart)), s0, t0, 2, 0.1);
    els.forEach((el, ei) => {
        if (ei > 0) { const [pe, te] = exit(els[ei - 1]), [ps, ts] = entry(el); loop(pe, te, ps, ts, 2, el.loopK ?? 0.2, el.loopLift ?? 0); }
        if (el.t === 'H') {
            const per = Math.hypot(HELIX_R * HELIX_TURN, HELIX_RISE), dt = ds / per, r0 = res;
            const ha = norm(sub(el.B, el.A));
            for (let t = 0; t <= el.nr + 1e-6; t += dt) { res = r0 + t; pushTube(helixPoint(el, t), helixTan(el, t), add(el.A, mul(ha, t * HELIX_RISE))); }
            res = r0 + el.nr;
        } else {
            const a = norm(sub(el.E, el.S)), L = len(sub(el.E, el.S)), w = norm(cross(el.n, a));
            const nRows = Math.max(4, Math.round(L / rowH)), r0 = res, arrowL = Math.min(L * 0.4, STRAND_W * 1.35), aS = L - arrowL;
            const hwAt = (s, inArrow) => !inArrow ? STRAND_W / 2 : (STRAND_W * 0.9) * Math.max(0.1, 1 - (s - aS) / arrowL);
            for (let i = 0; i < nRows; i++) {
                const s = (i + 0.5) * L / nRows, inA = s >= aS, rh = L / nRows;
                const hw = hwAt(s, inA), hw0 = hwAt(Math.max(s - rh / 2, aS), inA), hw1 = hwAt(s + rh / 2, inA);
                res = r0 + el.nr * (s / L);
                const c = add(el.S, mul(a, s));
                rows.push({ c, a, w, n: el.n, hw, hw0, hw1, rowH: rh, res, strand: ei, i, arrow: inA });
                spine.push({ p: c, res });
            }
            res = r0 + el.nr;
        }
    });
    // C-terminal tail toward the working yarn
    const [pe, te] = exit(els[els.length - 1]);
    const tailEnd = add(add(pe, mul(te, 5)), mul(norm(sub(pe, mid)), 7));
    loop(pe, te, tailEnd, norm(sub(tailEnd, pe)), 2, 0.05);
    pushTube(tailEnd, norm(sub(tailEnd, pe)));
    // centre on the centroid
    const cen = spine.reduce((m, s) => add(m, s.p), [0, 0, 0]).map(v => v / spine.length);
    for (const s of tube) { s.p = sub(s.p, cen); if (s.ax) s.ax = sub(s.ax, cen); }
    for (const r of rows) r.c = sub(r.c, cen);
    for (const s of spine) s.p = sub(s.p, cen);
    spine.sort((a, b) => a.res - b.res);
    return { tube, rows, spine, nres: res };
}
