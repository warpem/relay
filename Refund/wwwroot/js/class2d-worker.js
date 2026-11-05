import { fetchMRC, MRCDataType } from './mrc-parser.js';

// One decoded stack per URL, shared by the class grid and parent comparison tiles.
// Buffers and previews are released as soon as their final view is disposed.
const stacks = new Map();
const owners = new Map();

function acquire(id, url) {
    let stack = stacks.get(url);
    if (!stack) {
        const controller = new AbortController();
        stack = { controller, owners: new Set(), images: new Map() };
        stack.loaded = fetchMRC(url, {
            signal: controller.signal,
            onProgress(received, total) {
                for (const owner of stack.owners)
                    postMessage({ id: owner, type: 'progress', received, total });
            }
        }).then(({ header, data }) => {
            const { x, y, z } = header.dimensions;
            if (x < 1 || y < 1 || z < 1 || x !== y ||
                ![MRCDataType.Byte, MRCDataType.Short, MRCDataType.UnsignedShort,
                    MRCDataType.Float, MRCDataType.Half].includes(header.mode))
                throw new Error('Expected an MRC stack of square, real-valued class images');
            return { width: x, height: y, count: z, data };
        });
        stacks.set(url, stack);
    }
    stack.owners.add(id);
    owners.set(id, { url, stack });
    return stack;
}

function release(id) {
    const owner = owners.get(id);
    if (!owner) return;
    owners.delete(id);
    owner.stack.owners.delete(id);
    if (owner.stack.owners.size === 0) {
        owner.stack.controller.abort();
        owner.stack.images.clear();
        stacks.delete(owner.url);
    }
}

function quantile(sorted, fraction) {
    const position = (sorted.length - 1) * fraction;
    const lower = Math.floor(position), upper = Math.ceil(position);
    return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

async function preview(stack, index, signed) {
    const { width, height, count, data } = await stack.loaded;
    if (!Number.isInteger(index) || index < 0 || index >= count)
        throw new Error(`Image ${index + 1} is outside the ${count}-image stack`);
    const values = data.subarray(index * width * height, (index + 1) * width * height);
    const sorted = new Float32Array(values.length);
    for (let i = 0; i < values.length; i++) {
        if (!Number.isFinite(values[i])) throw new Error(`Non-finite value in image ${index + 1}`);
        sorted[i] = signed ? Math.abs(values[i]) : values[i];
    }
    sorted.sort();
    const low = signed ? 0 : quantile(sorted, 0.005);
    const scale = Math.max(signed ? quantile(sorted, 0.995) : sorted[sorted.length - 1] - low, 1e-20);
    const pixels = new Uint8ClampedArray(width * height * 4);
    for (let y = 0; y < height; y++) {
        for (let x = 0; x < width; x++) {
            const value = values[y * width + x];
            const normalized = signed ? value / scale * 0.5 + 0.5 : (value - low) / scale;
            const gray = Math.min(255, Math.floor(Math.max(0, normalized) * 256));
            // Match Matplotlib origin='lower', independently of storage/DOM ordering.
            const pixel = ((height - 1 - y) * width + x) * 4;
            pixels[pixel] = pixels[pixel + 1] = pixels[pixel + 2] = gray;
            pixels[pixel + 3] = 255;
        }
    }
    const canvas = new OffscreenCanvas(width, height);
    canvas.getContext('2d').putImageData(new ImageData(pixels, width, height), 0, 0);
    return canvas.convertToBlob({ type: 'image/png' });
}

async function render({ id, url, indices, signed }) {
    const stack = acquire(id, url);
    try {
        await stack.loaded;
        for (const index of indices) {
            if (!owners.has(id)) return;
            const key = `${signed}:${index}`;
            let image = stack.images.get(key);
            if (!image) {
                image = preview(stack, index, signed);
                stack.images.set(key, image);
            }
            const blob = await image;
            if (!owners.has(id)) return;
            postMessage({ id, type: 'image', index, blob });
        }
        if (owners.has(id)) postMessage({ id, type: 'done' });
    } catch (error) {
        if (owners.has(id) && error.name !== 'AbortError')
            postMessage({ id, type: 'error', message: error.message });
    }
}

onmessage = ({ data }) => {
    if (data.type === 'release') release(data.id);
    else if (data.type === 'render') void render(data);
};
