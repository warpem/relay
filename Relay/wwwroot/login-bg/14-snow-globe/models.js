// Hand-modelled macromolecules for the snow globe.
// Each model is a smooth union of soft primitives (spheres, capsules, ellipsoids) in nanometres, with the true
// symmetry and proportions of the complex, a little hand-pressed lumpiness, and a few colour "parts".
// It is meshed once at setup with naive surface nets, then normals, baked occlusion and part tints are attached.
//
//   apoferritin   24-mer, octahedral (432): 12 antiparallel dimers on the faces of a rhombic dodecahedron,
//                 each 4-helix bundle running from a 3-fold to a 4-fold channel. 12 nm outside, 8 nm inside.
//   GroEL         D7 double heptamer (equatorial / intermediate / apical domains), 14.6 x 13.7 nm, open cavity.
//   GroEL-GroES   the "bullet": cis-ring apical domains swing up and out under a heptameric GroES dome.
//   20S           a7 b7 b7 a7 barrel, D7, 15 x 11.5 nm, a-rings in a second colour, closed gates.
//   70S ribosome  large subunit (central protuberance, L1 stalk, L7/L12 stalk) and small subunit
//                 (head, beak, body, platform, shoulder, spur) in two colours, ~25 nm.
//   spike         prefusion trimer (C3): S2 stalk, SD1, NTD and RBD per protomer, glycan knobs;
//                 one variant with a single RBD in the "up" state.

const V = {
    add: (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]],
    sub: (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]],
    mul: (a, s) => [a[0] * s, a[1] * s, a[2] * s],
    dot: (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2],
    cross: (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]],
    len: (a) => Math.hypot(a[0], a[1], a[2]),
    norm: (a) => { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; },
    lerp: (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t],
};
// cylindrical helper: (radius, angle, height along the symmetry axis) with the axis = +y
const cyl = (r, a, y) => [r * Math.cos(a), y, r * Math.sin(a)];

class Model {
    constructor(name, k = 0.7) { this.name = name; this.k = k; this.prims = []; }
    sphere(c, r, part = 0, shade = 0) { this.prims.push({ t: 0, c, r, R: r, part, shade }); return this; }
    capsule(a, b, r, part = 0, shade = 0) {
        const ab = V.sub(b, a), c = V.lerp(a, b, 0.5);
        this.prims.push({ t: 1, a, ab, abl2: V.dot(ab, ab), r, c, R: V.len(ab) / 2 + r, part, shade }); return this;
    }
    // rounded hexagonal prism: hexagon in the x-z plane (inradius hx), half-thickness hy along y
    hex(c, hx, hy, r) { this.prims.push({ t: 3, c, hx, hy, r, R: hx * 1.16 + hy + r, part: 0, shade: 0 }); return this; }
    // ellipsoid with semi-axes (ra, rb, rc) along orthonormal axes e1, e2, e3
    ell(c, radii, e1, e2, e3, part = 0, shade = 0) {
        this.prims.push({ t: 2, c, radii, e: [e1, e2, e3], R: Math.max(...radii), part, shade }); return this;
    }
}

