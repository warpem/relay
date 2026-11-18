import { loadPlane } from './mrc-parser.js';
import { isTiff, parseTiff } from './tiff-parser.js';

// Shared with main-thread viewers (the amplitude spectrum); kept exported here for existing callers.
export { loadPlane };

/**
 * One image plane from an MRC or TIFF file (told apart by their first bytes, since file URLs carry no extension).
 * MRC planes stream with range requests; a TIFF is read whole.
 */
export async function loadImage(url, layer = 0, { onProgress } = {}) {
    const head = await fetch(url, { headers: { Range: 'bytes=0-3' } });
    if (!head.ok) throw new Error(`File request failed (${head.status})`);
    const bytes = new Uint8Array(await head.arrayBuffer());
    if (!isTiff(bytes)) return loadPlane(url, layer, { onProgress });
    // A server that ignores Range already sent the whole file.
    const buffer = head.status === 206 ? await (async () => {
        const response = await fetch(url);
        if (!response.ok) throw new Error(`File request failed (${response.status})`);
        return response.arrayBuffer();
    })() : bytes.buffer;
    return parseTiff(buffer, layer);
}

export function bin2(data, width, height) {
    const w = Math.max(1, Math.floor(width / 2)), h = Math.max(1, Math.floor(height / 2));
    const next = new Float32Array(w * h);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
        const x0 = Math.min(width - 1, x * 2), x1 = Math.min(width - 1, x * 2 + 1);
        const y0 = Math.min(height - 1, y * 2), y1 = Math.min(height - 1, y * 2 + 1);
        next[y * w + x] = (data[y0 * width + x0] + data[y0 * width + x1] +
            data[y1 * width + x0] + data[y1 * width + x1]) / 4;
    }
    return { data: next, width: w, height: h };
}

/** Mean and standard deviation of the finite values whose index passes `include` (all when omitted). */
function statistics(data, include) {
    let mean = 0, variance = 0, count = 0;
    for (let i = 0; i < data.length; i++) {
        const value = data[i];
        if (!Number.isFinite(value) || (include && !include(i))) continue;
        count++;
        const delta = value - mean;
        mean += delta / count;
        variance += delta * (value - mean);
    }
    return { mean, std: count ? Math.sqrt(variance / count) : 0, count };
}

/** Masked pixels this far around junk are left out too: Warp blends restored junk into the ice over 12 Å (masks are 8 Å/pixel). */
export const ContrastMaskMargin = 2;

/** The mask grown by `margin` pixels (a disc), as a byte per pixel. */
function grownMask(mask, margin) {
    const { data, width, height } = mask, grown = new Uint8Array(width * height);
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
        if (!(data[y * width + x] >= 0.5)) continue;
        for (let dy = -margin; dy <= margin; dy++) for (let dx = -margin; dx <= margin; dx++) {
            const xx = x + dx, yy = y + dy;
            if (dx * dx + dy * dy <= margin * margin && xx >= 0 && yy >= 0 && xx < width && yy < height) grown[yy * width + xx] = 1;
        }
    }
    return grown;
}

/**
 * The display window for particles in ice, as Warp's micrograph previews compute it: mean ± 2.5 sd over the central half
 * of the plane in each dimension, leaving out the junk `mask` (any grid covering the same field, rows in plane order)
 * and its margin. Too few such pixels (under 1% of that region) fall back to all unmasked pixels, then to the central
 * half, then to the whole plane.
 */
export function particleContrast(data, width, height, mask = null) {
    const grown = mask ? grownMask(mask, ContrastMaskMargin) : null;
    const column = new Int32Array(width), row = new Int32Array(height);
    if (grown) {
        for (let x = 0; x < width; x++) column[x] = Math.min(mask.width - 1, Math.floor((x + 0.5) * mask.width / width));
        for (let y = 0; y < height; y++) row[y] = Math.min(mask.height - 1, Math.floor((y + 0.5) * mask.height / height)) * mask.width;
    }
    const x0 = Math.floor(width / 4), x1 = width - x0, y0 = Math.floor(height / 4), y1 = height - y0;
    const central = i => { const x = i % width, y = (i - x) / width; return x >= x0 && x < x1 && y >= y0 && y < y1; };
    const clear = i => !grown || !grown[row[(i - i % width) / width] + column[i % width]];
    const minimum = Math.max(16, Math.ceil((x1 - x0) * (y1 - y0) / 100));
    let stats = null;
    for (const include of grown ? [i => central(i) && clear(i), clear, central, null] : [central, null]) {
        stats = statistics(data, include);
        if (stats.count >= minimum) break;
    }
    return displayWindow(stats);
}

function displayWindow({ mean, std }) {
    return { low: mean - 2.5 * std, scale: std > 0 ? 255 / (5 * std) : 0 };
}

