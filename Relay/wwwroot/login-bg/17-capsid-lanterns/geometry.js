// 17-capsid-lanterns — capsid geometry.
// Icosahedral and prolate capsids follow the Caspar–Klug construction: a triangular lattice is cut into
// an icosahedral net (faces spanned by the lattice vector (h,k), T = h² + hk + k²), every lattice point
// becomes a capsomer centre and the dual tiling gives 12 pentamers + 10(T−1) hexamers. Prolate heads
// stretch the ten equatorial faces along a five-fold axis by a further lattice vector c
// (Q = T + |h·c₂ − k·c₁|, capsomers = 5(T+Q)+2). Helical rods place one subunit every Δφ, Δz on a
// one-start helix (TMV: 16.33 subunits per turn, 23 Å pitch on a 90 Å radius).
// Every model is a list of "capsomers": a polygon of corner indices (CCW seen from outside) plus an
// apex lifted along the normal, so each folded-paper capsomer is a fan of 5 or 6 subunit facets.

const S3 = Math.sqrt(3);
const D2R = Math.PI / 180;
const lat = (i, j) => [i + 0.5 * j, (S3 / 2) * j];
const rot2 = (v, deg) => { const c = Math.cos(deg * D2R), s = Math.sin(deg * D2R); return [v[0] * c - v[1] * s, v[0] * s + v[1] * c]; };
const add2 = (a, b) => [a[0] + b[0], a[1] + b[1]];
const sub3 = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
const dot3 = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const cross3 = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const len3 = (a) => Math.hypot(a[0], a[1], a[2]);
const norm3 = (a) => { const l = len3(a) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };
const lerp3 = (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];

export const tNumber = (h, k) => h * h + h * k + k * k;

/**
 * Caspar–Klug capsid. h,k: lattice vector (T = h²+hk+k²); c: [c1,c2] lattice elongation (prolate) or null.
 * blend: 0 = flat-faceted polyhedron, 1 = fully inflated (sphere / ellipsoid). bulge: capsomer fold height.
 * geodesic: return the triangulated geodesic sphere (plain paper panels) instead of the capsomer tiling.
 */
