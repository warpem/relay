import { loadPlane } from './mrc-parser.js';

// Shared with main-thread viewers (the amplitude spectrum); kept exported here for existing callers.
export { loadPlane };

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

export function pixels(data, width, height, mask = false) {
    let mean = 0, variance = 0, count = 0;
    if (!mask) for (const value of data) {
        if (!Number.isFinite(value)) continue;
        count++;
        const delta = value - mean;
        mean += delta / count;
        variance += delta * (value - mean);
    }
    const std = count ? Math.sqrt(variance / count) : 0;
    const low = mean - 2.5 * std, scale = std > 0 ? 255 / (5 * std) : 0;
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

async function bitmap(plane, mask = false) {
    const canvas = new OffscreenCanvas(plane.width, plane.height);
    canvas.getContext('2d').putImageData(new ImageData(pixels(plane.data, plane.width, plane.height, mask), plane.width, plane.height), 0, 0);
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

async function load(options) {
    try {
        let reported = -1;
        let plane = await loadPlane(options.url, options.layer, { onProgress: (loaded, total) => {
            const percent = Math.floor(loaded / total * 100);
            if (percent !== reported) { reported = percent; postMessage({ type: 'progress', loaded, total }); }
        } });
        const { width, height, pixelSize } = plane;
        const levels = [];
        while (true) {
            levels.push(await bitmap(plane));
            if (Math.max(plane.width, plane.height) <= 512) break;
            plane = bin2(plane.data, plane.width, plane.height);
        }
        postMessage({ type: 'image', width, height, pixelSize, levels }, levels);
        // Optional overlays fail independently, leaving the image usable.
        await Promise.all([
            options.maskUrl && (async () => {
                const mask = await bitmap(await loadPlane(options.maskUrl), true);
                postMessage({ type: 'mask', mask }, [mask]);
            })().catch(error => postMessage({ type: 'warning', message: `Mask: ${error.message}` })),
            options.particlesUrl && (async () => {
                const response = await fetch(options.particlesUrl);
                if (!response.ok) throw new Error(`File request failed (${response.status})`);
                postMessage({ type: 'particles', particles: parseParticles(await response.text(), width, height) });
            })().catch(error => postMessage({ type: 'warning', message: `Particles: ${error.message}` }))
        ]);
    } catch (error) { postMessage({ type: 'error', message: error.message }); }
}
if (typeof self !== 'undefined') self.onmessage = ({ data }) =>
    (data.type === 'particles' ? loadParticles(data) : load(data)).finally(() => self.close());
