// Draws Warp amplitude spectra straight from their MRC files.
import { loadPlane } from './mrc-parser.js';

/**
 * Converts a Warp spectrum plane to display pixels.
 *
 * Warp stores a width x width/2 half-plane with zero frequency at (width/2, height-1). The right half
 * is returned flipped vertically, which puts zero frequency in the top-left corner; contrast spans
 * mean - 1.5 sd to mean + 3 sd within the fitting annulus (fractions of Nyquist).
 *
 * @returns {{width: number, height: number, rgba: Uint8ClampedArray}}
 */
export function spectrumPixels(data, width, height, rangeMin, rangeMax) {
    const half = Math.floor(width / 2), outWidth = width - half;
    const minimum2 = (rangeMin * half) ** 2, maximum2 = (rangeMax * half) ** 2;
    let sum = 0, sum2 = 0, samples = 0;
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
        const dx = x - half, dy = height - 1 - y, radius2 = dx * dx + dy * dy, value = data[y * width + x];
        if (radius2 < minimum2 || radius2 > maximum2 || !Number.isFinite(value)) continue;
        sum += value; sum2 += value * value; samples++;
    }
    const mean = samples ? sum / samples : 0;
    const sd = samples ? Math.sqrt(Math.max(0, sum2 / samples - mean * mean)) : 0;
    const low = mean - 1.5 * sd, scale = sd > 0 ? 255 / (4.5 * sd) : 0;

    const rgba = new Uint8ClampedArray(outWidth * height * 4);
    for (let row = 0; row < height; row++) {
        const source = (height - 1 - row) * width;
        for (let x = half; x < width; x++) {
            const value = data[source + x], i = (row * outWidth + x - half) * 4;
            rgba[i] = rgba[i + 1] = rgba[i + 2] = Number.isFinite(value) ? (value - low) * scale : 0;
            rgba[i + 3] = 255;
        }
    }
    return { width: outWidth, height, rgba };
}

// Latest request per canvas, so a slow response never paints over a newer movie or tilt.
const requests = new WeakMap();

/**
 * Loads one plane of a spectrum file (a tilt series has one per tilt) and draws it into the canvas,
 * resizing the canvas bitmap to the spectrum and revealing it.
 *
 * @param {HTMLCanvasElement} canvas
 * @param {{url: string, layer: number, rangeMin: number, rangeMax: number}} options
 * @returns {Promise<boolean>} false when a newer request replaced this one.
 */
export async function renderSpectrum(canvas, options) {
    const request = {};
    requests.set(canvas, request);
    const plane = await loadPlane(options.url, options.layer ?? 0);
    if (requests.get(canvas) !== request || !canvas.isConnected) return false;
    const { width, height, rgba } = spectrumPixels(plane.data, plane.width, plane.height, options.rangeMin, options.rangeMax);
    canvas.width = width;
    canvas.height = height;
    canvas.getContext('2d').putImageData(new ImageData(rgba, width, height), 0, 0);
    canvas.style.opacity = '1';
    return true;
}