export function buildCapsid({ h, k, c = null, blend = 0.8, bulge = 0.22, geodesic = false }) {
    const a = lat(h, k), La = Math.hypot(a[0], a[1]);
    const cc = c ? lat(c[0], c[1]) : [0, 0];
    const b60 = rot2(a, 60), bm60 = rot2(a, -60);
    const U = [], N = [], D = [], S = [];
    for (let i = 0; i <= 5; i++) {
        U[i] = [a[0] * i, a[1] * i];
        N[i] = add2(U[i], b60);
        D[i] = add2(add2(U[i], bm60), cc);
        S[i] = add2(D[i], bm60);
    }
    // 3D frame: five-fold axis along z, unit edge = |a|
    const r = 1 / (2 * Math.sin(36 * D2R));
    const capH = Math.sqrt(1 - r * r);
    const l1 = Math.hypot(D[0][0] - U[0][0], D[0][1] - U[0][1]) / La, l2 = Math.hypot(D[0][0] - U[1][0], D[0][1] - U[1][1]) / La;
    const Lb = (l1 + l2) / 2, chord = 2 * r * Math.sin(18 * D2R);
    const zU = Math.sqrt(Math.max(Lb * Lb - chord * chord, 0.01)) / 2;
    const ring = (i, off, z) => [r * Math.cos((72 * i + off) * D2R), r * Math.sin((72 * i + off) * D2R), z];
    const top = [0, 0, zU + capH], bot = [0, 0, -zU - capH];
    const faces = [];
    for (let i = 0; i < 5; i++) {
        const Ut0 = ring(i, 0, zU), Ut1 = ring(i + 1, 0, zU), Db0 = ring(i, 36, -zU), Db1 = ring(i + 1, 36, -zU);
        faces.push([[U[i], U[i + 1], N[i]], [Ut0, Ut1, top]]);
        faces.push([[U[i], U[i + 1], D[i]], [Ut0, Ut1, Db0]]);
        faces.push([[D[i], D[i + 1], U[i + 1]], [Db0, Db1, Ut1]]);
        faces.push([[D[i], D[i + 1], S[i]], [Db0, Db1, bot]]);
    }
    // lattice points of every face → 3D (flat polyhedron), deduplicated
    const flat = [], keys = new Map();
    const toIJ = (p) => { const j = p[1] / (S3 / 2); return [p[0] - 0.5 * j, j]; };
    for (const [P, Q] of faces) {
        const ij = P.map(toIJ);
        const i0 = Math.floor(Math.min(...ij.map(v => v[0]))) - 1, i1 = Math.ceil(Math.max(...ij.map(v => v[0]))) + 1;
        const j0 = Math.floor(Math.min(...ij.map(v => v[1]))) - 1, j1 = Math.ceil(Math.max(...ij.map(v => v[1]))) + 1;
        const [A, B, C] = P;
        const det = (B[0] - A[0]) * (C[1] - A[1]) - (C[0] - A[0]) * (B[1] - A[1]);
        for (let i = i0; i <= i1; i++) for (let j = j0; j <= j1; j++) {
            const p = lat(i, j);
            const l1b = ((p[0] - A[0]) * (C[1] - A[1]) - (C[0] - A[0]) * (p[1] - A[1])) / det;
            const l2b = ((B[0] - A[0]) * (p[1] - A[1]) - (p[0] - A[0]) * (B[1] - A[1])) / det;
            const l0b = 1 - l1b - l2b;
            if (l0b < -1e-9 || l1b < -1e-9 || l2b < -1e-9) continue;
            const X = [0, 1, 2].map(d => l0b * Q[0][d] + l1b * Q[1][d] + l2b * Q[2][d]);
            const key = X.map(v => Math.round(v * 1e5)).join(',');
            if (!keys.has(key)) { keys.set(key, flat.length); flat.push(X); }
        }
    }
    // inflated surface: sphere for icosahedra, ellipsoid for prolate heads (strictly convex → hull = lattice)
    const ratio = (zU + capH) / 0.9511;
    const Az = c ? 1 + (ratio - 1) * 0.8 : 1;
    const ellip = (p) => { const s = Math.sqrt((p[0] * p[0] + p[1] * p[1]) + (p[2] * p[2]) / (Az * Az)); return [p[0] / s, p[1] / s, p[2] / s]; };
    const ellN = (p) => norm3([p[0], p[1], p[2] / (Az * Az)]);
    const fl = flat.map(p => [p[0] / 0.9511, p[1] / 0.9511, c ? (p[2] * Az) / (zU + capH) : p[2] / 0.9511]);
    const E = flat.map(ellip);
    const tris = hull(E);
    const nV = E.length;
    if (geodesic) return geodesicPanels({ h, k, tris, fl, E, ellip, ellN, blend, bulge, nV });
    // capsomers = lattice points; corners = centroids of the incident triangles
    const inc = Array.from({ length: nV }, () => []);
    tris.forEach((t, ti) => t.forEach(v => inc[v].push(ti)));
    const surf = (pF, pE) => lerp3(pF, ellip(pE), blend);
    const corners = tris.map(([i, j, l]) => {
        const cf = [(fl[i][0] + fl[j][0] + fl[l][0]) / 3, (fl[i][1] + fl[j][1] + fl[l][1]) / 3, (fl[i][2] + fl[j][2] + fl[l][2]) / 3];
        const ce = [(E[i][0] + E[j][0] + E[l][0]) / 3, (E[i][1] + E[j][1] + E[l][1]) / 3, (E[i][2] + E[j][2] + E[l][2]) / 3];
        return surf(cf, ce);
    });
    const caps = [];
    let pent = 0, bad = 0;
    for (let v = 0; v < nV; v++) {
        const n = ellN(E[v]);
        const t1 = norm3(cross3(Math.abs(n[2]) < 0.9 ? [0, 0, 1] : [1, 0, 0], n)), t2 = cross3(n, t1);
        const ord = inc[v].map(ti => { const d = sub3(corners[ti], E[v]); return [Math.atan2(dot3(d, t2), dot3(d, t1)), ti]; })
            .sort((x, y) => x[0] - y[0]).map(x => x[1]);
        const centre = surf(fl[v], E[v]);
        let rad = 0; for (const ti of ord) rad += len3(sub3(corners[ti], centre)); rad /= ord.length;
        const apex = [centre[0] + n[0] * rad * bulge, centre[1] + n[1] * rad * bulge, centre[2] + n[2] * rad * bulge];
        if (ord.length === 5) pent++; else if (ord.length !== 6) bad++;
        caps.push({ corners: ord, apex, centre, normal: n, rad, pent: ord.length === 5 });
    }
    // seams: dual edges between triangles that share a lattice edge; neighbours for the assembly order
    const edgeMap = new Map(), seams = [], nbr = Array.from({ length: nV }, () => new Set());
    tris.forEach((t, ti) => {
        for (let e = 0; e < 3; e++) {
            const p = t[e], q = t[(e + 1) % 3], key = p < q ? p * 4096 + q : q * 4096 + p;
            nbr[p].add(q); nbr[q].add(p);
            if (edgeMap.has(key)) seams.push([edgeMap.get(key), ti]); else edgeMap.set(key, ti);
        }
    });
    const T = tNumber(h, k), Q = c ? T + Math.abs(h * c[1] - k * c[0]) : T;
    const model = finish({ points: corners, caps, seams, nbr: nbr.map(s => [...s]) });
    // the unique portal vertex of phage-like heads sits on the long (z) axis
    let portal = -1, best = -1;
    caps.forEach((cp, i) => { if (cp.pent && cp.centre[2] > best) { best = cp.centre[2]; portal = i; } });
    Object.assign(model, { kind: c ? 'prolate' : 'icosa', T, Q, pentamers: pent, ok: pent === 12 && bad === 0 && nV === 5 * (T + Q) + 2, portal });
    return model;
}

