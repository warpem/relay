// 14 — Snow globe: hand-modelled proteins suspended in a thin film, like a snow globe on a windowsill.
// Felted figurines of real complexes (apoferritin, GroEL and GroEL-GroES, the 20S proteasome, the 70S ribosome,
// the spike trimer) drift and tumble at random orientations through the water, at different depths, with depth
// of field. Now and then the film is plunged: a glassy sheen sweeps across and a fresh batch fills the globe.
//
// Rendering: raw WebGL2 on p5's context. Meshes are built once (models.js). Each frame the particles are sorted
// into depth-of-field layers by their circle of confusion (far 4..1, sharp, near 1..4); every blurred layer is
// drawn at 1/2..1/16 resolution, Gaussian-blurred and composited back (bicubic) in depth order, and each particle
// is split between its two nearest layers so focus changes are continuous. The sharp layer uses 4x MSAA.
import { buildMeshes, MESH } from './models.js';
import { MESH_VS, MESH_FS, FS_VS, BG_FS, OVER_FS, BLUR_FS, COMP_FS, TILE_VS } from './glsl.js';

export const meta = {
    title: 'Snow Globe',
    tagline: 'Felted proteins tumbling in a thin film of glowing water',
    description: 'Soft, hand-modelled macromolecules (apoferritin cages, GroEL rings with and without their GroES lid, ' +
        'proteasome barrels, two-tone ribosomes and spike trimers) drift and tumble at random orientations through a thin ' +
        'film, in and out of focus. By day it is a snow globe on a winter windowsill, cool light through a window; at night ' +
        'a lamp below warms it and the figurines glow. Brownian tumbling, a slowly breathing focal plane, drifting currents ' +
        'and random plunges (each freezing a fresh film with a new sample), plus the odd hexagonal ice crystal drifting far back, keep it from ever repeating.',
    renderer: 'webgl',
    fps: 30,
    pixelDensity: 1,
    maxStep: 0.1,
    post: { paper: 0.3, grain: 0.32, vignette: 0.32 },
};

const TAU = Math.PI * 2;
const lerp = (a, b, t) => a + (b - a) * t;
const clamp = (v, a = 0, b = 1) => (v < a ? a : v > b ? b : v);
const sstep = (a, b, v) => { const t = clamp((v - a) / (b - a)); return t * t * (3 - 2 * t); };
const hex = (s) => { const n = parseInt(s.replace('#', ''), 16); return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255]; };
const mixv = (a, b, t) => a.map((v, i) => lerp(v, b[i], t));
const norm3 = (v) => { const l = Math.hypot(v[0], v[1], v[2]) || 1; return [v[0] / l, v[1] / l, v[2] / l]; };

// Felt dyes. Each sample dyes its species afresh: the dominant species takes the sample's lead dye (rotated so it
// never repeats, sage only now and then), the others take distinct dyes. [main, light accent, night version]
const DYES = {
    amber: ['#e9a24a', '#f6dca6', '#f0bd78'],
    coral: ['#e2775f', '#f6cdb3', '#ee9a86'],
    cornflower: ['#7aa2d8', '#dbe6f4', '#a3bfeb'],
    mauve: ['#c288bd', '#f1d6e8', '#d9a7d4'],
    honey: ['#efc15c', '#fbe9b8', '#f5d389'],
    rose: ['#e5939f', '#f8dcd6', '#f0b0ba'],
    sage: ['#a6c9b6', '#eef3e8', '#a8d6c6'],   // light and cool, so the backlight can't turn it khaki
};
const LEAD = ['amber', 'coral', 'cornflower', 'mauve', 'honey', 'rose', 'sage'];
const LEAD_W = [1, 1, 1, 1, 1, 0.7, 0.35];
// two-tone complexes: the second part takes a second dye's main colour (ribosome SSU, proteasome alpha rings);
// GroES and glycans take the light accent of the species' own dye
const TWO_TONE = { ribosome: true, proteasome: true };
const SPECIES = ['apoferritin', 'groel', 'proteasome', 'ribosome', 'spike'];

// blur levels (Gaussian sigma in px) and the downsampling of the layer that renders each
const LEVELS = [0, 3.2, 6.4, 12.8, 25.6];
const DOWN = [1, 2, 4, 8, 16];
const NL = 9;   // far4 far3 far2 far1 sharp near1 near2 near3 near4
const GLOW_SIGMA = 12.8, GLOW_LV = 3;
const BASE = 6.4;
const TILE = 48;    // composite tile size (css px)   // px per nm at mid depth on a 1440x900 screen

