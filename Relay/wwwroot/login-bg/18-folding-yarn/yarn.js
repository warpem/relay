// yarn.js — one yarn, one gauge: ply-bump sprites, tinted per colourway, and the wound ball made of the same bumps.
// Lighting is baked per orientation (neutral grey), then tinted by multiply, so every part of the piece
// (loops, helices, knitted stitches, the working strand and the ball) shares exactly the same texture and scale.

export const NO = 32;           // orientations over 360° (the residual is applied as a small rotation)
export const NL = 3;            // depth / shade levels
export const NC = 10;           // dye steps along a colourway (dithered between)
export const NB = 6;            // ball colour steps
export let D = 12, SB = 26, SPACING = 5, BALL_R = 37, BALL = 112;

export function setGauge(px) {
    D = px; SB = Math.ceil(D * 2.15); SPACING = D * 0.42;
    BALL_R = Math.round(D * 3.1); BALL = Math.ceil(2 * BALL_R + SB + 6);
}

const clamp = (v, a = 0, b = 1) => (v < a ? a : v > b ? b : v);
export const hex = (s) => { const n = parseInt(s.replace('#', ''), 16); return [(n >> 16) & 255, (n >> 8) & 255, n & 255]; };
export const mixc = (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
export const css = (c, a = 1) => `rgba(${clamp(c[0], 0, 255) | 0},${clamp(c[1], 0, 255) | 0},${clamp(c[2], 0, 255) | 0},${a.toFixed(3)})`;
export function makeCanvas(w, h) { const c = document.createElement('canvas'); c.width = Math.max(1, Math.ceil(w)); c.height = Math.max(1, Math.ceil(h)); return c; }
const n3 = (x, y, z) => { const l = Math.hypot(x, y, z); return [x / l, y / l, z / l]; };
function rng(seed) { let s = seed >>> 0 || 1; return () => { s ^= s << 13; s ^= s >>> 17; s ^= s << 5; return (s >>> 0) / 4294967296; }; }

// hand-dyed tonal colourways, N → C (cool to warm, like a ribbon diagram, but soft and limited)
export const COLOURWAYS = [
    ['#5b86a8', '#86a8a2', '#d6b06c'],   // denim → sea glass → honey
    ['#6c9878', '#b3ad62', '#d38a55'],   // sage → ochre → pumpkin
    ['#7a8cbd', '#b98fae', '#dc8f80'],   // periwinkle → orchid → coral
    ['#4f8d96', '#98b06c', '#dcae4c'],   // teal → moss → mustard
    ['#8a7cae', '#c98692', '#e1a46c'],   // lavender → rose → apricot
].map(w => w.map(hex));
export function dye(way, t) {
    const w = COLOURWAYS[way]; t = clamp(t) * (w.length - 1);
    const i = Math.min(w.length - 2, Math.floor(t));
    return mixc(w[i], w[i + 1], t - i);
}

// screen-space lights (x right, y DOWN, z toward viewer)
export const LIGHT = {
    light: n3(-0.5, -0.62, 0.62),     // window, upper left
    dark: n3(-0.66, 0.42, 0.55),      // hearth, lower left
};
const FIRE = [1.0, 0.72, 0.5];
const LV = { light: [1, 0.84, 0.7], dark: [1, 0.76, 0.58] };
const NEUTRAL = 204;              // neutral grey the sprites are lit in; tint = colour / 0.8

/** wool colour for a surface normal; s = across-tube coordinate (−1..1) for occlusion */
function shade(theme, base, n, s = 0) {
    const L = LIGHT[theme], diff = Math.max(0, n[0] * L[0] + n[1] * L[1] + n[2] * L[2]), occ = 1 - 0.3 * s * s;
    if (theme === 'light') {
        const k = (0.56 + 0.5 * diff) * occ, sh = 0.08 * diff ** 4;
        return [base[0] * k + 255 * sh, base[1] * k + 244 * sh, base[2] * k + 222 * sh];
    }
    const k = (0.3 + 1.0 * diff) * occ, rim = diff * Math.abs(s) ** 3 * 0.45;
    return [base[0] * FIRE[0] * k + 140 * rim, base[1] * FIRE[1] * k + 78 * rim, base[2] * FIRE[2] * k + 34 * rim];
}

// ------------------------------------------------------------------ ply bump (one twist of a 2-ply S-twisted yarn)
function drawBump(ctx, cx, cy, theta, theme, ply, lvl, rand) {
    const R = D / 2, f = LV[theme][lvl];
    const base = [NEUTRAL * f, NEUTRAL * f, NEUTRAL * f];
    ctx.save();
    ctx.translate(cx, cy); ctx.rotate(theta);
    const g = ctx.createLinearGradient(0, -R, 0, R), cs = Math.cos(theta), sn = Math.sin(theta);
    const b = ply ? base.map(v => v * 0.88) : base;
    for (let i = 0; i <= 8; i++) {
        const s = -1 + i / 4, z = Math.sqrt(Math.max(0, 1 - s * s));
        g.addColorStop(i / 8, css(shade(theme, b, [-s * sn, s * cs, z], s)));
    }
    ctx.save();
    ctx.beginPath(); ctx.rect(-SB / 2, -R, SB, 2 * R); ctx.clip();
    const tw = 0.62;
    ctx.save(); ctx.rotate(tw); ctx.beginPath(); ctx.ellipse(0, 0, D * 0.78, D * 0.3, 0, 0, 6.2832); ctx.restore();
    ctx.fillStyle = g; ctx.fill();
    ctx.save(); ctx.rotate(tw);
    const ge = ctx.createLinearGradient(-D * 0.78, 0, D * 0.78, 0), dk = theme === 'light' ? 'rgba(60,46,40,' : 'rgba(10,5,6,';
    ge.addColorStop(0, dk + '0.38)'); ge.addColorStop(0.3, dk + '0)'); ge.addColorStop(0.7, dk + '0)'); ge.addColorStop(1, dk + '0.38)');
    ctx.beginPath(); ctx.ellipse(0, 0, D * 0.78, D * 0.3, 0, 0, 6.2832); ctx.fillStyle = ge; ctx.fill();
    ctx.lineWidth = 0.7; ctx.strokeStyle = dk + (theme === 'light' ? '0.2)' : '0.36)'); ctx.stroke();
    ctx.beginPath(); ctx.ellipse(0, -D * 0.06, D * 0.42, D * 0.07, 0, 0, 6.2832);
    ctx.restore();
    const lit = shade(theme, b.map(v => Math.min(255, v * 1.2)), [-0.4 * sn - 0.2 * cs, 0.4 * cs - 0.2 * sn, 0.9], 0.3);
    ctx.fillStyle = css(lit, theme === 'light' ? 0.2 : 0.16); ctx.fill();
    ctx.restore();
    // a few stray fibres past the silhouette
    ctx.lineCap = 'round';
    for (let i = 0; i < 6; i++) {
        const side = rand() < 0.5 ? -1 : 1, x0 = (rand() - 0.5) * D * 0.9, y0 = side * R * (0.6 + rand() * 0.35);
        const ln = 1.5 + rand() * D * 0.3, a = side * (0.5 + rand() * 1.2) + (rand() - 0.5);
        const c = shade(theme, b.map(v => Math.min(255, v * 1.12)), [-side * sn, side * cs, 0.2], 0.9);
        ctx.strokeStyle = css(c, 0.18 + rand() * 0.22); ctx.lineWidth = 0.5 + rand() * 0.4;
        ctx.beginPath(); ctx.moveTo(x0, y0);
        ctx.quadraticCurveTo(x0 + Math.cos(a) * ln * 0.6, y0 + Math.sin(a) * ln * 0.3 + side * ln * 0.5, x0 + Math.cos(a) * ln, y0 + side * ln * 0.75);
        ctx.stroke();
    }
    ctx.restore();
}

/** neutral atlas: cell (oi, ply*NL + lvl) */
export function buildNeutralBumps(theme, seed) {
    const c = makeCanvas(NO * SB, 2 * NL * SB), ctx = c.getContext('2d'), rand = rng(seed);
    for (let ply = 0; ply < 2; ply++) for (let lvl = 0; lvl < NL; lvl++) for (let oi = 0; oi < NO; oi++)
        drawBump(ctx, oi * SB + SB / 2, (ply * NL + lvl) * SB + SB / 2, oi / NO * 6.2832, theme, ply, lvl, rand);
    return c;
}

let tmp = null;
/** multiply-tint a neutral block with a colour, keeping its alpha */
function tintInto(dst, dx, dy, src, sx, sy, w, h, col) {
    if (!tmp || tmp.width < w || tmp.height < h) tmp = makeCanvas(Math.max(w, tmp ? tmp.width : 0), Math.max(h, tmp ? tmp.height : 0));
    const t = tmp.getContext('2d');
    t.globalCompositeOperation = 'source-over'; t.clearRect(0, 0, tmp.width, tmp.height);
    t.drawImage(src, sx, sy, w, h, 0, 0, w, h);
    t.globalCompositeOperation = 'multiply'; t.fillStyle = css(col.map(v => v * 255 / NEUTRAL)); t.fillRect(0, 0, w, h);
    t.globalCompositeOperation = 'destination-in'; t.drawImage(src, sx, sy, w, h, 0, 0, w, h);
    t.globalCompositeOperation = 'source-over';
    dst.getContext('2d').drawImage(tmp, 0, 0, w, h, dx, dy, w, h);
}

/** colourway atlas: cell (oi, (ci*2 + ply)*NL + lvl) */
export function tintBumps(neutral, way) {
    const bh = 2 * NL * SB, c = makeCanvas(NO * SB, NC * bh);
    for (let ci = 0; ci < NC; ci++) tintInto(c, 0, ci * bh, neutral, 0, 0, NO * SB, bh, dye(way, ci / (NC - 1)));
    return c;
}

// ------------------------------------------------------------------ yarn ball, wound from the very same yarn
export function buildNeutralBall(theme, neutral, seed) {
    const c = makeCanvas(BALL, BALL), ctx = c.getContext('2d'), rand = rng(seed), R = BALL_R, cx = BALL / 2, cy = BALL / 2;
    const L = LIGHT[theme];
    // core in shadow (shows between wraps as grooves)
    const g = ctx.createRadialGradient(cx + L[0] * R * 0.4, cy + L[1] * R * 0.4, R * 0.1, cx, cy, R);
    const lo = theme === 'light' ? 0.7 : 0.4;
    g.addColorStop(0, css([NEUTRAL * lo * 1.25, NEUTRAL * lo * 1.25, NEUTRAL * lo * 1.25])); g.addColorStop(1, css([NEUTRAL * lo * 0.6, NEUTRAL * lo * 0.6, NEUTRAL * lo * 0.6]));
    ctx.fillStyle = g; ctx.beginPath(); ctx.arc(cx, cy, R * 0.98, 0, 6.2832); ctx.fill();
    const wraps = 30, rr = R - D * 0.42;
    for (let k = 0; k < wraps; k++) {
        let ax = [rand() - 0.5, rand() - 0.5, rand() - 0.5]; const al = Math.hypot(...ax); ax = ax.map(v => v / al);
        const e1n = Math.hypot(ax[1], -ax[0]) > 0.1 ? [ax[1], -ax[0], 0] : [0, ax[2], -ax[1]];
        const l1 = Math.hypot(...e1n), e1 = e1n.map(v => v / l1);
        const e2 = [ax[1] * e1[2] - ax[2] * e1[1], ax[2] * e1[0] - ax[0] * e1[2], ax[0] * e1[1] - ax[1] * e1[0]];
        const da = SPACING / rr, a0 = rand() * 6.2832;
        let ply = 0;
        for (let a = a0; a < a0 + 6.2832; a += da) {
            const p = [0, 1, 2].map(i => e1[i] * Math.cos(a) + e2[i] * Math.sin(a));
            if (p[2] < 0.08) continue;
            const q = [0, 1, 2].map(i => e1[i] * Math.cos(a + 0.01) + e2[i] * Math.sin(a + 0.01));
            const ang = Math.atan2(q[1] - p[1], q[0] - p[0]);
            const diff = p[0] * L[0] + p[1] * L[1] + p[2] * L[2];
            const lvl = diff > 0.05 ? 0 : diff > -0.35 ? 1 : 2;
            const oi = ((Math.round((ang < 0 ? ang + 6.2832 : ang) / 6.2832 * NO) % NO) + NO) % NO;
            ctx.drawImage(neutral, oi * SB, ((ply++ & 1) * NL + lvl) * SB, SB, SB, cx + p[0] * rr - SB / 2, cy + p[1] * rr - SB / 2, SB, SB);
        }
    }
    // limb darkening over the wraps
    ctx.globalCompositeOperation = 'source-atop';
    const gl = ctx.createRadialGradient(cx, cy, R * 0.55, cx, cy, R * 1.1);
    gl.addColorStop(0, 'rgba(0,0,0,0)'); gl.addColorStop(1, theme === 'light' ? 'rgba(40,30,28,0.2)' : 'rgba(4,2,3,0.4)');
    ctx.fillStyle = gl; ctx.fillRect(0, 0, BALL, BALL);
    ctx.globalCompositeOperation = 'source-over';
    return c;
}
/** NB tinted ball cells along a colourway */
export function tintBall(neutralBall, way) {
    const c = makeCanvas(NB * BALL, BALL);
    for (let i = 0; i < NB; i++) tintInto(c, i * BALL, 0, neutralBall, 0, 0, BALL, BALL, dye(way, i / (NB - 1)));
    return c;
}

/** soft round shadow sprite */
export function buildShadowSprite() {
    const s = 128, c = makeCanvas(s, s), ctx = c.getContext('2d');
    const g = ctx.createRadialGradient(s / 2, s / 2, 0, s / 2, s / 2, s / 2);
    g.addColorStop(0, 'rgba(34,18,10,0.85)'); g.addColorStop(0.5, 'rgba(34,18,10,0.45)'); g.addColorStop(1, 'rgba(34,18,10,0)');
    ctx.fillStyle = g; ctx.fillRect(0, 0, s, s);
    return c;
}
