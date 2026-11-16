// Astigmatism polar plot. Points are [index, x, y, color, angle, magnitude] with x, y = Δ·(cos 2θ, sin 2θ)
// and positive y drawn downward, as in desktop Warp. The ellipse is [meanX, meanY, sxx, syy, sxy, sigma].
const states = new Map();
const LABEL_GAP = 6, POINT_RADIUS = 2.5;

export function initialize(canvas, dotnet, id) {
    if (!canvas) return;
    dispose(id);
    const s = { canvas, dotnet, points: [], radius: .1, selected: -1, hover: -1, ellipse: null,
        base: document.createElement('canvas'), size: 0, dpr: 1, ready: false, disposed: false };
    states.set(id, s);
    s.resize = new ResizeObserver(() => drawBase(s));
    s.resize.observe(canvas);
    s.move = e => {
        if (!s.ready || s.disposed) return;
        const r = canvas.getBoundingClientRect(), x = e.clientX - r.left, y = e.clientY - r.top;
        let closest = -1, best = 64;
        for (let i = 0; i < s.points.length; i++) {
            const p = s.points[i], d = (s.cx + p[1] * s.scale - x) ** 2 + (s.cy + p[2] * s.scale - y) ** 2;
            if (d < best) { best = d; closest = i; }
        }
        if (s.hover === closest) return;
        s.hover = closest;
        const p = s.points[closest];
        canvas.title = p ? `#${(p[0] + 1).toLocaleString()}: ${p[5].toFixed(3)} µm, ${p[4].toFixed(0)}°` : 'Astigmatism; 0° and 180° are equivalent';
        paint(s);
    };
    s.leave = () => { s.hover = -1; paint(s); };
    s.click = () => { const p = s.points[s.hover]; if (!s.disposed && s.ready && p) s.dotnet.invokeMethodAsync('SelectMovie', p[0]).catch(() => {}); };
    canvas.addEventListener('pointermove', s.move);
    canvas.addEventListener('pointerleave', s.leave);
    canvas.addEventListener('click', s.click);
}

export function setData(id, points, radius, selected, ellipse) {
    const s = states.get(id);
    if (!s) return;
    Object.assign(s, { points, hover: -1, radius, selected, ellipse });
    drawBase(s);
}

export function setView(id, radius, selected, ellipse) {
    const s = states.get(id);
    if (!s) return;
    const changed = s.radius !== radius;
    Object.assign(s, { radius, selected, ellipse });
    changed ? drawBase(s) : paint(s);
}

// Places a label so the point (hx, hy) of its glyph box (0 = left/top, 1 = right/bottom) lands at (x, y).
function label(ctx, text, x, y, hx, hy) {
    const m = ctx.measureText(text);
    const width = m.actualBoundingBoxLeft + m.actualBoundingBoxRight, height = m.actualBoundingBoxAscent + m.actualBoundingBoxDescent;
    ctx.fillText(text, x - hx * width + m.actualBoundingBoxLeft, y - hy * height + m.actualBoundingBoxAscent);
}