function primDist(q, x, y, z) {
    if (q.t === 0) { const dx = x - q.c[0], dy = y - q.c[1], dz = z - q.c[2]; return Math.sqrt(dx * dx + dy * dy + dz * dz) - q.r; }
    if (q.t === 1) {
        const px = x - q.a[0], py = y - q.a[1], pz = z - q.a[2];
        let h = (px * q.ab[0] + py * q.ab[1] + pz * q.ab[2]) / q.abl2;
        h = h < 0 ? 0 : h > 1 ? 1 : h;
        const ex = px - q.ab[0] * h, ey = py - q.ab[1] * h, ez = pz - q.ab[2] * h;
        return Math.sqrt(ex * ex + ey * ey + ez * ez) - q.r;
    }
    if (q.t === 3) {
        let px = Math.abs(x - q.c[0]), pz = Math.abs(z - q.c[2]);
        const py = Math.abs(y - q.c[1]);
        const m = 2 * Math.min(-0.8660254 * px + 0.5 * pz, 0);
        px -= m * -0.8660254; pz -= m * 0.5;
        const lim = 0.57735 * q.hx, cx = Math.max(-lim, Math.min(lim, px));
        const d1 = Math.sqrt((px - cx) * (px - cx) + (pz - q.hx) * (pz - q.hx)) * Math.sign(pz - q.hx), d2 = py - q.hy;
        const a = Math.max(d1, 0), b = Math.max(d2, 0);
        return Math.min(Math.max(d1, d2), 0) + Math.sqrt(a * a + b * b) - q.r;
    }
    const px = x - q.c[0], py = y - q.c[1], pz = z - q.c[2];
    const [e1, e2, e3] = q.e, [ra, rb, rc] = q.radii;
    const u = (px * e1[0] + py * e1[1] + pz * e1[2]), v = (px * e2[0] + py * e2[1] + pz * e2[2]), w = (px * e3[0] + py * e3[1] + pz * e3[2]);
    const a0 = u / ra, b0 = v / rb, c0 = w / rc, a1 = a0 / ra, b1 = b0 / rb, c1 = c0 / rc;
    const k0 = Math.sqrt(a0 * a0 + b0 * b0 + c0 * c0), k1 = Math.sqrt(a1 * a1 + b1 * b1 + c1 * c1);
    return k1 < 1e-9 ? -Math.min(ra, rb, rc) : k0 * (k0 - 1) / k1;
}

// smooth value noise in 3D (periodic lattice), used for hand-pressed lumpiness
function makeNoise(rand) {
    const P = 32, lat = new Float32Array(P * P * P);
    for (let i = 0; i < lat.length; i++) lat[i] = rand() * 2 - 1;
    const g = (i, j, k) => lat[(((k % P) + P) % P) * P * P + (((j % P) + P) % P) * P + (((i % P) + P) % P)];
    return (x, y, z) => {
        const ix = Math.floor(x), iy = Math.floor(y), iz = Math.floor(z);
        let fx = x - ix, fy = y - iy, fz = z - iz;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        const l = (a, b, t) => a + (b - a) * t;
        return l(l(l(g(ix, iy, iz), g(ix + 1, iy, iz), fx), l(g(ix, iy + 1, iz), g(ix + 1, iy + 1, iz), fx), fy),
            l(l(g(ix, iy, iz + 1), g(ix + 1, iy, iz + 1), fx), l(g(ix, iy + 1, iz + 1), g(ix + 1, iy + 1, iz + 1), fx), fy), fz);
    };
}

// ---------------------------------------------------------------------------------------------- models
function octahedralGroup() {
    // the 24 proper rotations of the cube = signed permutation matrices with determinant +1
    const perms = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
    const out = [];
    for (const pm of perms) for (let s = 0; s < 8; s++) {
        const m = [[0, 0, 0], [0, 0, 0], [0, 0, 0]];
        for (let r = 0; r < 3; r++) m[r][pm[r]] = (s >> r) & 1 ? -1 : 1;
        const det = m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1]) - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0]) + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]);
        if (det > 0) out.push(m);
    }
    return out;
}
const mv = (m, v) => [m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2], m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2], m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2]];

function apoferritin(rand) {
    const M = new Model('apoferritin', 0.75);
    M.amp = 0.05;
    // face of the rhombic dodecahedron on the 2-fold axis (1,1,0): 4-fold vertices A, C; 3-fold vertices B, D.
    // The subunit fills the half of the rhombus along edge A-B (from a 4-fold towards a 3-fold channel);
    // its dimer partner (the 2-fold image) fills the half along C-D, antiparallel.
    const A = [1, 0, 0], B = [0.5, 0.5, 0.5], C = [0, 1, 0], D = [0.5, 0.5, -0.5];
    const mAD = V.lerp(A, D, 0.5), mBC = V.lerp(B, C, 0.5);
    const s0 = V.lerp(A, mAD, 0.5), s1 = V.lerp(B, mBC, 0.5);
    const R = 5.0;
    const p0 = V.mul(V.norm(V.lerp(s0, s1, 0.1)), R), p1 = V.mul(V.norm(V.lerp(s0, s1, 0.9)), R);
    const mid = V.norm(V.lerp(p0, p1, 0.5)), ax = V.norm(V.sub(p1, p0)), wdir = V.norm(V.cross(mid, ax));
    const G = octahedralGroup();
    G.forEach((m, i) => {
        const shade = (rand() - 0.5) * 0.9;
        // one rounded 4-helix bundle, a little flattened against the shell
        M.capsule(mv(m, p0), mv(m, p1), 1.18, 0, shade);
        void wdir;
    });
    M.hollowInner = 3.7;   // carve the inner cavity a little cleaner
    return M;
}

