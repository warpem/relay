// 13 · Darkroom — 2D classification as a photographer's darkroom.
// Two cotton drying lines hold small square prints with wooden clothespins. Every print is a class
// average: it starts as a disc of low-dose noise inside a circular mask and, as more particle images
// are averaged into it (noise falls as 1/sqrt(N), resolution sharpens), a crisp projection of the
// particle emerges. Particles are procedural pseudo-atomic 3D models (rings, proteasome-like barrels,
// trimeric spikes, ferritin-like shells, ribosomes, membrane proteins in micelles or nanodiscs, helical
// filaments, antibodies, small asymmetric enzymes) projected analytically at real orientations. Junk
// classes slip one pin, dangle, and then flutter to the floor (a quasi-steady falling-plate model).
// Taped beside the lines is a print of one micrograph of the current dataset (CTF via FFT), on which
// someone slowly circles particles with a red grease pencil. The scene is drawn as albedo and lit by a
// low-res light map (multiply): sunlight through a window by day, a muted red-amber safelight by night.

export const meta = {
    title: 'Darkroom',
    tagline: 'Class averages developing on the drying line',
    description: 'Small square prints hang from cotton lines with wooden clothespins, each one a 2D class average ' +
        'slowly developing from low-dose noise into a crisp projection, while junk classes slip a pin and flutter to the floor; ' +
        'beside them a micrograph print is being picked with a red grease pencil. ' +
        'By day it is a sunlit studio wall with a window patch drifting across it; by night the same wall under a dim red-amber safelight. ' +
        'New datasets bring new specimens, views, box sizes and micrographs, so the line never shows the same picture twice.',
    renderer: 'p2d',
    fps: 30,
    pixelDensity: 1.25,
    maxStep: 1 / 15,
    post: { paper: 0.22, grain: 0.3, vignette: 0.34 },
};

const PI = Math.PI, TAU = PI * 2;
const clamp = (v, a = 0, b = 1) => (v < a ? a : v > b ? b : v);
const smooth = t => { t = clamp(t); return t * t * (3 - 2 * t); };
const lerp = (a, b, t) => a + (b - a) * t;
const hashf = str => { let v = 2166136261; for (let i = 0; i < str.length; i++) v = Math.imul(v ^ str.charCodeAt(i), 16777619); return (v >>> 0) / 4294967296; };

function mulberry(a) {
    return () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
const gauss = rnd => { let u = 0, v = 0; while (u === 0) u = rnd(); v = rnd(); return Math.sqrt(-2 * Math.log(u)) * Math.cos(TAU * v); };
const canvas = (w, h) => { const c = document.createElement('canvas'); c.width = Math.max(1, Math.ceil(w)); c.height = Math.max(1, Math.ceil(h)); return c; };

// ---------- FFT (radix-2, in place) ----------
function fft1(re, im, off, stride, n, inv) {
    for (let i = 1, j = 0; i < n; i++) {
        let bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) {
            const a = off + i * stride, b = off + j * stride;
            let t = re[a]; re[a] = re[b]; re[b] = t; t = im[a]; im[a] = im[b]; im[b] = t;
        }
    }
    for (let len = 2; len <= n; len <<= 1) {
        const ang = (inv ? 2 : -2) * PI / len, wr = Math.cos(ang), wi = Math.sin(ang);
        for (let i = 0; i < n; i += len) {
            let cr = 1, ci = 0;
            for (let k = 0; k < len / 2; k++) {
                const a = off + (i + k) * stride, b = off + (i + k + len / 2) * stride;
                const xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                re[b] = re[a] - xr; im[b] = im[a] - xi; re[a] += xr; im[a] += xi;
                const ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
            }
        }
    }
}

// ---------- rotations ----------
function randRot(rnd) { // uniform on SO(3) (Shoemake)
    const u1 = rnd(), u2 = rnd() * TAU, u3 = rnd() * TAU;
    const a = Math.sqrt(1 - u1), b = Math.sqrt(u1);
    return quatMat(a * Math.sin(u2), a * Math.cos(u2), b * Math.sin(u3), b * Math.cos(u3));
}
function quatMat(x, y, z, w) {
    return [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
        2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
        2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)];
}
const mul3 = (A, B) => { const C = new Array(9); for (let i = 0; i < 3; i++) for (let j = 0; j < 3; j++) C[i * 3 + j] = A[i * 3] * B[j] + A[i * 3 + 1] * B[3 + j] + A[i * 3 + 2] * B[6 + j]; return C; };
const rotZ = a => [Math.cos(a), -Math.sin(a), 0, Math.sin(a), Math.cos(a), 0, 0, 0, 1];
const rotX = a => [1, 0, 0, 0, Math.cos(a), -Math.sin(a), 0, Math.sin(a), Math.cos(a)];
// psi in-plane, tilt of the model's z (symmetry or filament) axis away from the beam, phi about that axis
const viewMat = (tilt, phi, psi) => mul3(rotZ(psi), mul3(rotX(tilt), rotZ(phi)));