function drawBase(s) {
    if (s.disposed) return;
    const size = s.canvas.getBoundingClientRect().width;
    if (!Number.isFinite(size) || size < 60) { s.ready = false; return; }
    s.ready = true;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    s.size = size; s.dpr = dpr;
    for (const c of [s.canvas, s.base]) { c.width = Math.round(size * dpr); c.height = Math.round(size * dpr); }
    const ctx = s.base.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, size, size);
    const style = getComputedStyle(s.canvas);
    const fg = style.getPropertyValue('--colorNeutralForeground3').trim() || '#666';
    const line = style.getPropertyValue('--colorNeutralStroke2').trim() || '#ddd';
    // Shared with the particle histogram, whose plot area spans the same height as this circle.
    const inset = parseFloat(style.getPropertyValue('--plot-inset')) || 28;
    s.cx = s.cy = size / 2;
    const r = s.r = size / 2 - inset;
    s.scale = r / s.radius;
    ctx.strokeStyle = line; ctx.lineWidth = 1;
    for (const f of [.5, 1]) { ctx.beginPath(); ctx.arc(s.cx, s.cy, r * f, 0, Math.PI * 2); ctx.stroke(); }
    ctx.globalAlpha = .5;
    ctx.beginPath(); ctx.moveTo(s.cx - r, s.cy); ctx.lineTo(s.cx + r, s.cy); ctx.moveTo(s.cx, s.cy - r); ctx.lineTo(s.cx, s.cy + r); ctx.stroke();
    ctx.globalAlpha = 1;
    ctx.fillStyle = fg; ctx.font = `${style.fontSize} ${style.fontFamily}`; ctx.textAlign = 'left'; ctx.textBaseline = 'alphabetic';
    const out = r + LABEL_GAP, diagonal = out * Math.SQRT1_2;
    label(ctx, '135°', s.cx, s.cy - out, .5, 1);
    label(ctx, '45°', s.cx, s.cy + out, .5, 0);
    label(ctx, '0°', s.cx + out, s.cy, 0, .5);
    label(ctx, '90°', s.cx - out, s.cy, 1, .5);
    // The radius label sits at the 22.5° mark (45° on screen, down and right), as far out as the angles.
    label(ctx, `${Number(s.radius.toPrecision(3))} µm`, s.cx + diagonal, s.cy + diagonal, 0, 0);
    ctx.save();
    ctx.beginPath(); ctx.arc(s.cx, s.cy, r, 0, Math.PI * 2); ctx.clip();
    const groups = new Map();
    for (const p of s.points) {
        let path = groups.get(p[3]);
        if (!path) { path = new Path2D(); groups.set(p[3], path); }
        const x = s.cx + p[1] * s.scale, y = s.cy + p[2] * s.scale;
        path.moveTo(x + POINT_RADIUS, y); path.arc(x, y, POINT_RADIUS, 0, Math.PI * 2);
    }
    ctx.globalAlpha = .75;
    for (const [color, path] of groups) { ctx.fillStyle = color; ctx.fill(path); }
    ctx.restore();
    paint(s);
}

function paint(s) {
    if (!s.ready || s.disposed) return;
    const ctx = s.canvas.getContext('2d');
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, s.canvas.width, s.canvas.height);
    ctx.drawImage(s.base, 0, 0);
    ctx.setTransform(s.dpr, 0, 0, s.dpr, 0, 0);
    const style = getComputedStyle(s.canvas);
    const brand = style.getPropertyValue('--colorBrandForeground1').trim() || '#0078d4';
    const halo = style.getPropertyValue('--colorNeutralBackground1').trim() || '#fff';
    if (s.ellipse) {
        // Mahalanobis contour: mean + sigma * L * (cos t, sin t), with covariance = L Lᵀ.
        const [mx, my, sxx, syy, sxy, sigma] = s.ellipse;
        const a = Math.sqrt(sxx), b = sxy / a, c = Math.sqrt(Math.max(0, syy - b * b));
        ctx.save();
        ctx.beginPath(); ctx.arc(s.cx, s.cy, s.r, 0, Math.PI * 2); ctx.clip();
        ctx.beginPath();
        for (let i = 0; i <= 96; i++) {
            const t = i / 96 * Math.PI * 2, u = Math.cos(t), v = Math.sin(t);
            const x = s.cx + (mx + sigma * a * u) * s.scale, y = s.cy + (my + sigma * (b * u + c * v)) * s.scale;
            i ? ctx.lineTo(x, y) : ctx.moveTo(x, y);
        }
        ctx.strokeStyle = brand; ctx.lineWidth = 1.5; ctx.setLineDash([4, 3]); ctx.stroke(); ctx.setLineDash([]);
        const x = s.cx + mx * s.scale, y = s.cy + my * s.scale;
        ctx.beginPath(); ctx.moveTo(x - 4, y); ctx.lineTo(x + 4, y); ctx.moveTo(x, y - 4); ctx.lineTo(x, y + 4); ctx.stroke();
        ctx.restore();
    }
    const marks = [[s.points.findIndex(p => p[0] === s.selected), brand], [s.hover, style.getPropertyValue('--colorNeutralForeground1').trim() || '#333']];
    for (const [index, color] of marks) {
        const p = s.points[index];
        if (!p) continue;
        const x = s.cx + p[1] * s.scale, y = s.cy + p[2] * s.scale;
        ctx.beginPath(); ctx.arc(x, y, 5, 0, Math.PI * 2);
        ctx.strokeStyle = halo; ctx.lineWidth = 4; ctx.stroke();
        ctx.strokeStyle = color; ctx.lineWidth = 2; ctx.stroke();
    }
}

export function dispose(id) {
    const s = states.get(id);
    if (!s) return;
    s.disposed = true; s.ready = false;
    s.resize.disconnect();
    s.canvas.removeEventListener('pointermove', s.move);
    s.canvas.removeEventListener('pointerleave', s.leave);
    s.canvas.removeEventListener('click', s.click);
    states.delete(id);
}
