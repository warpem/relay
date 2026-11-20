// 20 · Molecular Workshop: a painted cell edge where the molecular machines go about their day.
// Canvas2D only. Every machine is a cached gouache sprite (light + night copies, crossfaded by h.mix); scenery
// pieces (membranes, organelles, filaments) are cached once into layers and slide in and out from the edges.
import { TAU, clamp, lerp, sm, hash, vnoise, mixc, css, rgb, PAL, mulberry, expo, blobPath, gouache, layer, makeTexture, spr, sprite } from './lib.js';
import { makeSprites, vesicleSprite } from './machines.js';
import { EDGE_ZONES, edgeMap, spotOf, PlasmaMembrane, Microtubule, Mito, Polysome, Chaperonins } from './features.js';
import { Nucleus } from './nucleus.js';

export const meta = {
    title: 'Molecular Workshop',
    tagline: 'The cell’s little machines, painted at work around the edges',
    description: 'A gouache picture-book cell: kinesins stroll hand over hand along microtubules with their vesicles, ATP synthases turn like watermills in mitochondrial cristae, ribosomes chug along an mRNA spinning out new chains, TRiC chaperonins open and close, actin treadmills and cargo slips through nuclear pores. By day the page is sunny cream; by night the same world is lit only by the warm lanterns of vesicles and the amber sparks of fresh ATP. Membranes, organelles and the cast slide in and out from the edges on their own random schedules while every machine runs its real, stochastic cycle, so the scene never comes round the same way twice.',
    renderer: 'p2d',
    fps: 24,
    maxStep: 0.1,
    post: { paper: 0.55, grain: 0.22, vignette: 0.3 },
};

const MIDS = ['T', 'B', 'L', 'R'], CORNERS = ['TL', 'TR', 'BL', 'BR'];
const ADJ = { TL: ['T', 'L'], TR: ['T', 'R'], BL: ['B', 'L'], BR: ['B', 'R'] };