// ---------- procedural specimens: pseudo-atomic Gaussian models (box units, box = [-1,1]) ----------
const KINDS = ['ring', 'barrel', 'spike', 'shell', 'ribosome', 'membrane', 'filament', 'igg', 'small', 'dumbbell'];
const NM = { ring: 14, barrel: 30, spike: 16, shell: 12.5, ribosome: 27, membrane: 14, igg: 15, small: 8.5, dumbbell: 16 }; // nm across the model
function makeModel(kind, rnd) {
    const A = [];
    const add = (x, y, z, s, w = 1) => A.push([x, y, z, s, w]);
    const sub = (x, y, z, s, w, n, spread) => { // a domain: one blob plus a few smaller "secondary structure" lumps
        add(x, y, z, s, w);
        for (let k = 0; k < n; k++) add(x + gauss(rnd) * spread, y + gauss(rnd) * spread, z + gauss(rnd) * spread, s * 0.5, w * 0.5);
    };
    let rough = false;
    const blob = (cx, cy, cz, rx, ry, rz, n, s, w) => { // n Gaussians filling an ellipsoid
        for (let i = 0; i < n; i++) {
            let x, y, z; do { x = rnd() * 2 - 1; y = rnd() * 2 - 1; z = rnd() * 2 - 1; } while (x * x + y * y + z * z > 1);
            add(cx + x * rx, cy + y * ry, cz + z * rz, s * (0.8 + rnd() * 0.4), w * (rough ? 0.55 + rnd() * 0.9 : 1));
        }
    };
    let symAxis = true;
    if (kind === 'ring') {
        const n = [6, 7, 7, 8, 9][(rnd() * 5) | 0], R = 0.34 + rnd() * 0.05, dbl = rnd() < 0.45;
        const unit = []; // one protomer, then apply C_n (and D_n for a double ring)
        unit.push([R - 0.08, 0, 0, 0.075, 1], [R + 0.07, 0.07, 0.02, 0.085, 1], [R + 0.01, -0.06, 0.09, 0.05, 0.7]);
        for (let k = 0; k < 3; k++) unit.push([R + gauss(rnd) * 0.05, gauss(rnd) * 0.05, gauss(rnd) * 0.04, 0.04, 0.5]);
        const z0 = dbl ? 0.13 : 0;
        for (let i = 0; i < n; i++) {
            const a = (i / n) * TAU, c = Math.cos(a), s = Math.sin(a);
            for (const [x, y, z, sg, w] of unit) {
                add(c * x - s * y, s * x + c * y, z + z0, sg, w);
                if (dbl) add(c * x - s * y, -(s * x + c * y), -(z + z0), sg, w); // D_n: 2-fold about x, (x,y,z)->(x,-y,-z)
            }
        }
    } else if (kind === 'barrel') { // proteasome-like: four stacked heptameric rings (a7 b7 b7 a7), with 0, 1 or 2 regulatory lids
        const n = 7, rings = [[-0.36, 0.36], [-0.12, 0.39], [0.12, 0.39], [0.36, 0.36]];
        rings.forEach(([z, r], ri) => {
            for (let i = 0; i < n; i++) {
                const a = (i / n) * TAU + ri * 0.22;
                add(Math.cos(a) * r, Math.sin(a) * r, z, 0.1, 1);
                add(Math.cos(a + 0.32) * (r + 0.03), Math.sin(a + 0.32) * (r + 0.03), z + 0.05, 0.06, 0.7);
                add(Math.cos(a - 0.2) * (r - 0.07), Math.sin(a - 0.2) * (r - 0.07), z - 0.04, 0.05, 0.6);
            }
        });
        const lid = (sign, rot) => { // a ring of ATPases plus an asymmetric regulatory bulk
            for (let i = 0; i < 6; i++) { const a = (i / 6) * TAU + rot; add(Math.cos(a) * 0.34, Math.sin(a) * 0.34, sign * 0.6, 0.1, 0.6); }
            const ox = Math.cos(rot) * 0.1, oy = Math.sin(rot) * 0.1;
            for (let i = 0; i < 12; i++) add(ox + gauss(rnd) * 0.24, oy + gauss(rnd) * 0.18, sign * (0.74 + rnd() * 0.18), 0.1 + rnd() * 0.03, 0.4);
            add(ox * 3.8, oy * 3.8, sign * 0.68, 0.1, 0.5); // the "hinge" lobe sticking out to one side
        };
        const lids = rnd() < 0.3 ? 0 : rnd() < 0.7 ? 1 : 2;
        if (lids > 0) lid(1, rnd() * TAU);
        if (lids > 1) lid(-1, rnd() * TAU);
    } else if (kind === 'dumbbell') { // two unequal lobes on a short neck, slightly bent
        const r1 = 0.26 + rnd() * 0.05, r2 = 0.2 + rnd() * 0.06, bend = (rnd() - 0.5) * 0.3, d = 0.27 + rnd() * 0.05;
        for (let i = 0; i < 14; i++) sub(gauss(rnd) * r1 * 0.45, gauss(rnd) * r1 * 0.45, d + gauss(rnd) * r1 * 0.4, 0.12, 1, 2, 0.07);
        for (let i = 0; i < 10; i++) sub(bend + gauss(rnd) * r2 * 0.45, gauss(rnd) * r2 * 0.45, -d + gauss(rnd) * r2 * 0.4, 0.11, 1, 2, 0.07);
        add(bend * 0.4, 0, 0.02, 0.11, 1); add(bend * 0.6, 0.02, -0.08, 0.1, 1);
        symAxis = false;
    } else if (kind === 'spike') { // trimeric glycoprotein: a coiled-coil stalk and three splayed head domains
        const tw = 0.6 + rnd() * 0.6, splay = 0.13 + rnd() * 0.07;
        for (let k = 0; k < 3; k++) {
            const a0 = (k / 3) * TAU;
            for (let j = 0; j < 6; j++) {
                const z = -0.5 + j * 0.08, a = a0 + z * tw;
                add(Math.cos(a) * 0.07, Math.sin(a) * 0.07, z, 0.07, 0.9);
            }
            for (let j = 0; j < 5; j++) {
                const z = -0.04 + j * 0.1, r = 0.11 + splay * Math.sin((j / 4) * PI * 0.85), a = a0 + z * tw + 0.35;
                sub(Math.cos(a) * r, Math.sin(a) * r, z, 0.105 + rnd() * 0.02, 1, 2, 0.05);
            }
        }
    } else if (kind === 'shell') { // 24 four-helix-bundle subunits with octahedral (432) symmetry, like apoferritin
        const rots = [];
        const perms = [[0, 1, 2], [1, 2, 0], [2, 0, 1], [1, 0, 2], [0, 2, 1], [2, 1, 0]];
        for (const pm of perms) for (let sx = -1; sx <= 1; sx += 2) for (let sy = -1; sy <= 1; sy += 2) for (let sz = -1; sz <= 1; sz += 2) {
            const M = [0, 0, 0, 0, 0, 0, 0, 0, 0], sg = [sx, sy, sz];
            for (let i = 0; i < 3; i++) M[i * 3 + pm[i]] = sg[i];
            const det = M[0] * (M[4] * M[8] - M[5] * M[7]) - M[1] * (M[3] * M[8] - M[5] * M[6]) + M[2] * (M[3] * M[7] - M[4] * M[6]);
            if (det > 0) rots.push(M);
        }
        const R = 0.44, d = [0.62, 0.27, 0.74], dl = Math.hypot(...d), u = d.map(v => v / dl);
        let t = [-u[1], u[0], 0]; const tl = Math.hypot(...t); t = t.map(v => v / tl);
        const t2 = [u[1] * t[2] - u[2] * t[1], u[2] * t[0] - u[0] * t[2], u[0] * t[1] - u[1] * t[0]];
        const unit = [];
        for (let k = -2; k <= 2; k++) for (let j = -1; j <= 1; j += 2) {
            const a = k * 0.075, b = j * 0.05, rr = R + j * 0.012;
            unit.push([u[0] * rr + t[0] * a + t2[0] * b, u[1] * rr + t[1] * a + t2[1] * b, u[2] * rr + t[2] * a + t2[2] * b, 0.088, 1]);
        }
        for (const M of rots) for (const [x, y, z, s, w] of unit) add(M[0] * x + M[1] * y + M[2] * z, M[3] * x + M[4] * y + M[5] * z, M[6] * x + M[7] * y + M[8] * z, s, w);
    } else if (kind === 'ribosome') { // large subunit with central protuberance, L1 and P stalks; small subunit with head, body, platform
        rough = true; // rRNA helices make the density lumpy
        blob(0, 0, 0.14, 0.46, 0.42, 0.3, 56, 0.11, 1);
        blob(0.03, -0.02, 0.5, 0.15, 0.13, 0.1, 7, 0.08, 1);
        blob(0.47, 0.12, 0.33, 0.07, 0.07, 0.16, 5, 0.06, 0.8);
        blob(-0.5, -0.04, 0.22, 0.05, 0.05, 0.14, 4, 0.05, 0.7);
        blob(0.06, 0.02, -0.27, 0.38, 0.3, 0.15, 30, 0.1, 1);
        blob(-0.25, 0.03, -0.4, 0.17, 0.15, 0.11, 9, 0.085, 1);
        blob(0.35, 0.0, -0.31, 0.1, 0.09, 0.08, 4, 0.07, 0.9);
        symAxis = false;
    } else if (kind === 'membrane') { // a C_n channel/transporter in a detergent micelle or lipid nanodisc, with a soluble domain
        const n = [3, 4, 4, 5][(rnd() * 4) | 0], per = 2 + ((rnd() * 3) | 0), nano = rnd() < 0.55, Rb = 0.5 + rnd() * 0.08, th = 0.16;
        for (let i = 0; i < 80; i++) { const a = rnd() * TAU, r = Math.sqrt(rnd()) * Rb; add(Math.cos(a) * r, Math.sin(a) * r, (rnd() - 0.5) * th * 1.6, 0.12, 0.32); }
        if (nano) for (let i = 0; i < 36; i++) { const a = (i / 36) * TAU; for (const z of [-0.06, 0.06]) add(Math.cos(a) * (Rb + 0.05), Math.sin(a) * (Rb + 0.05), z, 0.05, 0.45); }
        for (let k = 0; k < n; k++) for (let j = 0; j < per; j++) { // transmembrane helices crossing the bilayer
            const a0 = (k / n) * TAU + j * 0.55, r = 0.1 + j * 0.075, tilt = 0.5 + rnd() * 0.4;
            for (let t = -3; t <= 3; t++) { const z = t * 0.055, a = a0 + z * tilt; add(Math.cos(a) * r, Math.sin(a) * r, z, 0.045, 0.9); }
        }
        const top = 0.3 + rnd() * 0.06, ecd = rnd() < 0.75;
        for (let k = 0; k < n; k++) {
            const a = (k / n) * TAU + 0.3;
            if (ecd) sub(Math.cos(a) * 0.19, Math.sin(a) * 0.19, top + rnd() * 0.05, 0.1, 1, 3, 0.06);
            if (!ecd || rnd() < 0.5) sub(Math.cos(a + 0.4) * 0.14, Math.sin(a + 0.4) * 0.14, -0.3, 0.085, 0.85, 2, 0.05);
        }
    } else if (kind === 'filament') { // helical assembly: subunits placed by rise and twist along z
        const rod = rnd() < 0.45;
        const helix = rod // a TMV-like rod (16.33 subunits per 2.3 nm turn, central canal), or a two-stranded actin-like helix (rise 2.75 nm, twist -166.6 deg)
            ? { rise: 0.082 / 16.33, twist: TAU / 16.33, unit: [[0.14, 0, 0, 0.05, 0.75], [0.25, 0.15, 0.012, 0.06, 1]], nm: 28, dimer: 0 }
            : { rise: 0.148, twist: -166.6 * PI / 180, unit: [[0.12, 0, 0, 0.08, 1], [0.06, 0.9, 0.04, 0.065, 0.9], [0.17, -0.5, -0.03, 0.05, 0.6]], nm: 18.6, dimer: 0 };
        const atoms = [];
        for (let j = -Math.ceil(1.6 / helix.rise); j * helix.rise < 1.6; j++) {
            const wj = helix.dimer ? (Math.floor(j / helix.dimer) & 1 ? 0.86 : 1) : 1;
            for (const [r, da, dz, s, w] of helix.unit) { const a = j * helix.twist + da; atoms.push(r * Math.cos(a), r * Math.sin(a), j * helix.rise + dz, s, w * wj); }
        }
        return { kind, atoms: new Float32Array(atoms), symAxis: 'fil', helix };
    } else if (kind === 'igg') { // antibody: two Fab arms and an Fc, each a pair of Ig-domain pairs, on a flexible hinge
        const spread = 0.55 + rnd() * 0.4, arms = [PI / 2 + spread + (rnd() - 0.5) * 0.3, PI / 2 - spread + (rnd() - 0.5) * 0.3, -PI / 2 + (rnd() - 0.5) * 0.4];
        for (const a of arms) {
            const dx = Math.cos(a), dy = Math.sin(a), dz = (rnd() - 0.5) * 0.5;
            for (let d = 0; d < 2; d++) {
                const L = 0.17 + d * 0.23;
                for (const side of [-1, 1]) sub(dx * L - dy * side * 0.07, dy * L + dx * side * 0.07, dz * L + side * 0.03, 0.08, 1, 2, 0.035);
            }
        }
        symAxis = false;
    } else { // small asymmetric enzyme: two to four globular domains
        const nd = 2 + ((rnd() * 3) | 0);
        let x = 0, y = 0, z = 0, rPrev = 0;
        for (let d = 0; d < nd; d++) {
            const r = 0.16 + rnd() * 0.1;
            if (d) { const u = randRot(rnd), k = (rPrev + r) * 0.85; x += u[0] * k; y += u[3] * k; z += u[6] * k; }
            blob(x, y, z, r, r * (0.8 + rnd() * 0.3), r * (0.8 + rnd() * 0.3), 10 + ((r * 40) | 0), 0.1, 1);
            rPrev = r;
        }
        symAxis = false;
    }
    // centre on the mass and fit inside the mask
    let cx = 0, cy = 0, cz = 0, W = 0;
    for (const a of A) { const m = a[4] * a[3] ** 3; cx += a[0] * m; cy += a[1] * m; cz += a[2] * m; W += m; }
    cx /= W; cy /= W; cz /= W;
    let rmax = 0;
    for (const a of A) { a[0] -= cx; a[1] -= cy; a[2] -= cz; rmax = Math.max(rmax, Math.hypot(a[0], a[1], a[2]) + 1.6 * a[3]); }
    const k = 0.64 / rmax;
    const atoms = new Float32Array(A.length * 5);
    A.forEach((a, i) => { atoms.set([a[0] * k, a[1] * k, a[2] * k, a[3] * k, a[4]], i * 5); });
    return { kind, atoms, symAxis };
}

