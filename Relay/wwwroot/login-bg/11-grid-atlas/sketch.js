// 11 · Grid atlas — an endless, slow dive through cryo-EM grids, the way an operator navigates the atlas.
// A copper mesh disc → one grid square of holey carbon → a single hole of vitreous ice → particles in random
// orientations. The particle we zoom into swells into the next grid's copper disc, so the dive never ends.
// Rendering is one analytic fragment shader per grid, with every feature filtered by its own pixel footprint
// (scale-space LOD): sub-pixel holes fade into their area-averaged tone, the mesh into its average coverage,
// particles into a faint darkening of the ice, so the continuous zoom never pops.
import { VERT, FRAG } from './glsl.js';

export const meta = {
    title: 'Grid Atlas',
    tagline: 'A slow dive from copper mesh to particles in the ice, and on into the next grid',
    description: 'An endless, unhurried dive through cryo-EM grids: the copper mesh disc, one square of holey carbon ' +
        'with its ice-thickness gradients, broken squares, cracks and frost specks, a single hole of vitreous ice, ' +
        'then particles in random orientations, one of which swells into the next grid. By day it is a watercolour of the ' +
        'grid on a sunlit lightbox, warm copper over honey and rose ice washes; by night it is the honey-amber glow of an old ' +
        'microscope viewing screen, copper in shadow and thin ice glowing. Every grid is new (mesh, hole pattern, ice, damage, specimen), ' +
        'and the dive speed, heading and pauses drift.',
    renderer: 'webgl',
    fps: 30,
    pixelDensity: 0.75,
    maxStep: 1 / 15,
    post: { paper: 0.5, grain: 0.3, vignette: 0.38 },
};

const TAU = Math.PI * 2;
const clamp = (v, a = 0, b = 1) => (v < a ? a : v > b ? b : v);
const smooth = (a, b, x) => { const t = clamp((x - a) / (b - a)); return t * t * (3 - 2 * t); };
const bump = (x, c, w) => Math.exp(-(((x - c) / w) ** 2));

const R0 = 70;          // coin radius (css px) at zl = 0
const EMERGE = -1.5;    // zl at which the next grid starts to show through the target particle
const SWAP = 3.1;       // zl of the newest grid at which the previous one is dropped

// specimen types: [radius nm, cell nm]
const SAMPLES = [
    { type: 0, rp: 7.6, cell: 28 },   // D7 barrel
    { type: 1, rp: 6.2, cell: 20 },   // apoferritin-like shell
    { type: 2, rp: 12.5, cell: 36 },  // ribosome-like
];
// warm pigments for the particles in the light theme (one per grid)
const PIGMENTS = [[0.70, 0.50, 0.60], [0.82, 0.55, 0.45], [0.72, 0.52, 0.56], [0.80, 0.62, 0.42], [0.64, 0.52, 0.64]];
const MESHES = [
    { pm: 83, bw: 25, w: 5 },   // 300 mesh
    { pm: 125, bw: 36, w: 2 },  // 200 mesh
    { pm: 63, bw: 24, w: 0.7 }, // 400 mesh
];
const FILMS = [
    { d: 2.0, ph: 3.0, w: 4 },     // R2/1
    { d: 1.2, ph: 2.5, w: 4 },     // R1.2/1.3
    { d: 2.0, ph: 4.0, w: 1.5 },   // R2/2
    { d: 0.6, ph: 1.6, w: 1 },     // R0.6/1
    { d: 1.0, ph: 2.2, w: 1.5, hex: true }, // hexagonal pattern
];

