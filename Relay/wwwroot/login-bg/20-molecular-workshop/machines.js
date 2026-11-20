// machines.js: the painted cast as theme-pair sprites, plus a few static painters used inside cached layers.
// All sizes are in nm and scaled by k (px per nm). Sprites are drawn around their own centre.
import { TAU, clamp, lerp, mixc, css, rgb, lnOf, hiOf, blobPath, smoothOpen, gouache, sprite, canvas, hash } from './lib.js';

const shade = (c, f) => [c[0] * f, c[1] * f, c[2] * f];

export function makeSprites(G) {
    const k = G.k, S = {};
    const B = (x, cx, cy, rx, ry, rot, seed, c, o = {}) =>
        gouache(G, x, () => blobPath(x, cx * k, cy * k, rx * k, ry * k, rot, seed, o.wob ?? 0.09, o.n ?? 11), c, { lw: o.lw ?? Math.max(0.8, 0.32 * k), hi: o.hi ?? 0.5, hd: (o.hd ?? 0.55) * k, a: o.a });

    // ---- 80S ribosome, side view with the subunit interface edge-on. Frame: mRNA along +x (toward its 3' end) at
    // y = 0, threading the 40S neck just below the interface; the 60S lies beyond at -y. The 40S head/body axis and
    // the 60S central protuberance point along the line of sight here, so they project onto the bodies; the L1 stalk
    // is on the 5' (E-site) side and the P-stalk on the 3' (A-site, factor-binding) side. The ~10 nm exit tunnel runs
    // from the peptidyl-transferase centre near the interface to the solvent face of the 60S.
    // Sprite centres: 60S at (0,-10 nm), 40S at (0,+2.5 nm). Overall ~30 x 27 nm.
    S.r60 = sprite(G, 36 * k, 26 * k, x => {
        x.translate(0, 10 * k);
        const c = rgb(G, 'rib60'), cp = rgb(G, 'rib40');
        B(x, -14.2, -7.5, 2.8, 4.4, -0.45, 5, c);                     // L1 stalk (E-site side)
        B(x, 0.3, -10, 13.6, 8.1, 0.04, 1, c, { wob: 0.07, n: 13 });  // body
        x.strokeStyle = css(lnOf(G, c, 0.35)); x.lineWidth = 1.1 * k; x.lineCap = 'round';
        x.beginPath(); x.moveTo(12.5 * k, -6 * k); x.quadraticCurveTo(16 * k, -6.5 * k, 16.6 * k, -10.5 * k); x.stroke(); // P-stalk (A-site side)
        // ribosomal proteins dotted over the rRNA core
        const rp = [[-6, -13.6, 2.1], [4, -16.2, 2.3], [9, -12, 1.8], [-3, -6.4, 1.6], [7.5, -5.4, 1.8], [-9, -9.6, 1.6], [1.5, -12, 1.4]];
        rp.forEach(([px, py, r], i) => B(x, px, py, r, r * 0.85, i, 20 + i, mixc(cp, c, 0.25 + 0.2 * (i % 2)), { lw: 0.6, hi: 0.35 }));
        // central protuberance, seen end-on above the interface
        B(x, -2.5, -6.8, 4.2, 3.2, 0.1, 3, mixc(c, rgb(G, 'cream'), 0.12), { lw: 0.6, hi: 0.4 });
        // exit tunnel mouth on the solvent face
        x.fillStyle = css(lnOf(G, c, 0.55)); x.beginPath(); x.ellipse(4.5 * k, -17.4 * k, 1.5 * k, 0.8 * k, 0, 0, TAU); x.fill();
    });
    S.r40 = sprite(G, 28 * k, 17 * k, x => {
        x.translate(0, -2.5 * k);
        const c = rgb(G, 'rib40');
        B(x, 0, 2.7, 11, 5.6, 0.03, 7, c, { n: 12 });                // body, shoulder and platform
        B(x, 0.6, 0.2, 5.6, 4.3, 0.15, 9, mixc(c, rgb(G, 'rib60'), 0.12), { lw: 0.6, hi: 0.4 }); // head, end-on over the neck
        B(x, 4.4, -1.6, 1.5, 1.2, 0.4, 11, c, { lw: 0.5 });            // beak
        B(x, -6.5, 3.2, 1.7, 1.5, 0, 12, mixc(c, rgb(G, 'rib60'), 0.4), { lw: 0.5, hi: 0.3 });
        B(x, 5.5, 4.6, 1.5, 1.2, 0, 13, mixc(c, rgb(G, 'rib60'), 0.4), { lw: 0.5, hi: 0.3 });
    });
    // eIF3 (~800 kDa, 13 subunits): a five-lobed particle ~18 x 12 nm riding the solvent face of a scanning 40S
    S.eif3 = sprite(G, 24 * k, 17 * k, x => {
        const c = rgb(G, 'fold', 0);
        [[-5.2, -1.2, 3.4, 2.8], [-0.8, -2.4, 3.6, 3.0], [3.8, -1.0, 3.2, 2.7], [-3.0, 2.6, 3.0, 2.5], [2.4, 2.8, 3.1, 2.4], [7.0, 1.6, 1.9, 1.7]]
            .forEach(([px, py, rx, ry], i) => B(x, px, py, rx, ry, i * 0.7, 31 + i, mixc(c, rgb(G, 'fold', 4), (i % 3) * 0.18), { lw: 0.6, hi: 0.4 }));
    });
    // cap-binding eIF4F: m7G cap, eIF4E on it, the long eIF4G scaffold, and the eIF4A helicase
    S.eif4f = sprite(G, 20 * k, 12 * k, x => {
        const c = rgb(G, 'fold', 4);
        x.strokeStyle = css(lnOf(G, c, 0.25)); x.lineWidth = 1.6 * k; x.lineCap = 'round';
        x.beginPath(); x.moveTo(-3.2 * k, -0.6 * k); x.quadraticCurveTo(1 * k, -3.4 * k, 6.8 * k, -1.2 * k); x.stroke(); // eIF4G (largely disordered)
        B(x, -4.6, 0.6, 2.1, 1.9, 0, 34, mixc(c, rgb(G, 'fold', 2), 0.4), { lw: 0.6, hi: 0.4 }); // eIF4E
        B(x, 4.2, 1.6, 2.4, 1.7, 0.3, 35, mixc(c, rgb(G, 'fold', 1), 0.5), { lw: 0.6, hi: 0.4 }); // eIF4A
        B(x, -7.2, 1.4, 0.9, 0.8, 0, 36, rgb(G, 'spark'), { lw: 0.5, hi: 0.2 }); // m7G cap
    });

    // ---- kinesin-1 motor domain and light chains
    S.kHead = sprite(G, 10 * k, 7 * k, x => { B(x, 0, 0, 3.9, 2.4, 0, 41, rgb(G, 'kin'), { wob: 0.12 }); B(x, -1.6, -1.3, 1.1, 0.8, 0, 42, hiOf(G, rgb(G, 'kin'), 0.25), { lw: 0, hi: 0 }); });
    S.kLC = sprite(G, 8 * k, 6 * k, x => { B(x, 0, 0, 2.6, 1.8, 0.3, 43, rgb(G, 'kinLC')); });

    // ---- actin subunits: [state 0 ATP, 1 ADP-Pi, 2 ADP][depth 0 back .. 2 front]
    const AC = ['actATP', 'actPi', 'actADP'];
    S.act = AC.map((n, si) => [0.9, 0.96, 1].map((f, di) => sprite(G, 7.6 * k, 7.2 * k, x => {
        const c = mixc(shade(rgb(G, n), f), rgb(G, 'bg1'), (2 - di) * 0.16);
        B(x, -0.4, 0.2, 2.5, 2.3, 0.3, 50 + si, c, { wob: 0.1, hi: di === 2 ? 0.55 : 0.3, lw: Math.max(0.6, 0.2 * k) }); // ~5.5 x 5.5 x 3.5 nm subunit
        B(x, 1.1, -0.8, 1.4, 1.25, 0, 53 + si, mixc(c, rgb(G, 'cream'), 0.08), { lw: 0.4, hi: 0.2 });
    }, 0.3)));
    S.arp = sprite(G, 18 * k, 15 * k, x => { B(x, -2.2, 0.4, 5.2, 4.3, 0.2, 61, rgb(G, 'arp')); B(x, 3.3, -0.8, 4.4, 3.8, -0.3, 62, mixc(rgb(G, 'arp'), rgb(G, 'actADP'), 0.4)); B(x, -0.4, 4.2, 2.6, 1.9, 0, 64, mixc(rgb(G, 'arp'), rgb(G, 'cream'), 0.2), { lw: 0.6 }); }); // ~15 x 10 nm, 7 subunits
    S.capz = sprite(G, 9 * k, 9 * k, x => { B(x, 0, 0, 3.2, 2.6, 0, 63, rgb(G, 'mprot', 2)); });

    // ---- TRiC/CCT: one ring of eight paralogous subunits (drawn alike), side view, frames by openness (0 closed .. 1 open)
    S.tric = [0, 0.2, 0.4, 0.6, 0.8, 1].map(o => sprite(G, 20 * k, 12 * k, x => { x.translate(0, 6 * k); x.scale(1, 0.8); tricRing(G, x, k, o); }, 0.35));

    // ---- prefoldin: a little jellyfish (beta-barrel body, six coiled-coil tentacles); pointing down = tentacles
    S.pfd = sprite(G, 12 * k, 14 * k, x => {
        const c = rgb(G, 'pfd');
        x.strokeStyle = css(lnOf(G, c, 0.2)); x.lineCap = 'round';
        for (let i = 0; i < 6; i++) {
            const bx = (i - 2.5) * 1.3 * k;
            x.lineWidth = 1.0 * k;
            x.beginPath(); x.moveTo(bx, -2.6 * k); x.quadraticCurveTo(bx * 1.35 + (i % 2 ? 0.6 : -0.6) * k, 0.8 * k, bx * 1.2, 4.2 * k); x.stroke();
        }
        B(x, 0, -3.8, 4.0, 2.3, 0, 71, c, { wob: 0.06 });
    });

    // ---- importin-beta (a solenoid crescent) with a small cargo, and an mRNP
    S.imp = sprite(G, 16 * k, 14 * k, x => {
        const c = rgb(G, 'importin');
        x.lineCap = 'round'; x.strokeStyle = css(lnOf(G, c, 0.4)); x.lineWidth = 3.8 * k;
        x.beginPath(); x.arc(0, 0, 4.6 * k, 0.3, TAU * 0.8); x.stroke();
        x.strokeStyle = css(c); x.lineWidth = 3.0 * k; x.stroke();
        x.strokeStyle = css(hiOf(G, c, 0.4), 0.7); x.lineWidth = 0.6 * k; x.setLineDash([0.9 * k, 0.9 * k]); x.stroke(); x.setLineDash([]);
        B(x, 0.8, 0.4, 2.5, 2.2, 0, 81, rgb(G, 'fold', 3));
    });
    // importin-beta after RanGTP (a small GTPase) has bound it on the nuclear side and released the cargo
    S.impR = sprite(G, 16 * k, 14 * k, x => {
        const c = rgb(G, 'importin');
        x.lineCap = 'round'; x.strokeStyle = css(lnOf(G, c, 0.4)); x.lineWidth = 3.8 * k;
        x.beginPath(); x.arc(0, 0, 4.6 * k, 0.3, TAU * 0.8); x.stroke();
        x.strokeStyle = css(c); x.lineWidth = 3.0 * k; x.stroke();
        x.strokeStyle = css(hiOf(G, c, 0.4), 0.7); x.lineWidth = 0.6 * k; x.setLineDash([0.9 * k, 0.9 * k]); x.stroke(); x.setLineDash([]);
        B(x, 0.2, 0.2, 2.2, 1.9, 0, 82, rgb(G, 'spark'));
    });
    S.mrnp = sprite(G, 26 * k, 14 * k, x => {
        const c = rgb(G, 'mrnp');
        x.strokeStyle = css(rgb(G, 'mrna')); x.lineWidth = 0.9 * k; x.lineCap = 'round';
        x.beginPath(); x.moveTo(-11 * k, 1 * k); x.bezierCurveTo(-4 * k, -6 * k, 4 * k, 6 * k, 11 * k, -1 * k); x.stroke();
        for (let i = 0; i < 4; i++) B(x, -7 + i * 4.7, (i % 2 ? 1.2 : -1.4), 2.6, 2.3, i, 85 + i, i % 2 ? c : mixc(c, rgb(G, 'fold', 1), 0.4));
    });

    // ---- folded products (small globular proteins), a few shapes
    S.fold = [0, 1, 2, 3, 4, 5].map(i => sprite(G, 12 * k, 12 * k, x => {
        const c = rgb(G, 'fold', i);
        if (i % 3 === 2) { B(x, -1.3, 0.4, 2.4, 2.1, 0, 90 + i, c); B(x, 1.5, -0.6, 2.0, 1.9, 0, 96 + i, mixc(c, rgb(G, 'cream'), 0.12)); }
        else B(x, 0, 0, 2.6 + (i % 2) * 0.5, 2.2, i, 90 + i, c);
    }));
    // a folded actin monomer (two lobes, nucleotide cleft) and a tubulin dimer, released by TRiC
    S.actMono = sprite(G, 9 * k, 8 * k, x => { B(x, -0.9, 0.3, 2.8, 2.5, 0.2, 101, rgb(G, 'actATP')); B(x, 1.6, -0.6, 1.9, 1.7, 0, 102, rgb(G, 'actPi')); });
    S.tubMono = sprite(G, 8 * k, 8 * k, x => { B(x, 0, 0, 2.3, 2.7, 0.2, 103, rgb(G, 'tubB')); }); // a folded beta-tubulin monomer (dimers assemble later, with the tubulin cofactors)

    // ---- ATP synthase stator (a-subunit, peripheral stalk, alpha3beta3 head, OSCP). Origin = membrane midplane
    // at the c-ring centre; the head points to -y (into the matrix), the dimer partner lies toward +x. Sprite centre
    // at (0,-8.5 nm).
    S.atp = sprite(G, 19 * k, 28 * k, x => { x.translate(0, 8.5 * k); atpStator(G, x, k); }, 0.35);
    // ATP, a tiny bead trio (adenosine + phosphates)
    S.atpMol = sprite(G, 5 * k, 3 * k, x => {
        B(x, -0.6, 0, 0.65, 0.55, 0, 111, rgb(G, 'spark'), { lw: 0.5, hi: 0.3 });
        for (let i = 0; i < 3; i++) { x.fillStyle = css(rgb(G, 'pabp')); x.beginPath(); x.arc((0.25 + i * 0.42) * k, 0.1 * k, 0.24 * k, 0, TAU); x.fill(); }
    }, 0);
    // warm light for the night scene (not theme-paired: only ever drawn with 'lighter' at mix)
    const N = 128, gc = canvas(N, N), gx = gc.getContext('2d'), rg = gx.createRadialGradient(N / 2, N / 2, 0, N / 2, N / 2, N / 2);
    rg.addColorStop(0, 'rgba(255,200,120,0.9)'); rg.addColorStop(0.25, 'rgba(255,180,95,0.42)'); rg.addColorStop(0.6, 'rgba(240,150,80,0.12)'); rg.addColorStop(1, 'rgba(230,140,70,0)');
    gx.fillStyle = rg; gx.fillRect(0, 0, N, N);
    S.glow = gc;
    return S;
}

