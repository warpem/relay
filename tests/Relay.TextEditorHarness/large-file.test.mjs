import assert from 'node:assert/strict';
import { stat, open } from 'node:fs/promises';
import { chromium } from 'playwright';
const base = process.env.EDITOR_TEST_URL || 'http://127.0.0.1:5287';
const browser = await chromium.launch({ channel: 'chrome', headless: true });
try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const page = await context.newPage();
    page.setDefaultTimeout(120000);
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${base}/sign-in`);
    await page.goto(`${base}/?large`);
    const token = await page.evaluate(() => window.editorToken);
    const info = await (await context.request.get(`${base}/api/text-editor/${token}`)).json();
    const start = Date.now();
    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await page.getByRole('status').filter({ hasText: /^Ready$/ }).waitFor();
    const loaded = Date.now();
    assert.equal(await page.evaluate(async () => {
        const { monaco } = await import('/_content/Refund/monaco/editor.js');
        const model = monaco.editor.getModels()[0];
        assertNeverGetValue(model);
        function assertNeverGetValue(model) { model.getValue = () => { throw new Error('Whole-document getValue must not be used'); }; }
        model.pushEditOperations([], [{ range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 2 }, text: '2' }], () => null);
        return model.getLanguageId();
    }), 'plaintext');
    await page.getByText('Large file · Plain text', { exact: false }).waitFor();
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    await cdp.send('Network.emulateNetworkConditions', {
        offline: false, latency: 30, downloadThroughput: -1, uploadThroughput: 16 * 1024 * 1024
    });
    const phases = [];
    await page.exposeFunction('savePhase', text => phases.push(text));
    await page.evaluate(() => new MutationObserver(() => window.savePhase(document.querySelector('.editor-status').textContent))
        .observe(document.querySelector('.editor-status'), { childList: true }));
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.editor-status').textContent.startsWith('Uploading '));
    const progress = page.getByRole('progressbar', { name: 'File transfer progress' });
    assert.equal(await progress.isVisible(), true);
    assert.equal(await page.getByRole('button', { name: 'Save', exact: true }).isDisabled(), true);
    await page.screenshot({ path: '/tmp/relay-monaco-saving.png' });
    await page.getByRole('status').filter({ hasText: /^Saved$/ }).waitFor();
    const saved = Date.now();
    assert.equal((await stat(info.path)).size, info.size);
    const file = await open(info.path, 'r');
    const bytes = Buffer.alloc(1);
    await file.read(bytes, 0, 1, 0);
    await file.close();
    assert.equal(bytes.toString(), '2');
    assert.ok(phases.some(text => text.startsWith('Preparing upload')));
    assert.ok(phases.some(text => text.startsWith('Uploading ')));
    assert.ok(phases.includes('Finishing save…'));
    assert.deepEqual(errors, []);
    console.log(`Passed: ${info.size / 1024 / 1024} MiB plaintext load (${((loaded - start) / 1000).toFixed(1)}s), streamed save with progress (${((saved - loaded) / 1000).toFixed(1)}s at 16 MiB/s), no whole-document getValue, exact output size and edited byte.`);
} finally { await browser.close(); }
