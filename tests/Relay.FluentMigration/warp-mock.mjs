// Run against scripts/warp-dev.py only; this starts/stops processing and emits exports.
import assert from 'node:assert/strict';
import { readFile, mkdir } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { tmpdir } from 'node:os';
const state = process.argv[2] ?? process.env.WARP_DEV_STATE;
if (!state) throw new Error('Usage: node warp-mock.mjs <local dev state directory>');
const config = JSON.parse(await readFile(join(resolve(state), 'dev-session.json'), 'utf8'));
const url = new URL(config.url);
assert.equal(url.hostname, '127.0.0.1', 'Only the isolated local mock launcher is supported.');
const expected = Number(process.env.WARP_MOCK_COUNT ?? 4);
const artifacts = process.env.WARP_TEST_ARTIFACTS ?? join(tmpdir(), 'warp-mock-browser');
await mkdir(artifacts, { recursive: true });
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const browser = await chromium.launch({ headless: true,
    ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH } : {}) });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();
const errors = [];
page.on('pageerror', error => errors.push(error.message));
page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
const button = text => page.locator('fluent-button').filter({ hasText: new RegExp('^\\s*' + text + '\\s*$') });
const tab = text => page.locator('.workspace-nav').getByRole('button', { name: text, exact: true }).click();
const waitStopped = () => page.waitForFunction(() =>
    document.querySelector('.run-label')?.textContent.trim() === 'Stopped' &&
    !document.querySelector('#processing-toggle')?.hasAttribute('disabled'), null, { timeout: 90000 });
const waitCaughtUp = () => page.waitForFunction(count =>
    Number(document.querySelector('#movie-count')?.textContent.replaceAll(',', '')) >= count &&
    document.querySelector('.telemetry')?.textContent.includes('caught up'), expected, { timeout: 180000 });
try {
    const login = await context.request.post(new URL('/process-login', url).href,
        { form: { Username: config.username, Password: config.password } });
    assert.ok(login.ok(), 'Login failed');
    await page.goto(new URL(config.route ?? '/P1/S1/V1/J1', url).href);
    await page.locator('.study-page').waitFor();
    assert.match(await page.locator('.session-identity').innerText(), /mock/i,
        'Refusing to mutate a session that is not labelled MOCK.');
    if ((await page.locator('#processing-toggle').innerText()).includes('Stop')) {
        await page.locator('#processing-toggle').click();
        await waitStopped();
    }
    await page.locator('#processing-toggle').click();
    await waitCaughtUp();
    assert.equal(await page.locator('.error-link').count(), 0, 'Worker failures appeared');
    assert.equal(await page.locator('.action-error').count(), 0, 'Lifecycle command failed');
    console.log('PASS: actual worker pipeline caught up');

    const pool = page.locator('fluent-button[title="Worker resources"]');
    const initialPool = Number((await pool.innerText()).match(/\d+/)[0]);
    await page.locator('fluent-button[title="Add worker"]').click();
    await page.waitForFunction(n => document.querySelector('fluent-button[title="Worker resources"]')?.textContent.trim().startsWith(String(n)), initialPool + 1);
    await page.locator('fluent-button[title="Remove worker"]').click();
    await page.waitForFunction(n => document.querySelector('fluent-button[title="Worker resources"]')?.textContent.trim().startsWith(String(n)), initialPool);
    console.log('PASS: running pool target resized up/down');

    await tab('Monitor');
    await page.locator('.real-spectrum canvas.chart').waitFor();
    await page.locator('.micrograph-viewer-container canvas').first().waitFor();
    await page.waitForTimeout(500);
    assert.match(await page.locator('.histogram-key').innerText(), /[1-9]/);
    await page.screenshot({ path: join(artifacts, 'monitor.png'), fullPage: true });
    await tab('Picking');
    await page.locator('.micrograph-viewer-container canvas').first().waitFor();
    await page.waitForTimeout(300);
    await page.screenshot({ path: join(artifacts, 'picking.png'), fullPage: true });
    console.log('PASS: generated micrograph, spectrum and particle views');

    await tab('Exports');
    // Count only once the view has rendered; persisted sessions already list earlier exports.
    await page.locator('.export-history, .export-empty').first().waitFor();
    const exportsBefore = await page.locator('.export-entry').count();
    await page.waitForFunction(() => [...document.querySelectorAll('fluent-button')].some(b => b.textContent.trim() === 'Export micrographs' && !b.hasAttribute('disabled')));
    await button('Export micrographs').click();
    await page.waitForFunction(n => document.querySelectorAll('.export-entry').length === n, exportsBefore + 1);
    await page.waitForFunction(() => [...document.querySelectorAll('fluent-button')].some(b => b.textContent.trim() === 'Export particles' && !b.hasAttribute('disabled')));
    await button('Export particles').click();
    await page.waitForFunction(n => document.querySelectorAll('.export-entry').length === n, exportsBefore + 2);
    assert.equal(await page.locator('.action-error').count(), 0);
    console.log('PASS: micrograph and particle export jobs created');

    await page.locator('#processing-toggle').click();
    await waitStopped();
    await page.locator('#processing-toggle').click();
    await waitCaughtUp();
    await page.locator('#processing-toggle').click();
    await waitStopped();
    console.log('PASS: stop, resume and stop through normal Relay lifecycle');
    assert.deepEqual(errors, []);
} catch (error) {
    await page.screenshot({ path: join(artifacts, 'failure.png'), fullPage: true }).catch(() => {});
    console.error((await page.locator('body').innerText()).slice(-4000));
    throw error;
} finally {
    await browser.close();
}
