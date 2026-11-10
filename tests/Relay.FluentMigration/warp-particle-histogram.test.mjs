import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const source = fs.readFileSync(new URL('../../ComponentDev/Components/WarpPreview/WarpParticleHistogram.razor.js', import.meta.url), 'utf8');
let moduleNumber = 0;

async function harness(t, { width = 245, height = 140, invoke } = {}) {
    const originals = new Map(['window', 'ResizeObserver', 'getComputedStyle'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
    const observers = [], calls = [], draws = [], listeners = new Map();
    const context = {
        fillRect(...args) { draws.push({ kind: 'fill', args, color: this.fillStyle }); },
        strokeRect(...args) { draws.push({ kind: 'stroke', args }); },
        fillText(...args) { draws.push({ kind: 'text', args }); },
        setTransform(...args) { draws.push({ kind: 'transform', args }); },
        save() {}, restore() {}, translate() {}, rotate() {}
    };
    const canvas = {
        width: 0, height: 0, title: '', bounds: { left: 10, top: 20, width, height }, captures: [], released: [], held: new Set(),
        getBoundingClientRect() { return this.bounds; },
        getContext(kind) { assert.equal(kind, '2d'); return context; },
        addEventListener(type, handler) { assert.ok(!listeners.has(type), `duplicate ${type} handler`); listeners.set(type, handler); },
        removeEventListener(type, handler) { if (listeners.get(type) === handler) listeners.delete(type); },
        setPointerCapture(id) { this.captures.push(id); this.held.add(id); },
        hasPointerCapture(id) { return this.held.has(id); },
        releasePointerCapture(id) { this.released.push(id); this.held.delete(id); }
    };
    globalThis.window = { devicePixelRatio: 3 };
    globalThis.getComputedStyle = () => ({ getPropertyValue: () => '#555' });
    globalThis.ResizeObserver = class {
        constructor(callback) { this.callback = callback; this.disconnected = false; observers.push(this); }
        observe(target) { this.target = target; }
        disconnect() { this.disconnected = true; }
    };
    const mod = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}#${moduleNumber++}`);
    const id = `histogram-${moduleNumber}`;
    mod.initialize(canvas, id, { invokeMethodAsync(...args) { calls.push(args); return invoke ? invoke(...args) : Promise.resolve(); } });
    t.after(() => {
        mod.dispose(id);
        for (const [key, descriptor] of originals) {
            if (descriptor) Object.defineProperty(globalThis, key, descriptor);
            else delete globalThis[key];
        }
    });
    function emit(type, correlation, power, extra = {}) {
        const { left, top, width: w, height: h } = canvas.bounds;
        const event = { button: 0, pointerId: 7, clientX: left + 38 + correlation * (w - 45), clientY: top + 8 + (1 - power / 10) * (h - 40), ...extra };
        listeners.get(type)?.(event);
    }
    function drag(from = [.2, 2], to = [.8, 8]) { emit('pointerdown', ...from); emit('pointermove', ...to); emit('pointerup', ...to); }
    function update(bins = [1, 2, 3, 4], bounds = [0, 1, 0, 10]) { mod.update(id, bins, 2, 2, 10, bounds); }
    return { mod, id, canvas, context, calls, draws, listeners, observers, emit, drag, update };
}

const close = (actual, expected) => assert.ok(Math.abs(actual - expected) < 1e-8, `${actual} != ${expected}`);
function closeArray(actual, expected) { assert.equal(actual.length, expected.length); actual.forEach((v, i) => close(v, expected[i])); }

// These tests load the real module without a browser; no server or worker is started.
test('histogram draws bins with power increasing upward and reuses bins for range-only updates', async t => {
    const h = await harness(t);
    assert.equal(h.listeners.size, 4);
    assert.equal(h.observers[0].target, h.canvas);
    h.update();
    assert.equal(h.canvas.width, 490); // device scale is capped at two
    assert.equal(h.canvas.height, 280);
    closeArray(h.draws.find(d => d.kind === 'transform').args, [2, 0, 0, 2, 0, 0]);
    const fills = h.draws.filter(d => d.kind === 'fill');
    assert.deepEqual(fills.slice(1, 5).map(d => d.args), [[38, 58, 100, 50], [138, 58, 100, 50], [38, 8, 100, 50], [138, 8, 100, 50]]);
    assert.notEqual(fills[1].color, fills[4].color);
    h.draws.length = 0;
    h.update(null, [.25, .75, 2, 8]);
    assert.equal(h.draws.filter(d => d.kind === 'fill').length, 9); // background + four bins + four outside-range masks
    closeArray(h.draws.find(d => d.kind === 'stroke').args, [88.5, 28.5, 99, 59]);
    h.emit('pointermove', .8, 8);
    assert.match(h.canvas.title, /4 particles/);
});

test('hidden canvas waits for a visible resize and draws the latest data', async t => {
    const h = await harness(t, { width: 0, height: 0 });
    h.update([0, 0, 0, 0]);
    h.observers[0].callback();
    assert.equal(h.draws.length, 0);
    h.canvas.bounds.width = 245;
    h.canvas.bounds.height = 140;
    h.observers[0].callback();
    assert.ok(h.draws.some(d => d.kind === 'text' && d.args[0] === 'No up-to-date picks'));
    assert.ok(h.draws.filter(d => d.kind === 'fill').every(d => d.args[2] >= 0 && d.args[3] >= 0));
});

test('reverse rectangle drag commits normalized correlation and absolute power bounds', async t => {
    const h = await harness(t);
    h.update();
    h.drag([.8, 8], [.2, 2]);
    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0][0], 'SelectRange');
    closeArray(h.calls[0].slice(1), [.2, .8, 2, 8]);
    assert.deepEqual(h.canvas.captures, [7]);
    h.drag([.5, 5], [.51, 5.1]);
    assert.equal(h.calls.length, 1, 'click-sized movement must not filter');
    h.emit('pointerdown', .1, 1);
    h.emit('pointermove', .9, 9);
    h.emit('pointercancel', .9, 9);
    h.emit('pointerup', .9, 9);
    assert.equal(h.calls.length, 1, 'cancelled gesture must not filter');
});