// a 2D Gaussian splat into out[G*G] (pixel coordinates)
function splat(out, G, px, py, sp, amp) {
    const r = Math.ceil(sp * 3), inv = -1 / (2 * sp * sp);
    const x0 = Math.max(0, (px - r) | 0), x1 = Math.min(G - 1, (px + r) | 0), y0 = Math.max(0, (py - r) | 0), y1 = Math.min(G - 1, (py + r) | 0);
    for (let yy = y0; yy <= y1; yy++) {
        const dy = yy + 0.5 - py, row = yy * G;
        for (let xx = x0; xx <= x1; xx++) { const dx = xx + 0.5 - px; out[row + xx] += amp * Math.exp((dx * dx + dy * dy) * inv); }
    }
}
// accumulate the analytic projection (sum of 2D Gaussians) of a model into out[G*G]
function project(atoms, M, out, G, scale, cx, cy, extra, gain) {
    for (let i = 0; i < atoms.length; i += 5) {
        const x = atoms[i], y = atoms[i + 1], z = atoms[i + 2], s = atoms[i + 3], w = atoms[i + 4];
        const X = M[0] * x + M[1] * y + M[2] * z, Y = M[3] * x + M[4] * y + M[5] * z;
        const s2 = s * s + extra * extra;
        splat(out, G, cx + X * scale, cy - Y * scale, Math.sqrt(s2) * scale, gain * w * s * s * s / s2);
    }
}

// ---------- class averages ----------
const G = 64; // class-average box (pixels)

function pickView(roll, rnd) {
    const u = rnd(), m = roll.model;
    let tilt;
    if (m.symAxis === 'fil') tilt = PI / 2 + gauss(rnd) * 0.035; // filaments lie flat in the ice
    else if (!m.symAxis) tilt = Math.acos(1 - 2 * rnd());      // no symmetry axis: plain random orientations
    else if (u < roll.pTop) tilt = Math.abs(gauss(rnd)) * 0.12;
    else if (u < roll.pTop + roll.pSide) tilt = PI / 2 + gauss(rnd) * 0.1;
    else tilt = Math.acos(1 - 2 * rnd());
    return viewMat(tilt, rnd() * TAU, rnd() * TAU);
}

function buildSignal(pr, roll) {
    const rnd = mulberry(pr.seed), px = roll.px;
    const S = new Float32Array(G * G), B = new Float32Array(G * G), Hl = new Float32Array(G * G), c = G / 2;
    let M = null;
    if (!pr.junk) {
        M = pickView(roll, rnd);
        project(roll.model.atoms, M, S, G, px, c, c, 0, 1);
        project(roll.model.atoms, M, B, G, px, c, c, 4 / px, 1);
    } else if (pr.junk === 'ice') {
        const ox = gauss(rnd) * 0.05, oy = gauss(rnd) * 0.05, r = (0.3 + rnd() * 0.2) * 34 / px;
        for (let i = 0; i < G * G; i++) {
            const x = ((i % G) + 0.5 - c) / px - ox, y = (((i / G) | 0) + 0.5 - c) / px - oy;
            const d = Math.hypot(x, y * 1.1) / (1 + 0.1 * Math.sin(Math.atan2(y, x) * 3 + r * 9));
            S[i] = 0.85 * smooth((r - d) / 0.25 + 0.5) - 0.15 * Math.exp(-((d - r - 0.12) ** 2) / 0.008);
        }
        B.set(S);
    } else if (pr.junk === 'edge') {
        const a = rnd() * TAU, nx = Math.cos(a), ny = Math.sin(a), off = (rnd() - 0.5) * 0.3, curv = (rnd() - 0.5) * 0.6;
        for (let i = 0; i < G * G; i++) {
            const x = ((i % G) + 0.5 - c) / 34, y = (((i / G) | 0) + 0.5 - c) / 34;
            const along = -ny * x + nx * y, d = nx * x + ny * y - off - curv * along * along;
            S[i] = 0.3 / (1 + Math.exp(-d / 0.07)) + 0.22 * Math.exp(-(d * d) / 0.004) - 0.15 * Math.exp(-((d + 0.1) ** 2) / 0.005);
        }
        B.set(S);
    } else if (pr.junk === 'noise') { // "Einstein from noise": aligned noise averages to a faint central smudge
        for (let i = 0; i < G * G; i++) {
            const x = ((i % G) + 0.5 - c) / 34, y = (((i / G) | 0) + 0.5 - c) / 34;
            S[i] = 0.3 * Math.exp(-(x * x + y * y) / 0.06);
        }
        B.set(S);
    } else { // broken / aggregated: a blurry fragment of the particle, off centre
        const R = randRot(rnd), a = roll.model.atoms, keep = [];
        const cut = [gauss(rnd), gauss(rnd), gauss(rnd)];
        for (let i = 0; i < a.length; i += 5) if (a[i] * cut[0] + a[i + 1] * cut[1] + a[i + 2] * cut[2] > -0.05) keep.push(...a.slice(i, i + 5));
        project(new Float32Array(keep), R, S, G, px, c + gauss(rnd) * 3, c + gauss(rnd) * 3, 4.4 / px, 1);
        B.set(S);
    }
    if (!pr.junk || pr.junk === 'broken') {
        const srt = Float32Array.from(S).sort(), mx = srt[(G * G * 0.995) | 0];
        const k = (pr.junk ? 0.6 : 1) / (mx || 1);
        for (let i = 0; i < G * G; i++) { S[i] *= k; B[i] *= k; }
        if (pr.junk) B.set(S);
        // imperfect CTF correction suppresses the lowest frequencies: a soft dark halo around the particle
        if (M && roll.halo > 0.02) {
            project(roll.model.atoms, M, Hl, G, px, c, c, 10 / px, k * roll.halo);
            for (let i = 0; i < G * G; i++) { S[i] -= Hl[i]; B[i] -= Hl[i]; }
        }
    }
    pr.S = S; pr.B = B;
}

