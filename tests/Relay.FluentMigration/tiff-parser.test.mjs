import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { deflateSync } from 'node:zlib';
const source = await readFile(new URL('../../Refund/wwwroot/js/tiff-parser.js', import.meta.url), 'utf8');
const tiff = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

/** A little-endian classic TIFF with one image in the given strips. */
function makeTiff({ width, height, bits, format = 1, compression = 1, rowsPerStrip = height, strips, extra = [] }) {
    const entries = [[256, 4, [width]], [257, 4, [height]], [258, 3, [bits]], [259, 3, [compression]], [262, 3, [1]],
        [273, 4, strips.map(() => 0)], [277, 3, [1]], [278, 4, [rowsPerStrip]], [279, 4, strips.map(s => s.length)], [339, 3, [format]], ...extra]
        .sort((a, b) => a[0] - b[0]);
    const ifdSize = 2 + entries.length * 12 + 4;
    let overflow = 8 + ifdSize;
    const overflowParts = [];
    const size = type => type === 3 ? 2 : 4;
    const layout = entries.map(([tag, type, values]) => {
        const bytes = values.length * size(type);
        const at = bytes > 4 ? overflow : -1;
        if (bytes > 4) { overflow += bytes; overflowParts.push([tag, type, values]); }
        return { tag, type, values, at };
    });
    let dataOffset = overflow;
    const stripOffsets = strips.map(s => { const at = dataOffset; dataOffset += s.length; return at; });
    for (const l of layout) if (l.tag === 273) l.values = stripOffsets;
    const buffer = new ArrayBuffer(dataOffset), view = new DataView(buffer), bytes = new Uint8Array(buffer);
    bytes.set([0x49, 0x49, 42, 0]); view.setUint32(4, 8, true);
    view.setUint16(8, entries.length, true);
    const write = (offset, type, value) => type === 3 ? view.setUint16(offset, value, true) : view.setUint32(offset, value, true);
    layout.forEach((l, i) => {
        const entry = 10 + i * 12;
        view.setUint16(entry, l.tag, true); view.setUint16(entry + 2, l.type, true); view.setUint32(entry + 4, l.values.length, true);
        if (l.at < 0) l.values.forEach((v, k) => write(entry + 8 + k * size(l.type), l.type, v));
        else { view.setUint32(entry + 8, l.at, true); l.values.forEach((v, k) => write(l.at + k * size(l.type), l.type, v)); }
    });
    strips.forEach((s, i) => bytes.set(s, stripOffsets[i]));
    return buffer;
}

const floats = values => new Uint8Array(new Float32Array(values).buffer);

test('signatures of classic and big TIFFs are recognized, MRC is not', () => {
    assert.ok(tiff.isTiff(Uint8Array.of(0x49, 0x49, 42, 0)));
    assert.ok(tiff.isTiff(Uint8Array.of(0x4d, 0x4d, 0, 42)));
    assert.ok(tiff.isTiff(Uint8Array.of(0x49, 0x49, 43, 0)));
    assert.ok(!tiff.isTiff(Uint8Array.of(0x80, 0x0f, 0, 0)));
});

test('float32 masks in several strips come back with rows reversed, as Warp reads them', async () => {
    // Warp writes the last row of its image first, so reversing restores its row order (and the MRC orientation).
    const buffer = makeTiff({ width: 3, height: 2, bits: 32, format: 3, rowsPerStrip: 1, strips: [floats([0, 1, 0]), floats([1, 1, 0.5])] });
    const plane = await tiff.parseTiff(buffer);
    assert.equal(plane.width, 3); assert.equal(plane.height, 2);
    assert.deepEqual([...plane.data], [1, 1, 0.5, 0, 1, 0]);
});

test('Deflate-compressed 16-bit images decode', async () => {
    const raw = new Uint8Array(new Uint16Array([10, 20, 30, 40]).buffer);
    const plane = await tiff.parseTiff(makeTiff({ width: 2, height: 2, bits: 16, compression: 8, strips: [deflateSync(raw)] }));
    assert.deepEqual([...plane.data], [30, 40, 10, 20]);
});

test('LZW decodes the TIFF 6.0 specification example', () => {
    // 7 7 7 8 8 7 7 6 6 encodes as codes 256 7 258 8 8 258 6 6 257, 9 bits each, most significant bit first.
    const codes = [256, 7, 258, 8, 8, 258, 6, 6, 257];
    const bits = codes.map(c => c.toString(2).padStart(9, '0')).join('').padEnd(Math.ceil(codes.length * 9 / 8) * 8, '0');
    const packed = Uint8Array.from(bits.match(/.{8}/g), b => parseInt(b, 2));
    assert.deepEqual([...tiff.decodeLzw(packed, 9)], [7, 7, 7, 8, 8, 7, 7, 6, 6]);
});

test('PackBits decodes the TIFF 6.0 specification example', () => {
    const packed = Uint8Array.from('FE AA 02 80 00 2A FD AA 03 80 00 2A 22 F7 AA'.split(' '), h => parseInt(h, 16));
    const expected = 'AA AA AA 80 00 2A AA AA AA AA 80 00 2A 22 AA AA AA AA AA AA AA AA AA AA'.split(' ').map(h => parseInt(h, 16));
    assert.deepEqual([...tiff.decodePackBits(packed, expected.length)], expected);
});

test('horizontal differencing is undone and unsupported layouts are refused', async () => {
    const plane = await tiff.parseTiff(makeTiff({ width: 4, height: 1, bits: 8, strips: [Uint8Array.of(5, 1, 1, 253)], extra: [[317, 3, [2]]] }));
    assert.deepEqual([...plane.data], [5, 6, 7, 4]);
    await assert.rejects(tiff.parseTiff(makeTiff({ width: 1, height: 1, bits: 8, strips: [Uint8Array.of(1)], extra: [[277, 3, [3]]] })), /single-sample/);
    await assert.rejects(tiff.parseTiff(new ArrayBuffer(8)), /Not a TIFF/);
});