/** vesicle of radius R nm: lumen, bilayer with head beads, a handful of membrane proteins. */
export function vesicleSprite(G, R, v, seed) {
    const k = G.k;
    return sprite(G, (2 * R + 12) * k, (2 * R + 12) * k, x => {
        const lum = rgb(G, 'ves', v), head = rgb(G, 'head'), tail = rgb(G, 'tail');
        const rr = R * k;
        // lumen with a soft inner pool of light
        x.fillStyle = css(lum); x.beginPath(); x.arc(0, 0, rr, 0, TAU); x.fill();
        const g = x.createRadialGradient(-rr * 0.25, -rr * 0.3, 0, 0, 0, rr);
        g.addColorStop(0, css(hiOf(G, lum, 0.6), 0.9)); g.addColorStop(1, css(lum, 0));
        x.fillStyle = g; x.fill();
        // cargo inside
        for (let i = 0; i < 5 + (R / 4 | 0); i++) {
            const a = hash(i, seed) * TAU, d = Math.sqrt(hash(i, seed + 1)) * (R - 6);
            x.fillStyle = css(mixc(lum, rgb(G, 'fold', i), 0.35), 0.9);
            x.beginPath(); x.arc(Math.cos(a) * d * k, Math.sin(a) * d * k, (1 + hash(i, seed + 2) * 1.3) * k, 0, TAU); x.fill();
        }
        // bilayer
        x.lineWidth = 5.4 * k; x.strokeStyle = css(lnOf(G, head, 0.5), 0.9); x.beginPath(); x.arc(0, 0, rr, 0, TAU); x.stroke();
        x.lineWidth = 4.9 * k; x.strokeStyle = css(head); x.stroke();
        x.lineWidth = 2.8 * k; x.strokeStyle = css(tail); x.stroke();
        const hb = css(hiOf(G, head, 0.25)); x.fillStyle = hb; x.beginPath();
        for (const d of [-1.9, 1.9]) {
            const rad = (R + d) * k, n = Math.floor((TAU * rad) / (1.05 * k));
            for (let i = 0; i < n; i++) { const a = (i / n) * TAU, px = Math.cos(a) * rad, py = Math.sin(a) * rad; x.moveTo(px + 0.62 * k, py); x.arc(px, py, 0.62 * k, 0, TAU); }
        }
        x.fill();
        // integral membrane proteins (SNAREs, transporters, one bigger pump) spanning the bilayer, bulging out a little
        const np = 4 + (R / 8 | 0);
        for (let i = 0; i < np; i++) {
            const a = (i / np) * TAU + hash(i, seed + 5) * 0.8, big = i === 0, s = big ? 3.6 : 2.3 + hash(i, seed + 6) * 0.8;
            const px = Math.cos(a) * (R + s * 0.1) * k, py = Math.sin(a) * (R + s * 0.1) * k;
            gouache(G, x, () => blobPath(x, px, py, s * k, s * 0.8 * k, a, i + seed, 0.1, 9), rgb(G, 'mprot', i), { lw: 0.7, hi: 0.4, hd: 0.5 * k });
        }
    }, 0.3);
}

