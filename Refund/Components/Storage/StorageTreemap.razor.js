// Cushion treemap (van Wijk & van de Wetering, 1999) over a squarified layout. The base image is rendered per pixel
// into ImageData once per layout; hover outlines go on a second canvas so moving the pointer never re-renders tiles.

const states = new Map();

const NODE_BYTES = 32;
const KIND = { Root: 0, Project: 1, Space: 2, Folder: 3, Job: 4, JobFolder: 5, Files: 6, Leftovers: 7, Leftover: 8, Others: 9 };
const GROUP_KINDS = new Set([KIND.Root, KIND.Project, KIND.Space, KIND.Folder, KIND.Leftovers]);
const HEADER = 17, PAD = 2, CUSHION_H = 0.5, CUSHION_F = 0.75;
const AMBIENT = 0.48, DIFFUSE = 0.62;
const LIGHT = (() => { const l = [-1, -2, 10]; const n = Math.hypot(...l); return l.map(v => v / n); })();

// Validated palettes (see the dataviz notes in StorageTreemap.razor.cs).
const PALETTE = {
    light: {
        age: ['#86b6ef', '#5598e7', '#2a78d6', '#1c5cab', '#0d366b'],
        stage: ['#2a78d6', '#eb6834', '#1baf7a'],
        neutral: '#c9c8c0', leftover: '#9a988f', unknown: '#d9d8d2',
    },
    dark: {
        age: ['#256abf', '#3987e5', '#6da7ec', '#9ec5f4', '#cde2fb'],
        stage: ['#3987e5', '#d95926', '#199e70'],
        neutral: '#66655f', leftover: '#8a887f', unknown: '#55544f',
    },
};

export function create(id, canvas, overlay, tooltip, dotnet) {
    const s = {
        id, canvas, overlay, tooltip, dotnet, disposed: false,
        n: 0, parent: null, bytes: null, files: null, age: null, kind: null, stage: null, person: null, ageBin: null,
        labels: [], details: [], view: { metric: 'bytes', colorBy: 'age', dark: false, highlight: -1 },
        rects: null, value: null, firstChild: null, nextSibling: null, hover: -1, dpr: 1, frame: 0, lastSize: [0, 0],
    };
    states.set(id, s);
    s.observer = new ResizeObserver(() => schedule(s, true));
    s.observer.observe(canvas.parentElement);
    s.onMove = e => onMove(s, e);
    s.onLeave = () => setHover(s, -1, null);
    s.onClick = e => onClick(s, e, false);
    s.onDouble = e => onClick(s, e, true);
    canvas.addEventListener('pointermove', s.onMove);
    canvas.addEventListener('pointerleave', s.onLeave);
    canvas.addEventListener('click', s.onClick);
    canvas.addEventListener('dblclick', s.onDouble);
    schedule(s, true);
}

export function setData(id, nodes, labels, details) {
    const s = states.get(id);
    if (!s) return;
    const bytes = nodes instanceof Uint8Array ? nodes : new Uint8Array(nodes);
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const n = Math.floor(bytes.byteLength / NODE_BYTES);
    s.n = n;
    s.parent = new Int32Array(n); s.bytes = new Float64Array(n); s.files = new Float64Array(n); s.age = new Float32Array(n);
    s.kind = new Uint8Array(n); s.stage = new Uint8Array(n); s.person = new Uint8Array(n); s.ageBin = new Uint8Array(n);
    for (let i = 0; i < n; i++) {
        const o = i * NODE_BYTES;
        s.parent[i] = view.getInt32(o, true);
        s.bytes[i] = view.getFloat64(o + 4, true);
        s.files[i] = view.getFloat64(o + 12, true);
        s.age[i] = view.getFloat32(o + 20, true);
        s.kind[i] = bytes[o + 24]; s.stage[i] = bytes[o + 25]; s.person[i] = bytes[o + 26]; s.ageBin[i] = bytes[o + 27];
    }
    s.labels = labels || []; s.details = details || [];
    // Children lists (nodes arrive in pre-order: a parent precedes its children).
    s.firstChild = new Int32Array(n).fill(-1); s.nextSibling = new Int32Array(n).fill(-1);
    const last = new Int32Array(n).fill(-1);
    for (let i = 1; i < n; i++) {
        const p = s.parent[i];
        if (p < 0) continue;
        if (last[p] < 0) s.firstChild[p] = i; else s.nextSibling[last[p]] = i;
        last[p] = i;
    }
    s.hover = -1;
    schedule(s, true);
}

