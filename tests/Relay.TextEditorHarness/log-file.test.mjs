import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';

const base = process.env.EDITOR_TEST_URL || 'http://127.0.0.1:5287';
const browser = await chromium.launch({ channel: 'chrome', headless: true });
try {
    const context = await browser.newContext();
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${base}/sign-in`);
    await page.goto(`${base}/?log`);
    const token = await page.evaluate(() => window.editorToken);
    const info = await (await context.request.get(`${base}/api/text-editor/${token}`)).json();
    const original = await readFile(info.path);
    const download = await context.request.get(`${base}/api/text-editor/${info.token}/content`);
    assert.equal(download.status(), 200);
    const text = (await download.body()).toString('utf8');
    assert.ok(text.includes('\u001b[A'));
    assert.equal(text, original.toString('utf8').replace(/^\uFEFF/, ''));
    await page.getByRole('button', { name: 'Edit', exact: true }).click();
    await page.getByRole('status').filter({ hasText: /^Ready$/ }).waitFor();
    const editorText = await page.evaluate(async () => {
        const { monaco } = await import('/_content/Refund/monaco/editor.js');
        return monaco.editor.getModels()[0].getValue();
    });
    assert.equal((editorText.match(/\u001b/g) || []).length, (text.match(/\u001b/g) || []).length);
    await page.getByRole('button', { name: 'Save As…', exact: true }).click();
    await page.getByRole('textbox', { name: 'Destination file path' }).fill('log-copy.txt');
    await page.getByRole('button', { name: 'Save file', exact: true }).click();
    await page.getByRole('status').filter({ hasText: /^Saved$/ }).waitFor();
    const copy = info.path.replace(/std\.out$/, 'log-copy.txt');
    const expected = (info.encoding === 'utf8bom' ? '\uFEFF' : '') + editorText;
    assert.equal((await readFile(copy)).toString('utf8'), expected);
    assert.deepEqual(await readFile(info.path), original);
    // A genuinely invalid later chunk must show a download diagnostic, never a successful truncated file.
    await writeFile(copy, 'a'.repeat(200000) + '\0');
    await page.getByRole('button', { name: 'Reload', exact: true }).click();
    await page.getByRole('status').filter({ hasText: 'The file download was interrupted.' }).waitFor();
    assert.deepEqual(errors, []);
    console.log(`Passed: ${original.length}-byte job log opens, preserves all ANSI escapes through Save As, leaves original untouched, and diagnoses interrupted invalid-content downloads.`);
} finally { await browser.close(); }