/** one TRiC ring (8 paralogous subunits), equator at y = 0, apical domains toward -y, openness o. */
function tricRing(G, x, k, o) {
    const subs = [];
    for (let i = 0; i < 8; i++) { const th = (i / 8) * TAU + 0.21; subs.push({ i, s: Math.sin(th), c: Math.cos(th) }); }
    subs.sort((a, b) => a.c - b.c);
    for (const u of subs) {
        const f = 0.7 + 0.3 * (u.c * 0.5 + 0.5), col = shade(rgb(G, 'tric', 0), f); // the eight paralogs in one colour (their order round the ring is not shown)
        const xc = 5.0 * u.s, w = 1.9 + 1.0 * Math.max(0, u.c);
        // equatorial + intermediate domains
        gouache(G, x, () => blobPath(x, xc * k, -2.9 * k, w * k, 3.1 * k, 0, u.i, 0.08, 10), col, { lw: 0.8, hi: 0.35, hd: 0.5 * k });
        // apical domain: tilts out when open, folds in to form the iris lid when closed
        const tilt = lerp(-0.55, 0.42, o) * u.s, ax = (xc + Math.sin(tilt) * 3.6) * lerp(0.82, 1.05, o), ay = -6.6 - Math.cos(tilt) * 1.9;
        gouache(G, x, () => blobPath(x, ax * k, ay * k, w * 0.92 * k, 2.4 * k, tilt, u.i + 9, 0.1, 9), mixc(col, rgb(G, 'cream'), 0.06), { lw: 0.8, hi: 0.4, hd: 0.5 * k });
        // helical protrusion (the lid finger)
        const px = ax + Math.sin(tilt) * 2.6 - u.s * (1 - o) * 1.6, py = ay - Math.cos(tilt) * 2.2 - (1 - o) * 0.4;
        x.strokeStyle = css(lnOf(G, col, 0.15)); x.lineWidth = 1.0 * k; x.lineCap = 'round';
        x.beginPath(); x.moveTo(ax * k, (ay - 1.2) * k); x.lineTo(px * k, py * k); x.stroke();
    }
    if (o < 0.5) { // closed lid dome
        const a = (0.5 - o) * 2, c = rgb(G, 'tric', 0);
        gouache(G, x, () => { x.beginPath(); x.ellipse(0, -9.3 * k, 5.4 * k, 1.9 * k * a + 0.4 * k, 0, Math.PI, 0); x.closePath(); }, c, { lw: 0.7, hi: 0.5, a, hd: 0.4 * k });
    }
}