export function setView(id, view) {
    const s = states.get(id);
    if (!s) return;
    const relayout = view.metric !== s.view.metric;
    const recolor = relayout || view.colorBy !== s.view.colorBy || view.dark !== s.view.dark;
    s.view = { ...s.view, ...view };
    if (recolor) schedule(s, relayout || true);
    else drawOverlay(s);
}

export function dispose(id) {
    const s = states.get(id);
    if (!s) return;
    s.disposed = true;
    s.observer.disconnect();
    s.canvas.removeEventListener('pointermove', s.onMove);
    s.canvas.removeEventListener('pointerleave', s.onLeave);
    s.canvas.removeEventListener('click', s.onClick);
    s.canvas.removeEventListener('dblclick', s.onDouble);
    cancelAnimationFrame(s.frame);
    states.delete(id);
}

function schedule(s) {
    cancelAnimationFrame(s.frame);
    s.frame = requestAnimationFrame(() => { if (!s.disposed) render(s); });
}

// ---- Layout ----

function computeValues(s) {
    const leaf = s.view.metric === 'files' ? s.files : s.bytes;
    const value = new Float64Array(s.n);
    for (let i = s.n - 1; i >= 0; i--) {
        value[i] += leaf[i];
        if (s.parent[i] >= 0) value[s.parent[i]] += value[i];
    }
    return value;
}

function children(s, i) {
    const result = [];
    for (let c = s.firstChild[i]; c >= 0; c = s.nextSibling[c]) if (s.value[c] > 0) result.push(c);
    result.sort((a, b) => s.value[b] - s.value[a]);
    return result;
}

function layout(s, width, height) {
    s.value = computeValues(s);
    s.rects = new Float32Array(s.n * 4).fill(-1);
    s.cushion = new Float32Array(s.n * 4);
    s.header = new Uint8Array(s.n);
    if (s.n === 0 || s.value[0] <= 0) return;
    place(s, 0, 0, 0, width, height, CUSHION_H, [0, 0, 0, 0]);
}

function place(s, i, x0, y0, x1, y1, h, surface) {
    const r = i * 4;
    s.rects[r] = x0; s.rects[r + 1] = y0; s.rects[r + 2] = x1; s.rects[r + 3] = y1;
    const sf = surface.slice();
    const w = x1 - x0, hh = y1 - y0;
    const kids = children(s, i);
    const group = GROUP_KINDS.has(s.kind[i]) && kids.length > 0;

    // Groups get a label band and a little padding when there is room; their contents get their own cushions.
    let cx0 = x0, cy0 = y0, cx1 = x1, cy1 = y1;
    if (group && i !== 0 && w > 90 * s.dpr && hh > 54 * s.dpr) {
        s.header[i] = 1;
        cy0 += HEADER * s.dpr; cx0 += PAD * s.dpr; cx1 -= PAD * s.dpr; cy1 -= PAD * s.dpr;
    } else if (group && i !== 0 && w > 12 * s.dpr && hh > 12 * s.dpr) {
        cx0 += 1; cy0 += 1; cx1 -= 1; cy1 -= 1;
    }
    if (!group || s.header[i]) {
        addRidge(sf, 0, cx0, cx1, h);
        addRidge(sf, 2, cy0, cy1, h);
    }
    const c = i * 4;
    s.cushion[c] = sf[0]; s.cushion[c + 1] = sf[1]; s.cushion[c + 2] = sf[2]; s.cushion[c + 3] = sf[3];
    if (kids.length === 0 || cx1 - cx0 < 1 || cy1 - cy0 < 1) return;

    const total = s.value[i] - (s.view.metric === 'files' ? s.files[i] : s.bytes[i]);
    if (total <= 0) return;
    squarify(s, kids, total, cx0, cy0, cx1, cy1, (k, a, b, cc, d) => place(s, k, a, b, cc, d, s.header[i] || !group ? h * CUSHION_F : h, sf));
}

function addRidge(sf, o, a, b, h) {
    if (b - a <= 0) return;
    sf[o] += 4 * h * (b + a) / (b - a);
    sf[o + 1] -= 4 * h / (b - a);
}

