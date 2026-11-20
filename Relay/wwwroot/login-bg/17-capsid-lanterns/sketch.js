// 17-capsid-lanterns — folded-paper virus capsids as lanterns at a garden lantern festival.
// Every lantern is a true Caspar–Klug capsid (12 pentamers + 10(T−1) hexamers; prolate heads with T and Q;
// a TMV-like helical rod), each capsomer a folded paper fan of 5 or 6 subunit facets, lit from inside.
// Lanterns assemble in mid-air from drifting capsomers (nucleation, then growth along neighbours), float
// and turn slowly, and now and then open at a vertex to let their genome out as a ribbon of sparks before
// the paper pieces drift apart. Day: a pastel garden pond in soft afternoon light. Night: the same pond at a
// warm festival, lanterns glowing and bokeh. Plain geodesic paper lanterns join the capsids; string lights hang
// as catenaries between shore posts; reflections are mirrored about the pond plane per the camera model.
import { buildCapsid, buildHelix, assemblyOrder } from './geometry.js';

export const meta = {
    title: 'Capsid Lanterns',
    tagline: 'Folded-paper viruses drifting over a festival pond',
    description: 'Paper lanterns folded as true Caspar–Klug capsids (T = 1 to 27, including the skew laevo/dextro pairs T = 7, 13, 21: always 12 pentamers plus 10(T−1) hexamers), prolate phage heads of several elongations with a 12-fold portal, plain faceted geodesic lanterns, and a TMV-like helical rod. They assemble in mid-air from drifting capsomers, turn slowly and, now and then, open at a vertex to release their genome as a ribbon of sparks before the paper drifts apart. Light is a pastel garden pond on a soft afternoon with lanterns faintly lit; dark is a warm night festival with glowing paper and bokeh. Lights are strung on catenaries between shore posts, and every lantern is mirrored in the pond as the camera angle demands, cut at the far shore, rippled and Fresnel-dimmed. Assembly, release, rising lanterns, T-numbers, colour moods and the time of day all run on Poisson events and slow noise, so the sky never repeats.',
    renderer: 'p2d',
    fps: 30,
    pixelDensity: 1,
    maxStep: 1 / 20,
    post: { paper: 0.5, grain: 0.3, vignette: 0.34 },
};

const TAU = Math.PI * 2;
const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
const lerp = (a, b, t) => a + (b - a) * t;
const sstep = (a, b, x) => { const t = clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); };
const mixc = (a, b, t) => [lerp(a[0], b[0], t), lerp(a[1], b[1], t), lerp(a[2], b[2], t)];
const rgb = (c) => `rgb(${c[0] | 0},${c[1] | 0},${c[2] | 0})`;
const rgba = (c, a) => `rgba(${c[0] | 0},${c[1] | 0},${c[2] | 0},${a.toFixed(3)})`;
const easeOut = (t) => 1 - (1 - t) * (1 - t) * (1 - t);
const NB = 22; // shading bins per paper

// paper colours: L = paper in daylight, D = colour of the light that comes through it at night
const PAPERS = [
    { L: [243, 198, 192], D: [255, 146, 112] }, // blush
    { L: [246, 207, 170], D: [255, 168, 92] },  // apricot
    { L: [244, 228, 172], D: [255, 204, 116] }, // butter
    { L: [204, 225, 204], D: [250, 224, 156] }, // mint
    { L: [216, 205, 231], D: [240, 158, 170] }, // lavender
    { L: [200, 220, 229], D: [240, 200, 178] }, // sky
    { L: [247, 238, 221], D: [255, 220, 166] }, // cream
];
// pentamer accent pairings (index into PAPERS), by body paper
const ACCENT = [[2, 6, 4], [0, 6, 2], [0, 1, 4], [2, 6, 0], [0, 2, 6], [6, 2, 0], [0, 1, 4]];

// theme scenery colours
const SL = {
    sky0: [198, 212, 216], sky1: [233, 228, 216], sky2: [245, 229, 206],
    tree: [[188, 204, 190], [168, 190, 166], [146, 170, 142]], blossom: [[240, 206, 204], [244, 220, 196], [226, 212, 232]],
    shore: [128, 150, 122], water0: [224, 228, 220], water1: [196, 210, 210], ripple: [255, 255, 250], pad: [150, 176, 140],
    string: [140, 120, 104], bulb: [[243, 198, 192], [244, 228, 172], [200, 220, 229], [246, 207, 170]], bulbGlow: 0.0,
};
const SD = {
    sky0: [20, 16, 30], sky1: [36, 24, 38], sky2: [66, 38, 40],
    tree: [[44, 30, 40], [30, 21, 29], [20, 14, 20]], blossom: [[54, 34, 44], [50, 34, 38], [44, 32, 46]],
    shore: [14, 10, 14], water0: [30, 20, 26], water1: [12, 9, 14], ripple: [255, 190, 130], pad: [22, 26, 22],
    string: [60, 40, 36], bulb: [[255, 170, 110], [255, 206, 120], [255, 190, 150], [255, 160, 90]], bulbGlow: 1.0,
};