export default function sketch(p, h) {
    const R = () => p.random();
    const rr = (a, b) => a + (b - a) * p.random();
    const wpick = arr => { let s = 0; for (const x of arr) s += x.w; let k = R() * s; for (const x of arr) { k -= x.w; if (k <= 0) return x; } return arr[0]; };

    let gl, prog, loc = {}, vao, noiseTex;
    let grids = [];
    let sampleBag = [];
    let side = R() < 0.5 ? -1 : 1;
    let A = null;

    function nextSample() {
        if (!sampleBag.length) { sampleBag = [0, 1, 2]; for (let i = 2; i > 0; i--) { const j = Math.floor(R() * (i + 1)); [sampleBag[i], sampleBag[j]] = [sampleBag[j], sampleBag[i]]; } }
        return SAMPLES[sampleBag.pop()];
    }

    function newGrid(zl) {
        const mesh = wpick(MESHES), film = wpick(FILMS), smp = nextSample();
        const gridR = 1525, meshR = 1290;
        const fa = R() * TAU, fc = Math.cos(fa), fs = Math.sin(fa);
        const toFilm = (x, y) => [fc * x + fs * y, -fs * x + fc * y];
        const toMesh = (x, y) => [fc * x - fs * y, fs * x + fc * y];
        // operator picks a square with nice ice a little way from the centre
        let si, sj, cx, cy;
        do {
            si = Math.floor(rr(-1, 1) * meshR * 0.55 / mesh.pm); sj = Math.floor(rr(-1, 1) * meshR * 0.55 / mesh.pm);
            cx = (si + 0.5) * mesh.pm; cy = (sj + 0.5) * mesh.pm;
        } while (Math.hypot(cx, cy) > meshR * 0.55);
        // a hole near the square centre
        const inner = 0.5 * (mesh.pm - mesh.bw);
        const tx = cx + rr(-0.3, 0.3) * inner, ty = cy + rr(-0.3, 0.3) * inner;
        const [fx, fy] = toFilm(tx, ty);
        let hx, hy, hcx, hcy;
        if (film.hex) {
            const rowH = film.ph * 0.8660254;
            hy = Math.round(fy / rowH);
            const sx = (((hy % 2) + 2) % 2) * 0.5;
            hx = Math.round(fx / film.ph - sx);
            hcx = (hx + sx) * film.ph; hcy = hy * rowH;
        } else {
            hx = Math.round(fx / film.ph); hy = Math.round(fy / film.ph);
            hcx = hx * film.ph; hcy = hy * film.ph;
        }
        // a particle a little off the hole centre
        const hrNm = film.d * 500;
        let tcx, tcy, tpx, tpy;
        do {
            tcx = Math.floor(rr(-0.45, 0.45) * hrNm / smp.cell); tcy = Math.floor(rr(-0.45, 0.45) * hrNm / smp.cell);
            tpx = (tcx + 0.5) * smp.cell + rr(-0.2, 0.2) * (smp.cell - 2 * smp.rp);
            tpy = (tcy + 0.5) * smp.cell + rr(-0.2, 0.2) * (smp.cell - 2 * smp.rp);
        } while (Math.hypot(tpx, tpy) > hrNm * 0.5);
        const [hmx, hmy] = toMesh(hcx, hcy);
        const [omx, omy] = toMesh(tpx / 1000, tpy / 1000);
        const T = [hmx + omx, hmy + omy];
        const iceA = R() * TAU;
        return {
            zl, mesh, film, smp, gridR, meshR, fa,
            seed: (R() * 4294967295) >>> 0,
            pace: rr(0.8, 1.25),
            rot0: R() * TAU,
            sq: [si, sj], sqC: [cx, cy], sqOff: [T[0] - cx, T[1] - cy],
            hole: [hx, hy], holeOff: [omx, omy],
            tCell: [tcx, tcy], tp: [tpx, tpy], T,
            ice: [rr(60, 110), rr(0.6, 1.2), rr(0.35, 0.7), rr(0.5, 0.75)], iceDir: [Math.cos(iceA), Math.sin(iceA)],
            targetT: rr(48, 75),
            fail: [rr(0.01, 0.035), rr(0.02, 0.05), rr(0.03, 0.09), rr(0.3, 1.2)],
            dens: rr(0.45, 0.8),
            pig: PIGMENTS[Math.floor(R() * PIGMENTS.length)],
            side0: side, side: (side = R() < 0.65 ? -side : side),
        };
    }
    const sOf = g => (R0 / g.gridR) * Math.pow(2, g.zl);
    const targetPx = g => g.smp.rp * sOf(g) * 0.001;

    // dive speed in octaves per second: dwell at the atlas, the hole and the particles
    function rate(zl, pace) {
        const base = 1 / 24;
        // slower over the atlas, the hole and the particles; a little quicker through the single square
        const dwell = 1 - 0.3 * bump(zl, 5.2, 1.3) + 0.35 * bump(zl, 8.6, 1.3) - 0.35 * bump(zl, 12.8, 1.1) - 0.55 * bump(zl, 15.4, 1.0);
        return base * pace * dwell * (1 + 0.3 * h.drift('rate', 140));
    }

    function anchorTarget(g) {
        const W = p.width, H = p.height;
        const off = Math.min(0.29 * W, Math.max(0.21 * W, h.focus.w * 0.5 + 150));
        // a new grid keeps the previous side until the dive reaches the squares, then the stage pans across
        const sd = g.zl > 6 ? g.side : g.side0;
        return [W * 0.5 + sd * off + 40 * h.drift('ax', 70), H * 0.5 + H * 0.1 * h.drift('ay', 90)];
    }

    const ground = (name = h.theme) => { if (gl) gl.canvas.style.backgroundColor = name === 'dark' ? '#2c1d12' : '#fbf6ec'; };

    async function compile() {
        const mk = (type, src) => {
            const s = gl.createShader(type);
            gl.shaderSource(s, src); gl.compileShader(s);
            if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
            return s;
        };
        const pr = gl.createProgram();
        gl.attachShader(pr, mk(gl.VERTEX_SHADER, VERT));
        gl.attachShader(pr, mk(gl.FRAGMENT_SHADER, FRAG));
        gl.linkProgram(pr);
        // compile in the background where supported, so the page can paint its ground colour meanwhile
        const par = gl.getExtension('KHR_parallel_shader_compile');
        if (par) while (!gl.getProgramParameter(pr, par.COMPLETION_STATUS_KHR)) await new Promise(r => setTimeout(r, 8));
        if (!gl.getProgramParameter(pr, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(pr));
        const n = gl.getProgramParameter(pr, gl.ACTIVE_UNIFORMS);
        for (let i = 0; i < n; i++) { const u = gl.getActiveUniform(pr, i); loc[u.name] = gl.getUniformLocation(pr, u.name); }
        return pr;
    }

    function drawGrid(g, newer, phi) {
        const L = loc, s = sOf(g);
        const rot = phi + g.rot0, c = Math.cos(rot), sn = Math.sin(rot);
        // pan: at emergence the disc is centred on the anchor; the target slides under it over the first octaves
        const wpan = 1 - smooth(0, 5.5, g.zl);
        const O = [-g.T[0] * wpan, -g.T[1] * wpan];
        gl.uniform2f(L.uA, A[0], A[1]);
        gl.uniform2f(L.uRot, c, sn);
        gl.uniform1f(L.uS, s);
        gl.uniform2f(L.uO, O[0], O[1]);
        gl.uniform2i(L.uSqIdx, g.sq[0], g.sq[1]);
        gl.uniform2f(L.uSqOff, g.sqOff[0], g.sqOff[1]);
        gl.uniform2f(L.uSqC, g.sqC[0], g.sqC[1]);
        gl.uniform1f(L.uPm, g.mesh.pm); gl.uniform1f(L.uBw, g.mesh.bw);
        gl.uniform1f(L.uGridR, g.gridR); gl.uniform1f(L.uMeshR, g.meshR);
        gl.uniform2f(L.uFRot, Math.cos(g.fa), Math.sin(g.fa));
        gl.uniform1f(L.uPh, g.film.ph); gl.uniform1f(L.uHr, g.film.d * 0.5);
        gl.uniform1i(L.uHex, g.film.hex ? 1 : 0);
        gl.uniform2i(L.uHoleIdx, g.hole[0], g.hole[1]);
        gl.uniform2f(L.uHoleOff, g.holeOff[0], g.holeOff[1]);
        gl.uniform1ui(L.uSeed, g.seed);
        gl.uniform4f(L.uIce, ...g.ice);
        gl.uniform2f(L.uIceDir, ...g.iceDir);
        gl.uniform1f(L.uTargetT, g.targetT);
        gl.uniform4f(L.uFail, ...g.fail);
        gl.uniform4f(L.uSample, g.smp.type, g.smp.rp, g.dens, g.smp.cell);
        gl.uniform2i(L.uTCell, g.tCell[0], g.tCell[1]);
        gl.uniform2f(L.uTP, g.tp[0], g.tp[1]);
        gl.uniform3f(L.uPig, ...g.pig);
        // while a newer grid emerges, this one's particles fade; the newer one shows through its target first
        gl.uniform1f(L.uPartFade, newer ? 1 - smooth(-0.6, 1.6, newer.zl) : 1);
        gl.uniform1f(L.uReveal, smooth(EMERGE + 0.2, 0.6, g.zl));
        gl.uniform1f(L.uDiss, smooth(0.4, SWAP, g.zl) + (g === grids[0] ? 1 : 0));
        // the old grid can skip what the newer coin already covers completely
        const cov = newer && smooth(EMERGE + 0.2, 0.6, newer.zl) >= 1 ? R0 * Math.pow(2, newer.zl) * 0.93 : 0;
        const nw = newer ? 1 - smooth(0, 5.5, newer.zl) : 0;
        gl.uniform3f(L.uCover, A[0], A[1], cov * (nw > 0.999 ? 1 : 0));
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    }

    return {
        async setup() {
            gl = p.drawingContext;
            if (!(gl instanceof WebGL2RenderingContext)) throw new Error('needs WebGL2');
            // the empty canvas shows the ground colour of the opening frame instead of a blank grey
            ground();
            vao = gl.createVertexArray();
            prog = await compile();
            // random lattice for value noise (texelFetch + bilinear in float in the shader, not the 8-bit GPU filter)
            const N = 256, data = new Uint8Array(N * N);
            for (let i = 0; i < data.length; i++) data[i] = Math.floor(R() * 256);
            noiseTex = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, noiseTex);
            gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, N, N, 0, gl.RED, gl.UNSIGNED_BYTE, data);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
            gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
            gl.bindTexture(gl.TEXTURE_2D, null);
            grids = [newGrid(rr(2.6, 3.4))];
            A = anchorTarget(grids[0]);
        },
        step(dt) {
            const main = grids[grids.length - 1];
            const dz = rate(main.zl, main.pace) * dt;
            for (const g of grids) g.zl += dz;
            if (grids.length === 1 && targetPx(main) >= R0 * Math.pow(2, EMERGE)) {
                grids.push(newGrid(EMERGE + Math.log2(targetPx(main) / (R0 * Math.pow(2, EMERGE)))));
            }
            if (grids.length === 2 && grids[1].zl > SWAP) grids.shift();
            const [tx, ty] = anchorTarget(grids[grids.length - 1]);
            const k = 1 - Math.exp(-dt / 40);
            A[0] += (tx - A[0]) * k; A[1] += (ty - A[1]) * k;
        },
        theme(name) { ground(name); },
        resize() { A = anchorTarget(grids[grids.length - 1]); },
        render() {
            const L = loc, f = h.focus;
            gl.disable(gl.DEPTH_TEST); gl.disable(gl.CULL_FACE); gl.disable(gl.STENCIL_TEST); gl.disable(gl.SCISSOR_TEST);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.viewport(0, 0, gl.drawingBufferWidth, gl.drawingBufferHeight);
            gl.useProgram(prog);
            gl.bindVertexArray(vao);
            gl.activeTexture(gl.TEXTURE0);
            gl.bindTexture(gl.TEXTURE_2D, noiseTex);
            gl.uniform1i(L.uNoise, 0);
            gl.uniform2f(L.uRes, p.width, p.height);
            gl.uniform1f(L.uDpr, gl.drawingBufferWidth / p.width);
            gl.uniform1f(L.uMix, h.byTheme(0, 1));
            gl.uniform4f(L.uFocus, f.x, f.y, f.w, f.h);
            gl.uniform1f(L.uCalm, 1);
            const phi = 0.45 * h.drift('rot', 420);
            gl.disable(gl.BLEND);
            drawGrid(grids[0], grids[1], phi);
            if (grids[1]) {
                gl.enable(gl.BLEND);
                gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ZERO, gl.ONE);
                drawGrid(grids[1], null, phi);
            }
            gl.bindVertexArray(null);
            gl.useProgram(null);
            gl.bindTexture(gl.TEXTURE_2D, null);
            gl.enable(gl.BLEND); gl.enable(gl.DEPTH_TEST);
            gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
        },
    };
}