function groel(rand, withES) {
    const M = new Model(withES ? 'groel-groes' : 'groel', 1.15);
    const n = 7, ph = 0.12;
    for (const ring of [1, -1]) {
        const cis = withES && ring === 1;
        for (let i = 0; i < n; i++) {
            const a = (2 * Math.PI * i) / n + ph * ring;
            const shade = (rand() - 0.5) * 0.5;
            const e1 = [Math.cos(a), 0, Math.sin(a)], e2 = [-Math.sin(a), 0, Math.cos(a)], e3 = [0, 1, 0];
            const y = (h) => h * ring;
            // equatorial domain: the ring's inner, load-bearing base
            M.ell(cyl(4.55, a, y(1.95)), [2.2, 2.0, 2.0], e1, e2, e3, 0, shade);
            if (!cis) {
                M.sphere(cyl(5.3, a + 0.05 * ring, y(4.2)), 1.35, 0, shade);                     // intermediate (hinge)
                M.ell(cyl(4.85, a + 0.12 * ring, y(6.0)), [1.95, 1.85, 1.5], e1, e2, e3, 0, shade);  // apical
            } else {
                // GroES-bound cis ring: apical domains elevated and twisted outward, cavity doubled
                M.sphere(cyl(5.6, a + 0.1, y(4.7)), 1.2, 0, shade);
                const b = a + 0.55;
                M.ell(cyl(5.55, b, y(7.4)), [1.6, 1.8, 1.55], [Math.cos(b), 0, Math.sin(b)], [-Math.sin(b), 0, Math.cos(b)], e3, 0, shade);
            }
        }
    }
    if (withES) {
        // GroES: a heptameric dome capping the cis ring
        for (let i = 0; i < n; i++) {
            const a = (2 * Math.PI * i) / n + 0.3;
            M.sphere(cyl(3.6, a, 9.2), 1.45, 1, 0);
            M.sphere(cyl(1.6, a + 0.25, 10.35), 0.95, 1, 0);
        }
    }
    return M;
}

function proteasome(rand) {
    const M = new Model('proteasome20S', 1.3);
    const n = 7;
    const rings = [[5.6, 1, 0.22], [1.88, 0, 0], [-1.88, 0, 0], [-5.6, 1, -0.22]];  // y, part (a=1, b=0), phase
    for (const [y, part, ph] of rings) {
        for (let i = 0; i < n; i++) {
            const a = (2 * Math.PI * i) / n + ph + (part ? Math.PI / n : 0);
            const e1 = [Math.cos(a), 0, Math.sin(a)], e2 = [-Math.sin(a), 0, Math.cos(a)], e3 = [0, 1, 0];
            const shade = (rand() - 0.5) * 0.45;
            if (part) M.ell(cyl(3.75, a, y), [1.85, 1.6, 1.75], e1, e2, e3, 1, shade);
            else M.ell(cyl(3.95, a, y), [2.0, 1.7, 1.8], e1, e2, e3, 0, shade);
        }
        if (part) M.ell([0, y + Math.sign(y) * 0.9, 0], [1.6, 0.9, 1.6], [1, 0, 0], [0, 1, 0], [0, 0, 1], 1, 0); // closed gate
    }
    return M;
}

