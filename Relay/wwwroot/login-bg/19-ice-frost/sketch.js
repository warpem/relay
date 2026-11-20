// 19 · Window Frost — fern frost on a windowpane, seen from a warm room.
//
// The cryo-EM in-joke (hexagonal ice is the enemy) turned into something lovely. Frost creeps in from the
// cold corners and edges of the pane as dendritic ice Ih: every crystal has one lattice orientation θ0 and
// all of its growth directions are θ0 + k·60°, so side branches leave their stem at ±60° and run parallel
// to the crystal's other arms (fern frost from the edges; small six-armed stellar dendrites where the glass
// nucleates on its own). Neighbouring crystals stop where they meet, at grain boundaries.
// Where a crystal catches the light it sparkles with a faint single-crystal [0001] electron-diffraction
// pattern: six {10-10} spots normal to the prism faces (30° off the arms) and six {11-20} spots √3 farther
// out, along the arms.
// Warmth (the low sun, a breath on the glass) melts patches clear, drips run down and cut channels, and the
// wet glass later refreezes from the patch rims, drip trails and beads with new crystals in new orientations.
// The middle of the pane, behind the card, stays clear vitreous glass.

export const meta = {
    title: 'Window Frost',
    tagline: 'Fern frost on a windowpane, sunrise outside or a lamp within',
    description: 'Six-fold ice dendrites creep in from the cold corners of a windowpane, branching at 60° like fern frost, and glint with faint hexagonal diffraction spots. ' +
        'By day a winter sunrise glows peach and gold through the frost; by night the window shows a blue-hour snowscape with cottage lights while a lamp in the room lights the frost amber. ' +
        'Low sun and breath on the glass melt patches clear, drips run and cut channels, and the glass refreezes from the rims in new crystal orientations while the cold slowly shifts between corners, so the frost never repeats.',
    renderer: 'p2d',
    fps: 30,
    pixelDensity: 1,
    maxStep: 1 / 15,
    post: { paper: 0.22, grain: 0.3, vignette: 0.4 },
};

const TAU = Math.PI * 2, A60 = Math.PI / 3, SQ3 = Math.sqrt(3);
const lerp = (a, b, t) => a + (b - a) * t;
const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
const smooth = t => t * t * (3 - 2 * t);
const sstep = (a, b, v) => smooth(clamp((v - a) / (b - a), 0, 1));
const rgba = (c, a = 1) => `rgba(${c[0] | 0},${c[1] | 0},${c[2] | 0},${a})`;
const mkCanvas = (w, h) => { const c = document.createElement('canvas'); c.width = Math.max(1, w | 0); c.height = Math.max(1, h | 0); return c; };

const CELL = 6;                       // occupancy / melt grid (CSS px)
const TICK = 0.25;                    // field update interval (sim s)
const CHUNKS = 8;                     // the ambient field pass visits 1/CHUNKS of the grid per tick
const CORE_W = [1.8, 1.0, 0.75], CORE_A = [0.9, 0.66, 0.52];
const PT_STEP = [2, 2.5, 2.5];