/** Gray (or, for a mask, translucent red) RGBA pixels; `contrast` defaults to mean ± 2.5 sd of the whole plane. */
export function pixels(data, width, height, mask = false, contrast = null) {
    const { low, scale } = contrast ?? (mask ? { low: 0, scale: 0 } : displayWindow(statistics(data)));
    const rgba = new Uint8ClampedArray(width * height * 4);
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
        const value = data[(height - 1 - y) * width + x];
        const i = (y * width + x) * 4;
        const gray = Math.max(0, Math.min(255, (value - low) * scale));
        rgba[i] = mask ? 255 : gray;
        rgba[i + 1] = rgba[i + 2] = mask ? 0 : gray;
        rgba[i + 3] = mask ? Math.max(0, Math.min(1, value)) * 51 : 255;
    }
    return rgba;
}

async function bitmap(plane, mask = false, contrast = null) {
    const canvas = new OffscreenCanvas(plane.width, plane.height);
    canvas.getContext('2d').putImageData(new ImageData(pixels(plane.data, plane.width, plane.height, mask, contrast), plane.width, plane.height), 0, 0);
    return canvas.transferToImageBitmap();
}

// STAR loops can have other columns/blocks, quoted values, comments and wrapped rows.
export function parseParticles(text, width, height) {
    const tokens = (text.match(/#[^\r\n]*|;[^]*?\n;|"[^"\n]*"|'[^'\n]*'|[^\s]+/g) ?? []).filter(token => !token.startsWith('#'));
    const points = [];
    for (let i = 0; i < tokens.length; i++) {
        if (tokens[i] !== 'loop_') continue;
        const columns = [];
        let j = i + 1;
        while (tokens[j]?.startsWith('_') || tokens[j]?.startsWith('#')) {
            if (tokens[j].startsWith('_')) columns.push(tokens[j]);
            j++;
        }
        const xi = columns.indexOf('_rlnCoordinateX'), yi = columns.indexOf('_rlnCoordinateY');
        while (columns.length && j + columns.length <= tokens.length &&
            !/^(loop_|data_|stop_|_)/.test(tokens[j])) {
            if (tokens[j].startsWith('#')) { j++; continue; }
            if (xi >= 0 && yi >= 0) {
                const number = token => Number(token.replace(/^["']|["']$/g, ''));
                const x = number(tokens[j + xi]), y = number(tokens[j + yi]);
                if (Number.isFinite(x) && Number.isFinite(y)) points.push({ x, y });
            }
            j += columns.length;
        }
        i = j - 1;
    }
    const normalized = points.length > 0 && points.every(p => Math.max(p.x, p.y) < 1.1);
    return points.map(p => ({ x: normalized ? p.x * (width - 1) : p.x,
        y: height - 1 - (normalized ? p.y * (height - 1) : p.y) }));
}

// Overlay requests do not fetch or retain a micrograph plane.
export async function loadParticles(options) {
    try {
        const response = await fetch(options.particlesUrl);
        if (!response.ok) throw new Error(`File request failed (${response.status})`);
        postMessage({ type: 'particles', particles: parseParticles(await response.text(), options.width, options.height) });
    } catch (error) { postMessage({ type: 'warning', message: `Particles: ${error.message}` }); }
}

// Masks are drawn stretched over the micrograph, so they need no plane and no image dimensions.
export async function loadMask(options) {
    try {
        const mask = await bitmap(await loadImage(options.maskUrl), true);
        postMessage({ type: 'mask', mask }, [mask]);
    } catch (error) { postMessage({ type: 'warning', message: `Mask: ${error.message}` }); }
}

async function load(options) {
    try {
        let reported = -1;
        let plane = await loadImage(options.url, options.layer, { onProgress: (loaded, total) => {
            const percent = Math.floor(loaded / total * 100);
            if (percent !== reported) { reported = percent; postMessage({ type: 'progress', loaded, total }); }
        } });
        const { width, height, pixelSize } = plane;
        // Each zoom level is stretched by its own statistics, as Warp's binned previews are; particle contrast leaves out
        // the junk mask, or only the border if the mask cannot be read.
        const mask = options.particleContrast && options.contrastMaskUrl ? await loadImage(options.contrastMaskUrl).catch(error => {
            postMessage({ type: 'warning', message: `Mask for contrast: ${error.message}` });
            return null;
        }) : null;
        const levels = [];
        while (true) {
            const contrast = options.particleContrast ? particleContrast(plane.data, plane.width, plane.height, mask) : null;
            levels.push(await bitmap(plane, false, contrast));
            if (Math.max(plane.width, plane.height) <= 512) break;
            plane = bin2(plane.data, plane.width, plane.height);
        }
        postMessage({ type: 'image', width, height, pixelSize, levels }, levels);
        // Optional overlays fail independently, leaving the image usable.
        if (options.particlesUrl) await (async () => {
            const response = await fetch(options.particlesUrl);
            if (!response.ok) throw new Error(`File request failed (${response.status})`);
            postMessage({ type: 'particles', particles: parseParticles(await response.text(), width, height) });
        })().catch(error => postMessage({ type: 'warning', message: `Particles: ${error.message}` }));
    } catch (error) { postMessage({ type: 'error', message: error.message }); }
}
if (typeof self !== 'undefined') self.onmessage = ({ data }) =>
    (data.type === 'particles' ? loadParticles(data) : data.type === 'mask' ? loadMask(data) : load(data)).finally(() => self.close());