function ribosome(rand) {
    const M = new Model('ribosome70S', 1.3);
    const X = [1, 0, 0], Y = [0, 1, 0], Z = [0, 0, 1];
    // large subunit (part 0)
    M.ell([-4.6, -1.2, 0], [5.9, 7.6, 9.4], X, Y, Z, 0, 0.1);
    M.ell([-3.4, 2.4, 1.5], [4.6, 5.4, 7.0], X, Y, Z, 0, -0.1);
    M.sphere([-2.6, 7.6, 0.4], 3.1, 0, 0.15);                        // central protuberance
    M.capsule([-3.2, 3.4, 8.2], [-1.6, 6.6, 11.6], 1.55, 0, -0.15);  // L1 stalk
    M.capsule([-3.4, 1.6, -8.4], [-1.8, 2.6, -11.6], 1.15, 0, 0.2);  // L7/L12 stalk
    M.sphere([-1.6, 2.8, -11.9], 1.4, 0, 0.2);
    // small subunit (part 1), on the interface side
    M.ell([3.3, -2.6, 0.6], [3.1, 6.0, 6.4], X, Y, Z, 1, 0);         // body
    M.sphere([3.7, 6.3, -1.4], 3.5, 1, 0.12);                        // head
    M.sphere([6.0, 7.8, -3.6], 1.45, 1, 0.12);                       // beak
    M.sphere([2.6, 0.2, 6.4], 2.4, 1, -0.1);                         // platform
    M.sphere([4.3, 2.6, -5.4], 2.1, 1, 0.05);                        // shoulder
    M.sphere([4.2, -8.6, -2.0], 1.35, 1, -0.05);                     // spur
    // knobbly rRNA and ribosomal proteins
    const base = M.prims.slice();
    for (let i = 0; i < 64; i++) {
        const q = base[(rand() * base.length) | 0];
        if (q.t === 1) continue;
        const d = V.norm([rand() * 2 - 1, rand() * 2 - 1, rand() * 2 - 1]);
        const rr = q.t === 0 ? q.r : Math.min(...q.radii) * 1.05;
        const c = V.add(q.c, V.mul(d, rr * (0.8 + rand() * 0.15)));
        M.sphere(c, 1.1 + rand() * 1.4, q.part, q.shade + (rand() - 0.5) * 0.3);
    }
    return M;
}

function spike(rand, oneUp) {
    const M = new Model(oneUp ? 'spike-1up' : 'spike', 1.45);
    for (let k = 0; k < 3; k++) {
        const a = (2 * Math.PI * k) / 3, shade = [0.0, 0.35, -0.35][k];
        const up = oneUp && k === 0;
        M.capsule(cyl(0.9, a, -6.6), cyl(1.5, a + 0.3, 1.6), 1.75, 0, shade);   // S2 central helices
        M.sphere(cyl(2.3, a - 0.4, -4.0), 1.6, 0, shade);                        // connector / HR region
        M.sphere(cyl(3.2, a + 0.55, 1.6), 1.8, 0, shade);                        // SD1/SD2
        const e = (b) => [[Math.cos(b), 0, Math.sin(b)], [-Math.sin(b), 0, Math.cos(b)], [0, 1, 0]];
        M.ell(cyl(4.35, a - 0.35, 4.1), [2.5, 2.3, 2.5], ...e(a - 0.35), 0, shade); // NTD
        if (!up) M.sphere(cyl(2.25, a + 0.62, 6.9), 2.1, 0, shade);              // RBD down
        else {
            M.sphere(cyl(4.0, a + 0.75, 9.6), 1.95, 0, shade);                    // RBD up
            M.capsule(cyl(3.2, a + 0.6, 3.2), cyl(3.8, a + 0.72, 8.2), 1.1, 0, shade);
        }
    }
    // N-linked glycans: small pale knobs on the surface
    const base = M.prims.slice();
    for (let i = 0; i < 20; i++) {
        const q = base[(rand() * base.length) | 0];
        const d = V.norm([rand() * 2 - 1, rand() * 1.4 - 0.5, rand() * 2 - 1]);
        const rr = q.t === 0 ? q.r : q.t === 2 ? Math.min(...q.radii) : q.r;
        const c = q.t === 1 ? V.add(V.add(q.a, V.mul(q.ab, rand())), V.mul(d, rr * 0.95)) : V.add(q.c, V.mul(d, rr * 0.97));
        if (V.len([c[0], 0, c[2]]) < 2.2 && c[1] > 4) continue;   // keep the apex clean
        M.sphere(c, 0.42 + rand() * 0.18, 1, 0);
    }
    return M;
}

