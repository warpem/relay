// bg.js — the table: linen cloth, window light and hearth glow.
import { makeCanvas, hex, css } from './yarn.js';

const clamp = (v, a = 0, b = 1) => (v < a ? a : v > b ? b : v);
function rng(seed) { let s = seed >>> 0 || 1; return () => { s ^= s << 13; s ^= s >>> 17; s ^= s << 5; return (s >>> 0) / 4294967296; }; }

export const CLOTH = {
    light: { base: hex('#ece1cd'), amp: 34 },
    dark: { base: hex('#4a3c3e'), amp: 22 },
};

/** seamless plain-weave linen tile with slubby threads (per-pixel, tile only) */
function linenTile(theme, rand) {
    const S = 256, P = 4, c = makeCanvas(S, S), ctx = c.getContext('2d'), img = ctx.createImageData(S, S), d = img.data;
    const { base, amp } = CLOTH[theme];
    const rows = S / P;
    // per-thread thickness and periodic slub variation along the thread
    const rowT = Float32Array.from({ length: rows }, () => 0.75 + rand() * 0.5), colT = Float32Array.from({ length: rows }, () => 0.75 + rand() * 0.5);
    const slub = (i, x, ph) => { let v = 0; for (let f = 1; f <= 3; f++) v += Math.sin((x / S) * 6.2832 * (f * 3 + (i % 5)) + ph[i * 3 + f - 1]) / f; return v; };
    const phR = Float32Array.from({ length: rows * 3 }, () => rand() * 6.28), phC = Float32Array.from({ length: rows * 3 }, () => rand() * 6.28);
    for (let y = 0; y < S; y++) for (let x = 0; x < S; x++) {
        const ry = (y / P) | 0, cx = (x / P) | 0, fy = (y % P + 0.5) / P, fx = (x % P + 0.5) / P;
        const over = (ry + cx) & 1;
        const weft = (1 - Math.abs(fy - 0.5) * 2 * (0.9 / rowT[ry])) * (0.92 + 0.08 * slub(ry, x, phR));
        const warp = (1 - Math.abs(fx - 0.5) * 2 * (0.9 / colT[cx])) * (0.92 + 0.08 * slub(cx, y, phC));
        let v = over ? weft : warp;
        v = clamp(v, 0, 1.2) - 0.55 + (rand() - 0.5) * 0.12;
        const i = (y * S + x) * 4, warm = v * amp;
        d[i] = clamp(base[0] + warm, 0, 255); d[i + 1] = clamp(base[1] + warm * 0.95, 0, 255); d[i + 2] = clamp(base[2] + warm * 0.85, 0, 255); d[i + 3] = 255;
    }
    ctx.putImageData(img, 0, 0);
    return c;
}

export function buildBackdrop(W, H, theme, seed, focus) {
    const rand = rng(seed + (theme === 'light' ? 11 : 23)), c = makeCanvas(W, H), ctx = c.getContext('2d');
    ctx.fillStyle = ctx.createPattern(linenTile(theme, rand), 'repeat'); ctx.fillRect(0, 0, W, H);
    const lt = theme === 'light';
    // soft mottling of the cloth
    for (let i = 0; i < 14; i++) {
        const x = rand() * W, y = rand() * H, r = 150 + rand() * 380, g = ctx.createRadialGradient(x, y, 0, x, y, r);
        const col = rand() < 0.5 ? (lt ? [255, 250, 240] : [90, 60, 45]) : (lt ? [190, 160, 120] : [10, 5, 8]);
        g.addColorStop(0, css(col, lt ? 0.07 : 0.08)); g.addColorStop(1, css(col, 0));
        ctx.fillStyle = g; ctx.fillRect(x - r, y - r, 2 * r, 2 * r);
    }
    // pressed creases from the cloth having been folded: one vertical, one horizontal, near the edges
    const crease = (x0, y0, x1, y1, w) => {
        const nx = -(y1 - y0), ny = x1 - x0, l = Math.hypot(nx, ny), ux = nx / l, uy = ny / l;
        const g = ctx.createLinearGradient((x0 + x1) / 2 - ux * w, (y0 + y1) / 2 - uy * w, (x0 + x1) / 2 + ux * w, (y0 + y1) / 2 + uy * w);
        g.addColorStop(0, 'rgba(0,0,0,0)');
        g.addColorStop(0.45, lt ? 'rgba(255,252,244,0.16)' : 'rgba(120,80,60,0.08)');
        g.addColorStop(0.5, lt ? 'rgba(150,120,85,0.11)' : 'rgba(0,0,0,0.14)');
        g.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.fillStyle = g; ctx.beginPath();
        ctx.moveTo(x0 - ux * w, y0 - uy * w); ctx.lineTo(x1 - ux * w, y1 - uy * w); ctx.lineTo(x1 + ux * w, y1 + uy * w); ctx.lineTo(x0 + ux * w, y0 + uy * w); ctx.fill();
    };
    crease(W * 0.83, -10, W * 0.835, H + 10, 26);
    crease(-10, H * 0.16, W + 10, H * 0.158, 26);
    // static light: window from the upper left by day; the hearth (lower left) at night
    if (lt) {
        const g = ctx.createLinearGradient(0, 0, W, H);
        g.addColorStop(0, 'rgba(255,246,225,0.35)'); g.addColorStop(0.6, 'rgba(255,240,220,0)'); g.addColorStop(1, 'rgba(150,110,80,0.16)');
        ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
    } else {
        // static part of the hearth light; the flickering falloff is applied per frame over everything
        ctx.globalCompositeOperation = 'screen';
        const fx = -W * 0.06, fy = H * 1.08, D2 = Math.hypot(W, H);
        const g2 = ctx.createRadialGradient(fx, fy, 0, fx, fy, D2 * 0.6);
        g2.addColorStop(0, 'rgba(255,150,70,0.30)'); g2.addColorStop(0.45, 'rgba(210,95,45,0.10)'); g2.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.fillStyle = g2; ctx.fillRect(0, 0, W, H);
        ctx.globalCompositeOperation = 'source-over';
    }
    return c;
}

/** low-res window-light map for 'multiply': warm shade everywhere except the four sunlit panes */
export function buildWindowLight(W, H) {
    const s = 8, w = Math.ceil(W / s) * 2, h = Math.ceil(H / s) * 2, c = makeCanvas(w, h), ctx = c.getContext('2d');
    ctx.fillStyle = 'rgb(232,214,194)'; ctx.fillRect(0, 0, w, h);
    ctx.save();
    ctx.translate(w * 0.5, h * 0.5);
    ctx.transform(1, 0.16, -0.38, 1, 0, 0);         // slanting afternoon light
    const pw = w * 0.15, ph = h * 0.19, gap = w * 0.011;
    ctx.fillStyle = 'rgb(255,252,246)';
    for (let i = 0; i < 2; i++) for (let j = 0; j < 2; j++) ctx.fillRect(-pw - gap / 2 + i * (pw + gap), -ph - gap / 2 + j * (ph + gap), pw, ph);
    ctx.restore();
    // soften: blend a blurred copy over the sharp one (window light has a soft penumbra)
    const t = makeCanvas(w / 4, h / 4); t.getContext('2d').drawImage(c, 0, 0, w / 4, h / 4);
    const out = makeCanvas(w, h), o = out.getContext('2d'); o.imageSmoothingQuality = 'high';
    o.drawImage(c, 0, 0); o.globalAlpha = 0.6; o.drawImage(t, 0, 0, w, h);
    return out;
}