function atpStator(G, x, k) {
    const st = rgb(G, 'stator'), A = rgb(G, 'atpA'), Bc = rgb(G, 'atpB');
    // a-subunit beside its c-ring, inside the bilayer: on the 10 nm crista-tip curvature, with the rotor axis tipped
    // 0.1 rad outward, the membrane midline at x = 3.8 nm lies at y = +1.1 nm in this frame
    gouache(G, x, () => blobPath(x, 3.8 * k, 1.1 * k, 1.75 * k, 2.6 * k, 0.33, 201, 0.08, 9), mixc(st, rgb(G, 'mprot', 0), 0.35), { lw: 0.8, hd: 0.5 * k });
    // peripheral stalk (b, d, F6) from the a-subunit straight up the side of F1 to OSCP on top
    const ps = [[4.0, -1.4], [4.9, -6.4], [5.15, -10.5], [5.0, -14.0], [3.8, -16.6], [1.4, -17.8]].map(([a, b]) => [a * k, b * k]);
    x.lineCap = 'round'; x.lineJoin = 'round';
    x.strokeStyle = css(lnOf(G, st, 0.3)); x.lineWidth = 1.25 * k; smoothOpen(x, ps); x.stroke();
    x.strokeStyle = css(mixc(st, rgb(G, 'cream'), 0.2)); x.lineWidth = 0.8 * k; smoothOpen(x, ps); x.stroke();
    // alpha3beta3 head: six lobes around the rotor axis, alternating alpha / beta, drawn back to front
    const lobes = [];
    for (let j = 0; j < 6; j++) { const th = (j / 6) * TAU + 0.5; lobes.push({ j, s: Math.sin(th), c: Math.cos(th) }); }
    lobes.sort((a, b) => a.c - b.c);
    for (const l of lobes) {
        const base = l.j % 2 ? Bc : A, f = 0.72 + 0.28 * (l.c * 0.5 + 0.5);
        gouache(G, x, () => blobPath(x, 2.95 * l.s * k, -12 * k, (1.9 + 1.0 * Math.max(0, l.c)) * k, 4.4 * k, 0, 210 + l.j, 0.07, 10), shade(base, f), { lw: 0.8, hi: 0.45, hd: 0.5 * k });
    }
    // OSCP cap where the stalk meets the head
    gouache(G, x, () => blobPath(x, 0.4 * k, -17.3 * k, 2.4 * k, 1.5 * k, 0, 220, 0.08, 9), mixc(st, rgb(G, 'cream'), 0.2), { lw: 0.8, hd: 0.4 * k });
}