// ---------------------------------------------------------------------------------------------- mesher
function evaluator(M, noise, amp, freq) {
    const prims = M.prims, k = M.k, k4 = k * 0.25;
    // bin primitives on a coarse grid so each point only visits its neighbours
    let lo = [1e9, 1e9, 1e9], hi = [-1e9, -1e9, -1e9];
    for (const q of prims) for (let a = 0; a < 3; a++) { lo[a] = Math.min(lo[a], q.c[a] - q.R); hi[a] = Math.max(hi[a], q.c[a] + q.R); }
    const B = 1.6, reach = B * 0.87 + k + 1.2;
    lo = lo.map(v => v - 3); hi = hi.map(v => v + 3);
    const bx = Math.ceil((hi[0] - lo[0]) / B), by = Math.ceil((hi[1] - lo[1]) / B), bz = Math.ceil((hi[2] - lo[2]) / B);
    const bins = new Array(bx * by * bz);
    for (let kk = 0; kk < bz; kk++) for (let j = 0; j < by; j++) for (let i = 0; i < bx; i++) {
        const c = [lo[0] + (i + 0.5) * B, lo[1] + (j + 0.5) * B, lo[2] + (kk + 0.5) * B];
        const list = [];
        for (const q of prims) if (primDist(q, c[0], c[1], c[2]) < reach) list.push(q);
        bins[(kk * by + j) * bx + i] = list;
    }
    const f = (x, y, z) => {
        const i = Math.floor((x - lo[0]) / B), j = Math.floor((y - lo[1]) / B), kk = Math.floor((z - lo[2]) / B);
        if (i < 0 || j < 0 || kk < 0 || i >= bx || j >= by || kk >= bz) return 2.5;
        const list = bins[(kk * by + j) * bx + i];
        if (!list.length) return 2.5;
        let d = 1e9;
        for (let n = 0; n < list.length; n++) {
            const di = primDist(list[n], x, y, z);
            const hh = Math.max(k - Math.abs(d - di), 0) / k;
            d = Math.min(d, di) - hh * hh * k4;
        }
        if (M.hollowInner) d = Math.max(d, M.hollowInner - Math.sqrt(x * x + y * y + z * z));
        if (d > 1.2) return d;
        return d + amp * noise(x * freq, y * freq, z * freq) + amp * 0.45 * noise(x * freq * 2.3 + 7, y * freq * 2.3, z * freq * 2.3);
    };
    f.near = (x, y, z) => {
        const i = Math.floor((x - lo[0]) / B), j = Math.floor((y - lo[1]) / B), kk = Math.floor((z - lo[2]) / B);
        if (i < 0 || j < 0 || kk < 0 || i >= bx || j >= by || kk >= bz) return prims;
        const l = bins[(kk * by + j) * bx + i];
        return l.length ? l : prims;
    };
    return f;
}