// ---------- one micrograph of the dataset (256 px, CTF via FFT), with the picks made on it ----------
const MN = 256;
function* micrographJob(roll, seed) { // a generator: the work is spread over many frames
    const r = mulberry(seed), N = MN, model = roll.model, hx = model.helix;
    const pix = roll.mgPix;                                          // A/px of the (binned) micrograph
    const pxu = (hx ? hx.nm : NM[model.kind] / 1.28) * 10 / pix;     // micrograph px per model unit
    const re = new Float64Array(N * N), im = new Float64Array(N * N), amp = new Float32Array(N * N), tmp = new Float32Array(N * N);
    // ice: a gentle thickness gradient across the hole
    const ga = r() * TAU, gs = 0.12 + r() * 0.22;
    for (let i = 0; i < N * N; i++) amp[i] = gs * (((i % N) / N - 0.5) * Math.cos(ga) + ((i / N | 0) / N - 0.5) * Math.sin(ga));
    // the rim of the hole: amorphous carbon (its own phase texture) beyond a large circle
    let onCarbon = () => false;
    if (r() < 0.4) {
        const a = r() * TAU, nx = Math.cos(a), ny = Math.sin(a), off = N * (0.32 + r() * 0.12), R = N * (3 + r() * 3);
        const hx0 = N / 2 - nx * (R - off), hy0 = N / 2 - ny * (R - off), cd = (x, y) => Math.hypot(x - hx0, y - hy0) - R;
        onCarbon = (x, y) => cd(x, y) > -6;
        for (let i = 0; i < N * N; i++) {
            const e = 1 / (1 + Math.exp(-cd(i % N, i / N | 0) / 1.5));
            if (e > 0.001) { re[i] += e * (0.9 + 0.4 * gauss(r)); amp[i] += 0.16 * e; }
        }
    }
    // ice contamination: dense, slightly irregular blobs
    const blobs = [];
    for (let k = r() < 0.45 ? 0 : 1 + (r() < 0.3 ? 1 : 0); k > 0; k--) {
        const bx = 20 + r() * (N - 40), by = 20 + r() * (N - 40), rad = 5 + r() * 9, ph = r() * TAU;
        blobs.push({ x: bx, y: by, rad });
        for (let i = 0; i < N * N; i++) {
            const dx = (i % N) - bx, dy = (i / N | 0) - by, d = Math.hypot(dx, dy);
            if (d > rad + 6) continue;
            const e = smooth((rad * (1 + 0.12 * Math.sin(3 * Math.atan2(dy, dx) + ph)) - d) / 2 + 0.5);
            re[i] += 2.2 * e; amp[i] += 0.9 * e;
        }
    }
    yield;
    // particles (or filaments)
    const D = 1.28 * pxu, picks = [];
    if (!hx) {
        const want = Math.round((0.13 + r() * 0.14) * N * N / (D * D * 0.785)), pos = [];
        for (let j = 0; j < want * 8 && pos.length < want; j++) {
            let x, y, agg = false;
            if (pos.length && r() < 0.07) { const o = pos[(r() * pos.length) | 0], a = r() * TAU; x = o[0] + Math.cos(a) * D * 0.82; y = o[1] + Math.sin(a) * D * 0.82; o[2] = agg = true; }
            else { x = -D / 2 + r() * (N + D); y = -D / 2 + r() * (N + D); }
            if (!agg && pos.some(([a, b]) => Math.hypot(a - x, b - y) < D * 1.02)) continue;
            if (blobs.some(b => Math.hypot(b.x - x, b.y - y) < b.rad + D * 0.55)) continue;
            pos.push([x, y, agg]);
        }
        for (let j = 0; j < pos.length; j++) {
            project(model.atoms, pickView(roll, r), tmp, N, pxu, pos[j][0], pos[j][1], 0, 1);
            if (j % 3 === 2) yield;
        }
        for (const [x, y, agg] of pos) {
            if (agg || onCarbon(x, y) || x < D * 0.6 || y < D * 0.6 || x > N - D * 0.6 || y > N - D * 0.6 || r() > 0.88) continue;
            picks.push({ type: 'o', x, y, rad: D * (0.6 + r() * 0.06), a0: r() * TAU, e1: 0.03 + r() * 0.04, e2: r() * TAU });
        }
    } else { // filaments: gently curving, subunits placed by rise and twist along the arc
        const nf = 2 + ((r() * 3) | 0), step = hx.rise * pxu;
        for (let f = 0; f < nf; f++) {
            let x = r() * N, y = r() * N, hd = r() * TAU;
            const L = (0.6 + r() * 0.8) * N, pts = [];
            x -= Math.cos(hd) * L / 2; y -= Math.sin(hd) * L / 2;
            const bend = gauss(r) * 0.0015, phi0 = r() * TAU;
            let j = 0, acc = 0;
            for (let s = 0; s < L; s += 1) {
                hd += bend + gauss(r) * 0.003; x += Math.cos(hd); y += Math.sin(hd); acc += 1;
                if (s % 8 === 0) pts.push([x, y]);
                while (acc >= step) {
                    acc -= step;
                    const tx = Math.cos(hd), ty = Math.sin(hd), wj = hx.dimer ? (Math.floor(j / hx.dimer) & 1 ? 0.86 : 1) : 1;
                    for (const [ru, da, dz, sg, w] of hx.unit) {
                        const off = ru * Math.cos(phi0 + j * hx.twist + da) * pxu, al = dz * pxu;
                        splat(tmp, N, x - ty * off + tx * al, y + tx * off + ty * al, sg * pxu, w * wj * sg);
                    }
                    j++;
                }
            }
            const inside = pts.filter(([a, b]) => a > 8 && b > 8 && a < N - 8 && b < N - 8 && !onCarbon(a, b));
            if (inside.length > 6) picks.push({ type: 'line', pts: inside, a0: r() * TAU });
            yield;
        }
    }
    let mx = 0;
    for (let i = 0; i < N * N; i++) mx = Math.max(mx, tmp[i]);
    for (let i = 0; i < N * N; i++) re[i] += (tmp[i] / (mx || 1)) * 2.6;
    for (const b of blobs) picks.push({ type: 'x', x: b.x, y: b.y, rad: b.rad + 5, a0: r() * 0.6 });
    // CTF: chi = pi*lambda*df*k^2 - pi/2*Cs*lambda^3*k^4, with astigmatism, amplitude contrast and a B-factor
    for (let row = 0; row < N; row++) fft1(re, im, row * N, 1, N, false);
    yield;
    for (let col = 0; col < N; col++) fft1(re, im, col, N, N, false);
    yield;
    const lam = 0.01969, Cs = 2.7e7, Ac = 0.07 + r() * 0.04, df = 11000 + r() * 21000, dfa = (r() - 0.5) * 1200, aa = r() * PI, Bf = 80 + r() * 80;
    const lp = 1 / (2 * (0.32 / pix) ** 2); // the display low-pass people use to look at micrographs
    for (let v = 0; v < N; v++) for (let u = 0; u < N; u++) {
        const fu = (u < N / 2 ? u : u - N) / (N * pix), fv = (v < N / 2 ? v : v - N) / (N * pix), k2 = fu * fu + fv * fv;
        const dfi = df + dfa * Math.cos(2 * (Math.atan2(fv, fu) - aa));
        const chi = PI * lam * dfi * k2 - 0.5 * PI * Cs * lam ** 3 * k2 * k2;
        const H = (Math.sqrt(1 - Ac * Ac) * Math.sin(chi) + Ac * Math.cos(chi)) * Math.exp(-Bf * k2 / 4 - k2 * lp);
        re[v * N + u] *= H; im[v * N + u] *= H;
    }
    for (let row = 0; row < N; row++) fft1(re, im, row * N, 1, N, true);
    yield;
    for (let col = 0; col < N; col++) fft1(re, im, col, N, N, true);
    // underfocus phase contrast: particles appear dark (standard micrograph convention); then shot noise
    const out = new Float32Array(N * N), t2 = new Float32Array(N * N), s = 1 / (N * N);
    let ss = 0;
    for (let i = 0; i < N * N; i++) { out[i] = -0.9 * re[i] * s; ss += out[i] * out[i]; }
    const nl = (2.2 + r() * 0.8) * Math.sqrt(ss / (N * N)); // low dose: the shot noise is stronger than the particles
    for (let i = 0; i < N * N; i++) out[i] += -amp[i] + gauss(r) * nl;
    for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) { const i = y * N + x; t2[i] = (out[y * N + Math.max(0, x - 1)] + 2 * out[i] + out[y * N + Math.min(N - 1, x + 1)]) / 4; }
    for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) { const i = y * N + x; out[i] = (t2[Math.max(0, y - 1) * N + x] + 2 * t2[i] + t2[Math.min(N - 1, y + 1) * N + x]) / 4; }
    let mean = 0, sq = 0;
    for (let i = 0; i < N * N; i++) mean += out[i];
    mean /= N * N;
    for (let i = 0; i < N * N; i++) sq += (out[i] - mean) ** 2;
    const sd = Math.sqrt(sq / (N * N)) || 1, g = new Float32Array(N * N);
    for (let i = 0; i < N * N; i++) g[i] = clamp(0.6 + 0.13 * (out[i] - mean) / sd, 0.04, 1);
    // order the picks the way a person works across a micrograph: nearest neighbour from a corner
    const ord = [], rest = picks.filter(pk => pk.type !== 'x'), xs = picks.filter(pk => pk.type === 'x');
    let cx = r() < 0.5 ? 0 : N, cy = 0;
    while (rest.length) {
        let bi = 0, bd = 1e9;
        rest.forEach((pk, i) => { const q = pk.pts ? pk.pts[0] : [pk.x, pk.y], d = Math.hypot(q[0] - cx, q[1] - cy); if (d < bd) { bd = d; bi = i; } });
        const pk = rest.splice(bi, 1)[0], q = pk.pts ? pk.pts[pk.pts.length - 1] : [pk.x, pk.y];
        cx = q[0]; cy = q[1]; ord.push(pk);
    }
    return { g, picks: ord.concat(xs) };
}

// ---------- falling paper: quasi-steady 2D plate (Andersen, Pesavento & Wang 2005), nondimensional ----------
// rho_f = 1, half-chord a = 1, g = 1. State: body-frame velocity (vx along the chord, vy normal), chord angle th, spin om.
function aeroStep(f, dt) {
    const a = 1, b = 0.06, m = PI * f.I, m11 = PI * b * b, m22 = PI * a * a, In = m * (a * a + b * b) / 4, Ia = PI / 8 * (a * a - b * b) ** 2, mp = m - PI * a * b;
    const { vx, vy, om, th } = f, V = Math.hypot(vx, vy) || 1e-9;
    const Gm = -1.2 * vx * vy / V + PI * om;                    // circulation: translational + rotational
    const c = (1.4 - (vx * vx - vy * vy) / (V * V)) * V / PI;   // drag, larger broadside than edgewise
    const ax = ((m + m22) * om * vy - Gm * vy - mp * Math.sin(th) - c * vx) / (m + m11);
    const ay = (-(m + m11) * om * vx + Gm * vx - mp * Math.cos(th) - c * vy) / (m + m22);
    const al = ((m11 - m22) * vx * vy - PI * (0.2 + 0.2 * Math.abs(om)) * om) / (In + Ia);
    f.vx += ax * dt; f.vy += ay * dt; f.om += al * dt; f.th += f.om * dt;
    const cs = Math.cos(f.th), sn = Math.sin(f.th);
    f.u += (f.vx * cs - f.vy * sn) * dt; f.w += (f.vx * sn + f.vy * cs) * dt;
}