// Bruls, Huizing & van Wijk: rows along the shorter side, each kept while it improves the worst aspect ratio.
function squarify(s, kids, total, x0, y0, x1, y1, emit) {
    let start = 0, rx0 = x0, ry0 = y0, rx1 = x1, ry1 = y1, remaining = total;
    while (start < kids.length) {
        const w = rx1 - rx0, h = ry1 - ry0;
        if (w <= 0 || h <= 0) return;
        const side = Math.min(w, h), area = w * h, scale = area / remaining;
        let end = start, rowSum = 0, worst = Infinity;
        while (end < kids.length) {
            const v = s.value[kids[end]] * scale;
            const sum = rowSum + v;
            const maxV = s.value[kids[start]] * scale, minV = v;
            const ratio = Math.max(side * side * maxV / (sum * sum), sum * sum / (side * side * minV));
            if (ratio > worst) break;
            worst = ratio; rowSum = sum; end++;
        }
        const thickness = rowSum / side;
        let offset = 0;
        for (let k = start; k < end; k++) {
            const length = s.value[kids[k]] * scale / thickness;
            if (w >= h) emit(kids[k], rx0, ry0 + offset, rx0 + thickness, k === end - 1 ? ry1 : ry0 + offset + length);
            else emit(kids[k], rx0 + offset, ry0, k === end - 1 ? rx1 : rx0 + offset + length, ry0 + thickness);
            offset += length;
        }
        for (let k = start; k < end; k++) remaining -= s.value[kids[k]];
        if (w >= h) rx0 += thickness; else ry0 += thickness;
        start = end;
    }
}

// ---- Rendering ----

function css(el, name, fallback) {
    return getComputedStyle(el).getPropertyValue(name).trim() || fallback;
}

function hex(c) {
    const v = parseInt(c.slice(1), 16);
    return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
}

function colorOf(s, i) {
    const p = s.view.dark ? PALETTE.dark : PALETTE.light;
    const kind = s.kind[i];
    if (kind === KIND.Leftover || kind === KIND.Leftovers) return p.leftover;
    if (kind === KIND.Others) {
        const parentKind = s.kind[s.parent[i]];
        if (parentKind !== KIND.Job && parentKind !== KIND.JobFolder) return p.neutral;
    }
    switch (s.view.colorBy) {
        case 'stage': return s.stage[i] < 3 ? p.stage[s.stage[i]] : p.neutral;
        // The same three hues as stages: the most any two neighbouring tiles can tell apart.
        case 'owner': return s.person[i] < 3 ? p.stage[s.person[i]] : p.neutral;
        default: return s.ageBin[i] < 5 ? p.age[s.ageBin[i]] : p.unknown;
    }
}

function render(s) {
    const started = performance.now();
    renderNow(s);
    // Exposed for diagnostics (how long a full layout and cushion pass takes).
    s.canvas.dataset.renderMs = Math.round(performance.now() - started);
}

function renderNow(s) {
    const box = s.canvas.parentElement.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const W = Math.max(1, Math.floor(box.width * dpr)), H = Math.max(1, Math.floor(box.height * dpr));
    s.dpr = dpr;
    for (const c of [s.canvas, s.overlay]) {
        c.width = W; c.height = H;
        c.style.width = box.width + 'px'; c.style.height = box.height + 'px';
    }
    if (Math.abs(box.width - s.lastSize[0]) > 2 || Math.abs(box.height - s.lastSize[1]) > 2) {
        s.lastSize = [box.width, box.height];
        s.dotnet.invokeMethodAsync('Resized', box.width, box.height).catch(() => { });
    }
    const ctx = s.canvas.getContext('2d');
    const surface = css(s.canvas, '--colorNeutralBackground1', s.view.dark ? '#333333' : '#ffffff');
    ctx.fillStyle = surface;
    ctx.fillRect(0, 0, W, H);
    if (s.n === 0) { drawOverlay(s); return; }

    layout(s, W, H);
    const image = ctx.getImageData(0, 0, W, H);
    const data = image.data;
    for (let i = 0; i < s.n; i++) {
        if (s.firstChild[i] >= 0 && hasVisibleChild(s, i)) continue;
        const r = i * 4;
        const x0 = Math.round(s.rects[r]), y0 = Math.round(s.rects[r + 1]), x1 = Math.round(s.rects[r + 2]), y1 = Math.round(s.rects[r + 3]);
        if (x1 <= x0 || y1 <= y0 || s.rects[r] < 0) continue;
        const [cr, cg, cb] = hex(colorOf(s, i));
        const s1x = s.cushion[r], s2x = s.cushion[r + 1], s1y = s.cushion[r + 2], s2y = s.cushion[r + 3];
        for (let y = y0; y < y1; y++) {
            const ny = -(2 * s2y * (y + 0.5) + s1y);
            let o = (y * W + x0) * 4;
            for (let x = x0; x < x1; x++, o += 4) {
                const nx = -(2 * s2x * (x + 0.5) + s1x);
                const cos = (nx * LIGHT[0] + ny * LIGHT[1] + LIGHT[2]) / Math.sqrt(nx * nx + ny * ny + 1);
                const k = AMBIENT + (cos > 0 ? cos * DIFFUSE : 0);
                data[o] = Math.min(255, cr * k); data[o + 1] = Math.min(255, cg * k); data[o + 2] = Math.min(255, cb * k); data[o + 3] = 255;
            }
        }
    }
    ctx.putImageData(image, 0, 0);
    drawLabels(s, ctx, surface);
    drawFolderLabels(s, ctx, css(s.canvas, '--fontFamilyBase', 'system-ui, sans-serif'));
    drawOverlay(s);
}