export default function sketch(p, h) {
    let W = 0, H = 0, S = 1, GW = 0, GH = 0, N = 0;
    let owner, cnt, M, clearT, warmT, cold, jit, nA, nB, nC, centre, exL, exR, eyT, eyB, perm;
    let permPos = 0, th = 0.42;
    let branches = [], active = [], dormant = [], dirty = [], nuc = [];
    let crystals = 0, T = 0, tickAcc = 0, nucAcc = 0, dorAcc = 0, pruneAcc = 0;
    let warm = [], drips = [], beads = [], sparkles = [], cottages = [];
    let clearQueue = [], needRebuild = true, maskDirty = true;
    let detail, dctx, comp, cctx, haze, hctx, mask, mctx, maskImg, bgA, bgActx, bgB, bgBctx, hills = [];
    let lastMix = -1, bgAge = 1e9, pregrowing = false, F, veil, vctx, veilImg, veilT, vtctx;
    const cw = [1, 1, 1, 1], nw = [1, 1, 1];

    // ------------------------------------------------------------------ geometry & fields
    function init() {
        W = p.width; H = p.height; S = Math.min(W, H);
        GW = Math.ceil(W / CELL); GH = Math.ceil(H / CELL); N = GW * GH;
        owner = new Int32Array(N); cnt = new Uint8Array(N); M = new Float32Array(N).fill(1);
        clearT = new Float32Array(N).fill(-1e9); warmT = new Float32Array(N).fill(-1e9);
        F = new Float32Array(N); cold = new Float32Array(N); jit = new Float32Array(N); centre = new Float32Array(N);
        nA = new Float32Array(N); nB = new Float32Array(N); nC = new Float32Array(N);
        const L = 0.24 * S;
        exL = new Float32Array(GW); exR = new Float32Array(GW); eyT = new Float32Array(GH); eyB = new Float32Array(GH);
        for (let i = 0; i < GW; i++) { const x = (i + 0.5) * CELL; exL[i] = Math.exp(-x / L); exR[i] = Math.exp(-Math.max(0, W - x) / L); }
        for (let j = 0; j < GH; j++) { const y = (j + 0.5) * CELL; eyT[j] = Math.exp(-y / L); eyB[j] = Math.exp(-Math.max(0, H - y) / L); }
        const o = [p.random(100), p.random(100), p.random(100)];
        const f = h.focus, pad = 0.12 * S + 50;
        for (let j = 0; j < GH; j++) for (let i = 0; i < GW; i++) {
            const k = j * GW + i, x = (i + 0.5) * CELL, y = (j + 0.5) * CELL;
            const n = (a, s) => p.noise(x * s + a, y * s + a * 1.7);
            // contrasty lobes: where the glass is colder the frost reaches further in
            nA[k] = sstep(0.28, 0.72, n(o[0], 0.0042) * 0.7 + n(o[0] + 9, 0.011) * 0.3);
            nB[k] = sstep(0.28, 0.72, n(o[1], 0.0034) * 0.7 + n(o[1] + 9, 0.013) * 0.3);
            nC[k] = sstep(0.28, 0.72, n(o[2], 0.0050) * 0.7 + n(o[2] + 9, 0.009) * 0.3);
            jit[k] = p.random();
            const dx = (x - f.cx) / (0.46 * W), dy = (y - f.cy) / (0.5 * H);
            centre[k] = 1.6 * h.focusFalloff(x, y, pad) + 0.45 * Math.exp(-(dx * dx + dy * dy) * 2.2);
        }
        perm = new Uint32Array(N);
        for (let i = 0; i < N; i++) perm[i] = i;
        for (let i = N - 1; i > 0; i--) { const r = (p.random() * (i + 1)) | 0, t = perm[i]; perm[i] = perm[r]; perm[r] = t; }
        branches = []; active = []; dormant = []; dirty = []; nuc = []; warm = []; drips = []; beads = []; sparkles = [];
        clearQueue = []; crystals = 0; permPos = 0;
        updateWeights();
        for (let k = 0; k < N; k++) cold[k] = coldOf(k);

        const d = p.pixelDensity();
        detail = mkCanvas(W * d, H * d); dctx = detail.getContext('2d'); dctx.setTransform(d, 0, 0, d, 0, 0);
        comp = mkCanvas(W * d, H * d); cctx = comp.getContext('2d');
        haze = mkCanvas(W / 5, H / 5); hctx = haze.getContext('2d');
        mask = mkCanvas(GW, GH); mctx = mask.getContext('2d'); maskImg = mctx.createImageData(GW, GH);
        veil = mkCanvas(GW, GH); vctx = veil.getContext('2d'); veilImg = vctx.createImageData(GW, GH);
        veilT = mkCanvas(GW, GH); vtctx = veilT.getContext('2d');
        bgA = mkCanvas(W / 4, H / 4); bgActx = bgA.getContext('2d');
        bgB = mkCanvas(W / 4, H / 4); bgBctx = bgB.getContext('2d');
        // the far snowy ridge and treeline outside (blurred: the eye is focused on the frost)
        hills = [];
        const ho = p.random(100);
        for (let i = 0; i <= 96; i++) {
            const u = i / 96;
            hills.push(0.79 + 0.05 * (p.noise(u * 2.2 + ho) - 0.5) * 2 - 0.018 * Math.max(0, p.noise(u * 14 + ho + 5) - 0.45) * 4);
        }
        cottages = [];
        const nc = 5 + ((p.random() * 3) | 0);
        for (let i = 0; i < nc; i++) {
            const u = p.random() < 0.5 ? p.random(0.03, 0.34) : p.random(0.62, 0.97);
            cottages.push({ u, dy: p.random(0.004, 0.02), on: p.random() < 0.75 ? 1 : 0, lvl: 0, r: p.random(0.6, 1.2), hue: p.random() });
        }
        for (const c of cottages) c.lvl = c.on;
        needRebuild = true; maskDirty = true; lastMix = -1; bgAge = 1e9;
    }

    function updateWeights() {
        // the coldest corner wanders slowly around the pane (a non-periodic angle), a second one comes and goes,
        // and the bottom of the pane is a little colder (cold air pools there)
        const phi = Math.PI * (0.25 + 1.7 * h.drift('dom', 900));
        const corner = [Math.PI * 1.25, Math.PI * 1.75, Math.PI * 0.25, Math.PI * 0.75]; // TL, TR, BR, BL (screen y down)
        for (let i = 0; i < 4; i++) {
            const c = Math.cos(phi - corner[i]);
            cw[i] = 0.3 + 1.1 * Math.pow(Math.max(0, c), 1.3) + 0.75 * Math.max(0, h.drift('c' + i, 520) + 0.15) + (i >= 2 ? 0.2 : 0);
        }
        nw[0] = Math.max(0, 1 + h.drift('nA', 640)); nw[1] = Math.max(0, 1 + h.drift('nB', 580)); nw[2] = Math.max(0, 1 + h.drift('nC', 710));
        const s = nw[0] + nw[1] + nw[2] + 1e-3; nw[0] /= s; nw[1] /= s; nw[2] /= s;
        th = 0.42 + 0.06 * h.drift('chill', 950);
    }
    const along = (a, b, t) => a * Math.pow(1 - t, 1.6) + b * Math.pow(t, 1.6);
    function coldOf(k) {
        const i = k % GW, j = (k / GW) | 0, u = i / GW, v = j / GH;
        const e = along(cw[0], cw[3], v) * exL[i] + along(cw[1], cw[2], v) * exR[i] + along(cw[0], cw[1], u) * eyT[j] + along(cw[3], cw[2], u) * eyB[j];
        const n = nw[0] * nA[k] + nw[1] * nB[k] + nw[2] * nC[k];
        return e * (0.42 + 1.15 * n) - centre[k];
    }
    const cellOf = (x, y) => {
        const i = (x / CELL) | 0, j = (y / CELL) | 0;
        return i < 0 || j < 0 || i >= GW || j >= GH ? -1 : j * GW + i;
    };
    function warmAt(x, y) {
        let w = 0;
        for (const e of warm) {
            if (e.I <= 0) continue;
            const dx = x - e.x, dy = y - e.y, d2 = (dx * dx + dy * dy) / (e.rr * e.rr);
            if (d2 < 6) w += e.I * Math.exp(-d2);
        }
        return w;
    }

    function clearCell(k) {
        if (cnt[k] === 0 && M[k] <= 0) return;
        owner[k] = 0; cnt[k] = 0; M[k] = 0; F[k] = 0; clearT[k] = T;
        if (h.warping || needRebuild) needRebuild = true;
        else if (clearQueue.length < 6000) clearQueue.push(k);
        else needRebuild = true;
        maskDirty = true;
    }

    // ------------------------------------------------------------------ crystals
    function newBranch(c, th0, gen, x, y, ang, v, maxLen, sp, grace, kind) {
        const b = {
            c, th0, gen, x, y, ang, v, len: 0, maxLen, sp, next: sp * p.random(0.4, 1.1), emit: 0, grace,
            curl: gen === 0 && kind !== 2 ? p.random(-0.0011, 0.0011) : 0, kind, // kind: 0 plain, 1 major, 2 stellar arm
            pts: [x, y, T], drawn: 1, last: cellOf(x, y), dirty: false, state: 1, retry: 0,
        };
        branches.push(b); active.push(b);
        return b;
    }
    function seedCrystal(x, y, target, stellar) {
        const c = ++crystals, th0 = p.random(A60);
        const k = Math.round((target - th0) / A60), base = th0 + k * A60;
        if (stellar) {
            const len = p.random(10, 38), v = p.random(2.2, 3.4);
            for (let a = 0; a < 6; a++) newBranch(c, th0, 0, x, y, th0 + a * A60, v, len * p.random(0.88, 1.12), p.random(4.5, 6), 6, 2);
            return;
        }
        const dirs = [base];
        const r = p.random();
        if (r < 0.45) dirs.push(base + (p.random() < 0.5 ? -A60 : A60));
        else if (r < 0.62) dirs.push(base - A60, base + A60);
        for (const a of dirs) newBranch(c, th0, 0, x, y, a, 4.6 * p.random(0.75, 1.25), p.random(280, 900), p.random(6.5, 9), 8, 0);
    }
    function spawnSides(b) {
        if (b.gen >= 2) return;
        for (const side of [-1, 1]) {
            const g = b.gen + 1, ang = b.ang + side * A60 + p.random(-0.04, 0.04);
            let maxLen, prob, sp = 0, kind = 0;
            if (b.kind === 2) { // stellar arms: short symmetric side plates
                prob = 0.9; maxLen = (2 + 0.35 * (b.maxLen - b.len) * p.random(0.6, 1)); if (g === 2) continue;
            } else if (g === 1) {
                prob = 0.88;
                if (p.random() < 0.07) { kind = 1; maxLen = 60 + 240 * p.random(); sp = p.random(5.5, 7.5); }
                else { maxLen = 4 + 48 * Math.pow(p.random(), 1.7); sp = p.random(5, 6.5); }
            } else {
                prob = b.kind === 1 ? 0.8 : 0.6;
                maxLen = b.kind === 1 ? 2 + 20 * Math.pow(p.random(), 1.6) : 1.5 + 8 * Math.pow(p.random(), 2);
            }
            if (p.random() > prob) continue;
            newBranch(b.c, b.th0, g, b.x, b.y, ang, b.v * (g === 1 ? (kind === 1 ? 0.6 : 0.42) : 0.5), maxLen, sp, 9, kind);
        }
    }
    function finish(b, state) {
        if (b.emit > 0.4) { b.pts.push(b.x, b.y, T); markDirty(b); b.emit = 0; }
        b.state = state;
        if (state === 2) { b.retry = p.random(1.5, 4); dormant.push(b); if (dormant.length > 5000) dormant.splice(0, 500).forEach(d => (d.state = 0)); }
    }
    function markDirty(b) { if (!b.dirty) { b.dirty = true; dirty.push(b); } }

    // grow one tip; returns false when it stops (done or dormant)
    function grow(b, dt) {
        const ca = Math.cos(b.ang), sa = Math.sin(b.ang);
        const ax = b.x + ca * 3.5, ay = b.y + sa * 3.5;
        const k = cellOf(ax, ay);
        if (k < 0) { finish(b, 0); return false; }
        const ce = cold[k] - 1.6 * warmAt(ax, ay);
        if (ce < th || M[k] < 0.6) { finish(b, 2); return false; }
        if (k !== b.last) {
            const o = owner[k];
            if (o && o !== b.c) { finish(b, 0); return false; }          // grain boundary
            if (o === b.c && cnt[k] >= 4 && b.grace <= 0) { finish(b, 0); return false; }
        }
        let dl = b.v * Math.min(1, 0.18 + (ce - th) / 0.22) * dt;
        let done = false;
        if (b.len + dl >= b.maxLen) { dl = Math.max(0, b.maxLen - b.len); done = true; }
        b.x += ca * dl; b.y += sa * dl; b.len += dl; b.grace -= dl; b.emit += dl; b.next -= dl;
        if (b.curl) b.ang += b.curl * dl;
        const kc = cellOf(b.x, b.y);
        if (kc >= 0 && kc !== b.last) { if (!owner[kc]) owner[kc] = b.c; if (cnt[kc] < 255) cnt[kc]++; b.last = kc; }
        if (b.emit >= PT_STEP[b.gen]) { b.pts.push(b.x, b.y, T); b.emit = 0; markDirty(b); }
        if (b.next <= 0 && b.sp > 0) { spawnSides(b); b.next = b.sp * p.random(0.85, 1.15); }
        else if (b.next <= 0 && b.kind === 2) { spawnSides(b); b.next = b.sp; }
        if (done) { finish(b, 0); return false; }
        return true;
    }

    let primaries = 0;
    function tryNucleate() {
        if (active.length > 3000 || primaries > 40) return;
        // refrozen beads and drip trails are good nucleation sites
        if (nuc.length && p.random() < 0.5) {
            const n = nuc.pop(), k = cellOf(n.x, n.y);
            if (k >= 0 && !cnt[k] && M[k] > 0.8 && cold[k] - 1.6 * warmAt(n.x, n.y) > th + 0.04) seedCrystal(n.x, n.y, p.random(TAU), n.stellar && p.random() < 0.6);
            return;
        }
        let x, y, inward = null;
        if (p.random() < 0.6) { // the frame edge, mostly near the cold corners
            const e = (p.random() * 4) | 0, u = p.random(), d = p.random(0, 4);
            if (e === 0) { x = d; y = u * H; inward = 0; } else if (e === 1) { x = W - d; y = u * H; inward = Math.PI; }
            else if (e === 2) { x = u * W; y = d; inward = Math.PI / 2; } else { x = u * W; y = H - d; inward = -Math.PI / 2; }
        } else { x = p.random(W); y = p.random(H); }
        const k = cellOf(x, y);
        if (k < 0 || cnt[k] || M[k] < 0.85) return;
        const ce = cold[k] - 1.6 * warmAt(x, y);
        if (ce < th + 0.08) return;
        const i0 = k % GW, j0 = (k / GW) | 0, R = inward !== null ? 3 : 4;
        let vx = 0, vy = 0, n = 0;
        for (let dj = -R; dj <= R; dj++) for (let di = -R; di <= R; di++) {
            const i = i0 + di, j = j0 + dj;
            if (i < 0 || j < 0 || i >= GW || j >= GH) continue;
            if (cnt[j * GW + i]) { vx -= di; vy -= dj; n++; }
        }
        if (inward !== null) {
            if (n) return; // only fresh crystals on bare frame; the rest grows on from existing ice
            // aim the fronds toward the warm middle of the pane
            const f = h.focus, a = Math.atan2(f.cy - y, f.cx - x);
            seedCrystal(x, y, lerp(inward, inward + (((a - inward + 3 * Math.PI) % TAU) - Math.PI), 0.6) + p.random(-0.35, 0.35), false);
            return;
        }
        // next to other ice? regrow away from it (refreezing from a rim) — but rarely: resumed tips do most of it
        if (n > 2 && n < 30 && p.random() < 0.35) { seedCrystal(x, y, Math.atan2(vy, vx) + p.random(-0.4, 0.4), false); return; }
        if (!n && ce > th + 0.12 && p.random() < 0.04) seedCrystal(x, y, 0, true);
    }

    // ------------------------------------------------------------------ warmth: sun, breath, drips
    function frostySpot(minPad) {
        for (let t = 0; t < 40; t++) {
            const k = (p.random() * N) | 0;
            if (cnt[k] && M[k] > 0.95) {
                const x = (k % GW + 0.5) * CELL, y = (((k / GW) | 0) + 0.5) * CELL;
                if (h.focusFalloff(x, y, minPad) === 0) return { x, y };
            }
        }
        return null;
    }
    let nextBreath = 20, nextSun = 100;
    const expo = mean => -Math.log(1 - p.random()) * mean;
    function breathe() { // someone breathes on the glass
        if (warm.filter(e => e.kind === 'breath').length >= 2) return;
        const s = frostySpot(150);
        if (!s) return;
        warm.push({ kind: 'breath', x: s.x, y: s.y, r: p.random(45, 95) * clamp(S / 900, 0.7, 1.3), rr: 1, t: 0, I: 0,
            rise: p.random(2, 4), hold: p.random(4, 8), fall: p.random(10, 18), peak: 1.6, dripped: false, vx: 0, vy: 0 });
    }
    function sunshaft() { // a low shaft of sun warms a patch of the pane
        if (warm.some(e => e.kind === 'sun')) return;
        const s = frostySpot(220);
        if (!s) return;
        const a = p.random(TAU), v = p.random(0.3, 0.9);
        warm.push({ kind: 'sun', x: s.x, y: s.y, r: p.random(110, 200) * clamp(S / 900, 0.7, 1.3), rr: 1, t: 0, I: 0,
            dur: p.random(50, 140), ramp: p.random(12, 25), peak: p.random(0.55, 0.8), vx: Math.cos(a) * v, vy: Math.sin(a) * v, dripAcc: 0 });
    }
    h.every(140, () => { // a distant cottage light goes on or off
        const c = cottages[(p.random() * cottages.length) | 0];
        if (c) c.on = c.on ? 0 : 1;
    });
    h.every(1.1, () => { // a crystal catches the light
        if (sparkles.length >= 5 || !branches.length) return;
        const b = branches[(p.random() * branches.length) | 0];
        const np = b.pts.length / 3;
        const i = (p.random() * np) | 0, x = b.pts[i * 3], y = b.pts[i * 3 + 1];
        const k = cellOf(x, y);
        if (k < 0 || M[k] < 0.95 || b.pts[i * 3 + 2] <= clearT[k] || h.focusFalloff(x, y, 160) > 0) return;
        if (p.random() > 0.35 + 0.65 * illum(x, y)) return;
        sparkles.push({ x, y, th0: b.th0, t: 0, life: p.random(5, 9), sz: p.random(7, 12), I: p.random(0.7, 1) });
    });
    function illum(x, y) { // how much light a point catches (sun by day, lamp by night)
        const m = smooth(h.mix);
        const sp = sunPos(), lp = lampPos();
        const ds = Math.hypot(x - sp.x, y - sp.y) / (0.7 * Math.hypot(W, H)), dl = Math.hypot(x - lp.x, y - lp.y) / (0.8 * Math.hypot(W, H));
        return lerp(Math.exp(-ds * ds * 3), Math.exp(-dl * dl * 3), m);
    }
    function sunPos() { return { x: W * (0.5 + 0.36 * h.drift('sunx', 1500)), y: H * (0.80 - 0.06 * (h.drift('suny', 1100) + 1)) }; }
    function lampPos() { return { x: W * (0.07 + 0.05 * h.drift('lampx', 1700)), y: H * 1.04 }; }

    function addDrip(x, y, mass) {
        drips.push({ x, y, v: 0, vt: p.random(8, 22), mass, trail: [x, y, T], emit: 0, stopped: false, frozen: false, tEnd: 0, wob: p.random(100) });
    }
    function stepWarm(dt) {
        for (const e of warm) {
            e.t += dt;
            if (e.kind === 'breath') {
                const t = e.t;
                e.I = t < e.rise ? e.peak * smooth(t / e.rise) : t < e.rise + e.hold ? e.peak : e.peak * Math.max(0, 1 - (t - e.rise - e.hold) / e.fall);
                e.rr = e.r * (0.65 + 0.35 * smooth(clamp(t / (e.rise + 1), 0, 1)));
                if (!e.dripped && t > e.rise + e.hold * 0.6) {
                    e.dripped = true;
                    const n = 1 + ((p.random() * 2.4) | 0);
                    for (let i = 0; i < n; i++) addDrip(e.x + p.random(-0.55, 0.55) * e.r, e.y + e.r * p.random(0.35, 0.6), p.random(0.6, 1.2));
                }
                if (t > e.rise + e.hold + e.fall) {
                    e.done = true;
                    const nb = 10 + ((p.random() * 18) | 0);
                    for (let i = 0; i < nb; i++) {
                        const a = p.random(TAU), r = Math.sqrt(p.random()) * e.r * 0.8;
                        beads.push({ x: e.x + Math.cos(a) * r, y: e.y + Math.sin(a) * r, r: p.random(0.7, 2.3), t: 0, life: p.random(30, 90) });
                    }
                }
            } else {
                const t = e.t;
                e.I = e.peak * smooth(clamp(t / e.ramp, 0, 1)) * smooth(clamp((e.dur - t) / e.ramp, 0, 1));
                e.rr = e.r;
                e.x += e.vx * dt; e.y += e.vy * dt;
                e.dripAcc += dt * e.I;
                if (e.dripAcc > 22) { e.dripAcc = 0; addDrip(e.x + p.random(-0.5, 0.5) * e.r, e.y + e.r * p.random(0.3, 0.6), p.random(0.4, 0.9)); }
                if (t > e.dur) e.done = true;
            }
        }
        if (warm.some(e => e.done)) warm = warm.filter(e => !e.done);

        for (const d of drips) {
            if (d.stopped) {
                if (!d.frozen && T - d.tEnd > 35) {
                    d.frozen = true;  // the trail has refrozen: it seeds new frost
                    const np = d.trail.length / 3;
                    for (let i = 0; i < 3; i++) { const j = (p.random() * np) | 0; nuc.push({ x: d.trail[j * 3], y: d.trail[j * 3 + 1], stellar: false }); }
                }
                continue;
            }
            if (p.random() < dt / 1.6) d.vt = p.random() < 0.25 ? 0 : p.random(6, 34) * (0.5 + 0.5 * d.mass);
            d.v += (d.vt - d.v) * Math.min(1, dt * 2.5);
            const dy = d.v * dt;
            d.y += dy;
            d.x += (p.noise(d.y * 0.03, d.wob) - 0.5) * dy * 0.9;
            d.mass -= dy * 0.006;
            d.emit += dy;
            if (d.emit > 3) { d.trail.push(d.x, d.y, T); d.emit = 0; }
            const k = cellOf(d.x, d.y);
            if (k >= 0) { clearCell(k); warmT[k] = T; }
            const k2 = cellOf(d.x + 3, d.y); if (k2 >= 0 && cnt[k2] && p.random() < 0.3) { clearCell(k2); warmT[k2] = T; }
            if (d.mass <= 0 || d.y > H + 8) {
                d.stopped = true; d.tEnd = T; d.trail.push(d.x, d.y, T);
                beads.push({ x: d.x, y: d.y, r: 2.2, t: 0, life: p.random(40, 80) });
            }
        }
        if (drips.length > 12 || drips.some(d => d.frozen && T - d.tEnd > 120)) drips = drips.filter(d => !(d.frozen && T - d.tEnd > 120)).slice(-12);

        for (const b of beads) {
            b.t += dt;
            if (b.t > b.life && !b.gone) { b.gone = true; if (p.random() < 0.3) nuc.push({ x: b.x, y: b.y, stellar: true }); }
        }
        if (beads.some(b => b.gone)) beads = beads.filter(b => !b.gone);
        if (nuc.length > 60) nuc.splice(0, nuc.length - 60);
    }

    function fieldTick() {
        updateWeights();
        // ambient pass over a shuffled slice of the grid: cold field, slow ambient melt and refreezing
        const n = Math.ceil(N / CHUNKS), dtk = TICK * CHUNKS;
        for (let q = 0; q < n; q++) {
            const k = perm[permPos]; permPos = (permPos + 1) % N;
            const c = cold[k] = coldOf(k);
            if (cnt[k] && M[k] >= 0.99 && F[k] < 1) F[k] = Math.min(1, F[k] + dtk / 50);   // hoar thickens on old frost
            if (cnt[k] && c < th - 0.12) {
                M[k] -= dtk * 0.03 * (0.8 + 0.4 * jit[k]) * Math.min(1, (th - 0.12 - c) / 0.1 + 0.3); maskDirty = true;
                if (M[k] <= 0.02) clearCell(k);
            } else if (M[k] < 1 && c > th - 0.05 && T - warmT[k] > 4) {
                M[k] = Math.min(1, M[k] + dtk / 16); maskDirty = true;
            }
        }
        // warm patches melt quickly
        for (const e of warm) {
            if (e.I <= 0.05) continue;
            const R = e.rr * 1.6, i0 = Math.max(0, ((e.x - R) / CELL) | 0), i1 = Math.min(GW - 1, ((e.x + R) / CELL) | 0);
            const j0 = Math.max(0, ((e.y - R) / CELL) | 0), j1 = Math.min(GH - 1, ((e.y + R) / CELL) | 0);
            const rate = e.kind === 'breath' ? 1.1 : 0.22;
            for (let j = j0; j <= j1; j++) for (let i = i0; i <= i1; i++) {
                const k = j * GW + i, dx = (i + 0.5) * CELL - e.x, dy = (j + 0.5) * CELL - e.y;
                const w = e.I * Math.exp(-(dx * dx + dy * dy) / (e.rr * e.rr)) * (0.7 + 0.6 * jit[k]);
                if (w > 0.3) {
                    warmT[k] = T;
                    if (M[k] > 0) { M[k] -= TICK * rate * (w - 0.3); maskDirty = true; if (M[k] <= 0.02) clearCell(k); }
                }
            }
        }
    }

    function prune() {
        // forget branches whose ice has all melted
        branches = branches.filter(b => {
            if (b.state === 1 || b.state === 2) return true;
            const pts = b.pts;
            for (let i = 0; i < pts.length; i += 3) { const k = cellOf(pts[i], pts[i + 1]); if (k >= 0 && pts[i + 2] > clearT[k]) return true; }
            return false;
        });
        dormant = dormant.filter(b => b.state === 2);
    }

    function stepSim(dt) {
        T += dt;
        for (let i = active.length - 1; i >= 0; i--) {
            const b = active[i];
            if (!grow(b, dt)) { active[i] = active[active.length - 1]; active.pop(); }
        }
        tickAcc += dt; while (tickAcc >= TICK) { tickAcc -= TICK; fieldTick(); }
        nucAcc += dt; while (nucAcc >= 0.35) { nucAcc -= 0.35; primaries = 0; for (const b of active) if (b.gen === 0 && b.kind !== 2) primaries++; tryNucleate(); }
        dorAcc += dt;
        if (dorAcc >= 0.5) { // dormant tips resume when the glass is cold again
            dorAcc = 0;
            for (let i = dormant.length - 1; i >= 0; i--) {
                const b = dormant[i];
                b.retry -= 0.5;
                if (b.retry > 0) continue;
                b.retry = p.random(1.5, 4);
                const kb = cellOf(b.x, b.y), k = cellOf(b.x + Math.cos(b.ang) * 3.5, b.y + Math.sin(b.ang) * 3.5);
                if (kb < 0 || k < 0 || owner[kb] !== b.c || M[kb] < 0.9) { b.state = 0; dormant[i] = dormant[dormant.length - 1]; dormant.pop(); continue; }
                if (M[k] > 0.6 && cold[k] - 1.6 * warmAt(b.x, b.y) > th + 0.02) { b.state = 1; active.push(b); dormant[i] = dormant[dormant.length - 1]; dormant.pop(); }
            }
        }
        if (!pregrowing) {
            nextBreath -= dt; if (nextBreath <= 0) { breathe(); nextBreath = 15 + expo(60); }
            nextSun -= dt; if (nextSun <= 0) { sunshaft(); nextSun = 40 + expo(150); }
        }
        stepWarm(dt);
        for (const s of sparkles) s.t += dt;
        if (sparkles.some(s => s.t > s.life)) sparkles = sparkles.filter(s => s.t <= s.life);
        pruneAcc += dt; if (pruneAcc > 20) { pruneAcc = 0; prune(); }
        for (const c of cottages) c.lvl += clamp(c.on - c.lvl, -dt / 3, dt / 3);
        if (h.warping) needRebuild = true;
    }

    // ------------------------------------------------------------------ drawing
    function strokeSegs(list, from) {
        // core strokes, batched by generation (plus a soft wide pass for a fuzzy, hoar-like edge)
        for (let pass = 0; pass < 2; pass++) for (let g = 0; g < 3; g++) {
            dctx.beginPath();
            let any = false;
            for (const b of list) {
                if (b.gen !== g) continue;
                const pts = b.pts, n = pts.length / 3;
                let i = from ? Math.max(0, b.drawn - 1) : 0, pen = false;
                for (; i < n; i++) {
                    const x = pts[i * 3], y = pts[i * 3 + 1];
                    let alive = true;
                    if (!from) { const k = cellOf(x, y); alive = k < 0 || pts[i * 3 + 2] > clearT[k]; }
                    if (!alive) { pen = false; continue; }
                    if (pen) dctx.lineTo(x, y); else { dctx.moveTo(x, y); pen = true; }
                    any = true;
                }
            }
            if (!any) continue;
            dctx.lineWidth = pass ? CORE_W[g] : CORE_W[g] * 2.8;
            dctx.strokeStyle = `rgba(255,255,255,${pass ? CORE_A[g] : CORE_A[g] * 0.14})`;
            dctx.stroke();
        }
        for (const b of list) { b.drawn = b.pts.length / 3; b.dirty = false; }
    }
    function rebuild() {
        dctx.clearRect(0, 0, W, H);
        dctx.lineCap = 'butt'; dctx.lineJoin = 'round';
        strokeSegs(branches, false);
        dirty = []; clearQueue = []; needRebuild = false;
    }
    function updateMask() {
        const d = maskImg.data, v = veilImg.data, t0 = th + 0.08, t1 = th + 0.8;
        for (let k = 0; k < N; k++) {
            const m = M[k], sm = m >= 1 ? 1 : smooth(Math.max(0, m));
            d[k * 4] = d[k * 4 + 1] = d[k * 4 + 2] = 255;
            d[k * 4 + 3] = (sm * 255) | 0;
            // milky hoar veil where the glass is coldest and the frost is old
            v[k * 4] = v[k * 4 + 1] = v[k * 4 + 2] = 255;
            v[k * 4 + 3] = F[k] > 0 ? (sm * F[k] * sstep(t0, t1, cold[k]) * 255) | 0 : 0;
        }
        mctx.putImageData(maskImg, 0, 0);
        vctx.putImageData(veilImg, 0, 0);
        maskDirty = false;
    }

    function drawBackground(m) {
        const c = bgActx, w = bgA.width, hh = bgA.height, sx = w / W, sy = hh / H;
        c.setTransform(sx, 0, 0, sy, 0, 0);
        // sky: winter sunrise by day, blue hour by night
        const g = c.createLinearGradient(0, 0, 0, H);
        g.addColorStop(0, rgba(h.byTheme([194, 190, 204], [17, 23, 40])));
        g.addColorStop(0.42, rgba(h.byTheme([226, 205, 200], [27, 36, 60])));
        g.addColorStop(0.68, rgba(h.byTheme([242, 204, 168], [44, 50, 80])));
        g.addColorStop(0.82, rgba(h.byTheme([247, 214, 160], [74, 64, 92])));
        g.addColorStop(1, rgba(h.byTheme([243, 222, 190], [40, 46, 70])));
        c.fillStyle = g; c.fillRect(0, 0, W, H);
        const sp = sunPos();
        if (m < 0.999) {
            const a = 1 - m;
            const sg = c.createRadialGradient(sp.x, sp.y, 0, sp.x, sp.y, 0.55 * W);
            sg.addColorStop(0, `rgba(255,246,222,${0.95 * a})`);
            sg.addColorStop(0.06, `rgba(255,232,180,${0.75 * a})`);
            sg.addColorStop(0.3, `rgba(255,206,150,${0.32 * a})`);
            sg.addColorStop(1, 'rgba(255,200,150,0)');
            c.fillStyle = sg; c.fillRect(0, 0, W, H);
        }
        // far ridge with a soft treeline, then the snowfield
        c.beginPath(); c.moveTo(0, H);
        for (let i = 0; i < hills.length; i++) c.lineTo((i / (hills.length - 1)) * W, hills[i] * H);
        c.lineTo(W, H); c.closePath();
        c.fillStyle = rgba(h.byTheme([188, 166, 178], [24, 30, 50])); c.fill();
        const sg2 = c.createLinearGradient(0, 0.84 * H, 0, H);
        sg2.addColorStop(0, rgba(h.byTheme([222, 196, 190], [44, 54, 82])));
        sg2.addColorStop(1, rgba(h.byTheme([204, 178, 180], [56, 64, 94])));
        c.beginPath(); c.moveTo(0, H);
        for (let i = 0; i < hills.length; i++) c.lineTo((i / (hills.length - 1)) * W, (0.865 + 0.35 * (hills[i] - 0.79)) * H);
        c.lineTo(W, H); c.closePath(); c.fillStyle = sg2; c.fill();
        // cottage windows across the snow at night
        if (m > 0.001) for (const ct of cottages) {
            if (ct.lvl <= 0.001) continue;
            const i = Math.round(ct.u * (hills.length - 1)), x = ct.u * W, y = (hills[i] + ct.dy) * H, r = 0.012 * S * ct.r;
            const cg = c.createRadialGradient(x, y, 0, x, y, r * 3);
            const col = ct.hue < 0.5 ? '255,184,104' : '255,206,140';
            cg.addColorStop(0, `rgba(${col},${0.85 * m * ct.lvl})`); cg.addColorStop(0.25, `rgba(${col},${0.35 * m * ct.lvl})`); cg.addColorStop(1, `rgba(${col},0)`);
            c.fillStyle = cg; c.fillRect(x - r * 3, y - r * 3, r * 6, r * 6);
        }
        bgBctx.setTransform(1, 0, 0, 1, 0, 0);
        bgBctx.filter = 'blur(2px)';
        bgBctx.drawImage(bgA, 0, 0);
        bgBctx.filter = 'none';
    }

    let pendingResize = 0;
    function render() {
        if (pendingResize && performance.now() - pendingResize > 250) { pendingResize = 0; init(); pregrow(); }
        const ctx = p.drawingContext, m = smooth(h.mix);
        if (needRebuild) rebuild();
        else {
            if (clearQueue.length) {
                for (const k of clearQueue) dctx.clearRect((k % GW) * CELL, ((k / GW) | 0) * CELL, CELL, CELL);
                clearQueue = [];
            }
            if (dirty.length) { strokeSegs(dirty, true); dirty = []; }
        }
        if (maskDirty) updateMask();
        bgAge++;
        if (Math.abs(m - lastMix) > 0.002 || bgAge > 15) { drawBackground(m); lastMix = m; bgAge = 0; }

        ctx.save();
        ctx.imageSmoothingEnabled = true;
        ctx.globalCompositeOperation = 'source-over'; ctx.globalAlpha = 1;
        ctx.drawImage(bgB, 0, 0, W, H);

        // the room's lamp reflected faintly in the glass at night
        const lp = lampPos(), sp = sunPos(), diag = Math.hypot(W, H);
        if (m > 0.001) {
            const lg = ctx.createRadialGradient(lp.x, lp.y, 0, lp.x, lp.y, 0.75 * W);
            lg.addColorStop(0, `rgba(255,170,90,${0.22 * m})`); lg.addColorStop(0.4, `rgba(220,130,70,${0.07 * m})`); lg.addColorStop(1, 'rgba(200,120,60,0)');
            ctx.fillStyle = lg; ctx.fillRect(0, 0, W, H);
        }

        // hoar veil (cell resolution, blurred), tinted like the frost
        vtctx.setTransform(1, 0, 0, 1, 0, 0);
        vtctx.globalCompositeOperation = 'copy'; vtctx.filter = 'blur(1.6px)';
        vtctx.drawImage(veil, 0, 0); vtctx.filter = 'none';
        vtctx.globalCompositeOperation = 'source-in';
        {
            const cx = lerp(sp.x, lp.x, m) / CELL, cy = lerp(sp.y, lp.y, m) / CELL;
            const vg = vtctx.createRadialGradient(cx, cy, 0, cx, cy, lerp(0.75, 0.95, m) * diag / CELL);
            vg.addColorStop(0, rgba(h.byTheme([255, 228, 184], [255, 190, 120])));
            vg.addColorStop(0.5, rgba(h.byTheme([255, 248, 238], [200, 140, 104])));
            vg.addColorStop(1, rgba(h.byTheme([252, 250, 248], [150, 120, 128])));
            vtctx.fillStyle = vg; vtctx.fillRect(0, 0, GW, GH);
        }
        vtctx.globalCompositeOperation = 'source-over';
        ctx.globalAlpha = lerp(0.62, 0.32, m);
        ctx.drawImage(veilT, 0, 0, GW * CELL, GH * CELL);
        ctx.globalAlpha = 1;

        // frost: detail × melt mask, tinted by the light that falls through (day) or onto (night) it
        cctx.setTransform(1, 0, 0, 1, 0, 0);
        cctx.globalCompositeOperation = 'copy'; cctx.globalAlpha = 1;
        cctx.drawImage(detail, 0, 0);
        const d = p.pixelDensity();
        cctx.setTransform(d, 0, 0, d, 0, 0);
        cctx.globalCompositeOperation = 'destination-in';
        cctx.imageSmoothingEnabled = true;
        cctx.drawImage(mask, 0, 0, GW * CELL, GH * CELL);
        cctx.globalCompositeOperation = 'source-in';
        const cx = lerp(sp.x, lp.x, m), cy = lerp(sp.y, lp.y, m);
        const tg = cctx.createRadialGradient(cx, cy, 0, cx, cy, lerp(0.75, 0.95, m) * diag);
        tg.addColorStop(0, rgba(h.byTheme([255, 224, 168], [255, 204, 132])));
        tg.addColorStop(0.3, rgba(h.byTheme([255, 241, 214], [246, 172, 104])));
        tg.addColorStop(0.7, rgba(h.byTheme([253, 250, 244], [206, 146, 112])));
        tg.addColorStop(1, rgba(h.byTheme([250, 248, 246], [176, 136, 136])));
        cctx.fillStyle = tg; cctx.fillRect(0, 0, W, H);
        cctx.globalCompositeOperation = 'source-over';

        // milky hoar veil + glow: a blurred, downscaled copy of the frost
        hctx.setTransform(1, 0, 0, 1, 0, 0);
        hctx.clearRect(0, 0, haze.width, haze.height);
        hctx.filter = 'blur(2.5px)';
        hctx.drawImage(comp, 0, 0, haze.width, haze.height);
        hctx.filter = 'none';

        ctx.globalAlpha = lerp(0.75, 0.45, m);
        ctx.drawImage(haze, 0, 0, W, H);
        if (m > 0.001) { ctx.globalCompositeOperation = 'lighter'; ctx.globalAlpha = 0.36 * m; ctx.drawImage(haze, 0, 0, W, H); ctx.globalCompositeOperation = 'source-over'; }
        ctx.globalAlpha = lerp(0.95, 0.9, m);
        if (m < 0.99) { // by day a faint warm shadow gives the white crystals some edge against the bright sky
            ctx.shadowColor = `rgba(110,70,60,${0.32 * (1 - m)})`; ctx.shadowOffsetX = 0.6; ctx.shadowOffsetY = 1.1; ctx.shadowBlur = 1.5;
        }
        ctx.drawImage(comp, 0, 0, W, H);
        ctx.shadowColor = 'rgba(0,0,0,0)'; ctx.shadowBlur = 0; ctx.shadowOffsetX = 0; ctx.shadowOffsetY = 0;
        ctx.globalAlpha = 1;

        drawWater(ctx, m);
        drawSparkles(ctx, m);
        ctx.restore();
    }

    function drawWater(ctx, m) {
        // drip trails: a thin wet line that slowly refreezes
        ctx.lineCap = 'round';
        for (const dr of drips) {
            const tr = dr.trail, n = tr.length / 3;
            if (n < 2) continue;
            const age = dr.stopped ? T - dr.tEnd : 0, a = Math.exp(-age / 22);
            if (a < 0.02) continue;
            ctx.beginPath(); ctx.moveTo(tr[0], tr[1]);
            for (let i = 1; i < n; i++) ctx.lineTo(tr[i * 3], tr[i * 3 + 1]);
            if (!dr.stopped) ctx.lineTo(dr.x, dr.y);
            ctx.lineWidth = 2.2; ctx.strokeStyle = rgba(h.byTheme([120, 92, 80], [8, 10, 20]), 0.035 * a); ctx.stroke();
            ctx.lineWidth = 0.9; ctx.strokeStyle = rgba(h.byTheme([255, 250, 238], [255, 196, 130]), 0.26 * a); ctx.stroke();
            if (!dr.stopped) drawDrop(ctx, dr.x, dr.y, 2.1 + 1.3 * Math.max(0, dr.mass), 1, m);
        }
        for (const b of beads) {
            const a = smooth(clamp(b.t / 2, 0, 1)) * smooth(clamp((b.life - b.t) / 8, 0, 1));
            if (a > 0.02) drawDrop(ctx, b.x, b.y, b.r, a, m);
        }
    }
    function drawDrop(ctx, x, y, r, a, m) {
        // a small water lens: darker rim from refraction, a bright catchlight from the light source
        ctx.beginPath(); ctx.arc(x, y, r, 0, TAU);
        ctx.fillStyle = rgba(h.byTheme([140, 108, 96], [6, 8, 18]), 0.16 * a); ctx.fill();
        ctx.beginPath(); ctx.arc(x, y + r * 0.2, r * 0.62, 0, TAU);
        ctx.fillStyle = rgba(h.byTheme([255, 240, 214], [70, 82, 120]), 0.35 * a); ctx.fill();
        ctx.beginPath(); ctx.arc(x - r * 0.3, y - r * 0.35, Math.max(0.5, r * 0.28), 0, TAU);
        ctx.fillStyle = rgba(h.byTheme([255, 255, 250], [255, 206, 140]), 0.75 * a); ctx.fill();
    }
    function drawSparkles(ctx, m) {
        // single-crystal ice Ih [0001] pattern: 6 {10-10} spots (30° off the dendrite arms), 6 {11-20} spots √3 out along them
        ctx.globalCompositeOperation = 'lighter';
        const col = h.byTheme([255, 244, 214], [255, 206, 140]);
        for (const s of sparkles) {
            const e = smooth(clamp(s.t / 1.8, 0, 1)) * smooth(clamp((s.life - s.t) / 2.8, 0, 1)) * s.I * (0.55 + 0.45 * illum(s.x, s.y));
            if (e < 0.01) continue;
            const g = ctx.createRadialGradient(s.x, s.y, 0, s.x, s.y, s.sz * 0.9);
            g.addColorStop(0, rgba(col, 0.75 * e)); g.addColorStop(0.25, rgba(col, 0.22 * e)); g.addColorStop(1, rgba(col, 0));
            ctx.fillStyle = g; ctx.fillRect(s.x - s.sz, s.y - s.sz, s.sz * 2, s.sz * 2);
            ctx.fillStyle = rgba(col, 0.8 * e);
            ctx.beginPath();
            for (let k = 0; k < 6; k++) {
                const a = s.th0 + Math.PI / 6 + k * A60, x = s.x + Math.cos(a) * s.sz, y = s.y + Math.sin(a) * s.sz;
                ctx.moveTo(x + 1.35, y); ctx.arc(x, y, 1.35, 0, TAU);
            }
            ctx.fill();
            ctx.fillStyle = rgba(col, 0.5 * e);
            ctx.beginPath();
            for (let k = 0; k < 6; k++) {
                const a = s.th0 + k * A60, x = s.x + Math.cos(a) * s.sz * SQ3, y = s.y + Math.sin(a) * s.sz * SQ3;
                ctx.moveTo(x + 1, y); ctx.arc(x, y, 1, 0, TAU);
            }
            ctx.fill();
        }
        ctx.globalCompositeOperation = 'source-over';
    }

    function pregrow() {
        // start on a pane that has been frosting for a while (not shown: done before the first frame)
        const w = h.warping; h.warping = true; pregrowing = true;
        for (let i = 0; i < 110 * 10; i++) stepSim(0.1);
        h.warping = w; pregrowing = false; needRebuild = true;
        nextBreath = p.random(12, 30); nextSun = p.random(70, 160);
    }

    return {
        setup() { init(); pregrow(); },
        step(dt) { stepSim(dt); },
        render,
        // window drags fire many resizes; rebuild the pane once the size settles
        resize() { pendingResize = performance.now(); },
    };
}