function mesh(M, noise, h, centre) {
    const T0 = performance.now();
    const amp = M.amp ?? 0.11, freq = 0.55;
    const F = evaluator(M, noise, amp, freq);
    let lo = [1e9, 1e9, 1e9], hi = [-1e9, -1e9, -1e9];
    for (const q of M.prims) for (let a = 0; a < 3; a++) { lo[a] = Math.min(lo[a], q.c[a] - q.R); hi[a] = Math.max(hi[a], q.c[a] + q.R); }
    const pad = 2 * h + amp * 2;
    lo = lo.map(v => v - pad); hi = hi.map(v => v + pad);
    const nx = Math.ceil((hi[0] - lo[0]) / h) + 1, ny = Math.ceil((hi[1] - lo[1]) / h) + 1, nz = Math.ceil((hi[2] - lo[2]) / h) + 1;
    const field = new Float32Array(nx * ny * nz);
    for (let k = 0; k < nz; k++) for (let j = 0; j < ny; j++) for (let i = 0; i < nx; i++)
        field[(k * ny + j) * nx + i] = F(lo[0] + i * h, lo[1] + j * h, lo[2] + k * h);
    const at = (i, j, k) => field[(k * ny + j) * nx + i];

    const T1 = performance.now();
    const cx = nx - 1, cy = ny - 1, cz = nz - 1;
    const cellV = new Int32Array(cx * cy * cz).fill(-1);
    const pos = [];
    const EDGES = [];
    for (let a = 0; a < 8; a++) for (const bit of [1, 2, 4]) if (!(a & bit)) EDGES.push([a, a | bit]);
    const cv = new Float32Array(8);
    for (let k = 0; k < cz; k++) for (let j = 0; j < cy; j++) for (let i = 0; i < cx; i++) {
        let mask = 0;
        for (let c = 0; c < 8; c++) { cv[c] = at(i + (c & 1), j + ((c >> 1) & 1), k + ((c >> 2) & 1)); if (cv[c] < 0) mask |= 1 << c; }
        if (mask === 0 || mask === 255) continue;
        let sx = 0, sy = 0, sz = 0, n = 0;
        for (const [a, b] of EDGES) {
            const va = cv[a], vb = cv[b];
            if ((va < 0) === (vb < 0)) continue;
            const t = va / (va - vb);
            sx += (a & 1) + (((b & 1) - (a & 1)) * t); sy += ((a >> 1) & 1) + ((((b >> 1) & 1) - ((a >> 1) & 1)) * t); sz += ((a >> 2) & 1) + ((((b >> 2) & 1) - ((a >> 2) & 1)) * t);
            n++;
        }
        cellV[(k * cy + j) * cx + i] = pos.length / 3;
        pos.push(lo[0] + (i + sx / n) * h, lo[1] + (j + sy / n) * h, lo[2] + (k + sz / n) * h);
    }
    const idx = [];
    const cell = (i, j, k) => cellV[(k * cy + j) * cx + i];
    const quad = (a, b, c, d) => { if (a < 0 || b < 0 || c < 0 || d < 0) return; idx.push(a, b, c, a, c, d); };
    for (let k = 1; k < cz; k++) for (let j = 1; j < cy; j++) for (let i = 0; i < cx; i++)
        if ((at(i, j, k) < 0) !== (at(i + 1, j, k) < 0)) quad(cell(i, j - 1, k - 1), cell(i, j, k - 1), cell(i, j, k), cell(i, j - 1, k));
    for (let k = 1; k < cz; k++) for (let j = 0; j < cy; j++) for (let i = 1; i < cx; i++)
        if ((at(i, j, k) < 0) !== (at(i, j + 1, k) < 0)) quad(cell(i - 1, j, k - 1), cell(i, j, k - 1), cell(i, j, k), cell(i - 1, j, k));
    for (let k = 0; k < cz; k++) for (let j = 1; j < cy; j++) for (let i = 1; i < cx; i++)
        if ((at(i, j, k) < 0) !== (at(i, j, k + 1) < 0)) quad(cell(i - 1, j - 1, k), cell(i, j - 1, k), cell(i, j, k), cell(i - 1, j, k));

    const T2 = performance.now();
    // project onto the surface, then normals, occlusion and part tints
    const nv = pos.length / 3, out = new Float32Array(nv * 9);
    const e = h * 0.5;
    const grad = (x, y, z) => [F(x + e, y, z) - F(x - e, y, z), F(x, y + e, z) - F(x, y - e, z), F(x, y, z + e) - F(x, y, z - e)];
    let cmx = 0, cmy = 0, cmz = 0;
    const dist = new Float64Array(M.prims.length);
    for (let v = 0; v < nv; v++) {
        let x = pos[v * 3], y = pos[v * 3 + 1], z = pos[v * 3 + 2];
        const d0 = F(x, y, z), g = grad(x, y, z), gl = g[0] * g[0] + g[1] * g[1] + g[2] * g[2];
        if (gl > 1e-12) { const s = d0 * (2 * e) / gl; x -= g[0] * s; y -= g[1] * s; z -= g[2] * s; }
        const il = 1 / (Math.sqrt(gl) || 1), n0 = g[0] * il, n1 = g[1] * il, n2 = g[2] * il;
        // occlusion: how much the field closes in along the normal (crevices, ring interiors)
        let occ = 0;
        for (let s = 1; s <= 5; s++) { const dd = s * 0.55; occ += (dd - F(x + n0 * dd, y + n1 * dd, z + n2 * dd)) / (1 << (s - 1)); }
        const ao = Math.max(0, Math.min(1, 1 - occ * 0.42));
        // soft part assignment by nearest primitives
        const list = F.near(x, y, z);
        let wsum = 0, part = 0, shade = 0, dmin = 1e9;
        for (let i = 0; i < list.length; i++) { const dd = primDist(list[i], x, y, z); dist[i] = dd; if (dd < dmin) dmin = dd; }
        for (let i = 0; i < list.length; i++) {
            const w = Math.exp(-(dist[i] - dmin) / 0.22);
            if (w < 1e-3) continue;
            wsum += w; part += w * list[i].part; shade += w * list[i].shade;
        }
        const o = v * 9;
        out[o] = x; out[o + 1] = y; out[o + 2] = z; out[o + 3] = n0; out[o + 4] = n1; out[o + 5] = n2;
        // shade: the primitive's tone plus a broad hand-dyed mottling of the felt
        const mott = noise(x * 1.9 + 11, y * 1.9, z * 1.9) * 0.33 + noise(x * 0.8 + 3, y * 0.8 + 5, z * 0.8) * 0.25;
        out[o + 6] = part / wsum; out[o + 7] = ao; out[o + 8] = shade / wsum + mott;
        cmx += x; cmy += y; cmz += z;
    }
    cmx /= nv; cmy /= nv; cmz /= nv;
    if (centre) [cmx, cmy, cmz] = centre;
    let rad = 0;
    for (let v = 0; v < nv; v++) {
        out[v * 9] -= cmx; out[v * 9 + 1] -= cmy; out[v * 9 + 2] -= cmz;
        rad = Math.max(rad, Math.hypot(out[v * 9], out[v * 9 + 1], out[v * 9 + 2]));
    }
    if (globalThis.__sgTime) console.log(M.name, 'field', (T1 - T0).toFixed(0), 'nets', (T2 - T1).toFixed(0), 'verts', (performance.now() - T2).toFixed(0));
    return { name: M.name, verts: out, idx: new Uint32Array(idx), radius: rad, centre: [cmx, cmy, cmz] };
}

export const SPECIES = ['apoferritin', 'groel', 'proteasome', 'ribosome', 'spike'];
// mesh index per species variant
export const MESH = { apoferritin: 0, groel: 1, groes: 2, proteasome: 3, ribosome: 4, spike: 5, spikeUp: 6, ice: 7 };

// a hexagonal ice-Ih plate: the classic crystalline contaminant (here a modest 40 nm across)
function iceCrystal() {
    const M = new Model('ice-Ih', 0.6);
    M.amp = 0.35;
    M.hex([0, 0, 0], 18, 3.2, 1.6);
    return M;
}

export function buildMeshes(rand) {
    const defs = [
        [apoferritin(rand), 0.27], [groel(rand, false), 0.33], [groel(rand, true), 0.35], [proteasome(rand), 0.32],
        [ribosome(rand), 0.45], [spike(rand, false), 0.31], [spike(rand, true), 0.31], [iceCrystal(), 0.7],
    ];
    // full detail for the sharp layers, and a coarse copy (same shape) for the blurred ones
    return defs.map(([M, h]) => {
        const noise = makeNoise(rand);
        const full = mesh(M, noise, h * 1.45);
        full.lod = mesh(M, noise, h * 2.8, full.centre);
        return full;
    });
}