function hasVisibleChild(s, i) {
    for (let c = s.firstChild[i]; c >= 0; c = s.nextSibling[c]) if (s.value[c] > 0 && s.rects[c * 4] >= 0) return true;
    return false;
}

function drawLabels(s, ctx, surface) {
    const d = s.dpr;
    const font = css(s.canvas, '--fontFamilyBase', 'system-ui, sans-serif');
    const ink = css(s.canvas, '--colorNeutralForeground2', s.view.dark ? '#d6d6d6' : '#424242');
    const band = css(s.canvas, '--colorNeutralBackground3', s.view.dark ? '#2a2a2a' : '#f5f5f5');
    ctx.textBaseline = 'middle';
    for (let i = 1; i < s.n; i++) {
        const r = i * 4;
        if (s.rects[r] < 0) continue;
        const x0 = s.rects[r], y0 = s.rects[r + 1], x1 = s.rects[r + 2], y1 = s.rects[r + 3];
        const w = x1 - x0, h = y1 - y0;
        if (s.header[i]) {
            ctx.fillStyle = band;
            ctx.fillRect(x0, y0, w, HEADER * d);
            ctx.fillStyle = ink;
            ctx.font = `600 ${11.5 * d}px ${font}`;
            fitText(ctx, s.labels[i], x0 + 6 * d, y0 + HEADER * d / 2 + 0.5 * d, w - 12 * d, valueText(s, i));
        } else if ((s.kind[i] === KIND.Job || s.kind[i] === KIND.Leftover) && w > 110 * d && h > 34 * d) {
            const [cr, cg, cb] = hex(colorOf(s, i));
            const light = (0.2126 * cr + 0.7152 * cg + 0.0722 * cb) / 255 > 0.55;
            ctx.font = `600 ${11 * d}px ${font}`;
            ctx.fillStyle = light ? 'rgba(20,20,20,0.9)' : 'rgba(255,255,255,0.95)';
            ctx.shadowColor = light ? 'rgba(255,255,255,0.45)' : 'rgba(0,0,0,0.45)';
            ctx.shadowBlur = 2 * d;
            fitText(ctx, s.labels[i], x0 + 5 * d, y0 + 10 * d, w - 10 * d, null);
            if (h > 48 * d) {
                ctx.font = `${10.5 * d}px ${font}`;
                fitText(ctx, valueText(s, i), x0 + 5 * d, y0 + 24 * d, w - 10 * d, null);
            }
            ctx.shadowBlur = 0;
        }
    }
}