/** Nucleosome geometry, in px relative to its centre. The histone octamer is a squat cylinder (radius 3.25 nm,
 *  ~5.8 nm tall); 147 bp of DNA wrap it in ~1.65 turns of a left-handed superhelix (DNA axis at 4.2 nm radius,
 *  pitch ~2.6 nm), so the whole particle is ~11 x 6 nm. The dyad sits at azimuth psi; the superhelix axis is tipped
 *  by tilt * 90 deg out of the line of sight (0 face-on, 1 edge-on) and the lot turned by rot in the image plane.
 *  Returns the projected DNA path (x, y, depth; depth > 0 is behind the core), the entry and exit points and their
 *  directions of travel, and the core's projected radii. Linker DNA starts and ends exactly at pin / pout. */
export function nucGeom(k, rot, tilt, psi) {
    const R = 4.2, c = 2.6 / TAU, T = 1.65 * TAU, tau = tilt * Math.PI * 0.5, ct = Math.cos(tau), st = Math.sin(tau), cr = Math.cos(rot), sr = Math.sin(rot);
    const proj = (X, Y, Z) => { const y2 = Y * ct - Z * st, z2 = Y * st + Z * ct; return [(X * cr - y2 * sr) * k, (X * sr + y2 * cr) * k, z2]; };
    const pts = [], n = 40;
    for (let i = 0; i <= n; i++) { const th = psi - T / 2 + (T * i) / n; pts.push(proj(R * Math.cos(th), R * Math.sin(th), -c * (th - psi))); } // z falls as th rises: left-handed
    const dir = (a, b) => { const dx = b[0] - a[0], dy = b[1] - a[1], l = Math.hypot(dx, dy) || 1; return [dx / l, dy / l]; };
    return { pts, pin: pts[0], tin: dir(pts[0], pts[2]), pout: pts[n], tout: dir(pts[n - 2], pts[n]), rot, rx: 3.25, ry: 3.25 * Math.abs(ct) + 2.9 * st };
}
/** the same nucleosome turned in the image plane: by rot, or (dir given) so that it is entered (or, with back set,
 *  left) heading along dir. */
