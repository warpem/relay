import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
// The module imports the MRC parser relatively; point that import at an inlined copy.
const parser = await readFile(new URL('../../Refund/wwwroot/js/mrc-parser.js', import.meta.url), 'utf8');
const parserUrl = `data:text/javascript;base64,${Buffer.from(parser).toString('base64')}`;
const source = (await readFile(new URL('../../Refund/wwwroot/js/amplitude-spectrum.js', import.meta.url), 'utf8'))
    .replace("'./mrc-parser.js'", JSON.stringify(parserUrl));
const { spectrumPixels } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

// Warp layout: width x width/2, zero frequency at (width/2, height-1).
function plane(width, value) {
    const height = width / 2, data = new Float32Array(width * height);
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) data[y * width + x] = value(x - width / 2, height - 1 - y);
    return { data, width, height };
}
const gray = (pixels, x, y) => pixels.rgba[(y * pixels.width + x) * 4];

test('zero frequency is drawn in the top-left corner of the right half', () => {
    const { data, width, height } = plane(16, (dx, dy) => dx === 0 && dy === 0 ? 10 : dx * dx + dy * dy < 30 ? 1 : 0);
    const pixels = spectrumPixels(data, width, height, 0, 1);
    assert.equal(pixels.width, 8);
    assert.equal(pixels.height, 8);
    assert.equal(gray(pixels, 0, 0), 255);
    assert.ok(gray(pixels, 7, 7) < gray(pixels, 1, 1), 'brightness falls off away from the top-left corner');
});

test('contrast comes from the fitting annulus only', () => {
    // A huge value inside the inner radius must not flatten the annulus contrast.
    const { data, width, height } = plane(32, (dx, dy) => { const r = Math.hypot(dx, dy); return r < 3 ? 1e6 : r % 2 < 1 ? 1 : 0; });
    const pixels = spectrumPixels(data, width, height, .25, .9);
    const values = new Set();
    for (let x = 5; x < 14; x++) values.add(gray(pixels, x, 0));
    assert.ok(Math.max(...values) - Math.min(...values) > 100, 'rings keep visible contrast');
});

test('constant or non-finite input does not throw', () => {
    const constant = plane(8, () => 3);
    assert.doesNotThrow(() => spectrumPixels(constant.data, constant.width, constant.height, 0, 1));
    const broken = plane(8, () => NaN);
    assert.equal(gray(spectrumPixels(broken.data, broken.width, broken.height, 0, 1), 0, 0), 0);
});