// Folders inside big jobs: one line each, biggest first, never over another label (the job's included).
function drawFolderLabels(s, ctx, font) {
    const d = s.dpr;
    const placed = [];
    const overlaps = (r) => placed.some(p => r[0] < p[2] && p[0] < r[2] && r[1] < p[3] && p[1] < r[3]);
    for (let i = 1; i < s.n; i++) {
        const r = i * 4;
        if (s.rects[r] < 0) continue;
        if ((s.kind[i] === KIND.Job || s.kind[i] === KIND.Leftover) && s.rects[r + 2] - s.rects[r] > 110 * d && s.rects[r + 3] - s.rects[r + 1] > 34 * d)
            placed.push([s.rects[r], s.rects[r + 1], s.rects[r] + 170 * d, s.rects[r + 1] + (s.rects[r + 3] - s.rects[r + 1] > 48 * d ? 32 : 18) * d]);
    }
    const folders = [];
    for (let i = 1; i < s.n; i++) {
        const r = i * 4;
        if (s.rects[r] < 0 || (s.kind[i] !== KIND.JobFolder && s.kind[i] !== KIND.Files)) continue;
        const w = s.rects[r + 2] - s.rects[r], h = s.rects[r + 3] - s.rects[r + 1];
        if (w > 120 * d && h > 24 * d) folders.push(i);
    }
    folders.sort((a, b) => s.value[b] - s.value[a]);
    ctx.font = `${10.5 * d}px ${font}`;
    ctx.textBaseline = 'middle';
    for (const i of folders) {
        const r = i * 4;
        const x0 = s.rects[r], y0 = s.rects[r + 1], x1 = s.rects[r + 2], y1 = s.rects[r + 3];
        const text = `${s.labels[i]}  ·  ${valueText(s, i)}`;
        const width = Math.min(x1 - x0 - 10 * d, ctx.measureText(text).width);
        // Top-left of the tile, or lower down its left edge if a label is already there.
        let box = null;
        for (let y = y0 + 3 * d; y + 16 * d <= y1 - 2 * d && y < y0 + 90 * d; y += 16 * d) {
            const candidate = [x0 + 3 * d, y, x0 + 7 * d + width, y + 15 * d];
            if (!overlaps(candidate)) { box = candidate; break; }
        }
        if (!box) continue;
        placed.push(box);
        const [cr, cg, cb] = hex(colorOf(s, i));
        const light = (0.2126 * cr + 0.7152 * cg + 0.0722 * cb) / 255 > 0.55;
        ctx.fillStyle = light ? 'rgba(20,20,20,0.8)' : 'rgba(255,255,255,0.85)';
        ctx.shadowColor = light ? 'rgba(255,255,255,0.4)' : 'rgba(0,0,0,0.4)';
        ctx.shadowBlur = 2 * d;
        fitText(ctx, s.labels[i], box[0] + 2 * d, box[1] + 7.5 * d, x1 - x0 - 10 * d, valueText(s, i));
        ctx.shadowBlur = 0;
    }
}

function fitText(ctx, text, x, y, maxWidth, trailing) {
    if (!text || maxWidth < 12) return;
    let full = trailing ? `${text}  ·  ${trailing}` : text;
    if (ctx.measureText(full).width <= maxWidth) { ctx.fillText(full, x, y); return; }
    if (trailing && ctx.measureText(text).width <= maxWidth) { ctx.fillText(text, x, y); return; }
    let lo = 0, hi = text.length;
    while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        if (ctx.measureText(text.slice(0, mid) + '…').width <= maxWidth) lo = mid; else hi = mid - 1;
    }
    if (lo > 1) ctx.fillText(text.slice(0, lo) + '…', x, y);
}

// ---- Interaction ----

function nodeAt(s, px, py) {
    if (!s.rects || s.n === 0) return -1;
    let i = 0;
    for (; ;) {
        let next = -1;
        for (let c = s.firstChild[i]; c >= 0; c = s.nextSibling[c]) {
            const r = c * 4;
            if (s.rects[r] < 0) continue;
            if (px >= s.rects[r] && px < s.rects[r + 2] && py >= s.rects[r + 1] && py < s.rects[r + 3]) { next = c; break; }
        }
        if (next < 0) return i;
        i = next;
    }
}

function onMove(s, e) {
    const box = s.canvas.getBoundingClientRect();
    const i = nodeAt(s, (e.clientX - box.left) * s.dpr, (e.clientY - box.top) * s.dpr);
    setHover(s, i > 0 ? i : -1, e);
}

function onClick(s, e, zoom) {
    const box = s.canvas.getBoundingClientRect();
    const i = nodeAt(s, (e.clientX - box.left) * s.dpr, (e.clientY - box.top) * s.dpr);
    if (i <= 0) return;
    s.dotnet.invokeMethodAsync('Clicked', i, zoom).catch(() => { });
}

function jobOf(s, i) {
    for (let k = i; k > 0; k = s.parent[k]) if (s.kind[k] === KIND.Job) return k;
    return -1;
}