export default function sketch(p, h) {
    const G = { p, h, ctx: null, W: 1, H: 1, k: 3, pd: 1, spd: 1, m: 0, t: 0, fa: 1, ox: 0, oy: 0, time: 0, glows: [], S: null };
    G.r = mulberry((h.seed * 2654435761) >>> 0);
    G.glow = (x, y, r, a) => { if (G.m > 0.01 && G.glows.length < 200) G.glows.push(x + G.ox, y + G.oy, r, a * G.fa); };
    let feats = [], pending = [], last = {}, crowd = null, drifters = [];

    // ---------- scene director ----------
    const claimed = () => new Set(feats.filter(f => f.dead < 0).flatMap(f => f.claims));
    const present = () => new Set(feats.map(f => f.kind)); // includes pieces still leaving, so a type never replaces itself
    const minDepth = z => (MIDS.includes(z) ? edgeMap(G, z).budget : 999);
    const placements = {
        pm: free => MIDS.filter(e => EDGE_ZONES[e].every(z => free.has(z)) && edgeMap(G, e).budget > 120).map(e => () => new PlasmaMembrane(G, e)),
        mt: free => MIDS.flatMap(e => [0, 1].filter(s => free.has(EDGE_ZONES[e][s ? 2 : 0]) && free.has(e) && edgeMap(G, e).budget > 150).map(s => () => new Microtubule(G, e, s))),
        mito: free => MIDS.filter(e => free.has(e) && edgeMap(G, e).budget > 170).map(e => () => new Mito(G, e)),
        nucleus: free => ['L', 'R'].filter(m => [m, 'T' + m, 'B' + m].every(z => free.has(z))).map(m => () => new Nucleus(G, m)),
        poly: free => [...CORNERS, ...MIDS].filter(z => free.has(z) && spotOf(G, z).rad > 120).map(z => () => new Polysome(G, z)),
        tric: free => [...CORNERS, ...MIDS].filter(z => free.has(z) && spotOf(G, z).rad > 85).map(z => () => new Chaperonins(G, z)),
    };
    function spawn(prefer, aged = 0) {
        const free = new Set([...CORNERS, ...MIDS]); for (const z of claimed()) free.delete(z);
        const have = present();
        const order = prefer ? [prefer] : Object.keys(placements).filter(t => !have.has(t))
            .map(t => [t, (h.time - (last[t] ?? -400)) + G.r() * 140]).sort((a, b) => b[1] - a[1]).map(a => a[0]);
        for (const t of order) {
            const opts = placements[t](free);
            // try placements in random order; reject any that would overlap another piece (so nothing ends up drawn
            // inside the wrong compartment, e.g. a polysome over a mitochondrion)
            for (let n = opts.length; n > 0; n--) {
                const f = opts.splice((G.r() * n) | 0, 1)[0]();
                if (f.valid === false || feats.some(o => overlaps(boxOf(o), boxOf(f)))) continue;
                f.age = aged; feats.push(f); last[t] = h.time;
                return f;
            }
        }
        return null;
    }
    function boxOf(f) {
        if (f.bb) return f.bb;
        if (f.box) return f.box;
        const sp = f.sp, r = sp.rad * 0.85 + 10;
        return [sp.cx - r, sp.cy - r, 2 * r, 2 * r];
    }
    const overlaps = (a, b) => Math.min(a[0] + a[2], b[0] + b[2]) - Math.max(a[0], b[0]) > 12 && Math.min(a[1] + a[3], b[1] + b[3]) - Math.max(a[1], b[1]) > 12;
    function retireOne() {
        const live = feats.filter(f => f.dead < 0 && f.age > 70);
        if (live.length < 3) return;
        // older pieces are likelier to leave
        let tot = 0; for (const f of live) tot += f.age; let r = G.r() * tot, pick = live[0];
        for (const f of live) { r -= f.age; if (r <= 0) { pick = f; break; } }
        pick.retire();
        pending.push(7 + G.r() * 6);
    }
    function initScene() {
        feats = []; pending = []; last = {};
        const big = ['pm', 'mito', 'nucleus'][(G.r() * 3) | 0];
        spawn(big, 30); spawn('mt', 30); spawn('poly', 30); spawn('tric', 30);
        if (claimed().size <= 6) spawn(null, 30);
    }

    // ---------- background: cytoplasm wash and a sparse painted crowd ----------
    function makeCrowd() {
        const { W, H } = G, f = h.focus, k = G.k;
        crowd = layer(G, 0, 0, W, H, x => {
            // soft pigment pools toward the edges, like a wash laid down before the molecules were painted
            const washes = ['ves', 'crowd', 'mcrowd', 'fold'];
            for (let i = 0; i < 16; i++) {
                const a = hash(i, 31) * TAU, ex = W / 2 + Math.cos(a) * W * 0.62, ey = H / 2 + Math.sin(a) * H * 0.62;
                const px = clamp(ex, -50, W + 50), py = clamp(ey, -50, H + 50), r = Math.min(W, H) * (0.18 + hash(i, 32) * 0.22);
                const g = x.createRadialGradient(px, py, 0, px, py, r), c = rgb(G, washes[i % 4], i);
                g.addColorStop(0, css(c, 0.32)); g.addColorStop(1, css(c, 0));
                x.fillStyle = g; x.fillRect(px - r, py - r, 2 * r, 2 * r);
            }
            const n = Math.round((W * H) / 1100);
            for (let i = 0; i < n; i++) {
                const px = hash(i, 11) * W, py = hash(i, 12) * H;
                const dEdge = Math.min(px, py, W - px, H - py), fo = h.focusFalloff(px, py, 260);
                const keep = Math.pow(clamp(1.05 - dEdge / (Math.min(W, H) * 0.36), 0, 1), 1.6) * (1 - fo);
                if (hash(i, 13) > keep) continue;
                const r = (1.3 + hash(i, 14) * 2.4) * k, lobes = 1 + (hash(i, 15) * 3 | 0), c = rgb(G, 'crowd', i), rot = hash(i, 16) * TAU;
                for (let j = 0; j < lobes; j++) {
                    const ox = Math.cos(rot + j * 2.1) * r * 0.75 * (j ? 1 : 0), oy = Math.sin(rot + j * 2.1) * r * 0.75 * (j ? 1 : 0);
                    gouache(G, x, () => blobPath(x, px + ox, py + oy, r * (j ? 0.75 : 1), r * 0.85 * (j ? 0.75 : 1), rot, i + j, 0.12, 8), c, { lw: 0.5, ln: 0.18, hi: 0.3, hd: 0.45 * k, a: 0.35 + 0.5 * keep });
                }
            }
        });
        crowd.soft = true;
        // free vesicles drifting near the edges (night lanterns)
        drifters = [];
        for (let i = 0; i < 3; i++) {
            const Rv = 17 + G.r() * 8; // 34-50 nm transport vesicles
            drifters.push({ x: 0, y: 0, a: G.r() * TAU, Rv, spr: vesicleSprite(G, Rv, (G.r() * 4) | 0, (G.r() * 999) | 0), seed: (G.r() * 999) | 0, u: G.r() });
        }
        for (const d of drifters) { placeDrifter(d); d.vis = 0; }
    }
    function placeDrifter(d) {
        const { W, H } = G;
        // a point on a loop that hugs the screen perimeter, kept clear of the card
        const inset = 40 + vnoise(h.time / 50, d.seed) * 25, w = W - 2 * inset, hh = H - 2 * inset, per = 2 * (w + hh), s = (d.u % 1) * per; // continuous loop, inset from the edges
        let x, y;
        if (s < w) { x = inset + s; y = inset; } else if (s < w + hh) { x = W - inset; y = inset + s - w; } else if (s < 2 * w + hh) { x = W - inset - (s - w - hh); y = H - inset; } else { x = inset; y = H - inset - (s - 2 * w - hh); }
        d.x = x + vnoise(h.time / 23, d.seed + 3) * 30; d.y = y + vnoise(h.time / 27, d.seed + 4) * 30;
    }

    // ---------- free vesicles: never drawn through anything else ----------
    // Every feature's footprint (its box where it is right now, slide included) plus the free-moving parts that can
    // leave it (kinesin cargo, nascent and released chains, TRiC, prefoldin ...) are obstacles. A vesicle that comes within a
    // margin of one fades out in a fraction of a second, well before contact, and only fades back in once clear;
    // the same holds between the vesicles themselves.
    function obstacles() {
        const B = [], C = [];
        for (const f of feats) {
            if (f.alpha !== undefined && f.alpha <= 0.003) continue;
            const sg = f.kind === 'nucleus' ? -1 : 1, sx = f.out[0] * f.slide * sg, sy = f.out[1] * f.slide * sg, b = boxOf(f);
            B.push([b[0] + sx, b[1] + sy, b[2], b[3]]);
            if (f.extras) for (const c of f.extras()) C.push([c[0] + sx, c[1] + sy, c[2]]);
        }
        return { B, C };
    }
    G.obstacles = obstacles; G.dbg = { feats: () => feats, drifters: () => drifters }; // (inspection hook for offline checks)
    function stepDrifters(dt) {
        const { B, C } = obstacles(), k = G.k, M = 26;
        for (const d of drifters) placeDrifter(d);
        const was = drifters.map(d => d.vis ?? 0);
        drifters.forEach((d, i) => {
            const R = d.Rv * k + M;
            // between two vesicles the more visible one keeps its place (ties: the earlier one); the other fades
            const hit = B.some(b => { const qx = clamp(d.x, b[0], b[0] + b[2]), qy = clamp(d.y, b[1], b[1] + b[3]); return Math.hypot(d.x - qx, d.y - qy) < R; })
                || C.some(c => Math.hypot(d.x - c[0], d.y - c[1]) < R + c[2])
                || drifters.some((o, j) => j !== i && (j < i ? o.vis > 0 && o.vis >= was[i] : was[j] > was[i]) && Math.hypot(d.x - o.x, d.y - o.y) < R + o.Rv * k);
            d.vis = clamp(was[i] + (hit ? -dt / 0.3 : dt / 1.5), 0, 1);
        });
    }

    function setup() {
        G.ctx = p.drawingContext; G.W = p.width; G.H = p.height; G.pd = p.pixelDensity(); G.spd = Math.max(1, G.pd);
        G.k = clamp((Math.min(G.W, G.H) / 900) * 2.5, 1.8, 2.9);
        G.reg = []; G.texCanvas = makeTexture(G.r);
        G.S = makeSprites(G);
        makeCrowd();
        initScene();
        h.every(65, retireOne);
    }

    return {
        _G: G, // (offline checks only)
        setup,
        resize() { setup(); },
        step(dt) {
            G.time = h.time;
            for (const f of feats) f.tick(dt);
            feats = feats.filter(f => f.alive);
            for (let i = pending.length - 1; i >= 0; i--) { pending[i] -= dt; if (pending[i] <= 0) { pending.splice(i, 1); spawn(null); } }
            if (!pending.length && (feats.filter(f => f.dead < 0).length < 3 || (claimed().size < 6 && G.r() < dt / 20))) pending.push(3 + G.r() * 5);
            for (const d of drifters) d.u += dt * 0.0016 * (1 + 0.5 * vnoise(h.time / 60, d.seed));
            stepDrifters(dt);
        },
        render() {
            const ctx = G.ctx, { W, H } = G;
            G.time = h.time; G.m = sm(h.mix); G.t = G.m; G.fa = 1; G.ox = G.oy = 0; G.glows.length = 0;
            ctx.setTransform(G.pd, 0, 0, G.pd, 0, 0);
            ctx.globalAlpha = 1; ctx.globalCompositeOperation = 'source-over';
            // cytoplasm: a warm wash, brightest where the light pools (drifts slowly about the card)
            const sx = W * (0.5 + 0.18 * h.drift('sunx', 240)), sy = H * (0.42 + 0.14 * h.drift('suny', 200));
            const g = ctx.createRadialGradient(sx, sy, 0, W / 2, H / 2, Math.hypot(W, H) * 0.62);
            g.addColorStop(0, css(rgb(G, 'sun'))); g.addColorStop(0.35, css(rgb(G, 'bg0'))); g.addColorStop(1, css(rgb(G, 'bg1')));
            ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
            crowd.draw();
            for (const d of drifters) {
                if (d.vis < 0.01) continue;
                spr(G, d.spr, d.x, d.y, 0.9 * d.vis); G.glow(d.x, d.y, d.Rv * G.k * 3, 0.42 * d.vis);
            }
            for (const f of feats) f.draw();
            // pre-paint the other theme's copy of one sprite or layer per frame, so a live theme switch never stalls
            if (G.reg && G.reg.length && (G.m === 0 || G.m === 1)) { const s = G.reg.pop(); s.get(G.m ? 0 : 1); }
            // night: the lanterns (vesicles) and ATP sparks light the painted world around them
            if (G.m > 0.01 && G.glows.length) {
                ctx.globalCompositeOperation = 'lighter';
                const gl = G.glows, img = G.S.glow;
                for (let i = 0; i < gl.length; i += 4) { const r = gl[i + 2]; ctx.globalAlpha = clamp(gl[i + 3] * G.m * 0.68, 0, 1); ctx.drawImage(img, gl[i] - r, gl[i + 1] - r, 2 * r, 2 * r); }
                ctx.globalCompositeOperation = 'source-over'; ctx.globalAlpha = 1;
            }
        },
    };
}