test('rejected callback after disposal is handled; disposal removes event listeners and observer', async t => {
    let reject;
    const h = await harness(t, { invoke: () => new Promise((_, rejectPromise) => { reject = rejectPromise; }) });
    h.update();
    h.drag();
    h.mod.dispose(h.id);
    assert.equal(h.listeners.size, 0);
    assert.equal(h.observers[0].disconnected, true);
    reject(new Error('DotNet reference disposed'));
    await new Promise(resolve => setImmediate(resolve));
    const before = h.draws.length;
    h.update();
    h.drag();
    h.mod.dispose(h.id);
    assert.equal(h.draws.length, before);
    assert.equal(h.calls.length, 1);
});

test('reinitializing an ID replaces listeners and disconnects the prior observer', async t => {
    const h = await harness(t);
    h.update();
    h.mod.initialize(h.canvas, h.id, { invokeMethodAsync: () => Promise.resolve() });
    assert.equal(h.observers[0].disconnected, true);
    assert.equal(h.observers.length, 2);
    assert.equal(h.listeners.size, 4);
    h.update();
    h.mod.dispose(h.id);
    assert.equal(h.observers[1].disconnected, true);
    assert.equal(h.listeners.size, 0);
});

test('queued resize callback cannot draw after disposal', async t => {
    const h = await harness(t);
    h.update();
    const queuedResize = h.observers[0].callback;
    h.mod.dispose(h.id);
    const before = h.draws.length;
    queuedResize();
    assert.equal(h.draws.length, before);
});

test('axis-margin drags do not start a selection', async t => {
    const h = await harness(t);
    h.update();
    for (const start of [[-.1, 2], [1.1, 2], [.2, -1], [.2, 11]]) {
        h.emit('pointerdown', ...start);
        h.emit('pointermove', .8, 8);
        h.emit('pointerup', .8, 8);
    }
    assert.equal(h.calls.length, 0);
});

test('only the pointer that began a rectangle can finish it', async t => {
    const h = await harness(t);
    h.update();
    h.emit('pointerdown', .2, 2);
    h.emit('pointerdown', .4, 4, { pointerId: 99 });
    h.emit('pointerup', .8, 8, { pointerId: 99 });
    assert.equal(h.calls.length, 0);
    h.emit('pointerup', .8, 8);
    assert.equal(h.calls.length, 1);
    closeArray(h.calls[0].slice(1), [.2, .8, 2, 8]);
});

test('dragging beyond the data rectangle clamps bounds and releases pointer capture', async t => {
    const h = await harness(t);
    h.update();
    h.drag([.2, 2], [1.5, 15]);
    closeArray(h.calls[0].slice(1), [.2, 1, 2, 10]);
    assert.deepEqual(h.canvas.released, [7]);
    assert.equal(h.canvas.held.size, 0);
});

test('becoming hidden cancels an active drag without committing stale coordinates', async t => {
    const h = await harness(t);
    h.update();
    h.emit('pointerdown', .2, 2);
    h.emit('pointermove', .8, 8);
    h.canvas.bounds.width = h.canvas.bounds.height = 0;
    h.observers[0].callback();
    assert.equal(h.canvas.held.size, 0);
    h.canvas.bounds.width = 245;
    h.canvas.bounds.height = 140;
    h.observers[0].callback();
    h.emit('pointerup', .8, 8);
    assert.equal(h.calls.length, 0);
});

test('synchronously disposed .NET reference cannot throw out of the pointer handler', async t => {
    const h = await harness(t, { invoke: () => { throw new Error('disposed .NET reference'); } });
    h.update();
    assert.doesNotThrow(() => h.drag());
    assert.equal(h.calls.length, 1);
    assert.equal(h.canvas.held.size, 0);
});
