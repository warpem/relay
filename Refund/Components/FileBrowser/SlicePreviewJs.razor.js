import { fetchMRCHeader, fetchSlice } from '../../js/mrc-parser.js';

const instances = new Map();

function createSpinner() {
    const el = document.createElement('div');
    el.style.cssText = 'position:absolute;inset:0;display:flex;align-items:center;justify-content:center;background:var(--neutral-layer-1,#1e1e1e);';
    el.innerHTML = '<div style="width:24px;height:24px;border:3px solid transparent;border-top-color:var(--accent-fill-rest,#60cdff);border-radius:50%;animation:slice-spin 0.8s linear infinite;"></div>';
    if (!document.getElementById('slice-preview-keyframes')) {
        const style = document.createElement('style');
        style.id = 'slice-preview-keyframes';
        style.textContent = '@keyframes slice-spin{to{transform:rotate(360deg)}}';
        document.head.appendChild(style);
    }
    return el;
}

/**
 * Initialize the slice preview viewer
 * @param {HTMLElement} container - The container element
 * @param {string} elementId - Unique element ID for this instance
 * @param {string|null} url - Optional URL to load immediately
 */
export function initialize(container, elementId, url) {
    container.style.position = 'relative';

    const canvas = document.createElement('canvas');
    canvas.style.maxWidth = '100%';
    canvas.style.maxHeight = '100%';
    canvas.style.objectFit = 'contain';
    canvas.style.display = 'none';
    container.appendChild(canvas);

    const spinner = createSpinner();
    spinner.style.display = 'none';
    container.appendChild(spinner);

    const state = {
        container,
        canvas,
        ctx: canvas.getContext('2d'),
        spinner,
        abortController: null
    };

    instances.set(elementId, state);

    if (url) {
        loadByUrl(elementId, url);
    }
}

function showSpinner(state) {
    state.spinner.style.display = 'flex';
    state.canvas.style.display = 'none';
}

function hideSpinner(state) {
    state.spinner.style.display = 'none';
    state.canvas.style.display = '';
}

/**
 * Load and render the central XY slice of an MRC file
 * @param {string} elementId - The instance element ID
 * @param {string} url - The URL to fetch the MRC file from
 */
export async function loadByUrl(elementId, url) {
    const state = instances.get(elementId);
    if (!state) return;

    // Cancel any previous load
    if (state.abortController) {
        state.abortController.abort();
    }
    state.abortController = new AbortController();
    const signal = state.abortController.signal;

    showSpinner(state);

    try {
        // Fetch header to get dimensions
        const header = await fetchMRCHeader(url, { signal });
        if (signal.aborted) return;

        const dims = header.dimensions;
        const centralSlice = Math.floor(dims.z / 2);

        // Fetch the central XY slice
        const sliceData = await fetchSlice(url, header, centralSlice, { signal });
        if (signal.aborted) return;

        // Compute auto-contrast from central 50% crop
        const { min, max } = computeContrast(sliceData, dims.x, dims.y);

        // Render to canvas
        renderSlice(state, sliceData, dims.x, dims.y, min, max);
        hideSpinner(state);
    } catch (err) {
        if (err.name === 'AbortError') return;
        console.error('SlicePreviewJs: failed to load', err);
        state.spinner.style.display = 'none';
    }
}

/**
 * Compute auto-contrast using mean ± 3σ from the central 50% crop
 */
function computeContrast(sliceData, width, height) {
    const x0 = Math.floor(width * 0.25);
    const x1 = Math.floor(width * 0.75);
    const y0 = Math.floor(height * 0.25);
    const y1 = Math.floor(height * 0.75);

    let sum = 0;
    let count = 0;
    for (let y = y0; y < y1; y++) {
        const rowOffset = y * width;
        for (let x = x0; x < x1; x++) {
            sum += sliceData[rowOffset + x];
            count++;
        }
    }
    const mean = sum / count;

    let sumSqDiff = 0;
    for (let y = y0; y < y1; y++) {
        const rowOffset = y * width;
        for (let x = x0; x < x1; x++) {
            const d = sliceData[rowOffset + x] - mean;
            sumSqDiff += d * d;
        }
    }
    const std = Math.sqrt(sumSqDiff / count);

    return {
        min: mean - 3 * std,
        max: mean + 3 * std
    };
}

/**
 * Render slice data to the canvas with Y-flip and grayscale mapping
 */
function renderSlice(state, sliceData, w, h, minI, maxI) {
    const { canvas, ctx } = state;
    canvas.width = w;
    canvas.height = h;

    const imageData = ctx.createImageData(w, h);
    const pixels = imageData.data;
    const scale = maxI > minI ? 255 / (maxI - minI) : 255;

    for (let yy = 0; yy < h; yy++) {
        const srcRow = (h - 1 - yy) * w; // Y-flip
        const dstRow = yy * w * 4;
        for (let xx = 0; xx < w; xx++) {
            const raw = (sliceData[srcRow + xx] - minI) * scale;
            const val = raw < 0 ? 0 : (raw > 255 ? 255 : raw);
            const idx = dstRow + xx * 4;
            pixels[idx] = val;
            pixels[idx + 1] = val;
            pixels[idx + 2] = val;
            pixels[idx + 3] = 255;
        }
    }

    ctx.putImageData(imageData, 0, 0);
}

/**
 * Dispose of an instance and clean up
 * @param {string} elementId - The instance element ID
 */
export function dispose(elementId) {
    const state = instances.get(elementId);
    if (state) {
        if (state.abortController) {
            state.abortController.abort();
        }
        instances.delete(elementId);
    }
}