export default function sketch(p, h) {
    const rnd = (a = 1, b) => (b === undefined ? p.random(0, a) : p.random(a, b));
    let W = 0, H = 0, pd = 1, Hw = 0;
    let bgL = null, bgD = null, glowSpr = [], whiteGlow = null, dayBloom = null;
    const lanterns = [], ribbons = [], bokeh = [];
    const models = new Map();
    let uid = 0, frame = 0;
    const T = {}; // scratch

    // ------------------------------------------------------------------ models
    function model(key) {
        if (models.has(key)) return models.get(key);
        let m;
        const [kind, a, b, c1, c2, bl] = key.split(':');
        if (kind === 'helix') m = buildHelix({ length: +a });
        else if (kind === 'geo') m = buildCapsid({ h: +a, k: +b, blend: +bl, bulge: 0.1, geodesic: true });
        else m = buildCapsid({ h: +a, k: +b, c: kind === 'prolate' ? [+c1, +c2] : null, blend: +bl, bulge: 0.26 });
        if (!m.ok) m = model('icosa:1:1:0:0:0.8'); // never expected: every built capsid is checked for 12 pentamers
        models.set(key, m);
        return m;
    }
    const pick = (list) => { let s = 0; for (const [, w] of list) s += w; let r = rnd(s); for (const [v, w] of list) { if ((r -= w) <= 0) return v; } return list[0][0]; };

    function chooseKind(tier) {
        // slowly drifting mood: which assemblies are "in season"
        const big = 0.5 + 0.5 * h.drift('bigT', 700);
        // icosa:h:k (T = h²+hk+k²; (2,1)/(1,2) etc. are the laevo/dextro skew pairs), prolate:h:k:c1:c2 (Q = T+|h·c2−k·c1|),
        // geo:h:k plain geodesic paper lanterns (class I (n,0), class II (n,n), class III), helix: TMV-like rod
        if (tier === 0) return pick([['icosa:1:0', 2], ['icosa:1:1', 2.5], ['icosa:2:0', 2], ['icosa:2:1', 1], ['icosa:1:2', 1],
            ['geo:2:0', 1], ['geo:1:1', 0.8], ['prolate:1:1:1:-1', 0.6]]);
        if (tier === 1) return pick([['icosa:1:1', 1.2], ['icosa:2:0', 1.2], ['icosa:2:1', 1 + big], ['icosa:1:2', 1 + big], ['icosa:3:0', 0.6 + big],
            ['icosa:2:2', 0.4 + big * 0.5], ['icosa:3:1', 0.4 + big * 0.6], ['icosa:1:3', 0.4 + big * 0.6],
            ['prolate:1:1:1:-1', 0.8], ['prolate:1:1:2:-2', 0.6], ['prolate:2:0:1:-2', 0.6], ['helix', 0.7],
            ['geo:3:0', 0.6], ['geo:2:2', 0.5], ['geo:2:1', 0.4], ['geo:4:0', 0.4]]);
        return pick([['icosa:2:1', 0.6], ['icosa:3:1', 0.7 + big * 0.5], ['icosa:1:3', 0.7 + big * 0.5], ['icosa:4:0', 0.5 + big * 0.5],
            ['icosa:3:2', 0.4 + big * 0.6], ['icosa:2:3', 0.4 + big * 0.6], ['icosa:4:1', 0.35 + big * 0.7], ['icosa:1:4', 0.35 + big * 0.7],
            ['icosa:5:0', 0.3 + big * 0.6], ['icosa:3:3', 0.3 + big * 0.6],
            ['prolate:3:1:1:-2', 0.6], ['prolate:2:1:1:-2', 0.4], ['geo:5:0', 0.35], ['geo:3:3', 0.35], ['geo:4:0', 0.3]]);
    }

    // ------------------------------------------------------------------ quaternion helpers
    function qmul(a, b) {
        return [a[0] * b[0] - a[1] * b[1] - a[2] * b[2] - a[3] * b[3], a[0] * b[1] + a[1] * b[0] + a[2] * b[3] - a[3] * b[2],
            a[0] * b[2] - a[1] * b[3] + a[2] * b[0] + a[3] * b[1], a[0] * b[3] + a[1] * b[2] - a[2] * b[1] + a[3] * b[0]];
    }
    function qnorm(q) { const l = Math.hypot(q[0], q[1], q[2], q[3]) || 1; q[0] /= l; q[1] /= l; q[2] /= l; q[3] /= l; return q; }
    function qmat(q, M) {
        const [w, x, y, z] = q;
        M[0] = 1 - 2 * (y * y + z * z); M[1] = 2 * (x * y - w * z); M[2] = 2 * (x * z + w * y);
        M[3] = 2 * (x * y + w * z); M[4] = 1 - 2 * (x * x + z * z); M[5] = 2 * (y * z - w * x);
        M[6] = 2 * (x * z - w * y); M[7] = 2 * (y * z + w * x); M[8] = 1 - 2 * (x * x + y * y);
        return M;
    }
    function randQuat() { const u1 = rnd(), u2 = rnd(TAU), u3 = rnd(TAU); const a = Math.sqrt(1 - u1), b = Math.sqrt(u1); return [a * Math.sin(u2), a * Math.cos(u2), b * Math.sin(u3), b * Math.cos(u3)]; }

    // ------------------------------------------------------------------ lantern lifecycle
    const TIERS = [
        { R: [15, 25], alpha: 0.82 },
        { R: [34, 62], alpha: 0.96 },
        { R: [90, 126], alpha: 1 },
    ];
    // camera: horizon at yH (hidden behind the far bank), far shore at Hw. A point on the water at depth Z
    // (shore = 1) projects to yW(Z) = yH + K/Z; a lantern of screen radius R sits at Z = Z0·size/R.
    let yH = 0, K = 0, FPX = 1;
    const yW = (Z) => yH + K / Z;
    const lowest = (R, Z) => Math.min(yW(Z) - R * 1.15, Hw + (H - Hw) * 0.42) - 4; // never below its own water
    function freeSpot(R, tier, Z) {
        const f = h.focus;
        let best = null, bestS = -1e9;
        const lowBig = tier === 2 && rnd() < 0.4; // now and then a big near lantern hangs low over the pond
        for (let k = 0; k < 40; k++) {
            const x = tier === 2 ? (rnd() < 0.5 ? rnd(-0.04, 0.2) : rnd(0.8, 1.04)) * W : rnd(R * 1.4, W - R * 1.4);
            const y0 = tier === 2 ? R * 0.5 : Math.max(R * 1.3, H * 0.06), y1 = Math.max(y0 + 1, lowest(R, Z));
            const low = lowBig || tier === 1 && rnd() < 0.35; // some lanterns skim low over the pond
            const y = low ? rnd(Math.max(y0, y1 - R * 1.6), y1) : rnd(y0, y1);
            const cd = cardDist(x, y);
            let s = Math.min(cd - R - 40, 140);
            if (cd < R + 40) s -= 1000;
            for (const L of lanterns) {
                const d = Math.hypot(L.x - x, L.y - y) - (L.R + R) * (L.tier === tier ? 1.4 : 0.8);
                s = Math.min(s, d);
            }
            if (tier === 2 && !lowBig && y > Hw - R * 1.3) s -= 60;
            s += rnd(20);
            if (s > bestS) { bestS = s; best = [x, y]; }
        }
        return best;
    }
    function cardDist(x, y) {
        const f = h.focus, dx = Math.max(f.x - x, 0, x - (f.x + f.w)), dy = Math.max(f.y - y, 0, y - (f.y + f.h));
        if (dx === 0 && dy === 0) return -Math.min(x - f.x, f.x + f.w - x, y - f.y, f.y + f.h - y);
        return Math.hypot(dx, dy);
    }

    function spawn(tier, state = 'assemble', age = 0) {
        const tr = TIERS[tier];
        const R = rnd(tr.R[0], tr.R[1]) * Math.min(1, Math.max(0.65, Math.min(W, H * 1.6) / 1440));
        // depth from apparent size, but big lanterns are also physically bigger: their water point is chosen
        // inside the visible pond, so the near ones that hang low get the reflection the eye expects
        const band = H - Hw;
        let Z = 40 * rnd(0.8, 1.25) / R;
        if (tier === 2) Z = K / (Hw - yH + band * rnd(0.12, 0.5));
        else if (tier === 1) Z = Math.max(Z, K / (Hw - yH + band * 0.7));
        const spot = freeSpot(R, tier, Z);
        if (!spot) return null;
        const key0 = chooseKind(tier);
        const blend = pick([[0.62, 1], [0.8, 2], [0.94, 1.4]]);
        const key = key0.startsWith('helix') ? 'helix:' + pick([[4.4, 2], [3.4, 1], [5.6, 1]]) : (key0.startsWith('prolate') ? key0 + ':' + blend : key0 + ':0:0:' + blend);
        const m = model(key);
        const mood = h.drift('mood', 900); // drifting festival palette
        const paper = pick(PAPERS.map((_, i) => [i, 1 + 0.9 * Math.max(0, Math.cos((i / PAPERS.length) * TAU + mood * 3))]));
        const accent = rnd() < 0.82 ? ACCENT[paper][(rnd() * 3) | 0] : paper;
        const axis = [rnd(-1, 1), rnd(-1, 1), rnd(-1, 1)];
        const L = {
            id: uid++, tier, R, m, key, x: spot[0], y: spot[1], vx: 0, vy: 0,
            Z, alpha: tr.alpha,
            q: randQuat(), axis, spin: rnd(0.035, 0.08) * (rnd() < 0.5 ? -1 : 1) * (tier === 2 ? 0.7 : 1),
            nx: rnd(1000), ny: rnd(1000), na: rnd(1000),
            paper, accent: m.kind === 'geodesic' ? paper : accent, portalVis: m.kind === 'prolate' || (m.kind === 'icosa' && m.T === 7) ? 1 : 0,
            state, st: 0, age, life: rnd(260, 720) * (tier === 0 ? 0.8 : 1),
            glow: state === 'stable' ? 1 : 0,
            order: assemblyOrder(m, rnd),
            A: m.nC > 150 ? rnd(46, 60) : m.nC > 60 ? rnd(36, 48) : rnd(24, 34),
            jit: new Float32Array(m.nC).map(() => rnd(-1, 1)),
            fly: null, u: new Float32Array(m.nC).fill(state === 'stable' ? 1 : 0),
            sprite: null, spriteDirty: true, lastSprite: -99, spriteMix: -1, rsprite: null, rLast: -99, rMix: -1,
            hole: -1, emitT: 0, emitPhase: rnd(TAU), refl: state === 'stable' ? 1 : 0,
            ripple: rnd(TAU),
        };
        // flight paths of the free capsomers (direction, distance, tumble axis/angle) — model space
        if (m.kind === 'helix') L.R *= 1.3;
        L.fly = new Float32Array(m.nC * 7);
        reseedFlight(L, 1.6, 3.2);
        lanterns.push(L);
        return L;
    }
    function reseedFlight(L, d0, d1) {
        const m = L.m;
        for (let i = 0; i < m.nC; i++) {
            const c = m.caps[i], o = i * 7;
            let dx = c.normal[0] + rnd(-0.9, 0.9), dy = c.normal[1] + rnd(-0.9, 0.9), dz = c.normal[2] + rnd(-0.9, 0.9);
            const l = Math.hypot(dx, dy, dz) || 1, dist = rnd(d0, d1);
            L.fly[o] = dx / l * dist; L.fly[o + 1] = dy / l * dist; L.fly[o + 2] = dz / l * dist;
            let ax = rnd(-1, 1), ay = rnd(-1, 1), az = rnd(-1, 1); const al = Math.hypot(ax, ay, az) || 1;
            L.fly[o + 3] = ax / al; L.fly[o + 4] = ay / al; L.fly[o + 5] = az / al; L.fly[o + 6] = rnd(1.5, 4.5) * (rnd() < 0.5 ? -1 : 1);
        }
    }

    function startOpen(L) {
        if (L.state !== 'stable') return;
        L.state = 'open'; L.st = 0; L.spriteDirty = true;
        // genome leaves through a vertex: the portal of phage-like heads, otherwise the most upward pentamer
        const M = qmat(L.q, T.M || (T.M = new Float32Array(9)));
        let best = -1, bs = -1e9;
        L.m.caps.forEach((c, i) => {
            if (!(c.pent || c.end)) return;
            const n = c.normal, vy = M[3] * n[0] + M[4] * n[1] + M[5] * n[2], vz = M[6] * n[0] + M[7] * n[1] + M[8] * n[2];
            const s = vy * 0.9 + vz * 0.4;
            if (s > bs) { bs = s; best = i; }
        });
        L.hole = L.portalVis && L.m.portal >= 0 && L.m.kind !== 'helix' ? L.m.portal : best;
        L.lid = !(L.portalVis && L.m.kind !== 'helix'); // a pentamer pops off; a portal stays and the genome threads through it
        L.O = rnd(16, 24);
    }
    function startDissolve(L) {
        L.state = 'dissolve'; L.st = 0; L.D = rnd(22, 32);
        reseedFlight(L, 1.2, 2.6);
        for (let i = 0; i < L.m.nC; i++) L.order[i] = rnd();
    }

    // ------------------------------------------------------------------ step
    function stepLantern(L, dt) {
        L.age += dt; L.st += dt;
        const m = L.m, nC = m.nC;
        if (L.state === 'assemble') {
            const F = L.A * 0.32;
            let all = true;
            for (let i = 0; i < nC; i++) {
                const s0 = L.order[i] * (L.A - F) * 0.92 + Math.abs(L.jit[i]) * (L.A - F) * 0.08;
                const u = clamp((L.st - s0) / F, 0, 1);
                L.u[i] = u; if (u < 1) all = false;
            }
            // the candle is lit early; each capsomer brightens as it settles into the shell
            L.glow = lerp(L.glow, all ? 1 : 0.55 + 0.3 * sstep(0.6, 1, L.st / L.A), Math.min(1, dt * 0.6));
            if (all) { L.state = 'stable'; L.st = 0; L.spriteDirty = true; }
        } else if (L.state === 'stable') {
            L.glow = lerp(L.glow, 1, Math.min(1, dt * 0.5));
            L.refl = Math.min(1, L.refl + dt / 3);
            if (L.age > L.life && L.tier > 0 && L.m.kind !== 'geodesic') startOpen(L);
            else if (L.age > L.life) { L.state = 'ascend'; L.st = 0; }
        } else if (L.state === 'open') {
            L.refl = Math.max(0, L.refl - dt / 2);
            if (L.lid && L.hole >= 0) L.u[L.hole] = 1 - easeOut(clamp((L.st - 1.2) / 5, 0, 1));
            const k = clamp((L.st - 2) / (L.O - 4), 0, 1);
            L.glow = 1 - 0.4 * sstep(0, 1, k);
            if (L.st > 2 && L.st < L.O - 2) emitGenome(L, dt); else if (L.st >= L.O - 2) endGenome(L);
            if (L.st >= L.O) startDissolve(L);
        } else if (L.state === 'dissolve') {
            for (let i = 0; i < nC; i++) {
                const s0 = L.order[i] * L.D * 0.45;
                L.u[i] = Math.min(L.u[i], 1 - clamp((L.st - s0) / (L.D * 0.55), 0, 1));
            }
            L.glow = Math.max(0.42, L.glow - dt * 0.9 / L.D);
            if (L.st >= L.D) L.dead = true;
        } else if (L.state === 'ascend') {
            L.refl = Math.max(0, L.refl - dt / 3);
            if (L.y < -L.R * 1.6) L.dead = true;
        }

        // motion: slow noise wander, buoyant bob, keep clear of the card, the water and each other
        const t = h.time;
        const wsc = (L.tier === 2 ? 2.2 : L.tier === 1 ? 3.2 : 2.4);
        let fx = (p.noise(L.nx, t * 0.011) - 0.5) * wsc * 2 + wind * 0.5;
        let fy = (p.noise(L.ny, t * 0.017) - 0.5) * wsc * 2.4;
        const cd = cardDist(L.x, L.y), pad = L.R + 44;
        if (cd < pad) {
            const f = h.focus, cx = clamp(L.x, f.x, f.x + f.w), cy = clamp(L.y, f.y, f.y + f.h);
            let gx = L.x - cx, gy = L.y - cy; const gl = Math.hypot(gx, gy);
            if (gl < 1e-3) { gx = L.x - f.cx; gy = L.y - f.cy; }
            const gl2 = Math.hypot(gx, gy) || 1, push = (pad - cd) * 0.06 + 2;
            fx += gx / gl2 * push; fy += gy / gl2 * push;
        }
        if (L.state !== 'ascend') {
            const top = L.tier === 2 ? L.R * 0.35 : L.R * 1.1 + H * 0.02, bot = lowest(L.R, L.Z);
            if (L.y < top) fy += (top - L.y) * 0.14;
            if (L.y > bot) fy -= (L.y - bot) * 0.08;
        } else fy -= 7 + L.st * 0.25;
        const xm = L.tier === 2 ? L.R * 0.2 : L.R * 1.2;
        if (L.x < xm) fx += (xm - L.x) * 0.04;
        if (L.x > W - xm) fx -= (L.x - (W - xm)) * 0.04;
        for (const O of lanterns) {
            if (O === L || O.dead) continue;
            const dx = L.x - O.x, dy = L.y - O.y, d = Math.hypot(dx, dy) || 1;
            const want = (L.R + O.R) * (O.tier === L.tier ? 1.25 : 0.7) + 12;
            if (d < want) { const k = (want - d) * (O.tier === L.tier ? 0.03 : 0.012); fx += dx / d * k; fy += dy / d * k; }
        }
        const drag = 0.55;
        L.vx += (fx - L.vx * drag) * dt; L.vy += (fy - L.vy * drag) * dt;
        L.x += L.vx * dt; L.y += L.vy * dt;

        // slow tumble about a wandering axis
        const ax = L.axis[0] + (p.noise(L.na, t * 0.006) - 0.5) * 1.6, ay = L.axis[1] + (p.noise(L.na + 9, t * 0.006) - 0.5) * 1.6, az = L.axis[2];
        const al = Math.hypot(ax, ay, az) || 1, w = L.spin * dt * 0.5 / al;
        L.q = qnorm(qmul([1, ax * w, ay * w, az * w], L.q));
    }

    // the genome leaves through the open vertex as one continuous thread of light that curls away
    function emitGenome(L, dt) {
        const M = qmat(L.q, T.M2 || (T.M2 = new Float32Array(9)));
        const c = L.m.caps[L.hole], a = c.apex;
        const px = (M[0] * a[0] + M[1] * a[1] + M[2] * a[2]) * L.R, py = -(M[3] * a[0] + M[4] * a[1] + M[5] * a[2]) * L.R;
        const n = c.normal;
        let dx = M[0] * n[0] + M[1] * n[1] + M[2] * n[2], dy = -(M[3] * n[0] + M[4] * n[1] + M[5] * n[2]);
        const nz = M[6] * n[0] + M[7] * n[1] + M[8] * n[2];
        const dl = Math.hypot(dx, dy) || 1;
        dx /= dl; dy /= dl;
        let rb = L.ribbon;
        if (!rb) {
            rb = L.ribbon = { pts: [], life: rnd(12, 16), emitting: true, th: rnd(TAU), behind: false, w: L.tier === 2 ? 1.35 : L.tier === 1 ? 1 : 0.7, age: 0, hx: 0, hy: 0 };
            ribbons.push(rb);
        }
        rb.behind = nz < -0.1;
        rb.hx = L.x + px; rb.hy = L.y + py;
        const sway = Math.sin(L.st * 0.55 + L.emitPhase) * 0.5 + Math.sin(L.st * 0.23 + L.emitPhase * 2) * 0.3;
        const ang = Math.atan2(dy, dx) + sway;
        L.emitT += dt * 8;
        while (L.emitT >= 1) {
            L.emitT -= 1;
            const sp = 0.55 * Math.max(L.R, 30);
            rb.pts.push({ x: rb.hx, y: rb.hy, vx: Math.cos(ang) * sp + L.vx, vy: Math.sin(ang) * sp + L.vy, age: 0, ph: rnd(TAU) });
        }
    }
    function endGenome(L) { if (L.ribbon) { L.ribbon.emitting = false; L.ribbon = null; } }

    let wind = 0;
    function stepWorld(dt) {
        wind = h.drift('wind', 240) * 2.2;
        for (const L of lanterns) stepLantern(L, dt);
        for (let i = lanterns.length - 1; i >= 0; i--) if (lanterns[i].dead) { endGenome(lanterns[i]); lanterns.splice(i, 1); }
        const dr = Math.exp(-dt * 0.42);
        for (let r = ribbons.length - 1; r >= 0; r--) {
            const rb = ribbons[r];
            rb.age += dt; rb.th += dt * 0.12 * Math.sin(rb.age * 0.21 + rb.life);
            const fx = Math.cos(rb.th) * 4 + wind * 1.2, fy = Math.sin(rb.th) * 2.5 - 4.5;
            for (const s of rb.pts) {
                s.age += dt;
                s.vx = s.vx * dr + fx * dt;
                s.vy = s.vy * dr + fy * dt;
                const cd = cardDist(s.x, s.y);
                if (cd < 60) { const f = h.focus; const gx = s.x - f.cx, gy = s.y - f.cy, gl = Math.hypot(gx, gy) || 1; s.vx += gx / gl * 24 * dt; s.vy += gy / gl * 24 * dt; }
                s.x += s.vx * dt; s.y += s.vy * dt;
            }
            while (rb.pts.length && rb.pts[0].age > rb.life) rb.pts.shift();
            if (!rb.emitting && !rb.pts.length) ribbons.splice(r, 1);
        }
        for (let i = bokeh.length - 1; i >= 0; i--) {
            const b = bokeh[i];
            b.age += dt; b.y -= b.vy * dt; b.x += (wind * 0.4 + Math.sin(b.age * 0.1 + b.ph) * 0.8) * dt;
            if (b.age > b.life) { bokeh.splice(i, 1); }
        }
        const want = Math.round(26 + 14 * h.drift('bokeh', 300));
        while (bokeh.length < want) addBokeh(false);
    }
    function addBokeh(init) {
        const side = rnd() < 0.5;
        let x = side ? rnd(0, 0.34) * W : rnd(0.66, 1) * W, y = rnd(0.04, 0.92) * Hw;
        if (rnd() < 0.3) { x = rnd(W); y = rnd(0.62, 0.98) * Hw; }
        bokeh.push({
            x, y, r: rnd(5, 22) * (rnd() < 0.15 ? 2 : 1), vy: rnd(1, 4), age: init ? rnd(60) : 0, life: rnd(40, 90),
            col: (rnd() * 4) | 0, a: rnd(0.25, 0.7), ph: rnd(TAU),
        });
    }

    function maintain() {
        if (W === 0) return;
        const target = Math.round(15 + 3 * h.drift('pop', 520));
        const live = lanterns.filter(L => L.state !== 'dissolve' && L.state !== 'ascend');
        const building = lanterns.filter(L => L.state === 'assemble').length;
        if (live.length >= target || building >= 3) return;
        const count = [0, 0, 0]; for (const L of live) count[L.tier]++;
        const want = [Math.round(target * 0.42), Math.round(target * 0.42), Math.round(1.5 + h.drift('near', 400) * 0.8)];
        let tier = 1, gap = -99;
        for (let k = 0; k < 3; k++) { const g = want[k] - count[k] + rnd(0.5); if (g > gap) { gap = g; tier = k; } }
        if (gap > 0) spawn(tier);
    }

    // ------------------------------------------------------------------ sprites / backgrounds
    function mk(w, hh) { const c = document.createElement('canvas'); c.width = Math.max(1, Math.ceil(w)); c.height = Math.max(1, Math.ceil(hh)); return c; }
    function glowSprite(col, soft = 1) {
        const s = 128, c = mk(s, s), x = c.getContext('2d');
        const g = x.createRadialGradient(s / 2, s / 2, 0, s / 2, s / 2, s / 2);
        g.addColorStop(0, rgba(col, 0.9)); g.addColorStop(0.18 * soft, rgba(col, 0.42)); g.addColorStop(0.5, rgba(col, 0.1)); g.addColorStop(1, rgba(col, 0));
        x.fillStyle = g; x.fillRect(0, 0, s, s);
        return c;
    }
    function discSprite() {
        const s = 96, c = mk(s, s), x = c.getContext('2d');
        x.filter = 'blur(2px)';
        const g = x.createRadialGradient(s / 2, s / 2, 0, s / 2, s / 2, s * 0.4);
        g.addColorStop(0, 'rgba(255,255,255,0.55)'); g.addColorStop(0.8, 'rgba(255,255,255,0.7)'); g.addColorStop(0.93, 'rgba(255,255,255,0.9)'); g.addColorStop(1, 'rgba(255,255,255,0)');
        x.fillStyle = g; x.beginPath(); x.arc(s / 2, s / 2, s * 0.4, 0, TAU); x.fill();
        return c;
    }

    // scenery is laid out once (same geometry in both themes) and painted per theme
    let scene = null;
    function layoutScene() {
        // far bank: three hedge layers with soft rounded canopies, tall at the sides, low under the card
        const layers = [];
        const LP = [{ base: 0.045, amp: 0.13, r: [16, 30] }, { base: 0.02, amp: 0.085, r: [20, 36] }, { base: 0.004, amp: 0.05, r: [24, 44] }];
        const sc = Math.min(1.25, W / 1440 + 0.25);
        LP.forEach((lp, li) => {
            const prof = [], blobs = [], off = rnd(100);
            for (let x = -20; x <= W + 20; x += 8) {
                const side = Math.pow(Math.abs(x / W - 0.5) * 2, 1.7);
                const hh = H * (lp.base + lp.amp * side) * (0.7 + 0.6 * p.noise(x * 0.003 + off, li * 7));
                prof.push([x, Hw - hh]);
            }
            let x = -10;
            while (x < W + 10) {
                const i = clamp(Math.round((x + 20) / 8), 0, prof.length - 1), r = rnd(lp.r[0], lp.r[1]) * sc * rnd(0.8, 1.5);
                const side = Math.abs(x / W - 0.5) * 2;
                const blossom = li === 1 && rnd() < 0.1 + 0.12 * side ? (rnd() * 3) | 0 : -1;
                // one tree = a loose cluster of rounded canopies
                const cy = prof[i][1] + r * rnd(0.2, 0.7);
                const parts = [[0, 0, 1]];
                const np = 3 + ((rnd() * 4) | 0);
                for (let k = 0; k < np; k++) { const a = rnd(Math.PI * 0.95, Math.PI * 2.05); parts.push([Math.cos(a) * r * rnd(0.45, 0.85), Math.sin(a) * r * rnd(0.3, 0.7), rnd(0.42, 0.72)]); }
                blobs.push({ x, y: cy, r, blossom, parts });
                x += r * rnd(0.9, 2.1);
            }
            layers.push({ prof, blobs });
        });
        const trees = layers;
        // festival garlands: hung between wooden posts standing on the far shore, each span a true catenary
        const strings = [], posts = [];
        for (const xs of [[0.035, 0.135, 0.245], [0.755, 0.865, 0.965]]) {
            const ph = H * rnd(0.085, 0.105);
            const px = xs.map(v => (v + rnd(-0.008, 0.008)) * W);
            for (const x of px) posts.push({ x, top: Hw - ph, w: rnd(2.6, 3.4) });
            for (let i = 0; i + 1 < px.length; i++) {
                const xa = px[i], xb = px[i + 1], y0 = Hw - ph + 3, span = xb - xa, sag = span * rnd(0.12, 0.16);
                // solve a·(cosh(span/2a) − 1) = sag for the catenary parameter a (bisection)
                let lo = span * 0.05, hi = span * 50;
                for (let it = 0; it < 60; it++) { const mid = (lo + hi) / 2; if (mid * (Math.cosh(span / (2 * mid)) - 1) > sag) lo = mid; else hi = mid; }
                const ac = (lo + hi) / 2, bulbs = [], nb = Math.max(3, Math.round(span / 24));
                for (let k = 1; k < nb; k++) bulbs.push({ t: k / nb, c: (rnd() * 4) | 0, r: rnd(2.6, 3.8) });
                strings.push({ xa, xb, y0, ac, span, bulbs });
            }
        }
        const pads = [];
        for (let i = 0; i < 16; i++) {
            const left = i % 2 === 0, x = left ? rnd(0, 0.3) * W : rnd(0.7, 1) * W;
            const yy = lerp(Hw, H, Math.pow(rnd(0.15, 1), 0.7));
            pads.push({ x, y: yy, r: lerp(8, 30, (yy - Hw) / (H - Hw)) * rnd(0.7, 1.2), a: rnd(TAU), flower: rnd() < 0.25 ? (rnd() * 3) | 0 : -1 });
        }
        const ripples = [];
        for (let i = 0; i < 70; i++) { const yy = lerp(Hw, H, Math.pow(rnd(), 0.8)); ripples.push({ x: rnd(W), y: yy, w: rnd(20, 120) * (0.4 + (yy - Hw) / (H - Hw)), a: rnd(0.04, 0.12) }); }
        const stars = [];
        for (let i = 0; i < 70; i++) stars.push({ x: rnd(W), y: Math.pow(rnd(), 1.6) * Hw * 0.7, r: rnd(0.4, 1.1), a: rnd(0.2, 0.6) });
        scene = { trees, strings, posts, pads, ripples, stars };
    }
    function catenary(s, t) {
        const x = lerp(s.xa, s.xb, t), u = x - (s.xa + s.xb) / 2;
        return [x, s.y0 + s.ac * (Math.cosh(s.span / (2 * s.ac)) - Math.cosh(u / s.ac))];
    }

    function paintScene(dark) {
        const P = dark ? SD : SL;
        const c = mk(W * pd, H * pd), x = c.getContext('2d');
        x.scale(pd, pd);
        // sky
        let g = x.createLinearGradient(0, 0, 0, Hw);
        g.addColorStop(0, rgb(P.sky0)); g.addColorStop(0.55, rgb(P.sky1)); g.addColorStop(1, rgb(P.sky2));
        x.fillStyle = g; x.fillRect(0, 0, W, Hw + 2);
        if (!dark) {
            g = x.createRadialGradient(W * 0.86, H * 0.04, 0, W * 0.86, H * 0.04, W * 0.55);
            g.addColorStop(0, 'rgba(255,244,222,0.75)'); g.addColorStop(0.4, 'rgba(255,238,212,0.25)'); g.addColorStop(1, 'rgba(255,238,212,0)');
            x.fillStyle = g; x.fillRect(0, 0, W, Hw);
        } else {
            for (const s of scene.stars) { x.fillStyle = `rgba(255,236,210,${s.a * 0.5})`; x.beginPath(); x.arc(s.x, s.y, s.r, 0, TAU); x.fill(); }
            g = x.createRadialGradient(W * 0.5, Hw, 0, W * 0.5, Hw, W * 0.6);
            g.addColorStop(0, 'rgba(150,80,48,0.32)'); g.addColorStop(0.5, 'rgba(110,56,44,0.12)'); g.addColorStop(1, 'rgba(90,40,40,0)');
            x.fillStyle = g; x.fillRect(0, 0, W, Hw);
        }
        // bank (drawn into its own layer so it can be mirrored on the water)
        const bank = mk(W * pd, H * pd), b = bank.getContext('2d');
        b.scale(pd, pd);
        scene.trees.forEach((ly, li) => {
            b.filter = `blur(${(2.6 - li * 1.0).toFixed(1)}px)`;
            b.fillStyle = rgb(P.tree[li]);
            b.beginPath(); b.moveTo(-30, Hw + 2);
            for (const [px, py] of ly.prof) b.lineTo(px, py);
            b.lineTo(W + 30, Hw + 2); b.closePath(); b.fill();
            for (const t of ly.blobs) {
                const col = t.blossom >= 0 ? mixc(P.blossom[t.blossom], P.tree[li], 0.25) : mixc(P.tree[li], P.tree[Math.max(0, li - 1)], (t.x * 7.3 % 1) * 0.5);
                b.fillStyle = rgb(col);
                b.beginPath();
                for (const [dx, dy, rr] of t.parts) { b.moveTo(t.x + dx + rr * t.r, t.y + dy); b.arc(t.x + dx, t.y + dy, rr * t.r, 0, TAU); }
                b.fill();
                b.fillRect(t.x - t.r * 0.8, t.y, t.r * 1.6, Hw - t.y + 2);
                // a lighter dapple on the sunny (upper-right) side by day
                if (!dark) {
                    b.fillStyle = 'rgba(255,250,232,0.14)';
                    b.beginPath(); b.arc(t.x + t.r * 0.25, t.y - t.r * 0.3, t.r * 0.5, 0, TAU); b.fill();
                }
            }
        });
        b.filter = 'none';
        // tiny lights tucked among the dark trees
        if (dark) {
            for (const t of scene.trees[1].blobs) if (t.x % 3 < 1.1) {
                const gx = t.x + (t.r * 0.5) * Math.sin(t.y), gy = t.y + t.r * 0.3;
                const gg = b.createRadialGradient(gx, gy, 0, gx, gy, 9);
                gg.addColorStop(0, 'rgba(255,200,130,0.5)'); gg.addColorStop(1, 'rgba(255,170,100,0)');
                b.fillStyle = gg; b.fillRect(gx - 9, gy - 9, 18, 18);
            }
        }
        // posts on the shore, then the garlands strung between them
        for (const po of scene.posts) {
            b.fillStyle = rgb(dark ? [70, 48, 42] : [150, 124, 100]);
            b.fillRect(po.x - po.w / 2, po.top, po.w, Hw - po.top + 1);
            b.fillStyle = rgb(dark ? [92, 64, 52] : [176, 150, 124]);
            b.fillRect(po.x - po.w / 2 - 1, po.top - 1.5, po.w + 2, 2.5);
            if (dark) { // the garland lights pick out the posts' faces
                const gg = b.createLinearGradient(0, po.top, 0, po.top + H * 0.05);
                gg.addColorStop(0, 'rgba(255,190,120,0.45)'); gg.addColorStop(1, 'rgba(255,190,120,0)');
                b.fillStyle = gg; b.fillRect(po.x - po.w / 2, po.top, po.w, H * 0.05);
            }
        }
        for (const s of scene.strings) {
            b.strokeStyle = rgba(P.string, dark ? 0.7 : 0.55); b.lineWidth = 0.8;
            b.beginPath();
            for (let i = 0; i <= 40; i++) { const [px, py] = catenary(s, i / 40); i ? b.lineTo(px, py) : b.moveTo(px, py); }
            b.stroke();
            for (const bl of s.bulbs) {
                const [px, py] = catenary(s, bl.t), col = P.bulb[bl.c];
                if (dark) {
                    const gg = b.createRadialGradient(px, py + bl.r, 0, px, py + bl.r, bl.r * 5);
                    gg.addColorStop(0, rgba(col, 0.45)); gg.addColorStop(1, rgba(col, 0));
                    b.fillStyle = gg; b.fillRect(px - bl.r * 5, py + bl.r - bl.r * 5, bl.r * 10, bl.r * 10);
                    b.fillStyle = rgba(mixc(col, [255, 240, 210], 0.5), 0.95);
                } else b.fillStyle = rgba(col, 0.95);
                b.beginPath(); b.ellipse(px, py + bl.r + 0.6, bl.r * 0.92, bl.r, 0, 0, TAU); b.fill();
                b.strokeStyle = rgba(P.string, 0.5); b.lineWidth = 0.6;
                b.beginPath(); b.moveTo(px, py); b.lineTo(px, py + 1); b.stroke();
            }
        }
        x.drawImage(bank, 0, 0, W, H);
        // shore line
        x.fillStyle = rgba(P.shore, dark ? 0.9 : 0.35); x.fillRect(0, Hw - 1.2, W, 2.2);
        // water: gradient, mirrored bank, ripples, lily pads
        g = x.createLinearGradient(0, Hw, 0, H);
        g.addColorStop(0, rgb(P.water0)); g.addColorStop(1, rgb(P.water1));
        x.fillStyle = g; x.fillRect(0, Hw, W, H - Hw);
        x.save(); x.beginPath(); x.rect(0, Hw, W, H - Hw); x.clip();
        x.globalAlpha = dark ? 0.55 : 0.35; x.filter = 'blur(3px)';
        x.translate(0, 2 * Hw); x.scale(1, -1);
        x.drawImage(bank, 0, 0, W, H);
        x.restore();
        x.filter = 'none';
        if (dark) {
            g = x.createRadialGradient(W * 0.5, Hw, 0, W * 0.5, Hw, W * 0.45);
            g.addColorStop(0, 'rgba(150,80,50,0.18)'); g.addColorStop(1, 'rgba(150,80,50,0)');
            x.fillStyle = g; x.fillRect(0, Hw, W, H - Hw);
        }
        for (const r of scene.ripples) { x.fillStyle = rgba(P.ripple, r.a * (dark ? 0.35 : 0.9)); x.fillRect(r.x, r.y, r.w, 1); }
        for (const pd2 of scene.pads) {
            const sq = 0.32 + 0.2 * (pd2.y - Hw) / (H - Hw);
            x.save(); x.translate(pd2.x, pd2.y); x.scale(1, sq);
            x.fillStyle = rgba(P.pad, dark ? 0.9 : 0.75);
            x.beginPath(); x.moveTo(0, 0); x.arc(0, 0, pd2.r, pd2.a + 0.25, pd2.a + TAU - 0.25); x.closePath(); x.fill();
            x.strokeStyle = rgba(dark ? [10, 12, 10] : [120, 146, 112], 0.4); x.lineWidth = 1; x.stroke();
            x.restore();
            if (pd2.flower >= 0) {
                const col = dark ? [70, 44, 52] : P.blossom[pd2.flower];
                x.fillStyle = rgba(col, 0.95);
                for (let k = 0; k < 6; k++) { const a = (k / 6) * TAU; x.beginPath(); x.ellipse(pd2.x + Math.cos(a) * pd2.r * 0.18, pd2.y - pd2.r * 0.12 + Math.sin(a) * pd2.r * 0.06, pd2.r * 0.16, pd2.r * 0.09, a, 0, TAU); x.fill(); }
            }
        }
        return c;
    }

    // ------------------------------------------------------------------ lantern rendering
    const MAXP = 2048;
    const V = new Float32Array(MAXP * 3);      // view-space points (scaled by R; y up)
    const CN = new Float32Array(1024 * 3);     // view-space capsomer normals
    const CC = new Float32Array(1024 * 3);     // view-space capsomer centres
    const ORD = new Int32Array(1024);
    const DEPTH = new Float32Array(1024);
    const M9 = new Float32Array(9);
    let binPaths = [];

    function rampLight(base, s) {
        const sh = [base[0] * 0.8, base[1] * 0.76, base[2] * 0.82], hi = [lerp(base[0], 255, 0.6), lerp(base[1], 255, 0.6), lerp(base[2], 252, 0.6)];
        return s < 0.5 ? mixc(sh, base, s / 0.5) : mixc(base, hi, (s - 0.5) / 0.5);
    }
    function rampDark(col, s, paper) {
        // unlit paper at night is a dusky mauve version of its daylight colour; lit paper takes the candle's colour
        const em = paper ? [paper[0] * 0.26 + 26, paper[1] * 0.2 + 12, paper[2] * 0.22 + 18] : [col[0] * 0.2 + 26, col[1] * 0.13 + 16, col[2] * 0.14 + 24], mid = [col[0] * 0.8, col[1] * 0.76, col[2] * 0.72], hot = mixc(col, [255, 247, 228], 0.62);
        // a warm ember stop keeps half-lit paper glowing rather than muddy
        const low = [col[0] * 0.55 + 22, col[1] * 0.4 + 10, col[2] * 0.32 + 10];
        return s < 0.3 ? mixc(em, low, s / 0.3) : s < 0.6 ? mixc(low, mid, (s - 0.3) / 0.3) : mixc(mid, hot, (s - 0.6) / 0.4);
    }
    function lanternColours(L, m) {
        const cs = L.cols || (L.cols = { bins: [[], []], seam: '', back: '', groove: '', inner: 0 });
        const papers = [PAPERS[L.paper], PAPERS[L.accent]];
        for (let r = 0; r < 2; r++) for (let b = 0; b < NB; b++) {
            const s = b / (NB - 1);
            cs.bins[r][b] = rgb(mixc(rampLight(papers[r].L, s), rampDark(papers[r].D, s, papers[r].L), m));
        }
        const base = papers[0].L, em = rampDark(papers[0].D, 0.05, base);
        cs.seam = rgba(mixc([base[0] * 0.6, base[1] * 0.56, base[2] * 0.58], [em[0] * 0.7, em[1] * 0.6, em[2] * 0.6], m), lerp(0.42, 0.6, m));
        cs.back = rgba(mixc([base[0] * 0.5, base[1] * 0.48, base[2] * 0.5], [em[0] * 0.5, em[1] * 0.4, em[2] * 0.4], m), lerp(0.07, 0.22 * L.glow, m));
        cs.crease = rgba(mixc([255, 255, 252], papers[0].D, m), lerp(0.22, 0.1, m));
        return cs;
    }

    function project(L, tilt = 0) {
        const m = L.m, R = L.R, pos = m.pos, n = m.nP + m.nC;
        qmat(L.q, M9);
        if (tilt) { // view from below (mirror camera): rotate about the screen x axis
            const c = Math.cos(tilt), s = Math.sin(tilt);
            for (let j = 0; j < 3; j++) { const r1 = M9[3 + j], r2 = M9[6 + j]; M9[3 + j] = c * r1 - s * r2; M9[6 + j] = s * r1 + c * r2; }
        }
        const [a, b, c, d, e, f, g, hh, i] = M9;
        for (let k = 0; k < n; k++) {
            const x = pos[k * 3], y = pos[k * 3 + 1], z = pos[k * 3 + 2];
            V[k * 3] = (a * x + b * y + c * z) * R; V[k * 3 + 1] = (d * x + e * y + f * z) * R; V[k * 3 + 2] = (g * x + hh * y + i * z) * R;
        }
        for (let k = 0; k < m.nC; k++) {
            const cp = m.caps[k], nn = cp.normal, cc = cp.centre;
            CN[k * 3] = a * nn[0] + b * nn[1] + c * nn[2]; CN[k * 3 + 1] = d * nn[0] + e * nn[1] + f * nn[2]; CN[k * 3 + 2] = g * nn[0] + hh * nn[1] + i * nn[2];
            CC[k * 3] = (a * cc[0] + b * cc[1] + c * cc[2]) * R; CC[k * 3 + 1] = (d * cc[0] + e * cc[1] + f * cc[2]) * R; CC[k * 3 + 2] = (g * cc[0] + hh * cc[1] + i * cc[2]) * R;
        }
    }

    // shading of one facet (apex A, corners B, C; view space): returns a 0..1 shade for the current theme mix
    const LX = -0.42, LY = 0.62, LZ = 0.66; // daylight from the upper left, towards the viewer
    function facetShade(L, ax, ay, az, bx, by, bz, cx, cy, cz, front, jit, mT, gm = 1) {
        let nx = (by - ay) * (cz - az) - (bz - az) * (cy - ay), ny = (bz - az) * (cx - ax) - (bx - ax) * (cz - az), nz = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        const nl = Math.hypot(nx, ny, nz) || 1; nx /= nl; ny /= nl; nz /= nl;
        if (!front) { nx = -nx; ny = -ny; nz = -nz; }
        // candle hangs just below the centre: irradiance on the paper from inside
        const R = L.R, px = (ax + bx + cx) / 3, py = (ay + by + cy) / 3 + R * 0.28, pz = (az + bz + cz) / 3;
        const pl = Math.min(Math.hypot(px, py, pz), R * 1.1) || 1; // loose capsomers keep a little of the light they carried
        const inc = Math.max(0, (nx * px + ny * py + nz * pz) / pl) * (front ? 1 : 0.6) + (front ? 0 : 0.4);
        const fall = (R * R * 0.95) / (pl * pl + R * R * 0.12);
        let sD = L.glow * gm * clamp(Math.pow(inc, 1.6) * 1.25 * fall, 0, 1.1) * (0.5 + 0.5 * Math.max(nz, 0)) + 0.05 + jit * 0.035;
        let sL = front ? 0.22 + 0.72 * Math.max(0, nx * LX + ny * LY + nz * LZ) + 0.1 * nz + jit * 0.04 + 0.08 * L.glow * inc
            : 0.22 + 0.15 * L.glow + jit * 0.03;
        return clamp(lerp(sL, sD, mT), 0, 1);
    }

    // stable shell: batched by shade bin (front facets only; the back structure shows through as faint seams)
    function drawShell(ctx, L, ox, oy, mT, tilt = 0) {
        const m = L.m, caps = m.caps, cols = lanternColours(L, mT);
        project(L, tilt);
        for (let r = 0; r < 2; r++) for (let b = 0; b < NB; b++) binPaths[r * NB + b] = null;
        const crease = new Path2D();
        for (let k = 0; k < m.nC; k++) {
            if (CN[k * 3 + 2] < -0.35) continue;
            const cp = caps[k], cr = cp.corners, A = cp.apexIdx * 3, nc = cr.length, ramp = cp.pent || cp.end ? 1 : 0;
            const ax = V[A], ay = V[A + 1], az = V[A + 2];
            for (let j = 0; j < nc; j++) {
                const B = cr[j] * 3, C = cr[(j + 1) % nc] * 3;
                const bx = V[B], by = V[B + 1], bz = V[B + 2], cx = V[C], cy = V[C + 1], cz = V[C + 2];
                const fz = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                if (fz <= 0) continue;
                const s = facetShade(L, ax, ay, az, bx, by, bz, cx, cy, cz, true, L.jit[k], mT);
                const bi = ramp * NB + Math.round(s * (NB - 1));
                const path = binPaths[bi] || (binPaths[bi] = new Path2D());
                path.moveTo(ox + ax, oy - ay); path.lineTo(ox + bx, oy - by); path.lineTo(ox + cx, oy - cy); path.closePath();
                if (j % 1 === 0) { crease.moveTo(ox + ax, oy - ay); crease.lineTo(ox + bx, oy - by); }
            }
        }
        for (let bi = 0; bi < 2 * NB; bi++) {
            const path = binPaths[bi]; if (!path) continue;
            ctx.fillStyle = cols.bins[bi >= NB ? 1 : 0][bi % NB];
            ctx.fill(path);
            ctx.strokeStyle = ctx.fillStyle; ctx.lineWidth = 0.6; ctx.stroke(path);
        }
        drawSeams(ctx, L, ox, oy, cols, null);
        ctx.strokeStyle = cols.crease; ctx.lineWidth = Math.max(0.5, L.R * 0.008); ctx.stroke(crease);
        drawDetails(ctx, L, ox, oy, mT, null);
    }

    function drawSeams(ctx, L, ox, oy, cols, P) {
        const m = L.m, seams = m.seams, Vp = P || V;
        const front = new Path2D(), back = new Path2D(), groove = m.kind === 'helix' ? new Path2D() : null;
        for (let s = 0; s < seams.length; s++) {
            const [i, j, type] = seams[s];
            if (P && (L.segA && (!L.segA[i] || !L.segA[j]))) continue;
            const zi = Vp[i * 3 + 2], zj = Vp[j * 3 + 2], z = (zi + zj) * 0.5;
            const tgt = z > -L.R * 0.04 ? (type === 1 && groove ? groove : front) : back;
            tgt.moveTo(ox + Vp[i * 3], oy - Vp[i * 3 + 1]); tgt.lineTo(ox + Vp[j * 3], oy - Vp[j * 3 + 1]);
        }
        const lw = clamp(L.R * 0.016, 0.55, 1.7);
        if (!P) { ctx.strokeStyle = cols.back; ctx.lineWidth = lw; ctx.stroke(back); }
        ctx.strokeStyle = cols.seam; ctx.lineWidth = lw * (groove ? 0.6 : 1); ctx.stroke(front);
        if (groove) { ctx.lineWidth = lw * 1.3; ctx.stroke(groove); }
    }

    // portal rosette (12-fold ring sitting in a 5-fold vertex) and the rod's central channel
    function drawDetails(ctx, L, ox, oy, mT, P) {
        const m = L.m;
        const vis = (k) => CN[k * 3 + 2] > 0.05;
        if (L.portalVis && m.portal >= 0 && m.kind !== 'helix' && vis(m.portal) && !(L.state === 'open' && L.lid) && (!P || L.u[m.portal] >= 1)) {
            const k = m.portal, cp = m.caps[k], n = [CN[k * 3], CN[k * 3 + 1], CN[k * 3 + 2]], A = cp.apexIdx * 3;
            const ax = V[A], ay = V[A + 1], az = V[A + 2];
            let t1 = [n[1], -n[0], 0]; const tl = Math.hypot(t1[0], t1[1]) || 1; t1 = [t1[0] / tl, t1[1] / tl, 0];
            const t2 = [n[1] * t1[2] - n[2] * t1[1], n[2] * t1[0] - n[0] * t1[2], n[0] * t1[1] - n[1] * t1[0]];
            const rr = cp.rad * L.R * 0.5, cols = L.cols;
            for (let j = 0; j < 12; j++) {
                const a0 = (j / 12) * TAU, a1 = ((j + 1) / 12) * TAU;
                const bx = ax + (Math.cos(a0) * t1[0] + Math.sin(a0) * t2[0]) * rr, by = ay + (Math.cos(a0) * t1[1] + Math.sin(a0) * t2[1]) * rr, bz = az + (Math.cos(a0) * t1[2] + Math.sin(a0) * t2[2]) * rr;
                const cx = ax + (Math.cos(a1) * t1[0] + Math.sin(a1) * t2[0]) * rr, cy = ay + (Math.cos(a1) * t1[1] + Math.sin(a1) * t2[1]) * rr, cz = az + (Math.cos(a1) * t1[2] + Math.sin(a1) * t2[2]) * rr;
                const hx = ax + n[0] * rr * 0.35, hy = ay + n[1] * rr * 0.35, hz = az + n[2] * rr * 0.35;
                const s = facetShade(L, hx, hy, hz, bx, by, bz, cx, cy, cz, true, 0, mT);
                ctx.fillStyle = cols.bins[1][Math.round(clamp(s * 0.92, 0, 1) * (NB - 1))];
                ctx.beginPath(); ctx.moveTo(ox + hx, oy - hy); ctx.lineTo(ox + bx, oy - by); ctx.lineTo(ox + cx, oy - cy); ctx.closePath(); ctx.fill();
                ctx.strokeStyle = cols.seam; ctx.lineWidth = 0.5; ctx.stroke();
            }
        }
        if (m.kind === 'helix') {
            for (const k of m.ends) {
                if (!vis(k) || (P && L.u[k] < 1)) continue;
                const cp = m.caps[k], n = [CN[k * 3], CN[k * 3 + 1], CN[k * 3 + 2]], A = cp.apexIdx * 3;
                let t1 = [n[1], -n[0], 0]; const tl = Math.hypot(t1[0], t1[1]) || 1; t1 = [t1[0] / tl, t1[1] / tl, 0];
                const t2 = [n[1] * t1[2] - n[2] * t1[1], n[2] * t1[0] - n[0] * t1[2], n[0] * t1[1] - n[1] * t1[0]];
                const rr = m.radius * L.R * 0.2;
                ctx.beginPath();
                for (let j = 0; j <= 16; j++) {
                    const a = (j / 16) * TAU, x = V[A] + (Math.cos(a) * t1[0] + Math.sin(a) * t2[0]) * rr, y = V[A + 1] + (Math.cos(a) * t1[1] + Math.sin(a) * t2[1]) * rr;
                    j ? ctx.lineTo(ox + x, oy - y) : ctx.moveTo(ox + x, oy - y);
                }
                ctx.fillStyle = rgba(mixc(PAPERS[L.paper].L.map(v => v * 0.55), [255, 236, 200], mT), lerp(0.6, 0.85 * L.glow, mT));
                ctx.fill();
            }
        }
    }

    // assembling / opening / dissolving: capsomers move independently → painter's order per capsomer
    const PV = new Float32Array(MAXP * 3);
    function drawLoose(ctx, L, ox, oy, mT, alphaMul) {
        const m = L.m, caps = m.caps, R = L.R, cols = lanternColours(L, mT);
        project(L);
        const [a, b, c, d, e, f, g, hh, i] = M9;
        // flight offsets in view space
        let nO = 0;
        for (let k = 0; k < m.nC; k++) {
            const u = L.u[k];
            if (u <= 0.001) continue;
            const off = (1 - easeOut(u));
            DEPTH[k] = CC[k * 3 + 2] + (g * L.fly[k * 7] + hh * L.fly[k * 7 + 1] + i * L.fly[k * 7 + 2]) * R * off;
            ORD[nO++] = k;
        }
        const ord = Array.from(ORD.subarray(0, nO)).sort((x, y) => DEPTH[x] - DEPTH[y]);
        for (const k of ord) {
            const cp = caps[k], cr = cp.corners, nc = cr.length, u = L.u[k], off = 1 - easeOut(u), o = k * 7;
            const fx = L.fly[o], fy = L.fly[o + 1], fz = L.fly[o + 2];
            const tx = (a * fx + b * fy + c * fz) * R * off, ty = (d * fx + e * fy + f * fz) * R * off, tz = (g * fx + hh * fy + i * fz) * R * off;
            // tumble about the capsomer centre (Rodrigues, view space)
            const kx = a * L.fly[o + 3] + b * L.fly[o + 4] + c * L.fly[o + 5], ky = d * L.fly[o + 3] + e * L.fly[o + 4] + f * L.fly[o + 5], kz = g * L.fly[o + 3] + hh * L.fly[o + 4] + i * L.fly[o + 5];
            const ang = L.fly[o + 6] * off, ca = Math.cos(ang), sa = Math.sin(ang);
            const cx0 = CC[k * 3], cy0 = CC[k * 3 + 1], cz0 = CC[k * 3 + 2];
            const tf = (src, dst) => {
                const vx = V[src] - cx0, vy = V[src + 1] - cy0, vz = V[src + 2] - cz0;
                const kd = kx * vx + ky * vy + kz * vz;
                const rx = vx * ca + (ky * vz - kz * vy) * sa + kx * kd * (1 - ca), ry = vy * ca + (kz * vx - kx * vz) * sa + ky * kd * (1 - ca), rz = vz * ca + (kx * vy - ky * vx) * sa + kz * kd * (1 - ca);
                PV[dst] = cx0 + rx + tx; PV[dst + 1] = cy0 + ry + ty; PV[dst + 2] = cz0 + rz + tz;
            };
            tf(cp.apexIdx * 3, 0);
            for (let j = 0; j < nc; j++) tf(cr[j] * 3, (j + 1) * 3);
            ctx.globalAlpha = alphaMul * sstep(0, 0.4, u);
            const gmul = 0.35 + 0.65 * easeOut(u) * easeOut(u);
            const ramp = cp.pent || cp.end ? 1 : 0;
            const ax = PV[0], ay = PV[1], az = PV[2];
            const outline = new Path2D();
            for (let j = 0; j < nc; j++) {
                const B = (j + 1) * 3, C = ((j + 1) % nc + 1) * 3;
                const bx = PV[B], by = PV[B + 1], bz = PV[B + 2], cx = PV[C], cy = PV[C + 1], cz = PV[C + 2];
                const fzn = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                const s = facetShade(L, ax, ay, az, bx, by, bz, cx, cy, cz, fzn > 0, L.jit[k], mT, gmul);
                ctx.fillStyle = cols.bins[ramp][Math.round(s * (NB - 1))];
                ctx.beginPath(); ctx.moveTo(ox + ax, oy - ay); ctx.lineTo(ox + bx, oy - by); ctx.lineTo(ox + cx, oy - cy); ctx.closePath(); ctx.fill();
                ctx.strokeStyle = ctx.fillStyle; ctx.lineWidth = 0.6; ctx.stroke();
                outline.moveTo(ox + bx, oy - by); outline.lineTo(ox + cx, oy - cy);
            }
            ctx.strokeStyle = cols.seam; ctx.lineWidth = clamp(R * 0.016, 0.55, 1.7); ctx.stroke(outline);
        }
        ctx.globalAlpha = 1;
        drawDetails(ctx, L, ox, oy, mT, PV);
    }

    function spriteFor(L, mT) {
        const half = Math.ceil(L.R * 1.12 + 4), sz = Math.ceil(half * 2 * pd);
        if (!L.sprite || L.sprite.width !== sz) { L.sprite = mk(sz, sz); L.sctx = L.sprite.getContext('2d'); L.half = half; }
        const x = L.sctx;
        x.setTransform(1, 0, 0, 1, 0, 0); x.clearRect(0, 0, sz, sz);
        x.setTransform(pd, 0, 0, pd, 0, 0);
        x.lineJoin = 'round'; x.lineCap = 'round';
        drawShell(x, L, half, half, mT);
        // paper fibres: mottled tissue seen against the light (only where paper was drawn)
        x.globalCompositeOperation = 'source-atop';
        x.globalAlpha = lerp(0.45, 0.7, mT);
        const ts = Math.max(L.R * 2.2, 90), o = (L.id * 37) % 128;
        x.drawImage(fibre, o, (o * 3) % 128, 256 - 128 * 0.5, 256 - 128 * 0.5, half - ts / 2, half - ts / 2, ts, ts);
        x.globalCompositeOperation = 'source-over'; x.globalAlpha = 1;
        L.spriteMix = mT; L.lastSprite = frame; L.spriteDirty = false;
    }

    // ---- pond reflections: mirror about the water plane under each lantern (camera model above), rendered
    // from the lantern geometry seen from the mirror camera, into a half-resolution buffer that is rippled
    // and Fresnel-dimmed on the way back. Anything whose mirror image lands above the shore line is hidden.
    let rbuf = null, rctx = null, RS = 0.5;
    function reflGeom(L) {
        const yr = 2 * yW(L.Z) - L.y;
        const t1 = Math.atan((L.y - yH) / FPX), t2 = Math.atan((yr - yH) / FPX);
        return { yr, tilt: -(t1 + t2) };
    }
    function reflSpriteFor(L, mT, tilt) {
        const half = Math.ceil(L.R * 1.12 + 4), sz = Math.ceil(half * 2 * pd * RS);
        if (!L.rsprite || L.rsprite.width !== sz) { L.rsprite = mk(sz, sz); L.rsctx = L.rsprite.getContext('2d'); }
        const x = L.rsctx;
        x.setTransform(1, 0, 0, 1, 0, 0); x.clearRect(0, 0, sz, sz);
        x.setTransform(pd * RS, 0, 0, pd * RS, 0, 0);
        x.lineJoin = 'round'; x.lineCap = 'round';
        drawShell(x, L, half, half, mT, tilt);
        L.rMix = mT; L.rLast = frame; L.rHalf = half;
    }
    function drawReflections(ctx, mT, order) {
        const bw = Math.ceil(W * pd * RS), bh = Math.ceil((H - Hw) * pd * RS);
        if (!rbuf || rbuf.width !== bw || rbuf.height !== bh) { rbuf = mk(bw, bh); rctx = rbuf.getContext('2d'); }
        rctx.setTransform(1, 0, 0, 1, 0, 0); rctx.clearRect(0, 0, bw, bh);
        const k = pd * RS;
        let any = false, budget = 3;
        for (const L of order) {
            if (L.refl <= 0.01 && L.state !== 'assemble' && L.state !== 'open' && L.state !== 'dissolve') continue;
            const { yr, tilt } = reflGeom(L);
            const ext = L.R * 1.15;
            if (yr - ext > H || yr + ext < Hw) continue; // mirror image entirely below the frame or behind the shore
            any = true;
            const ra = L.alpha * (L.state === 'stable' || L.state === 'ascend' ? L.refl : 1);
            // reflected candle glow
            if (L.glow > 0.02) {
                const hr = L.R * 2.3;
                rctx.setTransform(k, 0, 0, k, 0, 0);
                rctx.globalCompositeOperation = 'lighter';
                rctx.globalAlpha = L.glow * ra * lerp(0.05, 0.3, mT);
                rctx.drawImage(mT > 0.5 ? glowSpr[L.paper] : whiteGlow, L.x - hr, yr - Hw - hr, hr * 2, hr * 2);
                rctx.globalCompositeOperation = 'source-over';
            }
            rctx.globalAlpha = ra;
            if (L.state === 'stable' || L.state === 'ascend') {
                const stale = Math.abs(L.rMix - mT) > 0.03;
                if (!L.rsprite || frame - L.rLast >= 4 + (L.id % 3) && (!stale || budget-- > 0) || stale && budget-- > 0) reflSpriteFor(L, mT, tilt);
                const hf = L.rHalf;
                rctx.setTransform(k, 0, 0, -k, L.x * k, (yr - Hw) * k);
                rctx.drawImage(L.rsprite, -hf, -hf, hf * 2, hf * 2);
            } else {
                rctx.setTransform(k, 0, 0, -k, L.x * k, (yr - Hw) * k);
                rctx.lineJoin = 'round';
                drawLoose(rctx, L, 0, 0, mT, ra);
            }
            rctx.globalAlpha = 1;
            // a long glitter path on the water below bright lanterns at night
            if (mT > 0.01 && L.glow > 0.02) {
                rctx.setTransform(k, 0, 0, k, 0, 0);
                rctx.globalCompositeOperation = 'lighter';
                rctx.globalAlpha = 0.14 * mT * ra * L.glow;
                rctx.drawImage(glowSpr[L.paper], L.x - L.R * 0.7, Math.max(0, yr - Hw - L.R * 1.4), L.R * 1.4, L.R * 3.4);
                rctx.globalCompositeOperation = 'source-over'; rctx.globalAlpha = 1;
            }
        }
        if (!any) return;
        // the water tints and darkens what it mirrors (stronger by day, when the pond is pale and milky)
        rctx.setTransform(1, 0, 0, 1, 0, 0);
        rctx.globalCompositeOperation = 'source-atop';
        rctx.globalAlpha = lerp(0.2, 0.07, mT);
        rctx.fillStyle = mT < 0.5 ? 'rgb(96,128,130)' : 'rgb(24,12,22)';
        rctx.fillRect(0, 0, bw, bh);
        rctx.globalCompositeOperation = 'source-over'; rctx.globalAlpha = 1;
        // composite in thin strips: ripple displacement grows towards the viewer, reflectance falls (Fresnel),
        // and gentle wavelets dim alternate bands so the mirror image breaks up at its edges
        const strip = 2, n = Math.ceil(bh / strip), t = h.time;
        const a0 = lerp(0.94, 0.68, mT), a1 = lerp(0.72, 0.4, mT);
        for (let i = 0; i < n; i++) {
            const v = i / n, sy = i * strip;
            const dx = (Math.sin(t * 0.7 + i * 0.32 + Math.sin(i * 0.07 + t * 0.19) * 1.5) * 0.65
                + Math.sin(t * 1.1 - i * 0.15) * 0.25 + Math.sin(t * 1.7 + i * 1.3) * 0.42) * (1.2 + 5 * v);
            const wave = 0.5 + 0.5 * Math.sin(i * (0.55 - 0.25 * v) - t * 0.9 + Math.sin(i * 0.05 + t * 0.13) * 2);
            const fres = lerp(a0, a1, Math.pow(v, 0.7)) * (1 - 0.2 * wave * wave);
            ctx.globalAlpha = fres;
            ctx.drawImage(rbuf, 0, sy, bw, strip, dx, Hw + sy / k, W, strip / k);
        }
        ctx.globalAlpha = 1;
    }

    function drawRibbon(ctx, rb, mT) {
        const pts = rb.pts, n = pts.length;
        if (n < 2) return;
        const passes = [];
        if (mT < 0.999) passes.push(0);
        if (mT > 0.001) passes.push(1);
        for (const dark of passes) {
            const w = dark ? mT : 1 - mT;
            ctx.globalCompositeOperation = dark ? 'lighter' : 'source-over';
            const CH = 5;
            for (let i = 0; i < n - 1; i += CH) {
                const s0 = pts[i];
                const fa = sstep(0, 1.2, s0.age) * (1 - sstep(rb.life * 0.45, rb.life, s0.age)) * (1 - h.focusFalloff(s0.x, s0.y, 90));
                if (fa < 0.01) continue;
                // smooth curve through the midpoints of the thread
                const e = Math.min(n - 1, i + CH);
                ctx.beginPath();
                ctx.moveTo(i ? (pts[i - 1].x + s0.x) / 2 : s0.x, i ? (pts[i - 1].y + s0.y) / 2 : s0.y);
                for (let j = i; j < e; j++) ctx.quadraticCurveTo(pts[j].x, pts[j].y, (pts[j].x + pts[j + 1].x) / 2, (pts[j].y + pts[j + 1].y) / 2);
                if (e === n - 1) ctx.lineTo(pts[e].x, pts[e].y);
                if (e === n - 1 && rb.emitting) ctx.lineTo(rb.hx, rb.hy);
                if (dark) {
                    ctx.globalAlpha = w * fa * 0.14; ctx.strokeStyle = 'rgb(255,170,96)'; ctx.lineWidth = 4.5 * rb.w; ctx.stroke();
                    ctx.globalAlpha = w * fa * 0.55; ctx.strokeStyle = 'rgb(255,214,150)'; ctx.lineWidth = 1.1 * rb.w; ctx.stroke();
                } else {
                    ctx.globalAlpha = w * fa * 0.16; ctx.strokeStyle = 'rgb(255,226,160)'; ctx.lineWidth = 4 * rb.w; ctx.stroke();
                    ctx.globalAlpha = w * fa * 0.55; ctx.strokeStyle = 'rgb(206,150,74)'; ctx.lineWidth = 1.0 * rb.w; ctx.stroke();
                }
            }
            // a few brighter beads along the thread
            for (let i = n - 1; i >= 0; i -= 3) {
                const s = pts[i];
                const fa = sstep(0, 1.2, s.age) * (1 - sstep(rb.life * 0.45, rb.life, s.age)) * (1 - h.focusFalloff(s.x, s.y, 90));
                if (fa < 0.01) continue;
                const tw = 0.6 + 0.4 * Math.sin(s.age * 3.1 + s.ph);
                const r = (dark ? 7.5 : 3.6) * rb.w * (0.7 + 0.5 * tw);
                ctx.globalAlpha = w * fa * tw * (dark ? 0.95 : 0.7);
                ctx.drawImage(glowSpr[dark ? 7 : 8], s.x - r, s.y - r, r * 2, r * 2);
            }
        }
        ctx.globalCompositeOperation = 'source-over'; ctx.globalAlpha = 1;
    }

    function drawBokeh(ctx, mT) {
        const aL = 1 - mT;
        ctx.globalCompositeOperation = mT > 0.5 ? 'lighter' : 'source-over';
        for (const b of bokeh) {
            const fa = sstep(0, 8, b.age) * (1 - sstep(b.life - 10, b.life, b.age)) * (1 - h.focusFalloff(b.x, b.y, 140));
            if (fa <= 0.01) continue;
            ctx.globalAlpha = fa * b.a * lerp(0.12, 0.42, mT);
            const spr = bokehTint[b.col][mT > 0.5 ? 1 : 0];
            ctx.drawImage(spr, b.x - b.r, b.y - b.r, b.r * 2, b.r * 2);
        }
        ctx.globalCompositeOperation = 'source-over'; ctx.globalAlpha = 1;
    }
    let bokehTint = [], fibre = null;
    function fibreTexture() {
        const s = 256, c = mk(s, s), x = c.getContext('2d');
        for (let i = 0; i < 26; i++) {
            const cx = rnd(s), cy = rnd(s), r = rnd(14, 46), g = x.createRadialGradient(cx, cy, 0, cx, cy, r);
            const dk = rnd() < 0.6;
            g.addColorStop(0, dk ? 'rgba(90,50,30,0.10)' : 'rgba(255,250,240,0.12)'); g.addColorStop(1, 'rgba(90,50,30,0)');
            x.fillStyle = g; x.fillRect(cx - r, cy - r, r * 2, r * 2);
        }
        x.lineCap = 'round';
        for (let i = 0; i < 520; i++) {
            let px = rnd(s), py = rnd(s), a = rnd(TAU);
            const n = (rnd(6, 22)) | 0, dk = rnd() < 0.7;
            x.strokeStyle = dk ? `rgba(110,64,36,${rnd(0.08, 0.2).toFixed(2)})` : `rgba(255,252,244,${rnd(0.1, 0.25).toFixed(2)})`;
            x.lineWidth = rnd(0.4, 0.9);
            x.beginPath(); x.moveTo(px, py);
            for (let k = 0; k < n; k++) { a += rnd(-0.35, 0.35); px += Math.cos(a) * 1.6; py += Math.sin(a) * 1.6; x.lineTo(px, py); }
            x.stroke();
        }
        return c;
    }
    function tinted(src, col) {
        const c = mk(src.width, src.height), x = c.getContext('2d');
        x.drawImage(src, 0, 0); x.globalCompositeOperation = 'source-in'; x.fillStyle = rgb(col); x.fillRect(0, 0, c.width, c.height);
        return c;
    }

    function build() {
        W = p.width; H = p.height; pd = p.pixelDensity();
        Hw = Math.round(H * 0.8);
        K = H * 0.16; yH = Hw - K; FPX = H * 1.15;
        layoutScene();
        bgL = paintScene(false); bgD = paintScene(true);
    }

    return {
        setup() {
            glowSpr = PAPERS.map(pp => glowSprite(pp.D));
            glowSpr[7] = glowSprite([255, 208, 130], 0.8);
            glowSpr[8] = glowSprite([214, 160, 70], 0.7);
            whiteGlow = glowSprite([255, 240, 214]);
            dayBloom = glowSprite([255, 222, 160], 1.2);
            const disc = discSprite();
            fibre = fibreTexture();
            const bc = [[[255, 255, 248], [255, 190, 120]], [[255, 250, 236], [255, 160, 110]], [[250, 252, 255], [255, 210, 140]], [[255, 246, 240], [240, 150, 130]]];
            bokehTint = bc.map(([l, d]) => [tinted(disc, l), tinted(disc, d)]);
            build();
            // the festival is already under way when the page opens
            const tiers = [0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 2];
            const n0 = tiers.length;
            for (let k = 0; k < n0; k++) {
                const L = spawn(tiers[k], 'stable', rnd(0, 240));
                if (L) L.life = L.age + rnd(80, 600);
            }
            const A = spawn(1, 'assemble');
            if (A) A.st = A.A * 0.45;
            for (let k = 0; k < 30; k++) addBokeh(true);
            h.every(11, maintain);
            h.every(95, () => {
                const c = lanterns.filter(L => L.state === 'stable' && L.age > 80 && L.tier > 0 && L.m.kind !== 'geodesic');
                if (c.length) startOpen(c[(rnd() * c.length) | 0]);
            });
            h.every(170, () => {
                const c = lanterns.filter(L => L.state === 'stable' && L.age > 60 && L.tier < 2);
                if (c.length) { const L = c[(rnd() * c.length) | 0]; L.state = 'ascend'; L.st = 0; }
            });
        },
        step(dt) { stepWorld(dt); },
        resize() {
            const ox = W, oy = H;
            build();
            for (const L of lanterns) { L.x *= W / ox; L.y *= H / oy; L.spriteDirty = true; }
        },
        render() {
            frame++;
            const ctx = p.drawingContext;
            const mT = sstep(0, 1, h.mix);
            ctx.save();
            ctx.setTransform(pd, 0, 0, pd, 0, 0);
            ctx.globalAlpha = 1; ctx.globalCompositeOperation = 'source-over';
            if (mT < 0.999) ctx.drawImage(bgL, 0, 0, W, H);
            if (mT > 0.001) { ctx.globalAlpha = mT; ctx.drawImage(bgD, 0, 0, W, H); ctx.globalAlpha = 1; }
            // slow light drift: golden late afternoon by day, a warmer or bluer night
            const gold = 0.5 + 0.5 * h.drift('gold', 420);
            ctx.fillStyle = mT < 0.5 ? `rgba(255,196,130,${(0.09 * gold * (1 - mT)).toFixed(3)})` : `rgba(120,60,90,${(0.08 * gold * mT).toFixed(3)})`;
            ctx.fillRect(0, 0, W, Hw);
            // halfway through a theme change the garden passes through a rosy dusk rather than grey
            const dusk = 4 * mT * (1 - mT);
            if (dusk > 0.01) {
                const g = ctx.createLinearGradient(0, 0, 0, H);
                g.addColorStop(0, `rgba(120,80,130,${(0.10 * dusk).toFixed(3)})`); g.addColorStop(0.7, `rgba(250,150,100,${(0.2 * dusk).toFixed(3)})`); g.addColorStop(1, `rgba(200,110,90,${(0.12 * dusk).toFixed(3)})`);
                ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
            }
            drawBokeh(ctx, mT);

            // refresh a few lantern sprites per frame (they turn slowly)
            const order = lanterns.slice().sort((a, b) => a.tier - b.tier || b.Z - a.Z);
            let budget = 4; // during a theme crossfade, recolour at most a few sprites per frame
            for (const L of order) {
                if (L.state !== 'stable' && L.state !== 'ascend') continue;
                const every = L.tier === 2 ? 2 : L.tier === 1 ? 3 : 5;
                const stale = Math.abs(L.spriteMix - mT) > 0.03;
                if (L.spriteDirty || !L.sprite || (frame - L.lastSprite >= every + (L.id % 2) && (!stale || budget-- > 0)) || (stale && budget-- > 0)) spriteFor(L, mT);
            }
            drawReflections(ctx, mT, order);

            for (const L of order) {
                const fade = L.alpha;
                if (L.ribbon && L.ribbon.behind) drawRibbon(ctx, L.ribbon, mT);
                // halo of light around the lantern
                const hr = L.R * (2.4 + 0.3 * mT);
                if (L.glow > 0.02) {
                    ctx.globalCompositeOperation = mT > 0.5 ? 'lighter' : 'source-over';
                    ctx.globalAlpha = L.glow * fade * lerp(0.1, 0.32, mT) * (1 - 0.6 * h.focusFalloff(L.x, L.y, 60));
                    ctx.drawImage(mT > 0.5 ? glowSpr[L.paper] : whiteGlow, L.x - hr, L.y - hr, hr * 2, hr * 2);
                    ctx.globalCompositeOperation = 'source-over';
                }
                ctx.globalAlpha = fade;
                if ((L.state === 'stable' || L.state === 'ascend') && L.sprite) {
                    ctx.drawImage(L.sprite, L.x - L.half, L.y - L.half, L.half * 2, L.half * 2);
                } else {
                    ctx.lineJoin = 'round'; ctx.lineCap = 'round';
                    drawLoose(ctx, L, L.x, L.y, mT, fade);
                    L.sprite = null;
                }
                // candle bloom inside the paper (by day only a faint warmth low in the lantern)
                if (L.glow > 0.02) {
                    const br = L.R * 1.25;
                    if (mT > 0.01) {
                        ctx.globalCompositeOperation = 'lighter';
                        ctx.globalAlpha = 0.3 * mT * L.glow * fade;
                        ctx.drawImage(glowSpr[7], L.x - br, L.y - br + L.R * 0.18, br * 2, br * 2);
                        ctx.globalCompositeOperation = 'source-over';
                    }
                    if (mT < 0.99) {
                        ctx.globalAlpha = 0.2 * (1 - mT) * L.glow * fade;
                        ctx.drawImage(dayBloom, L.x - br * 0.8, L.y - br * 0.8 + L.R * 0.3, br * 1.6, br * 1.6);
                    }
                }
                if (L.ribbon && !L.ribbon.behind) drawRibbon(ctx, L.ribbon, mT);
                ctx.globalAlpha = 1;
            }
            for (const rb of ribbons) if (!rb.emitting || !lanterns.some(L => L.ribbon === rb)) drawRibbon(ctx, rb, mT);
            ctx.restore();
        },
    };
}
