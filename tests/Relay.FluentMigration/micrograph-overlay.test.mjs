import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const base = new URL('../../Refund/Components/MicrographViewer/MicrographViewer.razor.js', import.meta.url);
const source = (await readFile(base, 'utf8')).replaceAll('import.meta.url', JSON.stringify(base.href));
const viewer = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('filtering particles preserves micrograph buffers and zoom; cancelled overlay results are ignored', async () => {
    const workers = [], viewport = [];
    let closed = 0;
    const element = () => ({ style: {}, setAttribute() {}, removeAttribute() {}, addEventListener() {}, append() {}, replaceChildren() {}, getBoundingClientRect() { return { width: 600, height: 600 }; } });
    const surface = element();
    const root = { ...element(), id: 'overlay-test', isConnected: true, querySelector() { return surface; } };
    globalThis.document = { createElement: element };
    globalThis.window = { devicePixelRatio: 1 };
    globalThis.Image = class { removeAttribute() {} };
    globalThis.ResizeObserver = class { observe() {} disconnect() {} };
    globalThis.requestAnimationFrame = () => 1;
    globalThis.cancelAnimationFrame = () => {};
    globalThis.Worker = class {
        constructor() { workers.push(this); }
        postMessage(options) { this.options = options; }
        terminate() { this.terminated = true; }
    };
    const callback = { invokeMethodAsync(name, zoom) { viewport.push(zoom); return Promise.resolve(); } };
    const options = { url: '/micrograph?revision=1', layer: 0, particlesUrl: '/picks-a', minWidth: 200, minHeight: 200 };
    try {
        viewer.update(root, options, callback);
        await new Promise(resolve => setTimeout(resolve, 60));
        assert.equal(workers.length, 1);
        assert.equal(workers[0].options.particlesUrl, null);
        workers[0].onmessage({ data: { type: 'image', width: 1200, height: 1200, pixelSize: 2, levels: [{ close() { closed++; } }] } });
        assert.equal(workers.length, 2);
        assert.equal(workers[1].options.type, 'particles');
        viewer.command(root.id, 'zoom', 1.4);
        await new Promise(resolve => setTimeout(resolve, 110));
        const lastZoom = viewport.at(-1);
        viewer.update(root, { ...options, particlesUrl: '/picks-b' }, callback);
        assert.equal(closed, 0, 'image bitmaps remain retained');
        assert.equal(workers[0].terminated, undefined, 'image worker is untouched');
        assert.equal(workers[1].terminated, true, 'old particle fetch is cancelled');
        assert.equal(workers.length, 3);
        assert.equal(workers[2].options.url, undefined, 'new overlay worker cannot fetch MRC');
        assert.equal(workers[2].options.particlesUrl, '/picks-b');
        assert.equal(viewport.at(-1), lastZoom);
        workers[1].onmessage({ data: { type: 'warning', message: 'stale overlay failure' } });
        viewer.update(root, { ...options, particlesUrl: null }, callback);
        assert.equal(workers[2].terminated, true);
        assert.equal(workers.length, 3, 'hiding picks creates no worker');
        assert.equal(closed, 0);
    } finally { viewer.dispose(root.id); }
    assert.equal(closed, 1, 'final disposal releases image once');
});

test('masks load on their own, beside the preview; a new mask keeps the micrograph and tells .NET', async () => {
    const workers = [], calls = [];
    let closed = 0, maskClosed = 0;
    const created = [];
    const element = () => { const e = { style: {}, attributes: {}, setAttribute(k, v) { this.attributes[k] = v; }, removeAttribute(k) { delete this.attributes[k]; }, addEventListener() {}, append() {}, replaceChildren() {}, getBoundingClientRect() { return { width: 600, height: 600 }; } }; created.push(e); return e; };
    const surface = element();
    const root = { ...element(), id: 'mask-test', isConnected: true, querySelector() { return surface; } };
    globalThis.document = { createElement: element };
    globalThis.window = { devicePixelRatio: 1 };
    globalThis.Image = class { removeAttribute() {} };
    globalThis.ResizeObserver = class { observe() {} disconnect() {} };
    globalThis.requestAnimationFrame = () => 1;
    globalThis.cancelAnimationFrame = () => {};
    globalThis.Worker = class {
        constructor() { workers.push(this); }
        postMessage(options) { this.options = options; }
        terminate() { this.terminated = true; }
    };
    const callback = { invokeMethodAsync(name, ...args) { calls.push([name, ...args]); return Promise.resolve(); } };
    const options = { url: '/micrograph', layer: 0, maskUrl: '/mask-a', minWidth: 200, minHeight: 200 };
    try {
        viewer.update(root, options, callback);
        const status = created.find(e => e.className === 'micrograph-status');
        assert.equal(status.attributes['data-loading'], '', 'a spinner shows while the micrograph loads');
        // The mask worker starts at once; the plane follows after the short browsing delay.
        assert.equal(workers.length, 1);
        assert.deepEqual(workers[0].options, { type: 'mask', maskUrl: '/mask-a' });
        await new Promise(resolve => setTimeout(resolve, 60));
        assert.equal(workers.length, 2);
        assert.equal(workers[1].options.maskUrl, null, 'the plane worker does not read the mask');
        workers[1].onmessage({ data: { type: 'image', width: 800, height: 800, pixelSize: 1, levels: [{ close() { closed++; } }] } });
        assert.equal(status.attributes['data-loading'], undefined, 'and goes once it is shown');
        workers[0].onmessage({ data: { type: 'mask', mask: { close() { maskClosed++; } } } });
        assert.deepEqual(calls.filter(c => c[0] === 'OnMaskAvailability'), [['OnMaskAvailability', true]]);

        viewer.update(root, { ...options, maskUrl: '/mask-b' }, callback);
        assert.equal(maskClosed, 1, 'the old mask is released');
        assert.equal(closed, 0, 'the micrograph is kept');
        assert.equal(workers[1].terminated, undefined);
        assert.deepEqual(workers.at(-1).options, { type: 'mask', maskUrl: '/mask-b' });
        assert.deepEqual(calls.filter(c => c[0] === 'OnMaskAvailability').at(-1), ['OnMaskAvailability', false]);
        // A failed mask stays quiet: no status text over the micrograph.
        const warn = console.warn; let warned = 0; console.warn = () => warned++;
        try { workers.at(-1).onmessage({ data: { type: 'warning', message: 'Mask: File request failed (404)' } }); }
        finally { console.warn = warn; }
        assert.equal(warned, 1);

        const count = workers.length;
        viewer.update(root, { ...options, maskUrl: null }, callback);
        assert.equal(workers.length, count, 'no mask, no worker');
    } finally { viewer.dispose(root.id); }
    assert.equal(closed, 1);
});