function setHover(s, i, e) {
    if (i !== s.hover) {
        s.hover = i;
        drawOverlay(s);
        s.canvas.style.cursor = i > 0 ? 'pointer' : 'default';
    }
    const tip = s.tooltip;
    if (i <= 0 || !e) { tip.hidden = true; return; }
    fillTooltip(s, i);
    tip.hidden = false;
    const host = s.canvas.parentElement.getBoundingClientRect();
    const x = e.clientX - host.left, y = e.clientY - host.top;
    const tw = tip.offsetWidth, th = tip.offsetHeight;
    tip.style.left = Math.max(4, Math.min(host.width - tw - 4, x + 14)) + 'px';
    tip.style.top = (y + 18 + th > host.height ? Math.max(4, y - th - 12) : y + 18) + 'px';
}

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
function size(bytes) {
    if (bytes < 1024) return `${Math.round(bytes)} B`;
    let v = bytes, u = 0;
    while (v >= 1024 && u < UNITS.length - 1) { v /= 1024; u++; }
    return `${v < 10 ? v.toFixed(1) : Math.round(v)} ${UNITS[u]}`;
}
function count(n) {
    if (n < 10000) return Math.round(n).toLocaleString('en-US');
    if (n < 1e6) return `${+(n / 1e3).toFixed(n < 1e5 ? 1 : 0)} k`;
    if (n < 1e9) return `${+(n / 1e6).toFixed(n < 1e8 ? 1 : 0)} M`;
    return `${+(n / 1e9).toFixed(1)} B`;
}
function totals(s, i) {
    // Both metrics, whichever lays the map out.
    let b = 0, f = 0;
    const stack = [i];
    while (stack.length) {
        const k = stack.pop();
        b += s.bytes[k]; f += s.files[k];
        for (let c = s.firstChild[k]; c >= 0; c = s.nextSibling[c]) stack.push(c);
    }
    return [b, f];
}
function valueText(s, i) {
    const [b, f] = totals(s, i);
    return s.view.metric === 'files' ? `${count(f)} files` : size(b);
}
function age(days) {
    if (days < 0) return null;
    if (days < 1) return 'today';
    if (days < 61) return `${Math.round(days)} day${Math.round(days) === 1 ? '' : 's'} ago`;
    if (days < 730) return `${Math.round(days / 30.44)} months ago`;
    return `${Math.round(days / 365.25)} years ago`;
}

function fillTooltip(s, i) {
    const tip = s.tooltip;
    tip.replaceChildren();
    const [b, f] = totals(s, i);
    const value = document.createElement('div');
    value.className = 'tt-value';
    value.textContent = `${size(b)} · ${count(f)} files`;
    const name = document.createElement('div');
    name.className = 'tt-name';
    const swatch = document.createElement('span');
    swatch.className = 'tt-key';
    swatch.style.background = colorOf(s, i);
    name.append(swatch, document.createTextNode(s.labels[i] || ''));
    tip.append(value, name);
    if (s.details[i]) {
        const detail = document.createElement('div');
        detail.className = 'tt-detail';
        detail.textContent = s.details[i];
        tip.append(detail);
    }
    const a = age(s.age[i]);
    if (a && s.kind[i] !== KIND.Project && s.kind[i] !== KIND.Space && s.kind[i] !== KIND.Folder) {
        const modified = document.createElement('div');
        modified.className = 'tt-detail';
        modified.textContent = `Last modified ${a}`;
        tip.append(modified);
    }
    const hint = document.createElement('div');
    hint.className = 'tt-hint';
    const k = s.kind[i];
    hint.textContent = k === KIND.Project || k === KIND.Space || k === KIND.Folder ? 'Double-click to zoom in'
        : k === KIND.Leftover || k === KIND.Leftovers || (k === KIND.Others && jobOf(s, i) < 0) ? '' : 'Click to show it in the table';
    if (hint.textContent) tip.append(hint);
}

function drawOverlay(s) {
    const ctx = s.overlay.getContext('2d');
    ctx.clearRect(0, 0, s.overlay.width, s.overlay.height);
    if (!s.rects) return;
    const d = s.dpr;
    const outline = (i, width, color) => {
        const r = i * 4;
        if (i <= 0 || s.rects[r] < 0) return;
        ctx.lineWidth = width * d;
        ctx.strokeStyle = color;
        ctx.strokeRect(s.rects[r] + ctx.lineWidth / 2, s.rects[r + 1] + ctx.lineWidth / 2,
            s.rects[r + 2] - s.rects[r] - ctx.lineWidth, s.rects[r + 3] - s.rects[r + 1] - ctx.lineWidth);
    };
    const strong = s.view.dark ? 'rgba(255,255,255,0.95)' : 'rgba(17,17,17,0.9)';
    const soft = s.view.dark ? 'rgba(255,255,255,0.55)' : 'rgba(17,17,17,0.45)';
    if (s.view.highlight > 0) outline(s.view.highlight, 2, strong);
    if (s.hover > 0) {
        const job = jobOf(s, s.hover);
        if (job > 0 && job !== s.hover) outline(job, 2, soft);
        outline(s.hover, job === s.hover ? 2 : 1, strong);
    }
}