export function nucTurn(g, back, dir) {
    const rot = dir === undefined ? back : back ? dir + Math.PI - Math.atan2(g.tout[1], g.tout[0]) : dir - Math.atan2(g.tin[1], g.tin[0]);
    const c = Math.cos(rot), s = Math.sin(rot), R = p => [p[0] * c - p[1] * s, p[0] * s + p[1] * c];
    return { pts: g.pts.map(p => [...R(p), p[2]]), pin: R(g.pin), tin: R(g.tin), pout: R(g.pout), tout: R(g.tout), rot: g.rot + rot, rx: g.rx, ry: g.ry };
}
/** stroke the current path as a 2 nm DNA duplex: a darker edge and the body, the same everywhere (linkers and wraps). */
export function dnaPaint(G, x, k) {
    const d = rgb(G, 'dna');
    x.lineCap = 'round'; x.lineJoin = 'round';
    x.strokeStyle = css(lnOf(G, d, 0.4)); x.lineWidth = 2.1 * k; x.stroke();
    x.strokeStyle = css(d); x.lineWidth = 1.5 * k; x.stroke();
}
/** paint a nucleosome at (cx, cy) from its geometry: the gyres behind the core, the core over them, the gyres in front
 *  over the core. Every stretch is drawn as the same 2 nm duplex as the linkers. The back gyres are shaded darker with
 *  depth, the shading fading out over the last ~10 bp at entry and exit so the joins with the linkers are seamless.
 *  Each stretch's edge stroke stops one ~1 nm sample short of its ends: a round edge cap laid over the neighbouring
 *  stretch's body would draw a dark crescent across the strand at every join. */