/**
 * Plain geodesic paper lantern: the same lattice triangulation (class I (n,0), class II (n,n), …), but the
 * paper panels are the 20T triangles themselves (12 five-valent and 10(T−1) six-valent vertices), each panel
 * creased lightly from its centre.
 */
function geodesicPanels({ h, k, tris, fl, E, ellip, ellN, blend, bulge, nV }) {
    const pts = E.map((e, v) => lerp3(fl[v], e, blend));
    const valence = new Array(nV).fill(0);
    const caps = tris.map(([i, j, l]) => {
        valence[i]++; valence[j]++; valence[l]++;
        const ce = [(E[i][0] + E[j][0] + E[l][0]) / 3, (E[i][1] + E[j][1] + E[l][1]) / 3, (E[i][2] + E[j][2] + E[l][2]) / 3];
        const n = ellN(ce);
        const centre = [(pts[i][0] + pts[j][0] + pts[l][0]) / 3, (pts[i][1] + pts[j][1] + pts[l][1]) / 3, (pts[i][2] + pts[j][2] + pts[l][2]) / 3];
        let rad = 0; for (const q of [i, j, l]) rad += len3(sub3(pts[q], centre)); rad /= 3;
        return { corners: [i, j, l], apex: [centre[0] + n[0] * rad * bulge, centre[1] + n[1] * rad * bulge, centre[2] + n[2] * rad * bulge], centre, normal: n, rad, pent: false };
    });
    const edgeMap = new Map(), seams = [], nbr = caps.map(() => []);
    tris.forEach((t, ti) => {
        for (let e = 0; e < 3; e++) {
            const p = t[e], q = t[(e + 1) % 3], key = p < q ? p * 4096 + q : q * 4096 + p;
            if (edgeMap.has(key)) { const o = edgeMap.get(key); nbr[o].push(ti); nbr[ti].push(o); } else { edgeMap.set(key, ti); seams.push([p, q, 0]); }
        }
    });
    const T = tNumber(h, k);
    const model = finish({ points: pts, caps, seams, nbr, seamTyped: true });
    const five = valence.filter(v => v === 5).length;
    Object.assign(model, { kind: 'geodesic', T, Q: T, pentamers: five, ok: five === 12 && valence.every(v => v === 5 || v === 6) && caps.length === 20 * T, portal: -1 });
    return model;
}

