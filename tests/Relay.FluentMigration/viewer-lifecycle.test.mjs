import { readFile } from 'node:fs/promises';
import test from 'node:test';
import assert from 'node:assert/strict';

const source = await readFile(new URL('../../Refund/Components/MicrographViewer/MicrographViewer.razor.js', import.meta.url), 'utf8');
const viewer = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('disposing a viewer cancels its latest debounced resize, without disposing the .NET-owned reference', async () => {
    let resize, disconnected = false, calls = 0;
    globalThis.ResizeObserver = class {
        constructor(callback) { resize = callback; }
        observe() {}
        disconnect() { disconnected = true; }
    };
    const element = { id: 'fixture', isConnected: true };
    const reference = { invokeMethodAsync() { calls++; return Promise.resolve(); }, dispose() { assert.fail('.NET owns this reference'); } };
    viewer.observeResize(element, reference, 5);
    resize([{ contentRect: { width: 800, height: 600 } }]);
    viewer.disposeResizeObserver(element);
    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(disconnected, true);
    assert.equal(calls, 0);
});

test('an in-flight resize rejection after disposal is observed', async () => {
    let resize, reject, logged = 0;
    globalThis.ResizeObserver = class { constructor(callback) { resize = callback; } observe() {} disconnect() {} };
    const element = { id: 'in-flight', isConnected: true };
    const reference = { invokeMethodAsync() { return new Promise((resolve, fail) => reject = fail); } };
    const originalError = console.error;
    console.error = () => logged++;
    try {
        viewer.observeResize(element, reference, 1);
        resize([{ contentRect: { width: 800, height: 600 } }]);
        await new Promise(resolve => setTimeout(resolve, 10));
        viewer.disposeResizeObserver(element);
        reject(new Error('disposed .NET reference'));
        await new Promise(resolve => setTimeout(resolve, 1));
        assert.equal(logged, 0);
    } finally { console.error = originalError; }
});