export default function sketch(p, h) {
    const R = () => p.random();
    const gl = p.drawingContext;
    let meshes = [], progs = {}, fsVao = null, instBuf = null, inst = null, instOrder = null;
    let targets = null;
    let W = 0, H = 0;
    const stir = { x: 0, y: 0, vx: 0, vy: 0, k: 0, lx: null, ly: null };
    let parts = [], batch = null, plunge = null, lastPlunge = -1e9, ice = [0, 0, 0, 0];
    const flow = { modes: [], drift: [0, 0] };
    const motes = [[0, 0], [0, 0], [0, 0]];
    let shaftPh = [R() * TAU, R() * TAU];
    const caus = [R() * 50, R() * 50, R() * 50, R() * 50];

    // ------------------------------------------------------------------------------------- GL plumbing
    function compile(vs, fs) {
        const mk = (type, src) => {
            const s = gl.createShader(type);
            gl.shaderSource(s, src); gl.compileShader(s);
            if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
            return s;
        };
        const pr = gl.createProgram();
        gl.attachShader(pr, mk(gl.VERTEX_SHADER, vs)); gl.attachShader(pr, mk(gl.FRAGMENT_SHADER, fs));
        gl.linkProgram(pr);
        if (!gl.getProgramParameter(pr, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(pr));
        const loc = {};
        const n = gl.getProgramParameter(pr, gl.ACTIVE_UNIFORMS);
        for (let i = 0; i < n; i++) { const u = gl.getActiveUniform(pr, i); loc[u.name.replace(/\[0\]$/, '')] = gl.getUniformLocation(pr, u.name); }
        return { pr, loc };
    }
    function colorTarget(w, ht, depth) {
        const tex = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, ht, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        const fbo = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
        let rb = null;
        if (depth) {
            rb = gl.createRenderbuffer();
            gl.bindRenderbuffer(gl.RENDERBUFFER, rb);
            gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, w, ht);
            gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, rb);
        }
        return { tex, fbo, rb, w, h: ht };
    }
    function msaaTarget(w, ht) {
        const samples = Math.min(2, gl.getParameter(gl.MAX_SAMPLES) || 0);
        const fbo = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
        const c = gl.createRenderbuffer(), d = gl.createRenderbuffer();
        gl.bindRenderbuffer(gl.RENDERBUFFER, c);
        gl.renderbufferStorageMultisample(gl.RENDERBUFFER, samples, gl.RGBA8, w, ht);
        gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.RENDERBUFFER, c);
        gl.bindRenderbuffer(gl.RENDERBUFFER, d);
        gl.renderbufferStorageMultisample(gl.RENDERBUFFER, samples, gl.DEPTH_COMPONENT24, w, ht);
        gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, d);
        return { fbo, rbs: [c, d], w, h: ht };
    }
    function freeTargets() {
        if (!targets) return;
        const all = [targets.msaa, targets.resolve, targets.bg, targets.over, ...targets.lv.flat()];
        for (const t of all) {
            if (!t) continue;
            if (t.tex) gl.deleteTexture(t.tex);
            if (t.rb) gl.deleteRenderbuffer(t.rb);
            if (t.rbs) t.rbs.forEach(r => gl.deleteRenderbuffer(r));
            gl.deleteFramebuffer(t.fbo);
        }
        targets = null;
    }
    function makeTargets() {
        freeTargets();
        const dw = gl.drawingBufferWidth, dh = gl.drawingBufferHeight;
        targets = { dw, dh, msaa: msaaTarget(dw, dh), resolve: colorTarget(dw, dh, false), bg: colorTarget(Math.ceil(dw / 2), Math.ceil(dh / 2), false),
            over: colorTarget(Math.ceil(dw / 2), Math.ceil(dh / 2), false), lv: [null] };
        bgAge = 1e9; overAge = 1e9;
        for (let l = 1; l < LEVELS.length; l++) {
            const w = Math.max(4, Math.round(dw / DOWN[l])), ht = Math.max(4, Math.round(dh / DOWN[l]));
            targets.lv.push([colorTarget(w, ht, true), colorTarget(w, ht, false)]);
        }
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }
    function uploadMeshes(list) {
        instBuf = gl.createBuffer();
        inst = new Float32Array(16 * 512); instOrder = new Float32Array(16 * 512);
        gl.bindBuffer(gl.ARRAY_BUFFER, instBuf);
        gl.bufferData(gl.ARRAY_BUFFER, inst.byteLength, gl.DYNAMIC_DRAW);
        const vaoFor = (m) => {
            const vao = gl.createVertexArray();
            gl.bindVertexArray(vao);
            const vb = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, vb);
            gl.bufferData(gl.ARRAY_BUFFER, m.verts, gl.STATIC_DRAW);
            for (let a = 0; a < 3; a++) { gl.enableVertexAttribArray(a); gl.vertexAttribPointer(a, 3, gl.FLOAT, false, 36, a * 12); }
            const ib = gl.createBuffer();
            gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, ib);
            gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, m.idx, gl.STATIC_DRAW);
            gl.bindBuffer(gl.ARRAY_BUFFER, instBuf);
            for (let a = 3; a < 7; a++) { gl.enableVertexAttribArray(a); gl.vertexAttribDivisor(a, 1); gl.vertexAttribPointer(a, 4, gl.FLOAT, false, 64, (a - 3) * 16); }
            gl.bindVertexArray(null);
            return { vao, n: m.idx.length };
        };
        meshes = list.map(m => ({ full: vaoFor(m), lod: vaoFor(m.lod), radius: m.radius, name: m.name }));
        gl.bindBuffer(gl.ARRAY_BUFFER, null);
    }

    // ------------------------------------------------------------------------------------- scene
    const S = () => clamp(Math.sqrt(W * H) / 1140, 0.7, 1.4);
    function randomQuat() {
        const u1 = R(), u2 = R() * TAU, u3 = R() * TAU, a = Math.sqrt(1 - u1), b = Math.sqrt(u1);
        return [a * Math.sin(u2), a * Math.cos(u2), b * Math.sin(u3), b * Math.cos(u3)];
    }
    function newBatch(prev) {
        // one sample per grid: a main species, with a little of something else
        let main = SPECIES[(R() * SPECIES.length) | 0];
        if (prev && main === prev.main) main = SPECIES[(SPECIES.indexOf(main) + 1 + ((R() * (SPECIES.length - 1)) | 0)) % SPECIES.length];
        // the main species dominates; two to four others share the rest
        const w = {}, others = SPECIES.filter(s => s !== main);
        for (const s of others) w[s] = 0;
        const nOther = 2 + ((R() * 3) | 0);
        for (let i = 0; i < nOther; i++) w[others.splice((R() * others.length) | 0, 1)[0]] = 0.12 + R() * 0.3;
        w[main] = 1;
        // dye the sample
        const lastLead = prev ? prev.lead : null;
        let lead, tot = 0;
        LEAD.forEach((d, k) => { if (d !== lastLead) tot += LEAD_W[k]; });
        let r = R() * tot;
        for (let k = 0; k < LEAD.length; k++) { if (LEAD[k] === lastLead) continue; r -= LEAD_W[k]; if (r <= 0) { lead = LEAD[k]; break; } }
        lead = lead || 'amber';
        const NEAR = { amber: 'honey', honey: 'amber', coral: 'rose', rose: 'coral' };   // too alike to share a sample
        const pool = LEAD.filter(d => d !== lead && d !== 'sage' && d !== NEAR[lead]);
        if (R() < 0.35) pool.push('sage');
        const dyes = { [main]: lead };
        const used = new Set([lead]);
        for (const sp of SPECIES) {
            if (sp === main) continue;
            // prefer a dye with no look-alike already in the sample; fall back only if none is left
            const ok = pool.filter(d => !used.has(NEAR[d]));
            const pick = (ok.length ? ok : pool)[(R() * (ok.length || pool.length)) | 0];
            pool.splice(pool.indexOf(pick), 1);
            dyes[sp] = pick; used.add(pick);
        }
        const second = {};
        for (const sp of SPECIES) { const c = LEAD.filter(d => d !== dyes[sp] && d !== NEAR[dyes[sp]] && d !== 'sage'); second[sp] = c[(R() * c.length) | 0]; }
        return {
            main, w, lead, dyes, second,
            mag: 0.88 + R() * 0.26,
            conc: 0.75 + R() * 0.5,
            groes: 0.15 + R() * 0.55,      // fraction of GroEL with the GroES lid
            up: 0.25 + R() * 0.45,         // fraction of spikes with one RBD up
            awi: R() * 0.3,               // how strongly particles crowd the air-water interfaces
            ice: [R() * 100, R() * 100],
        };
    }
    function pickSpecies(b) {
        let tot = 0; for (const s of SPECIES) tot += b.w[s];
        let r = R() * tot;
        for (const s of SPECIES) { r -= b.w[s]; if (r <= 0) return s; }
        return b.main;
    }
    function meshFor(sp, b) {
        if (sp === 'groel') return R() < b.groes ? MESH.groes : MESH.groel;
        if (sp === 'spike') return R() < b.up ? MESH.spikeUp : MESH.spike;
        return MESH[sp];
    }
    function placeParticle(b, born, kind) {
        const sp = pickSpecies(b), m = meshFor(sp, b);
        let z;
        if (kind === 'near') z = 1.12 + R() * 0.35;
        else if (kind === 'far') z = -0.45 + R() * 0.3;
        else if (R() < b.awi) z = (R() < 0.5 ? 0.12 : 0.88) + (R() - 0.5) * 0.08;   // crowding at an air-water interface
        else z = clamp(0.5 + p.randomGaussian() * 0.2, -0.05, 1.05);                 // most sit in the thin middle of the film
        const M = 260;
        let x = 0, y = 0;
        for (let k = 0; k < 24; k++) {
            x = -M * 0.5 + R() * (W + M); y = -M * 0.5 + R() * (H + M);
            const pad = kind === 'near' ? 440 : 290;
            if (R() < 0.94 * h.focusFalloff(x, y, pad)) continue;
            let crowd = false;
            for (const q of parts) {
                if (q.die < Infinity || Math.abs(q.z - z) > 0.3) continue;
                const d = Math.hypot(q.x - x, q.y - y);
                if (d < (q.rad + meshes[m].radius) * 5.2 * S() * 0.9) { crowd = true; break; }
            }
            if (!crowd) break;
        }
        const D = DYES[b.dyes[sp]], D2 = DYES[b.second[sp]];
        const c = [hex(D[0]), hex(TWO_TONE[sp] ? D2[0] : sp === 'apoferritin' ? D[0] : D[1])];
        const cd = [hex(D[2]), hex(TWO_TONE[sp] ? D2[2] : sp === 'apoferritin' ? D[2] : D[1])], jit = 1 + (R() - 0.5) * 0.1, hue = (R() - 0.5) * 0.04;
        const tint = (v) => [clamp(v[0] * jit + hue), clamp(v[1] * jit), clamp(v[2] * jit - hue)];
        const rad = meshes[m].radius;
        return {
            m, sp, x, y, z, z0: z, vx: 0, vy: 0, vz: 0, q: randomQuat(), w: [0, 0, 0],
            rotSig: 0.085 * Math.pow(8 / rad, 1.2), rad,
            colA: tint(c[0]), colB: tint(c[1]), colAd: tint(cd[0]), colBd: tint(cd[1]), born, die: Infinity, seed: R(),
        };
    }
    function populate(b, born, bornFn) {
        // fill to a target coverage of the film, so a batch of ribosomes has fewer particles than one of apoferritin
        const M = 260, field = (W + M) * (H + M), sc = S() * BASE * b.mag;
        const target = field * 0.15 * b.conc;
        const out = [];
        const add = (kind) => { const q = placeParticle(b, born, kind); if (bornFn) q.born = bornFn(q); parts.push(q); out.push(q); return q; };
        let cov = 0;
        for (let i = 0; i < 34 && cov < target; i++) { const q = add('mid'); const r = q.rad * sc * 0.8; cov += Math.PI * r * r; }
        const nn = 1 + ((R() * 3) | 0), nf = 3 + ((R() * 4) | 0);
        for (let i = 0; i < nn; i++) add('near');
        for (let i = 0; i < nf; i++) add('far');
        return out;
    }
    function frontReach(x, y) {
        // time at which the plunge front reaches (x, y)
        return plunge.t0 + (x * plunge.dx + y * plunge.dy - plunge.f0) / plunge.v;
    }
    function startPlunge() {
        if (plunge || h.time - lastPlunge < 100) return;
        const a = R() * TAU, dx = Math.cos(a), dy = Math.sin(a);
        const proj = [[0, 0], [W, 0], [0, H], [W, H]].map(([x, y]) => x * dx + y * dy);
        const f0 = Math.min(...proj) - 380, f1 = Math.max(...proj) + 380;
        plunge = { t0: h.time, dx, dy, f0, f1, v: 125 + R() * 50 };
        plunge.t1 = plunge.t0 + (f1 - f0) / plunge.v;
        lastPlunge = h.time;
        for (const q of parts) if (q.die === Infinity) q.die = frontReach(q.x, q.y) - 0.5 + R() * 1.5;
        const prev = batch;
        batch = newBatch(prev);
        ice = [ice[2], ice[3], batch.ice[0], batch.ice[1]];
        populate(batch, 0, (q) => frontReach(q.x, q.y) + 0.8 + R() * 2.5);
    }

    // now and then a hexagonal ice crystal (the bane of every grid) drifts through, far back in the film
    function spawnIce() {
        if (parts.some(q => q.sp === 'ice')) return;
        const q = placeParticle(batch, h.time, 'far');
        q.sp = 'ice'; q.m = MESH.ice; q.rad = meshes[MESH.ice].radius;
        q.z = q.z0 = -0.32 + R() * 0.2;
        q.colA = hex('#dce8ee'); q.colB = q.colA; q.colAd = hex('#e6e2f0'); q.colBd = q.colAd; q.rotSig = 0.025;
        q.die = h.time + 140 + R() * 120;
        for (let k = 0; k < 20 && h.focusFalloff(q.x, q.y, 380) > 0; k++) { q.x = R() * W; q.y = R() * H; }
        parts.push(q);
    }
    function stepFlow(dt) {
        const s = S();
        const strength = (5 + 4.5 * (h.drift('current', 260) + 1)) * s;
        for (let i = 0; i < flow.modes.length; i++) {
            const md = flow.modes[i];
            const ang = md.a0 + 1.4 * h.drift('kdir' + i, 520);
            md.kx = Math.cos(ang) * md.k; md.ky = Math.sin(ang) * md.k;
            md.ph += dt * md.om * (1 + 0.6 * h.drift('kom' + i, 300));
            md.amp = strength * md.wt / md.k;
        }
        flow.drift = [3.2 * s * h.drift('gdx', 700), 2.2 * s * h.drift('gdy', 640) + 1.0 * s];
    }
    function flowAt(x, y) {
        let u = flow.drift[0], v = flow.drift[1];
        for (const md of flow.modes) {
            const c = Math.cos(md.kx * x + md.ky * y + md.ph) * md.amp;
            u += md.ky * c; v -= md.kx * c;
        }
        return [u, v];
    }

    // ------------------------------------------------------------------------------------- theme colours
    const TH = {
        light: {
            top: hex('#f7e5c6'), mid: hex('#ead7bd'), bot: hex('#aebfcc'),
            sun: [0.13, 0.095, 0.045], lamp: [0, 0, 0],
            fog: hex('#ecdcc6'), veil: hex('#efe4d6'),
            L: norm3([-0.45, 0.6, 0.1]), key: [0.7, 0.6, 0.47], sky: [0.46, 0.48, 0.58], gnd: [0.5, 0.4, 0.32],
            sheen: [0.35, 0.32, 0.28], glow: [1.0, 0.82, 0.55], glowK: 0.22, fogK: 0.26, lift: 0,
            rim: [1.0, 0.9, 0.68], rimK: 0.95,
            mote: hex('#fff6e2'), moteK: 0.8, moteGlow: 0.35, iceK: 0.08, shaft: 1, caus: 0.16,
        },
        dark: {
            top: hex('#16131c'), mid: hex('#1f1922'), bot: hex('#33231f'),
            sun: [0, 0, 0], lamp: [0.34, 0.2, 0.1],
            fog: hex('#251b22'), veil: hex('#2a2127'),
            L: norm3([0.18, -0.82, 0.5]), key: [0.98, 0.86, 0.72], sky: [0.3, 0.32, 0.5], gnd: [0.46, 0.36, 0.28],
            sheen: [0.8, 0.64, 0.5], glow: [1.0, 0.8, 0.6], glowK: 0.5, fogK: 0.6, lift: 0.22,
            rim: [1.0, 0.75, 0.5], rimK: 0, caus: 0,
            mote: hex('#ffcf8a'), moteK: 0.55, moteGlow: 1, iceK: 0.12, shaft: 0,
        },
    };
    const th = (k) => {
        const a = TH.light[k], b = TH.dark[k], t = h.mix * h.mix * (3 - 2 * h.mix);
        return typeof a === 'number' ? lerp(a, b, t) : mixv(a, b, t);
    };

    // ------------------------------------------------------------------------------------- render helpers
    const groups = Array.from({ length: NL + 1 }, () => []);   // the DOF layers, plus one glow layer
    let nInst = 0;
    let tileVao = null, tileBuf = null, tileData = null, tileMark = null, tileStamp = 0, tileCols = 0, tileRows = 0;
    function makeTiles() {
        tileCols = Math.ceil(W / TILE); tileRows = Math.ceil(H / TILE);
        tileMark = new Uint32Array(tileCols * tileRows);
        tileData = new Float32Array(tileCols * tileRows * 2 * (NL + 1));
        if (!tileBuf) {
            tileBuf = gl.createBuffer();
            tileVao = gl.createVertexArray();
            gl.bindVertexArray(tileVao);
            gl.bindBuffer(gl.ARRAY_BUFFER, tileBuf);
            gl.enableVertexAttribArray(0); gl.vertexAttribDivisor(0, 1);
            gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 8, 0);
            gl.bindVertexArray(null);
        }
        gl.bindBuffer(gl.ARRAY_BUFFER, tileBuf);
        gl.bufferData(gl.ARRAY_BUFFER, tileData.byteLength, gl.DYNAMIC_DRAW);
        gl.bindBuffer(gl.ARRAY_BUFFER, null);
    }
    // mark the tiles each layer's particles (with their blur) cover; layers get disjoint tile lists
    function buildTiles() {
        let n = 0;
        for (const g of groups) {
            tileStamp++;
            g.tile0 = n;
            for (const e of g) {
                const tx0 = Math.max(0, Math.floor((e.x - e.r) / TILE)), tx1 = Math.min(tileCols - 1, Math.floor((e.x + e.r) / TILE));
                const ty0 = Math.max(0, Math.floor((e.y - e.r) / TILE)), ty1 = Math.min(tileRows - 1, Math.floor((e.y + e.r) / TILE));
                for (let ty = ty0; ty <= ty1; ty++) for (let tx = tx0; tx <= tx1; tx++) {
                    // skip the corners of the bounding box that the round footprint cannot reach
                    const cx = Math.max(tx * TILE, Math.min(e.x, tx * TILE + TILE)), cy = Math.max(ty * TILE, Math.min(e.y, ty * TILE + TILE));
                    if ((cx - e.x) * (cx - e.x) + (cy - e.y) * (cy - e.y) > e.r * e.r) continue;
                    const k = ty * tileCols + tx;
                    if (tileMark[k] === tileStamp) continue;
                    tileMark[k] = tileStamp;
                    tileData[n * 2] = tx * TILE; tileData[n * 2 + 1] = ty * TILE; n++;
                }
            }
            g.tiles = n - g.tile0;
        }
        gl.bindBuffer(gl.ARRAY_BUFFER, tileBuf);
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, tileData, 0, n * 2);
        gl.bindBuffer(gl.ARRAY_BUFFER, null);
    }
    function pushInst(layer, m, q, alpha, x, y, z, scl) {
        if (nInst >= 512) return;
        const o = nInst * 16;
        inst[o] = x; inst[o + 1] = y; inst[o + 2] = z; inst[o + 3] = scl;
        inst[o + 4] = q.q[0]; inst[o + 5] = q.q[1]; inst[o + 6] = q.q[2]; inst[o + 7] = q.q[3];
        const A = q.colA, B = q.colB, Ad = q.colAd, Bd = q.colBd, mk = mixE;
        inst[o + 8] = A[0] + (Ad[0] - A[0]) * mk; inst[o + 9] = A[1] + (Ad[1] - A[1]) * mk; inst[o + 10] = A[2] + (Ad[2] - A[2]) * mk; inst[o + 11] = alpha;
        inst[o + 12] = B[0] + (Bd[0] - B[0]) * mk; inst[o + 13] = B[1] + (Bd[1] - B[1]) * mk; inst[o + 14] = B[2] + (Bd[2] - B[2]) * mk; inst[o + 15] = q.seed;
        const r = q.rad * scl + 2.6 * (layer === NL ? GLOW_SIGMA : LEVELS[Math.abs(layer - 4)]) + 4, bb = groups[layer].bb;
        groups[layer].push({ m, i: nInst, x, y, r });
        bb[0] = Math.min(bb[0], x - r); bb[1] = Math.min(bb[1], y - r); bb[2] = Math.max(bb[2], x + r); bb[3] = Math.max(bb[3], y + r);
        nInst++;
    }
    function particleState(q, t) {
        if (q.sp === 'ice') {
            const a = sstep(q.born, q.born + 14, t) * (1 - sstep(q.die, q.die + 14, t)) * lerp(0.32, 0.55, h.mix);
            return [a, q.z];
        }
        const ein = sstep(q.born, q.born + 5, t), eout = q.die === Infinity ? 0 : sstep(q.die, q.die + 4, t);
        const a = Math.min(ein, 1 - eout);
        // fresh particles settle out of the depths; old ones sink away into the blur
        const z = q.z - 0.45 * (1 - ein) - 0.5 * eout;
        return [a, z];
    }
    let glowOn = false, mixE = 0;
    function buildInstances() {
        mixE = h.mix * h.mix * (3 - 2 * h.mix);
        glowOn = h.mix > 0.02;
        for (const g of groups) { g.length = 0; g.bb = [1e9, 1e9, -1e9, -1e9]; }
        nInst = 0;
        const t = h.time, zf = 0.5 + 0.22 * h.drift('focus', 160) + 0.05 * h.drift('focus2', 41);
        const K = 19, s = S() * BASE * batchMag();
        for (const q of parts) {
            let [a, z] = particleState(q, t);
            if (a <= 0.003) continue;
            const scl = s * (0.72 + 0.42 * Math.min(z, 1.2));
            const r = q.rad * scl;
            if (q.x < -r - 60 || q.x > W + r + 60 || q.y < -r - 60 || q.y > H + r + 60) continue;
            const c = Math.min(K * Math.abs(z - zf), LEVELS[4]);
            // big out-of-focus blobs stay light near the card
            if (c > LEVELS[2]) a *= 1 - 0.7 * h.focusFalloff(q.x, q.y, 300) * sstep(LEVELS[2], LEVELS[3], c);
            let l = 0;
            while (l < 3 && c > LEVELS[l + 1]) l++;
            const f = clamp((c - LEVELS[l]) / (LEVELS[l + 1] - LEVELS[l]));
            const far = z < zf;
            const idx = (lv) => (lv === 0 ? 4 : far ? 4 - lv : 4 + lv);
            if (1 - f > 0.01) pushInst(idx(l), q.m, q, a * (1 - f), q.x, q.y, z, scl);
            if (f > 0.01) pushInst(idx(l + 1), q.m, q, a * f, q.x, q.y, z, scl);
            if (glowOn && c < LEVELS[3]) pushInst(NL, q.m, q, a * (1 - c / LEVELS[3]), q.x, q.y, z, scl);
        }
        // order each layer's draws by mesh so the attribute pointers are set once per mesh
        const order = instOrder;
        let k = 0;
        for (const g of groups) {
            g.sort((u, v) => u.m - v.m);
            const runs = [];
            for (const e of g) {
                order.set(inst.subarray(e.i * 16, e.i * 16 + 16), k * 16);
                if (runs.length && runs[runs.length - 1].m === e.m) runs[runs.length - 1].n++;
                else runs.push({ m: e.m, start: k, n: 1 });
                k++;
            }
            g.runs = runs;
        }
        gl.bindBuffer(gl.ARRAY_BUFFER, instBuf);
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, order, 0, nInst * 16);
        gl.bindBuffer(gl.ARRAY_BUFFER, null);
        buildTiles();
    }
    let magNow = 1;
    const batchMag = () => magNow;

    function setMeshUniforms(P) {
        const L = th('L');
        gl.uniform2f(P.loc.uRes, W, H);
        gl.uniform3fv(P.loc.uL, norm3(L));
        gl.uniform3fv(P.loc.uKey, th('key'));
        gl.uniform3fv(P.loc.uSkyA, th('sky'));
        gl.uniform3fv(P.loc.uGndA, th('gnd'));
        gl.uniform3fv(P.loc.uSheen, th('sheen'));
        gl.uniform3fv(P.loc.uGlow, th('glow'));
        gl.uniform1f(P.loc.uGlowK, th('glowK'));
        gl.uniform3fv(P.loc.uFog, th('fog'));
        gl.uniform1f(P.loc.uFogK, th('fogK'));
        gl.uniform3fv(P.loc.uVeil, th('veil'));
        gl.uniform1f(P.loc.uVeilK, lerp(0.5, 0.62, h.mix));
        gl.uniform4f(P.loc.uFocus, h.focus.cx, h.focus.cy, h.focus.w / 2, h.focus.h / 2);
        gl.uniform1f(P.loc.uFibre, 5.5);
        gl.uniform1f(P.loc.uLift, th('lift'));
        gl.uniform3fv(P.loc.uRimCol, th('rim'));
        gl.uniform1f(P.loc.uRimK, th('rimK'));
        gl.uniform2f(P.loc.uRimDir, -0.62, 0.78);
    }
    function drawLayer(li, shells, lod) {
        const P = progs.mesh;
        const g = groups[li];
        for (const pass of shells) {
            gl.uniform1f(P.loc.uShell, pass[0]);
            gl.uniform1f(P.loc.uShellA, pass[1]);
            gl.depthMask(pass[1] === 0);
            for (const run of g.runs) {
                const M = lod ? meshes[run.m].lod : meshes[run.m].full;
                gl.bindVertexArray(M.vao);
                gl.bindBuffer(gl.ARRAY_BUFFER, instBuf);
                for (let a = 3; a < 7; a++) gl.vertexAttribPointer(a, 4, gl.FLOAT, false, 64, run.start * 64 + (a - 3) * 16);
                gl.drawElementsInstanced(gl.TRIANGLES, M.n, gl.UNSIGNED_INT, 0, run.n);
            }
        }
        gl.depthMask(true);
    }
    // scissor to a layer's bounding box (css px) on a target of the given size
    function scissor(bb, tw, th) {
        const kx = tw / W, ky = th / H;
        const x0 = Math.max(0, Math.floor(bb[0] * kx) - 1), x1 = Math.min(tw, Math.ceil(bb[2] * kx) + 1);
        const y0 = Math.max(0, Math.floor((H - bb[3]) * ky) - 1), y1 = Math.min(th, Math.ceil((H - bb[1]) * ky) + 1);
        gl.enable(gl.SCISSOR_TEST);
        gl.scissor(x0, y0, Math.max(0, x1 - x0), Math.max(0, y1 - y0));
        return [x0, y0, x1, y1];
    }
    function fsPass(P, target) {
        gl.bindFramebuffer(gl.FRAMEBUFFER, target ? target.fbo : null);
        gl.viewport(0, 0, target ? target.w : targets.dw, target ? target.h : targets.dh);
        gl.useProgram(P.pr);
        gl.bindVertexArray(fsVao);
    }
    function composite(g, tex, cubic, gain = 1, additive = false) {
        if (!g.tiles) return;
        const P = progs.tile;
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        gl.viewport(0, 0, targets.dw, targets.dh);
        gl.useProgram(P.pr);
        gl.bindVertexArray(tileVao);
        gl.bindBuffer(gl.ARRAY_BUFFER, tileBuf);
        gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 8, g.tile0 * 8);
        gl.enable(gl.BLEND);
        if (additive) gl.blendFuncSeparate(gl.ONE, gl.ONE, gl.ZERO, gl.ONE);
        else gl.blendFuncSeparate(gl.ONE, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.uniform1i(P.loc.uTex, 0); gl.uniform1f(P.loc.uCubic, cubic ? 1 : 0); gl.uniform3fv(P.loc.uGain, typeof gain === 'number' ? [gain, gain, gain] : gain);
        gl.uniform2f(P.loc.uRes, W, H); gl.uniform1f(P.loc.uTileSize, TILE);
        gl.drawArraysInstanced(gl.TRIANGLES, 0, 6, g.tiles);
    }

    // the water changes slowly: render it into a cache every few frames (every frame while the theme crossfades)
    let bgAge = 1e9, bgMix = -1;
    function drawBackground() {
        bgAge++;
        if (bgAge >= 3 || h.mix !== bgMix) { renderBackground(); bgAge = 0; bgMix = h.mix; }
        const P = progs.comp;
        fsPass(P, null);
        gl.disable(gl.BLEND);
        gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, targets.bg.tex);
        gl.uniform1i(P.loc.uTex, 0); gl.uniform1f(P.loc.uCubic, 0); gl.uniform3fv(P.loc.uGain, [1, 1, 1]);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    }
    function renderBackground() {
        const P = progs.bg;
        fsPass(P, targets.bg);
        gl.disable(gl.SCISSOR_TEST);
        gl.disable(gl.BLEND); gl.disable(gl.DEPTH_TEST);
        gl.uniform2f(P.loc.uRes, W, H);
        gl.uniform1f(P.loc.uMix, h.mix);
        gl.uniform3fv(P.loc.uTop, th('top')); gl.uniform3fv(P.loc.uMidC, th('mid')); gl.uniform3fv(P.loc.uBot, th('bot'));
        const sunWarm = 1 + 0.35 * h.drift('sunwarm', 600);
        const sc = th('sun');
        gl.uniform3fv(P.loc.uSunCol, [sc[0] * sunWarm, sc[1], sc[2] / sunWarm]);
        gl.uniform3f(P.loc.uSun, W * (0.14 + 0.05 * h.drift('sunx', 900)), H * (0.06 + 0.04 * h.drift('suny', 800)), Math.max(W, H) * 0.42);
        const lampI = 1 + 0.12 * h.drift('lampI', 120);
        gl.uniform3fv(P.loc.uLampCol, th('lamp').map(v => v * lampI));
        gl.uniform3f(P.loc.uLamp, W * (0.5 + 0.06 * h.drift('lampx', 700)), H * 1.08, Math.min(W, H) * 0.62);
        gl.uniform4fv(P.loc.uIce, ice);
        gl.uniform1f(P.loc.uIceK, th('iceK'));
        frontUniform(P.loc.uFront);
        gl.uniform4f(P.loc.uShaft, 1.05 + 0.15 * h.drift('shaftA', 500), shaftPh[0], shaftPh[1], th('shaft'));
        gl.uniform4f(P.loc.uMoteA, motes[0][0], motes[0][1], motes[1][0], motes[1][1]);
        gl.uniform4f(P.loc.uMoteB, motes[2][0], motes[2][1], (h.time * 7.31) % 1, 0);
        gl.uniform3fv(P.loc.uMoteCol, th('mote'));
        gl.uniform1f(P.loc.uMoteK, th('moteK'));
        gl.uniform1f(P.loc.uMoteGlow, th('moteGlow'));
        gl.uniform4f(P.loc.uCaus, caus[0], caus[1], caus[2], caus[3]);
        gl.uniform1f(P.loc.uCausK, th('caus') * (0.75 + 0.25 * h.drift('causK', 300)));
        gl.uniform4f(P.loc.uFocus, h.focus.cx, h.focus.cy, h.focus.w / 2, h.focus.h / 2);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    }
    function frontUniform(loc) {
        if (!plunge) { gl.uniform4f(loc, 1, 0, -1e5, 0); return; }
        const f = plunge.f0 + (h.time - plunge.t0) * plunge.v;
        gl.uniform4f(loc, plunge.dx, plunge.dy, f, 1);
    }
    // reflections and the plunge sheen: soft, so they are rendered at half resolution every few frames
    let overAge = 1e9, overMix = -1, overBoxes = [];
    function drawOverlay() {
        overAge++;
        if (overAge >= (plunge ? 2 : 3) || h.mix !== overMix) { renderOverlay(); overAge = 0; overMix = h.mix; }
        const P = progs.comp;
        fsPass(P, null);
        gl.enable(gl.BLEND); gl.blendFuncSeparate(gl.ONE, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, targets.over.tex);
        gl.uniform1i(P.loc.uTex, 0); gl.uniform1f(P.loc.uCubic, 0); gl.uniform3fv(P.loc.uGain, [1, 1, 1]);
        for (const b of overBoxes) { scissor(b, targets.dw, targets.dh); gl.drawArrays(gl.TRIANGLES, 0, 3); }
        gl.disable(gl.SCISSOR_TEST);
    }
    function renderOverlay() {
        const P = progs.over;
        fsPass(P, targets.over);
        gl.disable(gl.SCISSOR_TEST);
        gl.clearColor(0, 0, 0, 0); gl.clear(gl.COLOR_BUFFER_BIT);
        gl.disable(gl.BLEND);
        gl.uniform2f(P.loc.uRes, W, H);
        gl.uniform1f(P.loc.uMix, h.mix);
        frontUniform(P.loc.uFront);
        let fk = 0;
        if (plunge) fk = sstep(plunge.t0, plunge.t0 + 2, h.time) * (1 - sstep(plunge.t1 - 3, plunge.t1, h.time));
        gl.uniform1f(P.loc.uFrontK, fk * lerp(0.2, 0.16, h.mix));
        gl.uniform3fv(P.loc.uVeil, th('veil'));
        gl.uniform1f(P.loc.uVeilK, 0);   // particles are veiled in their own shader
        const s = S(), m = h.mix * h.mix * (3 - 2 * h.mix);
        gl.uniform4f(P.loc.uArc, -2.45, 0.42, lerp(0.12, 0.06, m), 0);
        gl.uniform3fv(P.loc.uArcCol, mixv([1, 1, 1], [1.0, 0.78, 0.5], m));
        gl.uniform4f(P.loc.uFocus, h.focus.cx, h.focus.cy, h.focus.w / 2, h.focus.h / 2);
        // draw only where something is: the whole screen during a plunge, otherwise each reflection's box
        const boxes = [];
        if (fk > 0) boxes.push([0, 0, W, H]);
        else {
            const ax = [], ay = [];
            for (let a = -2.87; a <= -2.02; a += 0.085) { ax.push(W / 2 + W * 0.62 * 0.93 * Math.cos(a)); ay.push(H / 2 + H * 0.78 * 0.93 * Math.sin(a)); }
            boxes.push([Math.min(...ax) - 40, Math.min(...ay) - 40, Math.max(...ax) + 40, Math.max(...ay) + 40]);
        }
        for (const b of boxes) { scissor(b, targets.over.w, targets.over.h); gl.drawArrays(gl.TRIANGLES, 0, 3); }
        gl.disable(gl.SCISSOR_TEST);
        overBoxes = boxes;
    }

    function restore() {
        gl.bindVertexArray(null);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        gl.bindBuffer(gl.ARRAY_BUFFER, null);
        gl.useProgram(null);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, null);
        gl.depthMask(true);
        gl.disable(gl.SCISSOR_TEST);
        gl.enable(gl.BLEND); gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
        gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LEQUAL);
        gl.viewport(0, 0, gl.drawingBufferWidth, gl.drawingBufferHeight);
    }

    // ------------------------------------------------------------------------------------- contract
    return {
        setup() {
            if (typeof WebGL2RenderingContext === 'undefined' || !(gl instanceof WebGL2RenderingContext)) throw new Error('Snow globe needs WebGL2');
            W = p.width; H = p.height;
            progs.mesh = compile(MESH_VS, MESH_FS);
            progs.bg = compile(FS_VS, BG_FS);
            progs.over = compile(FS_VS, OVER_FS);
            progs.blur = compile(FS_VS, BLUR_FS);
            progs.tile = compile(TILE_VS, COMP_FS);
            progs.comp = compile(FS_VS, COMP_FS);
            fsVao = gl.createVertexArray();
            uploadMeshes(buildMeshes(R));
            makeTargets();
            makeTiles();
            restore();
            for (let i = 0; i < 4; i++) {
                const wl = [1700, 1150, 820, 2400][i];
                flow.modes.push({ k: TAU / wl, a0: R() * TAU, ph: R() * TAU, om: (R() < 0.5 ? -1 : 1) * (0.012 + R() * 0.02), wt: [0.5, 0.32, 0.22, 0.4][i] });
            }
            batch = newBatch(null);
            ice = [batch.ice[0], batch.ice[1], batch.ice[0], batch.ice[1]];
            magNow = batch.mag;
            populate(batch, -1e9);
            stepFlow(0);
            h.every(230, startPlunge);
            h.every(420, spawnIce);
        },

        step(dt) {
            const t = h.time;
            // pointer: velocity of recent movement, decaying
            const mx = p.mouseX, my = p.mouseY;
            if (mx !== stir.lx || my !== stir.ly) {
                if (stir.lx !== null && mx >= 0 && my >= 0 && mx <= W && my <= H && dt > 0) {
                    const vx = clamp((mx - stir.lx) / dt, -600, 600), vy = clamp((my - stir.ly) / dt, -600, 600);
                    stir.vx = lerp(stir.vx, vx, 0.5); stir.vy = lerp(stir.vy, vy, 0.5); stir.k = 1;
                }
                stir.x = mx; stir.y = my; stir.lx = mx; stir.ly = my;
            }
            stir.k *= Math.exp(-dt / 1.2);
            stepFlow(dt);
            magNow += (batch.mag - magNow) * Math.min(1, dt / 40);
            const s = S();
            const sv = Math.sqrt(dt);
            const f = h.focus;
            for (const q of parts) {
                // Brownian jitter (Ornstein-Uhlenbeck velocity) on top of the slow currents
                q.vx += -q.vx * dt / 4 + p.randomGaussian() * 1.6 * s * sv;
                q.vy += -q.vy * dt / 4 + p.randomGaussian() * 1.6 * s * sv;
                q.vz += -q.vz * dt / 30 + p.randomGaussian() * 0.0025 * sv - (q.z - q.z0) * dt * 0.01;
                const [u, v] = flowAt(q.x, q.y);
                const depthK = 0.6 + 0.4 * clamp(q.z, -0.5, 1.5);
                let ax = 0, ay = 0;
                // keep the card's surroundings calm: a soft push outward
                const ff = h.focusFalloff(q.x, q.y, q.z > 1.1 ? 240 : 170);
                if (ff > 0 && q.z > -0.1) {
                    const dx = q.x - f.cx, dy = q.y - f.cy, dl = Math.hypot(dx, dy) || 1;
                    ax = dx / dl * ff * 24 * s; ay = dy / dl * ff * 24 * s;
                }
                // a gentle stir where the pointer moves
                if (stir.k > 0.01) {
                    const dx = q.x - stir.x, dy = q.y - stir.y, d2 = dx * dx + dy * dy, rr = 190 * s;
                    if (d2 < rr * rr * 4) { const w = Math.exp(-d2 / (rr * rr)) * stir.k; ax += stir.vx * w * 0.35; ay += stir.vy * w * 0.35; }
                }
                q.x += (u * depthK + q.vx + ax) * dt;
                q.y += (v * depthK + q.vy + ay) * dt;
                q.z += q.vz * dt;
                const M = 260;
                if (q.x < -M) q.x += W + 2 * M; else if (q.x > W + M) q.x -= W + 2 * M;
                if (q.y < -M) q.y += H + 2 * M; else if (q.y > H + M) q.y -= H + 2 * M;
                // rotational diffusion: bigger complexes tumble more slowly
                const w = q.w, rs = q.rotSig * sv * 0.55;
                for (let a = 0; a < 3; a++) w[a] += -w[a] * dt / 6 + p.randomGaussian() * rs;
                const [x, y, z, qw] = q.q, hd = 0.5 * dt;
                const nx = x + hd * (w[0] * qw + w[1] * z - w[2] * y);
                const ny = y + hd * (-w[0] * z + w[1] * qw + w[2] * x);
                const nz = z + hd * (w[0] * y - w[1] * x + w[2] * qw);
                const nw = qw + hd * (-w[0] * x - w[1] * y - w[2] * z);
                const il = 1 / Math.hypot(nx, ny, nz, nw);
                q.q[0] = nx * il; q.q[1] = ny * il; q.q[2] = nz * il; q.q[3] = nw * il;
            }
            parts = parts.filter(q => q.die === Infinity || t < q.die + (q.sp === 'ice' ? 15 : 4.5));
            if (plunge && t > plunge.t1) plunge = null;
            // motes drift with the current, snow settles slowly
            const md = [flow.drift[0] * 0.6, flow.drift[1] * 0.6 + 2.5 * s];
            const sp = [0.7, 1.0, 0.45], period = [74 * 64, 118 * 64, 53 * 64];   // the mote lattice repeats every 64 cells
            for (let i = 0; i < 3; i++) {
                motes[i][0] = (motes[i][0] - md[0] * sp[i] * dt) % period[i];
                motes[i][1] = (motes[i][1] - md[1] * sp[i] * dt) % period[i];
            }
            // caustic ripples drift slowly (about 0.02 lattice units/s, so the wrap at 1000 comes after many hours)
            caus[0] = (caus[0] + dt * 0.022 * (1 + 0.5 * h.drift('c0', 200))) % 1000; caus[1] = (caus[1] + dt * 0.012) % 1000;
            caus[2] = (caus[2] - dt * 0.017) % 1000; caus[3] = (caus[3] + dt * 0.02 * (1 + 0.5 * h.drift('c1', 230))) % 1000;
            shaftPh[0] = (shaftPh[0] + dt * 0.05 * (1 + h.drift('sh0', 200))) % TAU;
            shaftPh[1] = (shaftPh[1] - dt * 0.031 * (1 + h.drift('sh1', 260))) % TAU;
        },

        render() {
            if (gl.drawingBufferWidth !== targets.dw || gl.drawingBufferHeight !== targets.dh) makeTargets();
            buildInstances();
            drawBackground();
            const P = progs.mesh;
            for (let li = 0; li <= NL; li++) {
                if (!groups[li].runs.length) continue;
                const glowPass = li === NL;
                const lv = glowPass ? GLOW_LV : Math.abs(li - 4);
                const bb = groups[li].bb;
                const T = lv === 0 ? targets.msaa : targets.lv[lv][0];
                gl.bindFramebuffer(gl.FRAMEBUFFER, T.fbo);
                gl.viewport(0, 0, T.w, T.h);
                if (lv === 0) scissor(bb, T.w, T.h); else gl.disable(gl.SCISSOR_TEST);
                gl.clearColor(0, 0, 0, 0); gl.clearDepth(1);
                gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
                gl.enable(gl.DEPTH_TEST); gl.depthFunc(gl.LESS);
                gl.enable(gl.BLEND); gl.blendFuncSeparate(gl.ONE, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
                gl.useProgram(P.pr);
                setMeshUniforms(P);
                const shells = lv === 0 && !glowPass ? [[0, 0], [0.3, 0.6]] : [[0, 0]];   // core + one felt fuzz shell
                drawLayer(li, shells, lv >= 2);
                gl.disable(gl.DEPTH_TEST);
                if (lv === 0) {
                    const [x0, y0, x1, y1] = scissor(bb, T.w, T.h);
                    gl.bindFramebuffer(gl.READ_FRAMEBUFFER, targets.msaa.fbo);
                    gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, targets.resolve.fbo);
                    gl.blitFramebuffer(x0, y0, x1, y1, x0, y0, x1, y1, gl.COLOR_BUFFER_BIT, gl.NEAREST);
                    gl.bindFramebuffer(gl.READ_FRAMEBUFFER, null); gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, null);
                    gl.disable(gl.SCISSOR_TEST);
                    composite(groups[li], targets.resolve.tex, false);
                } else {
                    const [A, B] = targets.lv[lv];
                    const Pb = progs.blur;
                    gl.disable(gl.BLEND);
                    gl.bindFramebuffer(gl.FRAMEBUFFER, B.fbo);
                    gl.clear(gl.COLOR_BUFFER_BIT);
                    scissor(bb, A.w, A.h);
                    fsPass(Pb, B);
                    gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, A.tex); gl.uniform1i(Pb.loc.uTex, 0);
                    gl.uniform2f(Pb.loc.uDir, 1 / A.w, 0);
                    gl.drawArrays(gl.TRIANGLES, 0, 3);
                    fsPass(Pb, A);
                    gl.bindTexture(gl.TEXTURE_2D, B.tex);
                    gl.uniform2f(Pb.loc.uDir, 0, 1 / A.h);
                    gl.drawArrays(gl.TRIANGLES, 0, 3);
                    gl.disable(gl.SCISSOR_TEST);
                    if (glowPass) { const g = sstep(0, 1, h.mix); composite(groups[li], A.tex, true, [0.34 * g, 0.24 * g, 0.14 * g], true); }
                    else composite(groups[li], A.tex, true);
                }
                gl.disable(gl.SCISSOR_TEST);
            }
            drawOverlay();
            restore();
        },

        resize() {
            const ow = W, oh = H;
            W = p.width; H = p.height;
            for (const q of parts) { q.x *= W / ow; q.y *= H / oh; }
            makeTargets();
            makeTiles();
            restore();
        },
    };
}