// convex hull of points on a strictly convex surface around the origin (restricted to near neighbours)
function hull(P) {
    const n = P.length, K = Math.min(10, n - 1), out = [], seen = new Set();
    for (let i = 0; i < n; i++) {
        const nb = [];
        for (let j = 0; j < n; j++) if (j !== i) nb.push([len3(sub3(P[j], P[i])), j]);
        nb.sort((x, y) => x[0] - y[0]);
        const cand = nb.slice(0, K).map(x => x[1]);
        for (let a = 0; a < cand.length; a++) for (let b = a + 1; b < cand.length; b++) {
            let j = cand[a], l = cand[b];
            let nn = cross3(sub3(P[j], P[i]), sub3(P[l], P[i]));
            if (dot3(nn, P[i]) < 0) { nn = [-nn[0], -nn[1], -nn[2]]; const t = j; j = l; l = t; }
            const key = [i, j, l].sort((x, y) => x - y).join(',');
            if (seen.has(key)) continue;
            let okk = true;
            for (let m = 0; m < n && okk; m++) if (m !== i && m !== j && m !== l && dot3(sub3(P[m], P[i]), nn) > 1e-9) okk = false;
            if (okk) { seen.add(key); out.push([i, j, l]); }
        }
    }
    return out;
}

/**
 * Helical rod (TMV-like): subunits on a one-start helix, 16.33 per turn, pitch/radius = 23/90.
 * Each subunit is a paper tile spanning the lattice cell n, n+1, n+17, n+16; the rod is cut to `length`
 * (in radii) with folded end discs around the central channel.
 */
export function buildHelix({ length = 4.4, perTurn = 16.33, pitch = 23 / 90, bulge = 0.18, rnd = Math.random }) {
    const dphi = (2 * Math.PI) / perTurn, dz = pitch / perTurn, half = length / 2;
    const pts = [], caps = [], seams = [];
    const P = (phi, z) => { pts.push([Math.cos(phi), Math.sin(phi), z]); return pts.length - 1; };
    const n0 = Math.floor(-half / dz) - 20, n1 = Math.ceil(half / dz) + 20;
    const idx = new Map();
    const nodeOf = (n) => { if (!idx.has(n)) idx.set(n, P(n * dphi, n * dz)); return idx.get(n); };
    const nt = Math.floor(perTurn); // n+16 is the neighbour one turn up (slightly behind), n+17 slightly ahead
    for (let n = n0; n <= n1; n++) {
        const zc = (n + (nt + 1) / 2) * dz;
        if (zc - pitch * 0.5 < -half || zc + pitch * 0.5 > half) continue;
        const c4 = [nodeOf(n), nodeOf(n + 1), nodeOf(n + nt + 1), nodeOf(n + nt)];
        const ctr = [0, 0, 0]; for (const ci of c4) for (let d = 0; d < 3; d++) ctr[d] += pts[ci][d] / 4;
        const nrm = norm3([ctr[0], ctr[1], 0]);
        const rad = len3(sub3(pts[c4[0]], pts[c4[2]])) / 2;
        const centre = [nrm[0] * 0.995, nrm[1] * 0.995, ctr[2]];
        caps.push({ corners: c4, apex: [centre[0] + nrm[0] * rad * bulge, centre[1] + nrm[1] * rad * bulge, centre[2]], centre, normal: nrm, rad, pent: false, n });
    }
    // seams: along the genetic helix (n→n+1, the prominent groove) and across turns
    const tiles = caps.slice();
    for (const cp of tiles) { const [a, b, c, d] = cp.corners; seams.push([a, b, 1], [b, c, 0], [c, d, 1], [d, a, 0]); }
    // end discs at the cut: polygon at the rod end with radial creases around the 4 nm channel (as a raised centre)
    const zs = tiles.map(c => c.centre[2]);
    const zTop = Math.max(...zs) + pitch * 0.5, zBot = Math.min(...zs) - pitch * 0.5;
    const ends = [];
    for (const [z, s] of [[zTop, 1], [zBot, -1]]) {
        const m = 17, ids = [];
        for (let i = 0; i < m; i++) { const ph = (s > 0 ? i : -i) * (2 * Math.PI / m); ids.push(P(ph, z)); }
        const centre = [0, 0, z];
        caps.push({ corners: ids, apex: [0, 0, z + s * 0.06], centre, normal: [0, 0, s], rad: 1, pent: false, end: true, channel: 0.22 });
        ends.push(caps.length - 1);
        for (let i = 0; i < m; i++) seams.push([ids[i], ids[(i + 1) % m], 2]);
    }
    // assembly neighbours: tiles adjacent in n
    const byN = new Map(); caps.forEach((c, i) => { if (c.n !== undefined) byN.set(c.n, i); });
    const nbr = caps.map((c, i) => {
        if (c.end) return [];
        return [c.n - 1, c.n + 1, c.n - nt, c.n + nt].map(q => byN.get(q)).filter(q => q !== undefined);
    });
    // scale so that the furthest point is at 1
    const R = Math.max(...pts.map(len3));
    for (const p of pts) { p[0] /= R; p[1] /= R; p[2] /= R; }
    for (const c of caps) { for (const k of ['apex', 'centre']) c[k] = c[k].map(v => v / R); c.rad /= R; }
    const model = finish({ points: pts, caps, seams, nbr, seamTyped: true });
    Object.assign(model, { kind: 'helix', ok: true, portal: ends[0], ends, radius: 1 / R, halfLen: half / R, perTurn, pitch });
    return model;
}

