import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';

const base = process.env.EDITOR_TEST_URL || 'http://127.0.0.1:5287';
const browser = await chromium.launch({ channel: 'chrome', headless: true });
try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    const workers = [];
    page.on('worker', worker => workers.push(worker.url()));
    await page.goto(`${base}/sign-in`);
    const token = await page.evaluate(() => window.editorToken);
    const load = await context.request.get(`${base}/api/text-editor/${token}`);
    assert.equal(load.status(), 200);
    const original = await load.json();
    const endpoint = `${base}/api/text-editor/${original.token}`;
    const anonymous = await browser.newContext();
    assert.equal((await anonymous.request.get(endpoint, { maxRedirects: 0 })).status(), 401);
    await anonymous.close();
    assert.equal((await context.request.put(endpoint, { headers: { 'Content-Type': 'text/plain' }, data: 'bad' })).status(), 400);
    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await page.getByRole('status').filter({ hasText: 'Ready' }).waitFor();
    await page.screenshot({ path: '/tmp/relay-monaco-editor.png' });
    const bounds = await page.locator('dialog').boundingBox();
    assert.equal(bounds.width, 1440);
    assert.equal(bounds.height, 900);
    const editor = { fill: text => page.evaluate(async text => {
        const { monaco } = await import('/_content/Refund/monaco/editor.js');
        const model = monaco.editor.getModels()[0];
        model.pushEditOperations([], [{ range: model.getFullModelRange(), text }], () => null);
    }, text) };
    await editor.fill('{\n  "changed": true\n}\n');
    await page.getByText('Unsaved changes', { exact: true }).waitFor();
    await page.keyboard.press('ControlOrMeta+s');
    await page.getByRole('status').filter({ hasText: /^Saved$/ }).waitFor();
    let bytes = await readFile(original.path);
    assert.deepEqual([...bytes.subarray(0, 3)], [239, 187, 191]);
    assert.equal(bytes.subarray(3).toString(), '{\r\n  "changed": true\r\n}\r\n');
    // Save intentionally overwrites changes made by another program.
    await writeFile(original.path, 'external change\n');
    await editor.fill('my edits\n');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByRole('status').filter({ hasText: /^Saved$/ }).waitFor();
    assert.equal((await readFile(original.path)).subarray(3).toString(), 'my edits\r\n');
    // Save As creates a copy, then subsequent Save targets that new file.
    await page.getByRole('button', { name: 'Save As…', exact: true }).click();
    await page.getByRole('textbox', { name: 'Destination file path' }).fill('copy.txt');
    await page.getByRole('button', { name: 'Save file', exact: true }).click();
    await page.getByRole('status').filter({ hasText: /^Saved$/ }).waitFor();
    const copyPath = original.path.replace(/input\.json$/, 'copy.txt');
    assert.equal((await readFile(copyPath)).subarray(3).toString(), 'my edits\r\n');
    await editor.fill('second save');
    await page.getByRole('button', { name: 'Save', exact: true }).click();
    await page.getByRole('status').filter({ hasText: /^Saved$/ }).waitFor();
    assert.equal((await readFile(copyPath)).subarray(3).toString(), 'second save');
    // Save As requires explicit overwrite confirmation.
    await page.getByRole('button', { name: 'Save As…', exact: true }).click();
    await page.getByRole('textbox', { name: 'Destination file path' }).fill('input.json');
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Save file', exact: true }).click();
    await page.getByRole('status').filter({ hasText: /^Saved$/ }).waitFor();
    assert.equal((await readFile(original.path)).subarray(3).toString(), 'second save');
    // Escape can cancel closing, then discard changes when explicitly confirmed.
    await editor.fill('unsaved');
    page.once('dialog', dialog => dialog.dismiss());
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('dialog').count(), 1);
    page.once('dialog', dialog => dialog.accept());
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    await page.locator('dialog').waitFor({ state: 'detached' });
    assert.deepEqual(await page.evaluate(() => window.closedEditor), ['OnTextEditorClosed', true]);
    assert.equal((await readFile(original.path)).subarray(3).toString(), 'second save');
    // Expected HTTP failures are logged by Chrome; no runtime/worker failures are allowed.
    assert.deepEqual(errors.filter(error => !error.includes('Failed to load resource')), []);
    assert.ok(workers.some(url => url.includes('json.worker')), 'JSON language worker must start');
    console.log('Passed: fullscreen Monaco, HTTP load/save, Save As, overwrite confirmation, overwrite-on-save, BOM/CRLF, CSRF, authentication, workers, and unsaved-close protection.');
} finally { await browser.close(); }