export function nucleosome(G, x, cx, cy, g, seed) {
    const k = G.k, h = rgb(G, 'histone'), d = rgb(G, 'dna'), ln = lnOf(G, d, 0.4), n = g.pts.length - 1, Q = 3;
    const back = [], front = []; // stretches as point lists; back ones split by quantised shade
    for (let i = 0, cur = null; i < n; i++) {
        const a = g.pts[i], b = g.pts[i + 1], z = (a[2] + b[2]) / 2;
        let q = -1;
        if (z > 0) { const e = clamp(Math.min(i, n - 1 - i) / 6, 0, 1); q = Math.round(clamp(z / 2.6, 0, 1) * e * e * (3 - 2 * e) * Q); }
        if (!cur || cur.q !== q) { cur = { q, seg: [a] }; (q < 0 ? front : back).push(cur); }
        cur.seg.push(b);
    }
    const path = (seg, j0, j1) => { x.beginPath(); x.moveTo(cx + seg[j0][0], cy + seg[j0][1]); for (let j = j0 + 1; j <= j1; j++) x.lineTo(cx + seg[j][0], cy + seg[j][1]); };
    const paint = list => {
        x.lineCap = 'round'; x.lineJoin = 'round';
        x.strokeStyle = css(ln); x.lineWidth = 2.1 * k;
        for (const r of list) if (r.seg.length > 2) { path(r.seg, 1, r.seg.length - 2); x.stroke(); }
        x.lineWidth = 1.5 * k;
        for (const r of list) { path(r.seg, 0, r.seg.length - 1); x.strokeStyle = css(r.q > 0 ? mixc(d, ln, 0.42 * r.q / Q) : d); x.stroke(); }
    };
    paint(back);
    gouache(G, x, () => blobPath(x, cx, cy, g.rx * k, g.ry * k, g.rot, seed, 0.05, 10), h, { lw: 0.7, hi: 0.5, hd: 0.5 * k, ln: 0.22 });
    paint(front);
}

