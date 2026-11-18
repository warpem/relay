import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const dataUrl = async name => `data:text/javascript;base64,${Buffer.from(await readFile(new URL(`../../Refund/wwwroot/js/${name}`, import.meta.url), 'utf8')).toString('base64')}`;
const source = (await readFile(new URL('../../Refund/wwwroot/js/micrograph-worker.js', import.meta.url), 'utf8'))
    .replace("'./mrc-parser.js'", JSON.stringify(await dataUrl('mrc-parser.js')))
    .replace("'./tiff-parser.js'", JSON.stringify(await dataUrl('tiff-parser.js')));
const worker = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('STAR coordinates preserve absolute/normalized conventions and empty loops', () => {
    const prefix = 'data_optics\nloop_\n_rlnOpticsGroup #1\n1\ndata_particles\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n';
    assert.deepEqual(worker.parseParticles(prefix+'0.5 0.25\n', 9, 5), [{x:4,y:3}]);
    assert.deepEqual(worker.parseParticles(prefix+'2 3 # an inline comment\n', 9, 5), [{x:2,y:1}]);
    assert.deepEqual(worker.parseParticles(prefix, 9, 5), []);
});
test('contrast and masks flip Y together and handle constant data', () => {
    const values = new Float32Array([0,0,1,1]);
    const gray = worker.pixels(values, 2, 2);
    assert.ok(gray[0] > gray[8]);
    const mask = worker.pixels(values, 2, 2, true);
    assert.equal(mask[3],51); assert.equal(mask[11],0);
    assert.deepEqual([...worker.pixels(new Float32Array([2]),1,1)], [0,0,0,255]);
});
test('particle contrast uses the central half without junk, like Warp previews', () => {
    // 8 × 8: the central 4 × 4 alternates ±1, the border is far brighter.
    const square = new Float32Array(64).fill(1000);
    for (let y = 2; y < 6; y++) for (let x = 2; x < 6; x++) square[y * 8 + x] = (x + y) % 2 ? -1 : 1;
    let { low, scale } = worker.particleContrast(square, 8, 8);
    assert.equal(low, -2.5); assert.equal(scale, 255 / 5);

    // 40 × 40 with a 10 × 10 mask: junk in mask columns 0–3 (image columns 0–15), grown by two mask pixels (to 23).
    const n = 40, m = 10, data = new Float32Array(n * n), mask = { data: new Float32Array(m * m), width: m, height: m };
    for (let y = 0; y < m; y++) for (let x = 0; x < 4; x++) mask.data[y * m + x] = 1;
    for (let i = 0; i < data.length; i++) data[i] = i % n < 16 ? -50 : (i % n < 24 ? 7 : (i % 3) - 1);
    const included = [];
    for (let y = 10; y < 30; y++) for (let x = 24; x < 30; x++) included.push(data[y * n + x]);
    const mean = included.reduce((a, b) => a + b) / included.length;
    const std = Math.sqrt(included.reduce((a, b) => a + (b - mean) ** 2, 0) / included.length);
    ({ low, scale } = worker.particleContrast(data, n, n, mask));
    assert.ok(Math.abs(low - (mean - 2.5 * std)) < 1e-9); assert.ok(Math.abs(scale - 255 / (5 * std)) < 1e-9);

    // Junk over the whole centre (mask columns 0–7 with the margin, image columns 0–31): the ice to its right counts.
    for (let y = 0; y < m; y++) for (let x = 0; x < 6; x++) mask.data[y * m + x] = 1;
    ({ low } = worker.particleContrast(data, n, n, mask));
    assert.ok(Math.abs(low - (-2.5 * Math.sqrt(2 / 3) - 0)) < 0.2);
    // The window is what pixels() stretches by.
    const gray = worker.pixels(new Float32Array([0]), 1, 1, false, { low: -1, scale: 100 });
    assert.equal(gray[0], 100);
});
test('mipmaps bin 2x2 blocks and terminate for narrow odd images', () => {
    const binned = worker.bin2(new Float32Array([1,2,3,4,5,6,7,8]),4,2);
    assert.deepEqual([...binned.data], [3.5,5.5]);
    const narrow = worker.bin2(new Float32Array(513),1,513);
    assert.equal(narrow.width,1);assert.equal(narrow.height,256);
});


test('particle-only worker fetches only STAR and uses supplied image dimensions', async () => {
    const requests = [], messages = [];
    const previousFetch = globalThis.fetch, previousPost = globalThis.postMessage;
    globalThis.fetch = async url => { requests.push(url); return { ok: true, text: async () => 'data_\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n2 3\n' }; };
    globalThis.postMessage = message => messages.push(message);
    try {
        await worker.loadParticles({ particlesUrl: '/filtered.star', width: 10, height: 20 });
        assert.deepEqual(requests, ['/filtered.star']);
        assert.deepEqual(messages, [{ type: 'particles', particles: [{ x: 2, y: 16 }] }]);
    } finally { globalThis.fetch = previousFetch; globalThis.postMessage = previousPost; }
});

test('TIFF planes are told from MRC by their first bytes and read whole', async () => {
    // A 2 × 1 little-endian float TIFF: header, one IFD, then the strip.
    const buffer = new ArrayBuffer(8 + 2 + 7 * 12 + 4 + 8), view = new DataView(buffer), bytes = new Uint8Array(buffer);
    bytes.set([0x49, 0x49, 42, 0]); view.setUint32(4, 8, true); view.setUint16(8, 7, true);
    const strip = 8 + 2 + 7 * 12 + 4;
    [[256, 3, 2], [257, 3, 1], [258, 3, 32], [273, 4, strip], [277, 3, 1], [279, 4, 8], [339, 3, 3]].forEach(([tag, type, value], i) => {
        const entry = 10 + i * 12;
        view.setUint16(entry, tag, true); view.setUint16(entry + 2, type, true); view.setUint32(entry + 4, 1, true);
        if (type === 3) view.setUint16(entry + 8, value, true); else view.setUint32(entry + 8, value, true);
    });
    view.setFloat32(strip, 0, true); view.setFloat32(strip + 4, 1, true);
    const requests = [], previousFetch = globalThis.fetch;
    try {
        // A server honouring Range sends the signature first; the whole file follows.
        globalThis.fetch = async (url, init) => {
            requests.push(init?.headers?.Range ?? 'whole');
            return init?.headers?.Range ? { ok: true, status: 206, arrayBuffer: async () => buffer.slice(0, 4) } : { ok: true, status: 200, arrayBuffer: async () => buffer };
        };
        let plane = await worker.loadImage('/mask');
        assert.deepEqual(requests, ['bytes=0-3', 'whole']);
        assert.deepEqual([plane.width, plane.height, ...plane.data], [2, 1, 0, 1]);
        // One that ignores Range has already sent everything.
        requests.length = 0;
        globalThis.fetch = async (url, init) => { requests.push(init?.headers?.Range ?? 'whole'); return { ok: true, status: 200, arrayBuffer: async () => buffer }; };
        plane = await worker.loadImage('/mask');
        assert.deepEqual(requests, ['bytes=0-3']);
        assert.deepEqual([...plane.data], [0, 1]);
    } finally { globalThis.fetch = previousFetch; }
});