// ---------- the scene ----------
export default function sketch(p, h) {
    let rollNext = 1e9, W = 0, H = 0, lines = [], roll = null, rollNo = 0, sheets = [], layout = {}, fallers = [], deck = [];
    let lightL, lightD, lightM, lw = 0, lh = 0, wallC, pinC, shadowC, shadowSoft;
    let noiseBank = [], frameNo = 0;
    let mouse = { x: -1e4, y: -1e4, vx: 0, vy: 0, has: false };
    const LIGHT_DIR = [-6, 9]; // sun from a window to the upper right: shadows fall down-left
    const INK = 'rgb(186,46,38)', FALL_TS = 0.55;

    // ---- noise bank: unit-variance, very slightly coloured tiles (one low-dose image each) ----
    function buildNoise() {
        const r = mulberry(h.seed ^ 0x9e37);
        for (let k = 0; k < 16; k++) {
            const a = new Float32Array(G * G), b = new Float32Array(G * G);
            for (let i = 0; i < G * G; i++) a[i] = gauss(r);
            let ss = 0;
            for (let y = 0; y < G; y++) for (let x = 0; x < G; x++) {
                const i = y * G + x, xr = (x + 1) % G, yd = ((y + 1) % G) * G;
                b[i] = a[i] * 0.7 + (a[y * G + xr] + a[yd + x]) * 0.15; ss += b[i] * b[i];
            }
            const n = 1 / Math.sqrt(ss / (G * G));
            for (let i = 0; i < G * G; i++) b[i] *= n;
            noiseBank.push(b);
        }
    }
    function addNoise(acc, amp, r) { // acc += amp * (random tile under a random D4 symmetry and torus shift)
        const t = noiseBank[(r() * 16) | 0], sym = (r() * 8) | 0, sx = (r() * G) | 0, sy = (r() * G) | 0;
        for (let y = 0; y < G; y++) for (let x = 0; x < G; x++) {
            let u = x, v = y;
            if (sym & 1) u = G - 1 - u;
            if (sym & 2) v = G - 1 - v;
            if (sym & 4) { const w = u; u = v; v = w; }
            acc[y * G + x] += amp * t[((v + sy) % G) * G + ((u + sx) % G)];
        }
    }

    // ---- sprites ----
    function buildSprites() {
        // clothespin, front view, drawn at 3x
        const k = 3, pw = 10, ph = 36;
        pinC = canvas(pw * k, ph * k);
        const c = pinC.getContext('2d');
        c.scale(k, k);
        const wood = c.createLinearGradient(0, 0, pw, 0);
        wood.addColorStop(0, '#b98f5f'); wood.addColorStop(0.25, '#e2c294'); wood.addColorStop(0.5, '#d6b384');
        wood.addColorStop(0.75, '#e6c89c'); wood.addColorStop(1, '#b08454');
        c.fillStyle = wood;
        c.beginPath(); c.roundRect(0.5, 0.5, pw - 1, ph - 1, [2.5, 2.5, 3.5, 3.5]); c.fill();
        c.strokeStyle = 'rgba(110,72,40,0.55)'; c.lineWidth = 0.5;
        c.beginPath(); c.moveTo(pw / 2, 1); c.lineTo(pw / 2, ph - 1); c.stroke(); // the split between the two legs
        c.strokeStyle = 'rgba(150,105,60,0.28)'; c.lineWidth = 0.35;
        for (let i = 0; i < 6; i++) { const x = 1.3 + i * 1.5; c.beginPath(); c.moveTo(x, 2); c.bezierCurveTo(x + 0.4, ph * 0.3, x - 0.4, ph * 0.6, x + 0.2, ph - 2); c.stroke(); }
        // the spring
        c.fillStyle = '#8d8a84'; c.fillRect(0, ph * 0.36, pw, 3.2);
        c.strokeStyle = 'rgba(240,238,230,0.7)'; c.lineWidth = 0.5;
        for (let i = 0; i < 5; i++) { c.beginPath(); c.moveTo(1 + i * 2, ph * 0.36); c.lineTo(2 + i * 2, ph * 0.36 + 3.2); c.stroke(); }
        // soft shadow sprite for prints (a blurred square)
        shadowC = canvas(160, 160);
        const s = shadowC.getContext('2d');
        s.filter = 'blur(9px)'; s.fillStyle = '#000'; s.fillRect(30, 30, 100, 100);
        shadowSoft = canvas(160, 160); // the same square with a wider penumbra (paper further from the wall)
        const s2 = shadowSoft.getContext('2d'); s2.filter = 'blur(16px)'; s2.fillStyle = '#000'; s2.fillRect(34, 34, 92, 92);
    }

    function buildWall() {
        wallC = canvas(Math.ceil(W / 4), Math.ceil(H / 4));
        const c = wallC.getContext('2d'), r = mulberry(h.seed ^ 0xa11);
        c.fillStyle = 'rgb(248,243,234)'; c.fillRect(0, 0, wallC.width, wallC.height);
        for (let i = 0; i < 200; i++) { // soft limewash mottling
            const x = r() * wallC.width, y = r() * wallC.height, rad = 3 + r() * 14, dk = r() < 0.35;
            const g = c.createRadialGradient(x, y, 0, x, y, rad);
            const col = dk ? '214,196,168' : '255,253,248';
            g.addColorStop(0, `rgba(${col},${dk ? 0.03 + r() * 0.03 : 0.08 + r() * 0.08})`); g.addColorStop(1, `rgba(${col},0)`);
            c.fillStyle = g; c.fillRect(x - rad, y - rad, rad * 2, rad * 2);
        }
    }

    // ---- layout ----
    function doLayout() {
        W = p.width; H = p.height;
        const f = h.focus;
        const topRoom = f.y, botRoom = H - (f.y + f.h);
        layout.size = clamp(Math.min(topRoom, botRoom) * 0.62, 64, 112);
        const S = layout.size;
        const mk = (y0, y1, sag, key) => {
            const old = lines.find(l => l.key === key);
            const gap = S * 0.36, n = Math.max(3, Math.floor((W + 30) / (S + gap)));
            const span = n * (S + gap), x0 = (W - span) / 2 + (S + gap) / 2;
            const slots = [];
            for (let i = 0; i < n; i++) slots.push(old?.slots[i] || { i, x: 0, print: null, cool: 0, rel: 0, relSide: 1 });
            slots.length = n;
            slots.forEach((s, i) => {
                s.x = x0 + i * (S + gap) + (hashf(key + i) - 0.5) * gap * 0.6; s.key = key + i;
                // prints above the card never drop: a falling print must not cross the form
                s.canDrop = key === 'bot' || Math.abs(s.x - f.cx) > f.w / 2 + S * 1.5;
            });
            return { key, y0, y1, sag, slots };
        };
        const yTop = Math.max(26, topRoom * 0.2), yBot = f.y + f.h + Math.max(26, botRoom * 0.22);
        lines = [mk(yTop + 2, yTop - 14, 40, 'top'), mk(yBot - 8, yBot + 4, 34, 'bot')];
        const side = f.x;
        layout.sheet = side > 300 ? { x: side * 0.47, y: f.cy + f.h * 0.06, rot: -0.03, P: Math.min(side * 0.6, H * 0.42, 300) } : null;
        layout.lamp = W - (f.x + f.w) > 260 ? { x: f.x + f.w + (W - f.x - f.w) * 0.58, y: f.y + f.h * 0.12, show: true } : { x: W * 0.92, y: -70, show: false };
        lw = Math.ceil(W / 6); lh = Math.ceil(H / 6);
        lightL = canvas(lw, lh); lightD = canvas(lw, lh); lightM = canvas(lw, lh);
        buildWall();
    }
    function lineY(L, x) {
        const sag = L.sag * (1 + 0.25 * h.drift('sag' + L.key, 300));
        const u = clamp((x + 40) / (W + 80), 0, 1);
        return lerp(L.y0, L.y1, u) + 4 * sag * u * (1 - u);
    }
    function lineSlope(L, x) { return (lineY(L, x + 4) - lineY(L, x - 4)) / 8; }

    // ---- datasets ----
    function nextKind() { // a shuffled deck, so every specimen comes round before any repeats
        if (!deck.length) { deck = KINDS.slice(); for (let i = deck.length - 1; i > 0; i--) { const j = (p.random() * (i + 1)) | 0; [deck[i], deck[j]] = [deck[j], deck[i]]; } if (deck[deck.length - 1] === roll?.model.kind) deck.unshift(deck.pop()); }
        return deck.pop();
    }
    function newRoll() {
        rollNo++;
        const kind = nextKind();
        const seed = (p.random() * 2 ** 31) | 0, r = mulberry(seed);
        const model = makeModel(kind, r), fil = !!model.helix, small = kind === 'small' || kind === 'igg';
        // per-dataset box: particle size in the box, mask radius and softness, CTF-correction halo, SNR
        const px = fil ? 30 + r() * 5 : small ? 21 + r() * 6 : 26 + r() * 10;
        const maskR = fil ? 26 + r() * 4 : Math.min(30, 0.64 * px * (1.12 + r() * 0.3) + 2), soft = 1.5 + r() * 3;
        const mask = new Float32Array(G * G);
        for (let i = 0; i < G * G; i++) mask[i] = smooth((maskR - Math.hypot((i % G) + 0.5 - G / 2, ((i / G) | 0) + 0.5 - G / 2)) / soft + 0.5);
        roll = {
            no: rollNo, seed, model, pTop: 0.15 + r() * 0.3, pSide: 0.25 + r() * 0.3,
            px, mask, halo: r() < 0.3 ? 0 : 0.1 + r() * 0.25, snr: small ? 1.45 + r() * 0.4 : 0.85 + r() * 0.4, qmax: small ? 0.72 : 1,
            junkRate: 0.2 + r() * 0.15, mgPix: 5.5 + r() * 2.5,
            tone: [[1, 1.0, 1.03], [0.94, 1.0, 1.1], [0.9, 1.0, 1.16]][(r() * 3) | 0],
            paper: [[251, 250, 246], [252, 248, 238], [250, 245, 232]][(r() * 3) | 0],
            started: h.time, sizes: r() < 0.5 ? [1] : [1, 0.88],
        };
        // the previous dataset's prints come down over the next few minutes
        for (const L of lines) for (const s of L.slots) if (s.print && s.print.roll !== roll) s.print.life = Math.min(s.print.life, s.print.age + 10 + p.random() * 320);
        // a new micrograph print: built in the background, it replaces the old one when ready
        sheets = sheets.filter(sh => sh.mg || sh.alpha > 0);
        sheets.push({ roll, seed: (p.random() * 2 ** 31) | 0, alpha: 0, job: null, mg: null, picks: null, nDone: 0, cur: -1, curT: 0, clock: 0, next: 2 + p.random() * 4,
            ink: null, inkN: 0, leaving: false, rot: (p.random() - 0.5) * 0.06, dx: (p.random() - 0.5) * 16, dy: (p.random() - 0.5) * 14 });
    }

    function newPrint(slot, age = 0) {
        const junkTypes = ['ice', 'edge', 'noise', 'broken'];
        const junk = slot.canDrop && p.random() < roll.junkRate ? junkTypes[(p.random() * 4) | 0] : null;
        slot.print = {
            roll, seed: (p.random() * 2 ** 31) | 0, junk, age, state: 'in',
            rate: junk ? 1 + p.random() * 3 : 1.2 + p.random() * 5,
            life: 160 + p.random() * 560, junkAt: 45 + p.random() * 60, dangle: 6 + p.random() * 9,
            th: 0, om: (p.random() - 0.5) * 0.15, swing: 0, swingOm: 0, out: 0, built: false, Nimg: 0,
            hangSide: p.random() < 0.5 ? -1 : 1, tilt: (p.random() - 0.5) * 0.05, scale: roll.sizes[(p.random() * roll.sizes.length) | 0],
        };
    }

    // ---- per-print image ----
    function printImage(pr) {
        if (!pr.S) buildSignal(pr, pr.roll);
        if (!pr.cv) { pr.cv = canvas(G, G); pr.cx = pr.cv.getContext('2d'); pr.img = pr.cx.createImageData(G, G); pr.A = new Float32Array(G * G); pr.r = mulberry(pr.seed ^ 0x77); }
        const N = 1 + pr.rate * pr.age;
        const dN = N - pr.Nimg;
        if (dN > 0) addNoise(pr.A, Math.sqrt(dN), pr.r);
        pr.Nimg = N;
        const R = pr.roll, q = R.qmax * smooth(Math.log(N) / Math.log(300)), sig = 2.2 * R.snr / N, tone = R.tone, mask = R.mask;
        const S = pr.S, B = pr.B, A = pr.A, d = pr.img.data;
        for (let i = 0; i < G * G; i++) {
            const m = mask[i];
            const v = 0.53 + m * (0.45 * (B[i] + (S[i] - B[i]) * q) + sig * A[i] * 0.24);
            const g = 0.1 + 0.86 * clamp(v, 0, 1); // matte paper: lifted blacks
            const o = i * 4;
            d[o] = 255 * g ** tone[0]; d[o + 1] = 255 * g ** tone[1]; d[o + 2] = 255 * g ** tone[2]; d[o + 3] = 255;
        }
        pr.cx.putImageData(pr.img, 0, 0);
        pr.built = true;
    }

    // ---- the micrograph print and its grease-pencil picks ----
    function buildSheet(sh, all) {
        if (!sh.job) sh.job = micrographJob(sh.roll, sh.seed);
        let res;
        do res = sh.job.next(); while (all && !res.done);
        if (!res.done) return;
        sh.job = null;
        const { g, picks } = res.value, tone = sh.roll.tone;
        const c = canvas(MN, MN), x = c.getContext('2d'), img = x.createImageData(MN, MN);
        for (let i = 0; i < MN * MN; i++) {
            const v = g[i], o = i * 4;
            img.data[o] = 255 * v ** tone[0]; img.data[o + 1] = 255 * v ** tone[1]; img.data[o + 2] = 255 * v ** tone[2]; img.data[o + 3] = 255;
        }
        x.putImageData(img, 0, 0);
        sh.mg = c; sh.picks = picks;
        sh.ink = canvas(MN * 2, MN * 2); sh.inkCtx = sh.ink.getContext('2d'); sh.inkCtx.scale(2, 2);
        // picking that "happened" while the print was waiting (fast-forward, start-up)
        sh.nDone = Math.min(picks.length, Math.floor(sh.clock / 4.5));
        sh.next = sh.clock + 1 + p.random() * 3;
    }
    function drawPick(c, pk, f, alpha) { // a waxy china-marker stroke, hand-wobbly, revealed up to fraction f
        c.strokeStyle = INK; c.lineCap = 'round'; c.lineJoin = 'round';
        for (const [lwid, al, ox, oy] of [[1.7, 0.78, 0, 0], [0.9, 0.32, 0.45, -0.35]]) {
            c.lineWidth = lwid; c.globalAlpha = al * alpha;
            c.beginPath();
            if (pk.type === 'o') {
                const span = (TAU + 0.5) * f, n = Math.max(2, Math.ceil(span / 0.16));
                for (let k = 0; k <= n; k++) {
                    const a = pk.a0 + span * k / n, t = (a - pk.a0) / TAU;
                    const rr = pk.rad * (1 + pk.e1 * Math.cos(2 * (a - pk.e2)) + 0.015 * Math.sin(5 * a + pk.e2) + 0.05 * t);
                    const X = pk.x + Math.cos(a) * rr + ox, Y = pk.y + Math.sin(a) * rr + oy;
                    k ? c.lineTo(X, Y) : c.moveTo(X, Y);
                }
            } else if (pk.type === 'line') { // helical picking: a trace along the filament, ticks at both ends
                const q = pk.pts, n = Math.max(1, Math.round((q.length - 1) * f));
                for (let k = 0; k <= n; k++) { const [X, Y] = q[k]; k ? c.lineTo(X + ox, Y + oy) : c.moveTo(X + ox, Y + oy); }
                for (const [i, j, on] of [[0, 1, true], [q.length - 1, q.length - 2, f >= 1]]) {
                    if (!on) continue;
                    const dx = q[j][0] - q[i][0], dy = q[j][1] - q[i][1], l = Math.hypot(dx, dy) || 1;
                    c.moveTo(q[i][0] - dy / l * 6 + ox, q[i][1] + dx / l * 6 + oy); c.lineTo(q[i][0] + dy / l * 6 + ox, q[i][1] - dx / l * 6 + oy);
                }
            } else { // an X over ice contamination
                const R = pk.rad, a = PI / 4 + pk.a0;
                for (let k = 0; k < 2; k++) {
                    const ff = clamp(f * 2 - k), b = a + k * PI / 2, dx = Math.cos(b) * R, dy = Math.sin(b) * R;
                    if (ff <= 0) continue;
                    c.moveTo(pk.x - dx + ox, pk.y - dy + oy); c.lineTo(pk.x - dx + 2 * dx * ff + ox, pk.y - dy + 2 * dy * ff + oy);
                }
            }
            c.stroke();
        }
        c.globalAlpha = 1;
    }
    function pickDur(pk) { return pk.type === 'line' ? 1.2 + pk.pts.length * 0.06 : 0.9; }

    // ---- light maps ----
    function buildLight() {
        const s = lw / W;
        // day: warm ambient, a window patch on the right-hand wall
        let c = lightL.getContext('2d');
        c.globalCompositeOperation = 'source-over'; c.filter = 'none';
        c.fillStyle = 'rgb(226,216,201)'; c.fillRect(0, 0, lw, lh);
        const lg = c.createLinearGradient(lw, 0, 0, lh);
        lg.addColorStop(0, 'rgba(255,250,240,0.55)'); lg.addColorStop(0.6, 'rgba(255,250,240,0)'); lg.addColorStop(1, 'rgba(190,175,160,0.25)');
        c.fillStyle = lg; c.fillRect(0, 0, lw, lh);
        const sunI = 0.78 + 0.12 * h.drift('cloud', 70) + 0.1 * h.drift('cloud2', 240);
        const f = h.focus, rc = f.x + f.w + (W - f.x - f.w) * 0.5;
        const px = rc + W * 0.03 * h.drift('sunx', 700), py = H * (0.5 + 0.05 * h.drift('suny', 900));
        const pw = Math.min(W * 0.075, (W - f.x - f.w) * 0.22), ph = H * 0.19, shear = -(0.38 + 0.1 * h.drift('shear', 800)), gap = 11;
        c.filter = `blur(${2.2}px)`;
        const gold = 0.5 + 0.5 * h.drift('golden', 1500); // the afternoon slowly turns golden and back
        c.fillStyle = `rgba(255,${lerp(252, 238, gold) | 0},${lerp(238, 204, gold) | 0},${clamp(sunI, 0, 1)})`;
        for (let i = 0; i < 2; i++) for (let j = 0; j < 2; j++) {
            const x0 = px + (i - 1) * (pw + gap), y0 = py + (j - 1) * (ph + gap);
            c.beginPath();
            c.moveTo((x0 + shear * (y0 - py)) * s, y0 * s);
            c.lineTo((x0 + pw + shear * (y0 - py)) * s, y0 * s);
            c.lineTo((x0 + pw + shear * (y0 + ph - py)) * s, (y0 + ph) * s);
            c.lineTo((x0 + shear * (y0 + ph - py)) * s, (y0 + ph) * s);
            c.closePath(); c.fill();
        }
        c.filter = 'none';
        // night: a dim red-amber safelight on the right with a long, soft falloff; a faint spill from the left
        c = lightD.getContext('2d');
        c.globalCompositeOperation = 'source-over';
        c.fillStyle = 'rgb(60,36,33)'; c.fillRect(0, 0, lw, lh);
        c.globalCompositeOperation = 'lighter';
        const L = layout.lamp, I = 0.92 + 0.08 * h.drift('lamp', 160);
        let g = c.createRadialGradient(L.x * s, (L.y + 40) * s, 0, L.x * s, (L.y + 40) * s, W * 0.55 * s);
        g.addColorStop(0, `rgba(206,116,84,${I})`); g.addColorStop(0.16, `rgba(168,86,62,${0.8 * I})`); g.addColorStop(0.42, `rgba(100,50,38,${0.45 * I})`);
        g.addColorStop(0.75, 'rgba(40,20,15,0.16)'); g.addColorStop(1, 'rgba(0,0,0,0)');
        c.fillStyle = g; c.fillRect(0, 0, lw, lh);
        g = c.createRadialGradient(0.02 * lw, 0.38 * lh, 0, 0.02 * lw, 0.38 * lh, lw * 0.45);
        g.addColorStop(0, 'rgba(132,66,50,0.7)'); g.addColorStop(0.5, 'rgba(70,34,27,0.32)'); g.addColorStop(1, 'rgba(0,0,0,0)');
        c.fillStyle = g; c.fillRect(0, 0, lw, lh);
        // keep the card's surroundings calm and dim
        c.globalCompositeOperation = 'multiply';
        g = c.createRadialGradient(f.cx * s, f.cy * s, 0, f.cx * s, f.cy * s, f.w * 0.9 * s);
        g.addColorStop(0, 'rgb(175,175,175)'); g.addColorStop(1, 'rgb(255,255,255)');
        c.fillStyle = g; c.fillRect(0, 0, lw, lh);
        c.globalCompositeOperation = 'source-over';
    }
    function composeLight() {
        const c = lightM.getContext('2d');
        c.globalAlpha = 1; c.drawImage(lightL, 0, 0);
        c.globalAlpha = smooth(h.mix); c.drawImage(lightD, 0, 0);
        c.globalAlpha = 1;
    }

    // ---- simulation ----
    function printXform(L, s, pr) {
        const S = layout.size, ax = s.x, ay = lineY(L, s.x), base = Math.atan(lineSlope(L, s.x)) + pr.th + pr.tilt;
        return { S, ax, ay, base };
    }
    function startFall(L, s, pr) { // the second pin lets go: hand the print over to the falling-plate model
        const { S, ax, ay, base } = printXform(L, s, pr), Sp = S * pr.scale, px = -pr.hangSide * S * 0.32;
        const qx0 = -px, qy0 = Sp / 2 - 4, cs = Math.cos(pr.swing), sn = Math.sin(pr.swing);
        const qx = px + cs * qx0 - sn * qy0, qy = sn * qx0 + cs * qy0, cb = Math.cos(base), sb = Math.sin(base);
        const sg = p.random() < 0.5 ? -1 : 1;
        fallers.push({
            pr, x0: ax + cb * qx - sb * qy, y0: ay + sb * qx + cb * qy, Sp, gam: base + pr.swing, gamV: (p.random() - 0.5) * 0.14,
            beta: 0, betaT: (p.random() < 0.5 ? -1 : 1) * (0.4 + p.random() * 0.45), I: 0.35 + p.random() * 0.55,
            vx: 0, vy: 0, th: PI / 2 - 0.02 * sg, om: -sg * (0.2 + p.random() * 0.3), u: 0, w: 0, dx: 0, dv: 0, t: 0,
        });
    }
    function fallPose(fl) { // screen-space centre, axes, depth and facing of a falling print
        const a = fl.Sp / 2, sb = Math.sin(fl.beta), cb = Math.cos(fl.beta), ct = Math.cos(fl.th), st = Math.sin(fl.th);
        const eps = 0.3 * smooth(fl.t / 2.5), se = Math.sin(eps), ce = Math.cos(eps); // we look at it slightly from above
        const X = fl.x0 + fl.u * sb * a * 0.6 + fl.dx, Y = fl.y0 - fl.w * a, zr = fl.u * cb * a * 0.3, Z = 4 + Math.sqrt(zr * zr + 36) - 6;
        const d = [-ct * sb, st, -ct * cb], hh = [cb, 0, -sb], cg = Math.cos(fl.gam), sg = Math.sin(fl.gam); // d: down the chord
        const ex = [cg * hh[0] + sg * d[0], cg * hh[1] + sg * d[1], cg * hh[2] + sg * d[2]];
        const ey = [-sg * hh[0] + cg * d[0], -sg * hh[1] + cg * d[1], -sg * hh[2] + cg * d[2]];
        const nz = ex[0] * ey[1] - ex[1] * ey[0], ny = ex[2] * ey[0] - ex[0] * ey[2], facing = -ny * se + nz * ce;
        const k = 1400 / (1400 - Z);
        return { X, Y: Y + Z * se, Z, k, ex, ey, ce, se, facing, wallY: Y };
    }
    function stepPrint(pr, slot, L, dt) {
        pr.age += dt;
        // gentle sway: a damped spring toward a slowly drifting rest angle, nudged by the pointer
        const target = 0.018 * h.drift('sway' + slot.key, 22) + 0.01 * h.drift('wind', 50);
        pr.om += (-(pr.th - target) * 9 - pr.om * 1.6) * dt;
        pr.th += pr.om * dt;
        if (pr.state === 'in' && pr.age > pr.life) pr.state = 'out';
        if (pr.state === 'in' && pr.junk && pr.age > pr.junkAt) { pr.state = 'swing'; pr.swingOm = 0.6 * pr.hangSide; pr.swingT = 0; }
        if (pr.state === 'swing') { // one pin let go: the print pivots about the other top corner, then that one goes too
            const eq = pr.hangSide * PI / 4;
            pr.swingOm += (-(pr.swing - eq) * 7 - pr.swingOm * 1.1) * dt;
            pr.swing += pr.swingOm * dt;
            pr.swingT += dt;
            if (pr.swingT > pr.dangle) { startFall(L, slot, pr); slot.print = null; slot.cool = 4 + p.random() * 22; return; }
        }
        if (pr.state === 'out') { // a finished print is taken down: lifted off the line
            pr.out += dt / 2.5;
            if (pr.out >= 1) { slot.print = null; slot.cool = 3 + p.random() * 22; }
        }
    }
    function stepFaller(fl, dt) {
        fl.t += dt;
        fl.beta += (fl.betaT - fl.beta) * Math.min(1, dt / 1.6);
        fl.gam += fl.gamV * dt;
        const n = Math.ceil(dt / FALL_TS / 0.012), du = dt / FALL_TS / n;
        for (let i = 0; i < n; i++) aeroStep(fl, du);
        // a soft draught keeps paper away from the card
        const f = h.focus, P = fallPose(fl), rel = P.X - f.cx, band = f.w / 2 + fl.Sp + 40;
        if (Math.abs(rel) < band && P.Y > f.y - fl.Sp * 2 && P.Y < f.y + f.h + fl.Sp) fl.dv += Math.sign(rel || 1) * 70 * (1 - Math.abs(rel) / band) * dt;
        fl.dv += (-fl.dv * 0.7 + 6 * h.drift('draught', 40)) * dt;
        fl.dx += fl.dv * dt;
        return P.Y - fl.Sp < H + 40 && fl.t < 150;
    }
    function step(dt) {
        if (!roll) return;
        if (h.time > rollNext) { newRoll(); rollNext = h.time + 220 + 160 * -Math.log(1 - p.random()); }
        for (const L of lines) for (const s of L.slots) {
            if (s.print) stepPrint(s.print, s, L, dt);
            else if ((s.cool -= dt) <= 0) newPrint(s);
            const tgt = s.print && s.print.state === 'swing' ? 1 : 0;
            if (tgt) s.relSide = s.print.hangSide;
            s.rel += (tgt - s.rel) * Math.min(1, dt * 4);
        }
        fallers = fallers.filter(fl => stepFaller(fl, dt));
        // micrograph prints: once the newest one is ready the old one fades out, then the new one fades in
        const newest = sheets[sheets.length - 1];
        if (newest && h.warping) newest.instant = true;
        const ready = newest && (newest.mg || newest.instant);
        for (const sh of sheets) if (sh !== newest && ready) sh.leaving = true;
        const oldUp = sheets.some(o => o.leaving && o.alpha > 0.02);
        for (const sh of sheets) sh.alpha = clamp(sh.alpha + (sh.leaving ? -dt / 3 : ((sh.mg || sh.instant) && !oldUp ? dt / 3 : 0)), 0, 1);
        sheets = sheets.filter(sh => !(sh.leaving && sh.alpha <= 0));
        for (const sh of sheets) { // the picker works slowly across the print
            if (sh.leaving) continue;
            sh.clock += dt;
            if (!sh.picks) continue;
            if (sh.cur >= 0) { sh.curT += dt / pickDur(sh.picks[sh.cur]); if (sh.curT >= 1) { sh.nDone++; sh.cur = -1; sh.next = sh.clock + 2 + p.random() * 3 + 2 * -Math.log(1 - p.random()); } }
            else if (sh.nDone < sh.picks.length && sh.clock > sh.next) { if (h.warping) { sh.nDone++; sh.next = sh.clock + 4.5; } else { sh.cur = sh.nDone; sh.curT = 0; } }
        }
        // pointer breeze
        if (!h.warping && p.mouseX > 0 && p.mouseY > 0 && p.mouseX < W && p.mouseY < H) {
            if (mouse.has) { mouse.vx = lerp(mouse.vx, (p.mouseX - mouse.x) / dt, 0.3); mouse.vy = lerp(mouse.vy, (p.mouseY - mouse.y) / dt, 0.3); }
            mouse.x = p.mouseX; mouse.y = p.mouseY; mouse.has = true;
            for (const L of lines) for (const s of L.slots) if (s.print) {
                const dx = s.x - mouse.x, dy = lineY(L, s.x) + layout.size * 0.5 - mouse.y, d2 = dx * dx + dy * dy;
                if (d2 < 140 * 140) s.print.om += clamp(mouse.vx, -800, 800) * 0.00004 * (1 - Math.sqrt(d2) / 140);
            }
        } else mouse.has = false;
    }

    // ---- drawing ----
    function drawLine(ctx, L, ox, oy) {
        ctx.beginPath();
        for (let x = -40; x <= W + 40; x += 24) { const y = lineY(L, x); x === -40 ? ctx.moveTo(x + ox, y + oy) : ctx.lineTo(x + ox, y + oy); }
        ctx.stroke();
    }
    function withPrint(ctx, L, s, pr, fn) {
        const { S, ax, ay, base } = printXform(L, s, pr);
        const Sp = S * pr.scale;
        ctx.save();
        ctx.translate(ax, ay);
        ctx.rotate(base);
        if (pr.state === 'swing') { // pivot about the held corner pin
            const px = -pr.hangSide * S * 0.32;
            ctx.translate(px, 0); ctx.rotate(pr.swing); ctx.translate(-px, 0);
        }
        if (pr.state === 'out') ctx.translate(0, -pr.out * 10);
        fn(Sp);
        ctx.restore();
    }
    function drawPrintFace(ctx, pr, S, x0, y0, a, dev) {
        ctx.globalAlpha = a;
        ctx.fillStyle = `rgb(${pr.roll.paper})`;
        ctx.fillRect(x0, y0, S, S);
        const b = S * 0.085;
        ctx.globalAlpha = a * dev;
        ctx.drawImage(pr.cv, x0 + b, y0 + b, S - 2 * b, S - 2 * b);
        ctx.globalAlpha = a * 0.08; // a soft curl: a touch darker toward the bottom edge
        ctx.fillStyle = '#5a4030';
        ctx.fillRect(x0, y0 + S * 0.9, S, S * 0.1);
        ctx.globalAlpha = 1;
    }
    function render() {
        const ctx = p.drawingContext;
        if (!roll) return;
        frameNo++;
        if (frameNo % 3 === 1 || h.mix !== composeLight.mix) { buildLight(); composeLight(); composeLight.mix = h.mix; }
        const m = smooth(h.mix), lamp = layout.lamp;
        ctx.save();
        ctx.imageSmoothingEnabled = true; ctx.imageSmoothingQuality = 'high';
        ctx.globalCompositeOperation = 'source-over';
        ctx.drawImage(wallC, 0, 0, W, H);

        // image refreshes: new prints immediately, developing prints a few per frame
        let budget = 3;
        const cand = [];
        for (const L of lines) for (const s of L.slots) {
            const pr = s.print; if (!pr) continue;
            if (!pr.built) printImage(pr);
            else if ((1 + pr.rate * pr.age) / pr.Nimg > 1.1 && pr.Nimg < 5000) cand.push(pr);
        }
        cand.sort((a, b) => (1 + b.rate * b.age) / b.Nimg - (1 + a.rate * a.age) / a.Nimg);
        for (const pr of cand) { if (budget-- <= 0) break; printImage(pr); }
        for (const sh of sheets) if (!sh.mg) buildSheet(sh, !!sh.instant || !sheets.some(o => o !== sh && o.alpha > 0));

        const shadow = (x, y) => {
            const dx = x - lamp.x, dy = y - lamp.y, d = Math.hypot(dx, dy) || 1;
            return [lerp(LIGHT_DIR[0], dx / d * 9, m), lerp(LIGHT_DIR[1], Math.max(2, dy / d * 9 + 4), m)];
        };
        const shA = lerp(0.16, 0.42, m);

        // the micrograph print, taped to the wall
        if (layout.sheet) for (const sh of sheets) {
            if (!sh.mg || sh.alpha <= 0) continue;
            const Ls = layout.sheet, P = Ls.P, bd = Math.round(P * 0.055), I = P - 2 * bd, a = smooth(sh.alpha);
            const [ox, oy] = shadow(Ls.x, Ls.y);
            ctx.save();
            ctx.translate(Ls.x + sh.dx, Ls.y + sh.dy); ctx.rotate(Ls.rot + sh.rot);
            ctx.globalAlpha = a * shA * 0.9;
            ctx.drawImage(shadowC, -P * 0.8 + ox * 0.6, -P * 0.8 + oy * 0.6, P * 1.6, P * 1.6);
            ctx.globalAlpha = a;
            ctx.fillStyle = `rgb(${sh.roll.paper})`; ctx.fillRect(-P / 2, -P / 2, P, P);
            ctx.drawImage(sh.mg, -I / 2, -P / 2 + bd, I, I);
            if (sh.inkN < sh.nDone) { for (let k = sh.inkN; k < sh.nDone; k++) drawPick(sh.inkCtx, sh.picks[k], 1, 1); sh.inkN = sh.nDone; }
            ctx.drawImage(sh.ink, -I / 2, -P / 2 + bd, I, I);
            if (sh.cur >= 0) {
                ctx.save(); ctx.translate(-I / 2, -P / 2 + bd); ctx.scale(I / MN, I / MN);
                drawPick(ctx, sh.picks[sh.cur], smooth(sh.curT), a);
                ctx.restore();
            }
            for (const sx of [-1, 1]) { // masking tape at the top corners
                ctx.save();
                ctx.globalAlpha = a;
                ctx.translate(sx * (P / 2 - 8), -P / 2 + 2); ctx.rotate(sx * 0.7 + (sh.seed % 7 - 3) * 0.03);
                ctx.fillStyle = 'rgba(232,214,174,0.82)';
                ctx.beginPath();
                ctx.moveTo(-17, -6); for (let k = 0; k <= 4; k++) ctx.lineTo(-17 + (k % 2) * 1.5, -6 + k * 3);
                ctx.lineTo(17, 6); for (let k = 0; k <= 4; k++) ctx.lineTo(17 - (k % 2) * 1.5, 6 - k * 3);
                ctx.closePath(); ctx.fill();
                ctx.restore();
            }
            ctx.restore();
        }

        // the safelight lamp (pendant)
        if (lamp.show) drawLamp(ctx, lamp, m);

        // drying lines: shadows, then string
        ctx.lineCap = 'round';
        for (const L of lines) {
            const [ox, oy] = shadow(W / 2, L.y0);
            ctx.strokeStyle = `rgba(40,25,15,${shA * 0.5})`; ctx.lineWidth = 2; drawLine(ctx, L, ox * 0.7, oy * 0.7);
        }
        // print shadows
        for (const L of lines) for (const s of L.slots) {
            const pr = s.print; if (!pr) continue;
            const a = smooth(Math.min(1, pr.age / 2.5)) * (1 - smooth(pr.out));
            const [ox, oy] = shadow(s.x, lineY(L, s.x));
            ctx.save(); ctx.translate(ox, oy); ctx.globalAlpha = shA * a;
            withPrint(ctx, L, s, pr, S => ctx.drawImage(shadowC, -S * 0.8, -4 - S * 0.3, S * 1.6, S * 1.6));
            ctx.restore();
        }
        ctx.globalAlpha = 1;
        for (const L of lines) { ctx.strokeStyle = 'rgb(214,204,186)'; ctx.lineWidth = 1.5; drawLine(ctx, L, 0, 0); ctx.strokeStyle = 'rgba(120,100,80,0.35)'; ctx.lineWidth = 0.6; drawLine(ctx, L, 0, 0.6); }

        // prints
        for (const L of lines) for (const s of L.slots) {
            const pr = s.print; if (!pr) continue;
            const a = smooth(Math.min(1, pr.age / 2.5)) * (1 - smooth(pr.out));
            const dev = smooth(pr.age / 30) * lerp(1, 0.75, h.focusFalloff(s.x, lineY(L, s.x) + layout.size / 2, 160));
            withPrint(ctx, L, s, pr, S => drawPrintFace(ctx, pr, S, -S / 2, -4, a, dev));
        }
        // clothespins (they stay on the line when a print comes down)
        const pw = 10, ph = 36;
        for (const L of lines) for (const s of L.slots) {
            const pr = s.print, S = layout.size;
            for (const side of [-1, 1]) {
                const x = s.x + side * S * 0.32;
                let ang = Math.atan(lineSlope(L, x)) + (pr ? pr.th * 0.7 : 0);
                if (side === s.relSide) ang += side * 0.12 * s.rel;
                const py = lineY(L, x);
                ctx.save(); ctx.translate(x, py); ctx.rotate(ang);
                const [ox, oy] = shadow(x, py);
                ctx.globalAlpha = shA * 0.8; ctx.fillStyle = '#2a1a10';
                ctx.beginPath(); ctx.roundRect(-pw / 2 + ox * 0.4, -ph * 0.4 + oy * 0.4, pw, ph, 3); ctx.fill();
                ctx.globalAlpha = 1;
                ctx.drawImage(pinC, -pw / 2, -ph * 0.42, pw, ph);
                ctx.restore();
            }
        }
        // falling prints: a shadow on the wall that softens with distance, then the paper itself
        for (const fl of fallers) {
            const P = fallPose(fl), pr = fl.pr, S = fl.Sp;
            if (!pr.built) printImage(pr);
            // shadow: the paper's outline projected onto the wall (so it foreshortens as the paper tilts), pushed a little
            // further from the lamp, softer and fainter as the paper moves off the wall; never more than ~1.2x the print
            const [ox, oy] = shadow(P.X, P.wallY), zf = clamp(P.Z / 45), off = 1 + 1.6 * zf, grow = 1 + 0.15 * zf, sa = shA * Math.exp(-P.Z / 22);
            if (sa > 0.004) {
                ctx.save();
                ctx.translate(P.X + ox * off, P.wallY + oy * off);
                ctx.transform(P.ex[0] * grow, P.ex[1] * grow, P.ey[0] * grow, P.ey[1] * grow, 0, 0);
                ctx.globalAlpha = sa * (1 - zf); ctx.drawImage(shadowC, -S * 0.8, -S * 0.8, S * 1.6, S * 1.6);
                ctx.globalAlpha = sa * zf; ctx.drawImage(shadowSoft, -S * 0.8, -S * 0.8, S * 1.6, S * 1.6);
                ctx.restore();
            }
            ctx.save();
            ctx.translate(P.X, P.Y);
            ctx.transform(P.k * P.ex[0], P.k * (P.ex[1] * P.ce + P.ex[2] * P.se), P.k * P.ey[0], P.k * (P.ey[1] * P.ce + P.ey[2] * P.se), 0, 0);
            if (P.facing > 0) drawPrintFace(ctx, pr, S, -S / 2, -S / 2, 1, 1);
            else { ctx.fillStyle = 'rgb(240,234,222)'; ctx.fillRect(-S / 2, -S / 2, S, S); }
            ctx.globalAlpha = (1 - Math.abs(P.facing)) * 0.22; // paper turned away from the light reads a little darker
            ctx.fillStyle = '#3a2a20'; ctx.fillRect(-S / 2, -S / 2, S, S);
            ctx.globalAlpha = 1;
            ctx.restore();
        }

        // light: multiply the whole scene by the (day or safelight) light map
        ctx.globalCompositeOperation = 'multiply';
        ctx.drawImage(lightM, 0, 0, W, H);
        // the safelight's own glow (drawn after the multiply so it emits)
        if (m > 0.01 && lamp.show) {
            ctx.globalCompositeOperation = 'lighter';
            const gy = lamp.y + 8, I = m * (0.92 + 0.08 * h.drift('lamp', 160));
            let g = ctx.createRadialGradient(lamp.x, gy, 0, lamp.x, gy, 200);
            g.addColorStop(0, `rgba(220,110,74,${0.24 * I})`); g.addColorStop(0.3, `rgba(150,64,40,${0.08 * I})`); g.addColorStop(1, 'rgba(0,0,0,0)');
            ctx.fillStyle = g; ctx.fillRect(lamp.x - 200, gy - 200, 400, 400);
            ctx.globalCompositeOperation = 'source-over';
            g = ctx.createLinearGradient(0, lamp.y, 0, lamp.y + 15);
            g.addColorStop(0, `rgba(250,190,140,${I})`); g.addColorStop(1, `rgba(226,120,78,${I})`);
            ctx.fillStyle = g;
            ctx.beginPath(); ctx.ellipse(lamp.x, lamp.y + 1.5, 24, 13, 0, 0, PI); ctx.fill();
        }
        ctx.restore();
    }
    function drawLamp(ctx, L, m) { // a pendant darkroom safelight: enamel dome over a red-amber glass bowl
        ctx.save();
        ctx.translate(L.x, L.y);
        ctx.strokeStyle = 'rgb(66,58,50)'; ctx.lineWidth = 1.5;
        ctx.beginPath(); ctx.moveTo(0, -L.y - 4); ctx.lineTo(0, -30); ctx.stroke();
        ctx.fillStyle = 'rgb(88,78,66)'; ctx.beginPath(); ctx.roundRect(-5, -34, 10, 10, 2); ctx.fill();
        const g = ctx.createLinearGradient(-36, 0, 36, 0);
        g.addColorStop(0, 'rgb(58,66,58)'); g.addColorStop(0.35, 'rgb(104,116,100)'); g.addColorStop(1, 'rgb(48,54,48)');
        ctx.fillStyle = g;
        ctx.beginPath(); ctx.moveTo(-36, 0); ctx.bezierCurveTo(-34, -22, -16, -27, 0, -27); ctx.bezierCurveTo(16, -27, 34, -22, 36, 0); ctx.closePath(); ctx.fill();
        ctx.fillStyle = 'rgb(232,226,212)'; ctx.fillRect(-37, -1.5, 74, 3); // white enamel rim
        const fg = ctx.createLinearGradient(0, 0, 0, 14);
        fg.addColorStop(0, `rgb(${lerp(178, 250, m)},${lerp(98, 170, m)},${lerp(60, 120, m)})`);
        fg.addColorStop(1, `rgb(${lerp(140, 230, m)},${lerp(66, 120, m)},${lerp(42, 78, m)})`);
        ctx.fillStyle = fg;
        ctx.beginPath(); ctx.ellipse(0, 1.5, 24, 13, 0, 0, PI); ctx.fill();
        ctx.fillStyle = `rgba(255,244,226,${0.45 * (1 - m)})`;
        ctx.beginPath(); ctx.ellipse(-9, 5, 6, 2.5, -0.3, 0, TAU); ctx.fill();
        ctx.restore();
    }

    return {
        setup() {
            buildNoise(); buildSprites(); doLayout();
            newRoll();
            for (const sh of sheets) { sh.alpha = 1; sh.instant = true; sh.clock = 40 + p.random() * 90; }
            // start mid-session: prints at various stages of development
            for (const L of lines) for (const s of L.slots) if (p.random() < 0.82) newPrint(s, p.random() * 160); else s.cool = p.random() * 20;
            rollNext = 200 + p.random() * 160;
        },
        step,
        render,
        resize() { doLayout(); },
    };
}