/** a generic membrane protein spanning a bilayer at the origin (x along membrane, +y = outside). */
export function membraneProtein(G, x, k, kind, seed) {
    const c = rgb(G, 'mprot', seed);
    if (kind === 0) { // channel: two half-barrels with a pore between
        for (const s of [-1, 1]) gouache(G, x, () => blobPath(x, s * 2.2 * k, 0, 2.0 * k, 4.2 * k, 0, seed + s, 0.08, 9), c, { lw: 0.8, hd: 0.5 * k });
    } else if (kind === 1) { // receptor: TM helix bundle + extracellular domain + glycans
        gouache(G, x, () => blobPath(x, 0, 0, 1.8 * k, 3.9 * k, 0, seed, 0.08, 9), c, { lw: 0.8, hd: 0.5 * k });
        gouache(G, x, () => blobPath(x, 0.6 * k, 7.6 * k, 3.6 * k, 3.4 * k, 0.4, seed + 3, 0.1, 10), mixc(c, rgb(G, 'cream'), 0.15), { lw: 0.8, hd: 0.5 * k });
        glycan(G, x, k, 2.4 * k, 10 * k, 0.5, seed);
    } else if (kind === 2) { // transporter, chunkier
        gouache(G, x, () => blobPath(x, 0, -0.4 * k, 3.8 * k, 4.6 * k, 0, seed, 0.1, 11), c, { lw: 0.8, hd: 0.5 * k });
    } else { // VDAC-like beta barrel with strands
        gouache(G, x, () => { x.beginPath(); x.roundRect(-2.2 * k, -2.6 * k, 4.4 * k, 5.2 * k, 1.2 * k); }, c, { lw: 0.8, hd: 0.5 * k });
        x.strokeStyle = css(lnOf(G, c, 0.25), 0.6); x.lineWidth = 0.35 * k;
        for (let i = -2; i <= 2; i++) { x.beginPath(); x.moveTo(i * 0.8 * k - 0.4 * k, -2.2 * k); x.lineTo(i * 0.8 * k + 0.4 * k, 2.2 * k); x.stroke(); }
    }
}
/** branched sugar tree growing toward +y from (x0,y0). */
export function glycan(G, x, k, x0, y0, ang, seed) {
    const c = rgb(G, 'glycan');
    const rec = (px, py, a, depth, s) => {
        const n = 1 + (hash(s, depth) * 2 | 0);
        for (let i = 0; i < n; i++) {
            const qx = px + Math.sin(a) * 1.6 * k, qy = py + Math.cos(a) * 1.6 * k;
            x.fillStyle = css(mixc(c, rgb(G, 'mprot', s + i), 0.25 * (i % 2)));
            x.beginPath(); x.arc(qx, qy, 0.72 * k, 0, TAU); x.fill();
            x.strokeStyle = css(lnOf(G, c, 0.4), 0.7); x.lineWidth = 0.5; x.stroke();
            px = qx; py = qy; a += (hash(s + i, depth + 9) - 0.5) * 0.7;
        }
        if (depth < 2) { rec(px, py, a - 0.6, depth + 1, s * 3 + 1); if (hash(s, 77) > 0.35) rec(px, py, a + 0.6, depth + 1, s * 3 + 2); }
    };
    rec(x0, y0, ang, 0, seed + 1);
}
/** respiratory complex I: L-shaped, membrane arm in the bilayer, peripheral arm into the matrix (-y). */
export function complexI(G, x, k, seed) {
    const c = rgb(G, 'cplx');
    gouache(G, x, () => blobPath(x, 2 * k, 0, 9.5 * k, 2.6 * k, 0, seed, 0.06, 13), c, { lw: 0.8, hd: 0.5 * k });
    gouache(G, x, () => blobPath(x, -6.2 * k, -7.6 * k, 3.2 * k, 6.6 * k, 0.12, seed + 1, 0.08, 11), mixc(c, rgb(G, 'cream'), 0.12), { lw: 0.8, hd: 0.5 * k });
}
