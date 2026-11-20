// nucleus.js: a corner of the nucleus: double envelope with nuclear pore complexes, lamina and chromatin.
import { TAU, clamp, lerp, sm, hash, vnoise, mixc, css, rgb, lnOf, hiOf, blobPath, gouache, layer, spr, expo, linePath, bilayer, smoothOpen, mulberry } from './lib.js';
import { nucleosome, nucGeom, nucTurn, dnaPaint } from './machines.js';
import { spotOf } from './features.js';


export class Nucleus {
    constructor(G, m) {
        // m: 'L' or 'R'. The envelope runs the height of that side (only the long sides leave room for a whole pore
        // between the screen edge and the card).
        this.G = G; this.claims = m === 'L' ? ['TL', 'L', 'BL'] : ['TR', 'R', 'BR']; this.kind = 'nucleus'; this.age = 0; this.dead = -1; this.alive = true;
        this.seed = (G.r() * 1e6) | 0;
        const k = G.k, f = G.h.focus, sx = m === 'L' ? 1 : -1, ex = m === 'L' ? 0 : G.W;
        // a real nucleus is 5-10 um across, so at ~2.5 px/nm its envelope is all but straight: radius 2.5-4 um, a
        // gentle bow of a few nm over the screen, tipped a little, and as far out as the card allows once the pores'
        // cytoplasmic filaments are counted
        const tilt = (G.r() - 0.5) * 0.2, ux = sx * Math.cos(tilt), uy = Math.sin(tilt);
        this.out = [ux, uy]; // slides in from the edge
        const Rn = (2500 + G.r() * 1500) * k, P0 = [m === 'L' ? f.x : f.x + f.w, f.cy];
        this.C = [P0[0] - ux * Rn, P0[1] - uy * Rn];
        for (let it = 0; it < 120; it++) {
            let worst = 0;
            const a0 = Math.atan2(uy, ux), span = (G.W + G.H) / Rn;
            for (let a = a0 - span; a < a0 + span; a += 6 / Rn) { const x = this.C[0] + Math.cos(a) * (Rn + 48 * k), y = this.C[1] + Math.sin(a) * (Rn + 48 * k); worst = Math.max(worst, G.h.focusFalloff(x, y, 26 * k)); }
            if (worst <= 0) break;
            this.C[0] -= ux * 8; this.C[1] -= uy * 8;
        }
        const Q = [ex, G.H / 2], Qs = [[ex, -20], [ex, G.H + 20]];
        // ONM and INM 40 nm apart centre to centre (perinuclear space ~35 nm); they fuse at each pore
        this.Ro = Rn; this.Ri = Rn - 40 * k; this.Rm = Rn - 20 * k;
        // visible arc
        const a0 = Math.atan2(uy, ux), vis = [];
        for (let d = -(G.W + G.H) / Rn; d <= (G.W + G.H) / Rn; d += 3 / Rn) {
            const a = a0 + d, x = this.C[0] + Math.cos(a) * Rn, y = this.C[1] + Math.sin(a) * Rn;
            if (x > -80 && y > -80 && x < G.W + 80 && y < G.H + 80) vis.push(a);
        }
        this.aLo = vis[0] - 40 / Rn; this.aHi = vis[vis.length - 1] + 40 / Rn;
        // pore positions: well on screen, far enough from the card for the filaments and basket (and mRNPs docking beyond it)
        // pore membrane ~92 nm across (midline at 46 nm from the axis); the sac ends turn with a 20 nm radius
        const half = (66 * k) / this.Rm, cands = [];
        for (let a = this.aLo + half * 1.3; a < this.aHi - half * 1.3; a += 8 / this.Rm) {
            // the whole complex in view: cytoplasmic filaments, both rings, the basket and its distal ring
            const ext = [[0, 0], [-64, -66], [64, -66], [-64, 30], [64, 30], [0, 92], [-24, 92], [24, 92]].map(([lx, ly]) => this.poreXY(a, lx * k, ly * k));
            if (ext.every(([x, y]) => x > 24 && y > 24 && x < G.W - 24 && y < G.H - 24) && ext.slice(0, 3).every(([x, y]) => G.h.focusFalloff(x, y, 24 * k) === 0)) cands.push(a);
        }
        this.pores = [];
        if (cands.length) {
            const a1 = cands[(G.r() * cands.length) | 0]; this.pores.push(a1);
            const far = cands.filter(a => Math.abs(a - a1) > half * 3.4);
            if (far.length && G.r() < 0.7) this.pores.push(far[(G.r() * far.length) | 0]);
        }
        this.pores.sort((a, b) => a - b);
        this.half = half; this.valid = this.pores.length > 0;
        this.cargo = []; this.spawnT = 3;
        // bounding box of the corner region
        const pts = [...Qs];
        for (let a = this.aLo; a <= this.aHi; a += 10 / Rn) pts.push([this.C[0] + Math.cos(a) * (Rn + 75 * k), this.C[1] + Math.sin(a) * (Rn + 75 * k)]);
        let x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
        for (const [x, y] of pts) { x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
        x0 = Math.max(-20, x0); y0 = Math.max(-20, y0); x1 = Math.min(G.W + 20, x1); y1 = Math.min(G.H + 20, y1);
        this.bb = [Math.floor(x0), Math.floor(y0), Math.ceil(x1 - x0), Math.ceil(y1 - y0)];
        this.layer = null;
    }
    retire() { if (this.dead < 0) this.dead = 0; }
    get alpha() { const fi = sm(clamp(this.age / 20, 0, 1)), fo = this.dead < 0 ? 1 : sm(clamp(1 - this.dead / 16, 0, 1)); return fi * fo; }
    get slide() { return (1 - sm(clamp(this.age / 20, 0, 1))) * 46 + (this.dead < 0 ? 0 : sm(clamp(this.dead / 16, 0, 1)) * 36); }
    tick(dt) {
        // the chromatin is laid out a few ms per frame before the nucleus fades in (all at once for the opening scene)
        if (!this.buildChrom(this.age > 0 ? Infinity : 3)) return;
        this.age += dt;
        if (this.dead >= 0) { this.dead += dt; if (this.dead > 16) this.alive = false; }
        const G = this.G, k = G.k;
        this.spawnT -= dt;
        if (this.spawnT < 0 && this.pores.length) {
            // one arrival clock per nucleus; each arrival picks a pore at random
            this.spawnT = expo(G.r, 9);
            // 'imp': importin-beta + cargo heading in; 'rel': RanGTP bound, cargo coming off; 'ret': importin-beta.RanGTP
            // heading back out; 'exp': an mRNP, which starts docked on the basket's distal ring
            const imp = G.r() < 0.7, q = { p: (G.r() * this.pores.length) | 0, st: imp ? 'imp' : 'exp', y: imp ? -62 * k : 96 * k, t: 0, rot: imp ? G.r() * TAU : Math.PI / 2 + (G.r() - 0.5) * 0.4, v: (3.2 + G.r() * 2) * k, seed: G.r() * 999 };
            q.x = this.lane(q) + (G.r() - 0.5) * 2 * k;
            const waiting = this.cargo.some(o => o.p === q.p && (o.st === 'rel' || (o.st === 'ret' && Math.abs(o.x - this.lane(o)) > 2 * k)));
            if (this.gap(q) > 3 * k && !(q.st === 'exp' && waiting)) this.cargo.push(q); // (staggered: never spawned onto another particle)
        }
        const born = [];
        for (const q of this.cargo) {
            q.t += dt;
            // Inbound and outbound traffic keep to their own side of the ~50 nm channel, so they pass side by side, and
            // every particle moves only where its envelope stays clear of every other particle in the pore: one that
            // would bump the particle ahead in its lane waits behind it.
            if (q.st === 'rel') { // stays put while the cargo comes off toward the nucleoplasm
                q.e = Math.min(1, q.e + dt / 1.5);
                if (q.e >= 1) { born.push({ p: q.p, st: 'free', x: q.x, y: q.y + 9.8 * k, t: 0, rot: q.rot, v: 0, seed: G.r() * 999, par: q }); q.st = 'ret'; q.t = 0; }
                continue;
            }
            const inCh = Math.abs(q.y) < 24 * k, dir = q.st === 'imp' || q.st === 'free' ? 1 : -1, lx = this.lane(q);
            let nx = q.x, ny = q.y, nr = q.rot;
            // a receptor that has just let go of its cargo crosses over to the outbound side once nothing outbound is
            // left below it (no new mRNP docks at this pore meanwhile), then heads out
            const crossing = q.st === 'ret' && Math.abs(q.x - lx) > 2 * k;
            if (crossing && !q.go) q.go = !this.cargo.some(o => o !== q && o.p === q.p && (o.st === 'exp' || o.st === 'ret') && o.y > q.y - 24 * k);
            const v = q.st === 'free' ? 1.2 * k : crossing ? 0 : q.v * (inCh ? 0.7 : 1);
            ny += dir * v * dt;
            nx += ((crossing && !q.go ? q.x : lx) - q.x) * (crossing ? 1.2 : 0.6) * dt + vnoise(G.time * 0.4 + q.t, q.seed) * 1.5 * k * dt;
            if (q.st === 'exp' && Math.abs(q.y) < 60 * k) nr += (Math.PI / 2 - q.rot) * (1 - Math.exp(-dt / 1.2)); // the mRNP lines up with the channel
            else if (q.st !== 'exp') nr += dt * (q.st === 'free' ? 0.2 : 0.3);
            const lim = y => (Math.abs(y) < 30 * k ? 18 : y > 0 && q.st === 'exp' ? 10.5 : 14) * k; // channel; basket (between the distal-ring densities); cytoplasmic mouth
            nx = clamp(nx, -lim(ny), lim(ny));
            if (q.st === 'imp') ny = Math.min(ny, 56 * k); // waits there for RanGTP and room to let its cargo go
            const g0 = this.gap(q), g1 = this.gap(q, nx, ny, nr);
            if (g1 >= (crossing ? 0.3 : 1.5) * k || g1 >= g0) { q.x = nx; q.y = ny; q.rot = nr; q.blk = 0; } // (a queue keeps ~1.5 nm apart)
            else { q.blk = (q.blk ?? 0) + dt; if (this.gap(q, q.x, q.y, nr) >= Math.min(k, g0)) q.rot = nr; } // (it may still turn in place)
            // past the nuclear ring RanGTP binds importin-beta and the cargo comes off; the receptor turns back out
            // (only once there is room for the cargo just beyond it)
            if (q.st === 'imp' && q.y > 54 * k && this.gap({ p: q.p, st: 'free', par: q }, q.x, q.y + 9.8 * k, 0) > 0.5 * k) { q.st = 'rel'; q.e = 0; }
        }
        this.cargo.push(...born);
        this.cargo = this.cargo.filter(q => q.st === 'free' ? q.t < 6 : q.y > -70 * k && q.y < 112 * k);
        // a freshly released cargo starts in contact with its receptor; from the moment they part they avoid each other
        for (const q of this.cargo) if (q.par && (!this.cargo.includes(q.par) || this.gap({ ...q, par: null }, q.x, q.y, q.rot, q.par) > 0.5 * k)) q.par = null;
    }
    lane(q) { return (q.st === 'imp' || q.st === 'rel' || q.st === 'free' ? -9.5 : 9.5) * this.G.k; }
    /** collision envelope of a particle: circles (pore-local px) at position (x, y), turned by rot. */
    env(q, x = q.x, y = q.y, rot = q.rot) {
        const k = this.G.k, c = Math.cos(rot), s = Math.sin(rot);
        const E = q.st === 'free' ? [[0, 0, 2.9]] : q.st === 'exp' ? [[-7, 0, 5], [0, 0, 5], [7, 0, 5]] : [[0, 0, 6.6]];
        const out = E.map(([ex, ey, r]) => [x + (ex * c - ey * s) * k, y + (ex * s + ey * c) * k, r * k]);
        if (q.st === 'rel') out.push([x, y + (0.4 + q.e * 9.4) * k, 2.9 * k]); // the cargo on its way off (pore frame)
        return out;
    }
    /** smallest surface-to-surface distance between q (optionally moved) and any other particle in its pore. */
    gap(q, x, y, rot, only = null) {
        const A = this.env(q, x, y, rot); let g = 1e9;
        for (const o of this.cargo) {
            if (o === q || o.p !== q.p || o.par === q || q.par === o || (only && o !== only)) continue;
            for (const b of this.env(o)) for (const a of A) g = Math.min(g, Math.hypot(a[0] - b[0], a[1] - b[1]) - a[2] - b[2]);
        }
        return g;
    }
    alphaOf(q) {
        const k = this.G.k;
        if (q.st === 'free') return 1 - sm(clamp((q.t - 3) / 3, 0, 1));
        if (q.st === 'imp') return sm(clamp(q.t / 1.5, 0, 1));
        const out = 1 - sm(clamp((-q.y - 54 * k) / (14 * k), 0, 1));
        return q.st === 'exp' ? sm(clamp(q.t / 1.5, 0, 1)) * out : out;
    }
    pt(a, r) { return [this.C[0] + Math.cos(a) * r, this.C[1] + Math.sin(a) * r]; }
    // local pore frame: x along the envelope, +y toward the nucleus
    poreXY(ap, lx, ly) { const t = [-Math.sin(ap), Math.cos(ap)], n = [-Math.cos(ap), -Math.sin(ap)], c = this.pt(ap, this.Rm); return [c[0] + t[0] * lx + n[0] * ly, c[1] + t[1] * lx + n[1] * ly]; }
    sacs() {
        // closed loops of envelope between pores: ONM out, turn, INM back, turn
        const k = this.G.k, ends = [this.aLo - 60 / this.Ro, ...this.pores.flatMap(a => [a - this.half, a + this.half]), this.aHi + 60 / this.Ro], out = [];
        for (let i = 0; i < ends.length; i += 2) {
            const a1 = ends[i], a2 = ends[i + 1], P = [];
            const n = Math.max(4, Math.ceil(((a2 - a1) * this.Ro) / 4));
            for (let j = 0; j <= n; j++) P.push(this.pt(lerp(a1, a2, j / n), this.Ro));
            const tc = this.pt(a2, this.Rm), ta = a2 + Math.PI / 2;
            for (let j = 1; j < 10; j++) { const b = (j / 10) * Math.PI, rr = 20 * k; P.push([tc[0] + Math.cos(a2) * rr * Math.cos(b) + Math.cos(ta) * rr * Math.sin(b), tc[1] + Math.sin(a2) * rr * Math.cos(b) + Math.sin(ta) * rr * Math.sin(b)]); }
            for (let j = n; j >= 0; j--) P.push(this.pt(lerp(a1, a2, j / n), this.Ri));
            const tc1 = this.pt(a1, this.Rm), tb = a1 - Math.PI / 2;
            for (let j = 1; j < 10; j++) { const b = (j / 10) * Math.PI, rr = 20 * k; P.push([tc1[0] - Math.cos(a1) * rr * Math.cos(b) + Math.cos(tb) * rr * Math.sin(b), tc1[1] - Math.sin(a1) * rr * Math.cos(b) + Math.sin(tb) * rr * Math.sin(b)]); }
            out.push(P);
        }
        return out;
    }
    paint(x) {
        const G = this.G, k = G.k, s = this.seed, C = this.C;
        // nucleoplasm
        x.fillStyle = css(rgb(G, 'nucleo')); x.beginPath(); x.arc(C[0], C[1], this.Ri + 2 * k, 0, TAU); x.fill();
        x.save(); x.beginPath(); x.arc(C[0], C[1], this.Ri - 3 * k, 0, TAU); x.clip();
        const Ri = this.Ri, gapAt = a => this.pores.some(ap => Math.abs(a - ap) < this.half * 0.85);
        const P2 = (u, d) => this.pt(u / Ri, Ri - d * k); // (arc length px along the INM, depth nm below its midline)
        // lamina: lamin filaments (~3.5 nm thick) as short runs of 20-150 nm at varied angles plus cross-sections,
        // all within a ~14 nm layer under the INM, sparse under the pores
        x.strokeStyle = css(rgb(G, 'lamina'), 0.72); x.fillStyle = css(rgb(G, 'lamina'), 0.72); x.lineWidth = 3.2 * k; x.lineCap = 'round'; x.lineJoin = 'round';
        const u0 = this.aLo * Ri, u1 = this.aHi * Ri;
        for (let i = 0, n = Math.ceil((u1 - u0) / (22 * k)); i < n; i++) {
            const L = (20 + 130 * Math.pow(hash(i, s + 21), 1.6)) * k, um = lerp(u0, u1, hash(i, s + 22)), dc = 7 + hash(i, s + 23) * 8;
            const slope = (hash(i, s + 24) - 0.5) * 2 * Math.min(0.6, (7.5 * k) / L);
            const P = [], flush = () => { if (P.length > 1) { linePath(x, P); x.stroke(); } P.length = 0; };
            for (let u = um - L / 2; u <= um + L / 2; u += 3 * k) {
                if (gapAt(u / Ri)) { flush(); continue; }
                const d = clamp(dc + ((u - um) * slope) / k + 0.8 * vnoise(u / (9 * k), s + i), 5, 16);
                P.push(P2(u, d));
            }
            flush();
        }
        for (let i = 0, n = Math.ceil((u1 - u0) / (16 * k)); i < n; i++) {
            const u = lerp(u0, u1, hash(i, s + 31)); if (gapAt(u / Ri)) continue;
            const [px, py] = P2(u, 5.5 + hash(i, s + 32) * 10); x.beginPath(); x.arc(px, py, 1.75 * k, 0, TAU); x.fill();
        }
        // chromatin (geometry built once, then painted for each theme)
        this.buildChrom(Infinity);
        const dnaStroke = B => { x.beginPath(); x.moveTo(B[0][0], B[0][1]); x.bezierCurveTo(B[1][0], B[1][1], B[2][0], B[2][1], B[3][0], B[3][1]); };
        for (const ch of this.chromG) {
            for (const B of ch.dna) { dnaStroke(B); dnaPaint(G, x, k); }
            ch.nucs.forEach(n => nucleosome(G, x, n.c[0], n.c[1], n.g, n.seed));
        }
        x.restore();
        // envelope: lumen, then both membranes as one continuous bilayer per sac
        for (const P of this.sacs()) {
            x.beginPath(); x.moveTo(P[0][0], P[0][1]); for (const q of P) x.lineTo(q[0], q[1]); x.closePath();
            x.fillStyle = css(rgb(G, 'lumen')); x.fill();
            bilayer(G, x, P, k, true);
        }
        // ribosomes bound to the outer membrane (it is continuous with the rough ER)
        const ribA = [];
        for (let i = 0; i < 6; i++) {
            const a = lerp(this.aLo, this.aHi, hash(i, s + 9));
            if (this.pores.some(ap => Math.abs(a - ap) < this.half * 1.6)) continue;
            if (ribA.some(b => Math.abs(a - b) * this.Ro < 40 * k)) continue; // 80S ~30 nm across: no overlaps
            const [px, py] = this.pt(a, this.Ro + 3 * k);
            if (px < -40 || py < -40 || px > G.W + 40 || py > G.H + 40) continue;
            ribA.push(a);
            x.save(); x.translate(px, py); x.rotate(a - Math.PI / 2);
            const S = G.S, th = G.t;
            // local +y points into the cytoplasm; the ONM midline is at y = -3 nm. The 60S docks on a Sec61
            // translocon with its exit tunnel (x = +4.5 nm) ~4 nm above the membrane surface; the 40S faces the cytoplasm
            gouache(G, x, () => blobPath(x, 4.5 * k, -2.2 * k, 3.0 * k, 3.6 * k, 0, 470 + i, 0.08, 9), rgb(G, 'mprot', 1), { lw: 0.8, hd: 0.5 * k });
            x.drawImage(S.r60.get(th), -S.r60.w / 2, 11.4 * k - S.r60.h / 2, S.r60.w, S.r60.h);
            x.drawImage(S.r40.get(th), -S.r40.w / 2, 23.9 * k - S.r40.h / 2, S.r40.w, S.r40.h);
            x.restore();
        }
        for (const ap of this.pores) this.paintPore(x, ap);
    }
    // Chromatin: long nucleosome chains, an in-situ fibre rather than digested mononucleosomes. Each linker leaves a
    // nucleosome exactly where its DNA superhelix ends, along the superhelix there, and the next nucleosome is turned
    // so that its superhelix starts exactly at the linker's end. Linkers are ~25-60 bp (8-20 nm). Each chain grows both
    // ways from a seed nucleosome as a self-avoiding walk: when the next placement is blocked it tries other bends,
    // linker lengths and orientations (looking one linker ahead), and backtracks when nothing fits. A chain ends when
    // it runs off the screen; only a walk that is truly boxed in stops at a short free end (the fibre leaving the
    // section plane). Chains too short to read as a fibre are taken out again. Heterochromatin packs the 16-80 nm layer
    // under the lamina compactly and irregularly; euchromatin fibres are sparser deeper in.
    *chromatinGen() { // (a generator, so the search can be spread over a few frames; see buildChrom)
        const G = this.G, k = G.k, s = this.seed, C = this.C, Ri = this.Ri;
        const clear = (px, py) => this.pores.some(ap => { const q = this.pt(ap, Ri); return Math.hypot(px - q[0], py - q[1]) < 80 * k; }); // chromatin-free channels beneath pores
        const inside = (px, py) => Math.hypot(px - C[0], py - C[1]) < Ri - 14 * k;
        const onScreen = (px, py) => px > -6 && py > -6 && px < G.W + 6 && py < G.H + 6;
        const depthOf = (px, py) => (Ri - Math.hypot(px - C[0], py - C[1])) / k;
        // a coarse grid of what is placed: nucleosome centres ('n') and points along every free DNA stretch ('d').
        // No two nucleosomes overlap (centres 10.8 nm apart: DNA ring outer radius 5.2 nm); no DNA stretch runs within
        // 6.2 nm of any nucleosome but its own (ring 5.2 nm + 1 nm) or back over its own core; DNA stretches keep 2.6 nm
        // apart (2 nm duplexes).
        const cell = 8 * k, grid = new Map(), cellOf = v => Math.floor(v / cell), keyOf = (px, py) => cellOf(px) * 100003 + cellOf(py);
        const put = (px, py, kind) => { const it = [px, py, kind], kk = keyOf(px, py); if (!grid.has(kk)) grid.set(kk, []); grid.get(kk).push(it); return it; };
        const unput = it => { const L = grid.get(keyOf(it[0], it[1])), i = L.indexOf(it); if (i >= 0) L.splice(i, 1); };
        const hit = (px, py, r, kind, skip = null) => {
            for (let ix = cellOf(px - r); ix <= cellOf(px + r); ix++) for (let iy = cellOf(py - r); iy <= cellOf(py + r); iy++) {
                const L = grid.get(ix * 100003 + iy); if (!L) continue;
                for (const it of L) if (it[2] === kind && it !== skip && !(skip && skip.has && skip.has(it)) && Math.hypot(it[0] - px, it[1] - py) < r) return true;
            }
            return false;
        };
        // (soft: the nucleosome two back along the chain. In a zigzag fibre it stacks against the new one, offset in
        // depth, so in projection the two may come a little closer: 9 nm between centres, DNA rings just touching)
        const free = (c, cfg, soft = null) => { const d = depthOf(c[0], c[1]); return d >= cfg.lo && d <= cfg.hi && inside(c[0], c[1]) && !clear(c[0], c[1]) && !hit(c[0], c[1], 10.8 * k, 'n', soft) && (!soft || Math.hypot(c[0] - soft[0], c[1] - soft[1]) >= 9 * k) && !hit(c[0], c[1], 6.4 * k, 'd'); };
        const ang = v => Math.atan2(v[1], v[0]);
        const bz = (B, u) => { const v = 1 - u; return [0, 1].map(i => v * v * v * B[0][i] + 3 * v * v * u * B[1][i] + 3 * v * u * u * B[2][i] + u * u * u * B[3][i]); };
        const US = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1];
        // (own: the nucleosome the stretch leaves; near: the DNA already joined to it on its other side, which may lie
        // close by in projection; the stretch must also not fold back toward its own core)
        const dnaOk = (B, own, near, soft = null) => {
            const d0 = Math.min(4.1 * k, Math.hypot(B[0][0] - own[0], B[0][1] - own[1]) - 0.2 * k), sk = new Set([own, soft]);
            return US.every(u => { const [px, py] = bz(B, u); return !onScreen(px, py) || (inside(px, py) && !hit(px, py, 6.2 * k, 'n', sk) && (!soft || Math.hypot(px - soft[0], py - soft[1]) >= 5.2 * k) && !hit(px, py, 2.6 * k, 'd', near) && Math.hypot(px - own[0], py - own[1]) >= d0); });
        };
        const putB = B => US.map(u => put(...bz(B, u), 'd'));
        const R = mulberry((s * 7 + 11) >>> 0), rn = (a, b) => a + (b - a) * R();
        const placeNuc = (c, g) => ({ c, g, it: put(c[0], c[1], 'n') });
        // the open end of a chain: point, direction the DNA leaves in, the nucleosome it leaves, which way we walk
        const endOf = (n, fwd, near, soft = null) => fwd ? { P: [n.c[0] + n.g.pout[0], n.c[1] + n.g.pout[1]], t: ang(n.g.tout), own: n.it, fwd, near, soft } : { P: [n.c[0] + n.g.pin[0], n.c[1] + n.g.pin[1]], t: ang(n.g.tin) + Math.PI, own: n.it, fwd, near, soft };
        // a placement whose own exit leads straight into a crowd (or back over its core) is a dead end: look one linker
        // ahead and prefer candidates with room there (otherwise take any that fits)
        const roomy = (c, pout, tout, cfg) => {
            if (tout[0] * pout[0] + tout[1] * pout[1] < 0.15 * Math.hypot(pout[0], pout[1])) return false;
            const A = [c[0] + pout[0] + tout[0] * 9 * k, c[1] + pout[1] + tout[1] * 9 * k], d = depthOf(A[0], A[1]);
            return !onScreen(A[0], A[1]) || (d > cfg.lo - 4 && d < cfg.hi + 4 && !clear(A[0], A[1]) && !hit(A[0], A[1], 8 * k, 'n') && Math.hypot(A[0] - c[0], A[1] - c[1]) > 7 * k);
        };
        const tryStep = (e, cfg) => {
            let fall = null;
            for (let tr = 0; tr < 16; tr++) { // a linker shape, then a few orientations for the next nucleosome on it
                const bend = rn(-1.4, 1.4), L = rn(cfg.L[0], cfg.L[1]) * k, d = e.t + bend, P = e.P;
                const q = [P[0] + Math.cos(e.t + bend / 2) * L, P[1] + Math.sin(e.t + bend / 2) * L];
                const B = [P, [P[0] + Math.cos(e.t) * L / 3, P[1] + Math.sin(e.t) * L / 3], [q[0] - Math.cos(d) * L / 3, q[1] - Math.sin(d) * L / 3], q];
                if (!dnaOk(B, e.own, e.near, e.soft)) continue;
                if (!onScreen(q[0], q[1])) return { B, off: true }; // the fibre runs on off the screen
                for (let o = 0; o < 4; o++) {
                    // (mostly oblique views: those separate the two gyres, as in the classic side-view picture)
                    const g = nucTurn(nucGeom(k, 0, 0.3 + 0.7 * R(), R() * TAU), e.fwd ? null : 1, d);
                    const c = e.fwd ? [q[0] - g.pin[0], q[1] - g.pin[1]] : [q[0] - g.pout[0], q[1] - g.pout[1]];
                    if (!free(c, cfg, e.soft)) continue;
                    const st = { B, c, g };
                    if (e.fwd ? roomy(c, g.pout, g.tout, cfg) : roomy(c, g.pin, [-g.tin[0], -g.tin[1]], cfg)) return st;
                    fall ??= st;
                }
            }
            return fall;
        };
        const walk = function* (n0, fwd, cfg, near) {
            const stack = []; let e = endOf(n0, fwd, near), budget = 600, back = 0;
            while (stack.length < cfg.max && budget-- > 0) {
                yield;
                const st = tryStep(e, cfg);
                if (st && st.off) { stack.push({ B: st.B, items: putB(st.B), off: true }); return { stack, end: null }; }
                if (st) { const items = putB(st.B), n = placeNuc(st.c, st.g); stack.push({ B: st.B, items, n, prev: e }); e = endOf(n, fwd, new Set(items), e.own); continue; }
                if (!stack.length || back++ > 100) break;
                const last = stack.pop(); last.items.forEach(unput); unput(last.n.it); e = last.prev; // backtrack
            }
            return { stack, end: e };
        };
        const stubB = (e, st) => [0, 0.35, 0.7, 1].map(f => [e.P[0] + Math.cos(e.t) * st * f, e.P[1] + Math.sin(e.t) * st * f]);
        const cfgs = [
            { kind: 'het', lo: 16, hi: 80, L: [8, 16], max: 60, starts: 90, d0: [20, 70] },
            { kind: 'eu', lo: 84, hi: 185, L: [10, 20], max: 40, starts: 10, d0: [95, 170] },
        ];
        const out = []; this.chrom = [];
        for (const cfg of cfgs) for (let st = 0; st < cfg.starts; st++) {
            const c = this.pt(lerp(this.aLo, this.aHi, R()), Ri - rn(cfg.d0[0], cfg.d0[1]) * k);
            if (!onScreen(c[0], c[1]) || !free(c, cfg)) continue;
            const n0 = placeNuc(c, nucGeom(k, R() * TAU, 0.3 + 0.7 * R(), R() * TAU));
            const A = yield* walk(n0, false, cfg, null), Bw = yield* walk(n0, true, cfg, A.stack.length ? new Set(A.stack[0].items) : null);
            const nucs = [...A.stack.filter(q => q.n).map(q => q.n).reverse(), n0, ...Bw.stack.filter(q => q.n).map(q => q.n)];
            if (nucs.length < 6) { // too short to read as a fibre: take it back out (leaving the gap)
                for (const w of [A, Bw]) for (const q of w.stack) { q.items.forEach(unput); if (q.n) unput(q.n.it); }
                unput(n0.it); continue;
            }
            const dna = [], freeEnds = [0];
            for (const w of [A, Bw]) {
                for (const q of w.stack) dna.push(q.B);
                if (w.end) { // boxed in: a short free end, shortened if crowded
                    if (onScreen(...w.end.P)) freeEnds[0]++;
                    for (let f = 1; f > 0.3; f -= 0.35) { const B = stubB(w.end, (3 + R() * 4) * k * f); if (dnaOk(B, w.end.own, w.end.near)) { putB(B); dna.push(B); break; } }
                }
            }
            out.push({ dna, nucs: nucs.map((n, i) => ({ c: n.c, g: n.g, seed: (st * 131 + i) | 0 })) });
            this.chrom.push({ kind: cfg.kind, n: nucs.length, vis: nucs.filter(n => onScreen(n.c[0], n.c[1])).length, freeEnds: freeEnds[0] });
        }
        return out;
    }
    /** advance the chromatin search for up to ms milliseconds (Infinity: finish it); true once the geometry is ready. */
    buildChrom(ms) {
        if (this.chromG) return true;
        this.gen ??= this.chromatinGen();
        const t0 = performance.now();
        for (;;) { const r = this.gen.next(); if (r.done) { this.chromG = r.value; this.gen = null; return true; } if (performance.now() - t0 > ms) return false; }
    }
    paintPore(x, ap) {
        // Axial cross-section through an 8-fold NPC: the cut passes through two opposite spokes. Local frame: x along
        // the envelope, +y toward the nucleus; ONM at y = -20 nm, INM at +20, pore membrane at |x| = 46 nm.
        const G = this.G, k = G.k, s = this.seed;
        const L = (lx, ly) => this.poreXY(ap, lx * k, ly * k);
        const blob = (lx, ly, rx, ry, rot, sd, c) => { const [px, py] = L(lx, ly); gouache(G, x, () => blobPath(x, px, py, rx * k, ry * k, rot + ap + Math.PI / 2, sd, 0.1, 10), c, { lw: 0.9, hi: 0.45, hd: 0.5 * k }); };
        const npc = rgb(G, 'npc'), fg = rgb(G, 'fg');
        // The fused pore membrane: each sac end turns with a 20 nm radius about (+-66, 0), so the bilayer surface
        // facing the pore is the circle of radius ~22.5 nm about that centre. Rings are placed on that curved surface.
        const onRim = (th, d) => [66 - (22.5 + d) * Math.sin(th), -(22.5 + d) * Math.cos(th)]; // th from the cytoplasmic face toward the pore
        const ringC = [[...onRim(0.2, 4.2), 4.6, 4.0], [...onRim(0.68, 4.4), 4.6, 4.2], [...onRim(0.42, 11.5), 4.2, 3.6], [...onRim(1.05, 3.6), 3.4, 3.4]];
        const inner = [[38, -11, 4.6, 5.6], [38.5, 0, 4.8, 5.6], [38, 11, 4.6, 5.4], [31, -5, 3.4, 4.4], [31, 6, 3.4, 4.2]];
        // FG-repeat meshwork filling the ~50 nm central channel. A third of the drawn FG chains are tethered to the
        // inner-ring lumps that line the channel (Nup62 complex, Nup98); the rest are cut where they leave the section
        x.lineCap = 'round';
        for (let i = 0; i < 30; i++) {
            const P = [];
            let lx = (hash(i, s) - 0.5) * 48, ly = (hash(i, s + 1) - 0.5) * 34, tx = 0;
            if (i < 10) { const sg = i % 2 ? 1 : -1, lp = inner[3 + ((i >> 1) % 2)]; lx = sg * (lp[0] - lp[2] + 0.6); ly = lp[1] + (hash(i, s + 4) - 0.5) * 5; tx = -sg * 4; }
            for (let j = 0; j < 6; j++) { P.push(L(lx, ly)); lx += (hash(i * 7 + j, s + 2) - 0.5) * 12 + tx; ly += (hash(i * 7 + j, s + 3) - 0.5) * 12; lx = clamp(lx, -28, 28); ly = clamp(ly, -22, 22); }
            x.strokeStyle = css(fg, 0.7); x.lineWidth = 0.8 * k; smoothOpen(x, P); x.stroke();
        }
        // cytoplasmic filaments (Nup358, ~30 nm drawn) rooted on the top of the outer cytoplasmic-ring lumps; the nuclear
        // basket (Tpr, Nup153) from the matching nuclear-ring lump to the distal ring
        const fil = () => { x.strokeStyle = css(mixc(npc, lnOf(G, npc, 0.12), 0.5)); x.lineWidth = 2.0 * k; x.lineCap = 'round'; };
        for (const sg of [-1, 1]) {
            for (const [rx0, ry0, , ry] of [ringC[2], ringC[0]]) {
                const bx = sg * rx0, by = ry0 - ry + 0.6, w = hash(rx0 | 0, s + sg);
                const P = [L(bx, by), L(bx + sg * (1.5 + w * 2), by - 10), L(bx - sg * 1.5, by - 20), L(bx + sg * w, by - 30)];
                fil(); smoothOpen(x, P); x.stroke();
                const [ex, ey] = P[3]; gouache(G, x, () => blobPath(x, ex, ey, 2.4 * k, 2.1 * k, 0, sg + rx0, 0.1, 8), npc, { lw: 0.7, hi: 0.4, hd: 0.4 * k });
            }
            const [nx0, ny0, , nry] = ringC[2], P = [L(sg * nx0, -ny0 + nry - 0.6), L(sg * 40, 58), L(sg * 21, 85)]; fil(); smoothOpen(x, P); x.stroke();
            blob(sg * 20, 87.5, 3.6, 3.0, 0, 401 + sg, npc); // the distal ring, cut through twice
        }
        for (const sg of [-1, 1]) {
            // outer (cytoplasmic and nuclear) Y-complex rings reach ~60 nm from the axis (~120 nm across); the inner ring
            // lines the pore membrane and bounds the ~50 nm channel; the luminal ring (gp210) sits in the lumen
            const lumps = [...ringC, ...ringC.map(([lx, ly, rx, ry]) => [lx, -ly, rx, ry]), ...inner];
            lumps.forEach(([lx, ly, rx, ry], i) => blob(sg * lx, ly, rx, ry, i, 410 + i * 3 + sg, mixc(npc, i % 3 === 0 ? rgb(G, 'lamina') : rgb(G, 'cream'), [0.05, 0.18, 0.1][i % 3])));
            blob(sg * 53, 0, 4.5, 4, 0, 450 + sg, rgb(G, 'mprot', 0)); // gp210 luminal ring, against the pore membrane
        }
    }
    draw() {
        const G = this.G, a = this.alpha;
        if (a <= 0.003 || !this.chromG) return;
        const ctx = G.ctx, k = G.k, S = G.S, sl = this.slide;
        ctx.save(); G.ox = this.out[0] * -sl; G.oy = this.out[1] * -sl; ctx.translate(G.ox, G.oy); G.fa = a; ctx.globalAlpha = a;
        if (!this.layer) this.layer = layer(G, ...this.bb, x => this.paint(x));
        this.layer.draw();
        for (const q of this.cargo) {
            const ap = this.pores[q.p]; if (ap === undefined) continue;
            const [px, py] = this.poreXY(ap, q.x, q.y);
            ctx.save(); ctx.translate(px, py); ctx.rotate(q.rot + ap + Math.PI / 2); // (q.rot is in the pore frame)
            if (q.st === 'free') spr(G, S.fold[3], 0, 0, this.alphaOf(q));
            else if (q.st === 'ret') spr(G, S.impR, 0, 0, this.alphaOf(q)); // back to the cytoplasm (RanGAP there, not drawn)
            else if (q.st === 'rel') { // RanGTP binds and the cargo comes off, toward the nucleoplasm
                const e = sm(q.e), c = Math.cos(q.rot), sn = Math.sin(q.rot), oy = (0.4 + q.e * 9.4) * k;
                spr(G, S.imp, 0, 0, 1 - e); spr(G, S.impR, 0, 0, e);
                spr(G, S.fold[3], oy * sn, oy * c, e); // (pore-frame offset (0, oy) in this turned frame)
            }
            else if (q.st === 'imp') {
                const fade = this.alphaOf(q);
                spr(G, S.imp, 0, 0, fade);
            } else spr(G, S.mrnp, 0, 0, this.alphaOf(q));
            ctx.restore();
        }
        for (const ap of this.pores) { const [px, py] = this.poreXY(ap, 0, 0); G.glow(px, py, 30 * k, 0.42 + 0.12 * vnoise(G.time * 0.2, ap * 100)); }
        ctx.restore(); G.fa = 1; G.ox = G.oy = 0;
    }
}