// shared post-processing: flat typed arrays for fast per-frame rotation
function finish({ points, caps, seams, nbr, seamTyped = false }) {
    let maxR = 0;
    for (const p of points) maxR = Math.max(maxR, len3(p));
    for (const c of caps) maxR = Math.max(maxR, len3(c.apex));
    const nP = points.length, nC = caps.length;
    const pos = new Float32Array((nP + nC) * 3);
    points.forEach((p, i) => { pos[i * 3] = p[0] / maxR; pos[i * 3 + 1] = p[1] / maxR; pos[i * 3 + 2] = p[2] / maxR; });
    caps.forEach((c, i) => {
        c.apexIdx = nP + i;
        pos[(nP + i) * 3] = c.apex[0] / maxR; pos[(nP + i) * 3 + 1] = c.apex[1] / maxR; pos[(nP + i) * 3 + 2] = c.apex[2] / maxR;
        c.centre = c.centre.map(v => v / maxR); c.rad /= maxR;
    });
    return { pos, nP, nC, caps, nbr, seams: seams.map(s => seamTyped ? s : [s[0], s[1], 0]) };
}

/** assembly order (0..1 per capsomer): nucleate at a random capsomer, grow along neighbours breadth-first with jitter */
export function assemblyOrder(model, rnd = Math.random, start = -1) {
    const nC = model.nC, nbr = model.nbr, order = new Int32Array(nC).fill(-1);
    if (start < 0) start = (rnd() * nC) | 0;
    let frontier = [start], rank = 0; order[start] = rank++;
    while (frontier.length) {
        const next = [];
        for (let i = frontier.length - 1; i > 0; i--) { const j = (rnd() * (i + 1)) | 0; const t = frontier[i]; frontier[i] = frontier[j]; frontier[j] = t; }
        for (const v of frontier) for (const w of nbr[v] || []) if (order[w] < 0) { order[w] = rank++; next.push(w); }
        frontier = next;
    }
    const out = new Float32Array(nC);
    for (let i = 0; i < nC; i++) out[i] = (order[i] < 0 ? rank++ : order[i]) / Math.max(1, nC - 1);
    return out;
}
