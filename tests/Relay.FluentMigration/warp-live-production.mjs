// Exercises an isolated Warp Live test catalog only. Requires 12,384 illustrative rows
// and the named fixture exposure; never point this mutation test at a real collection.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const baseUrl = process.env.WARP_LIVE_TEST_URL;
const route = process.env.WARP_LIVE_TEST_ROUTE;
const username = process.env.WARP_LIVE_TEST_USERNAME;
const password = process.env.WARP_LIVE_TEST_PASSWORD;
if (!baseUrl || !route || !username || !password)
    throw new Error('Set WARP_LIVE_TEST_URL, WARP_LIVE_TEST_ROUTE, WARP_LIVE_TEST_USERNAME and WARP_LIVE_TEST_PASSWORD for the isolated seeded test host.');
const artifacts = process.env.WARP_LIVE_TEST_ARTIFACTS ?? join(tmpdir(), 'warp-live-browser-artifacts');
await mkdir(artifacts, { recursive: true });
const browser = await chromium.launch({ headless: true,
    ...(process.env.PLAYWRIGHT_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_EXECUTABLE_PATH } : {}) });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
await context.request.post(new URL('/process-login', baseUrl).href, { form: { Username: username, Password: password } });
const errors=[],checks=[];const page=await context.newPage();const watch=p=>{p.on('pageerror',e=>errors.push(e.message));p.on('console',m=>{if(m.type()==='error')errors.push(m.text())})};watch(page);
const settle=async()=>page.waitForTimeout(250);
const fb=(root,text)=>root.locator('fluent-button').filter({hasText:new RegExp('^\\s*'+text.replace(/[.*+?^${}()|[\]\\]/g,'\\$&')+'\\s*$')});
const tab=async(p,t)=>{await p.locator('.workspace-nav').getByRole('button',{name:t,exact:true}).click();await settle()};
const shot=async(name)=>page.screenshot({path:join(artifacts, 'warp-live-10k-'+name+'.png'),fullPage:true});
let other;
try{
const begin=Date.now();await page.goto(new URL(route, baseUrl).href);await page.locator('.study-page').waitFor();assert.equal((await page.locator('#movie-count').innerText()).replaceAll(',',''),'12384');console.log('catalog load ms',Date.now()-begin);
await tab(page,'Monitor');await page.waitForTimeout(1400);await shot('monitor-1440-sidebar');console.log('monitor',await page.locator('.histogram-key').innerText());assert.ok((await page.locator('.histogram-key').innerText()).includes('2,074,794'));checks.push('12,384 rows and aggregate histogram restored');
await page.getByRole('button',{name:'Collapse right panel',exact:true}).click();await settle();await shot('monitor-1440-wide');
await fb(page.locator('.window-buttons'),'All').click();await settle();assert.match(await page.locator('.session-trends').innerText(),/12,384/);checks.push('all-collection plots');
const maxCtf=page.locator('.metric-filter input').nth(1);await maxCtf.fill('3.5');await maxCtf.press('Tab');await settle();await maxCtf.fill('5');await maxCtf.press('Tab');await settle();checks.push('immediate CTF filter');
await tab(page,'Movies');assert.equal(await page.locator('.movie-table tbody tr').count(),25);await page.getByRole('button',{name:'Last page',exact:true}).click();await settle();assert.match(await page.locator('.pagination').innerText(),/12,384/);assert.equal(await page.locator('.movie-table tbody tr').count(),9);checks.push('paginated catalog');
const search=page.locator('.movie-search input');await search.fill('FoilHole_0008_Data_000999_Fractions.eer');await search.press('Tab');await settle();assert.equal(await page.locator('.movie-table tbody tr').count(),1);await fb(page.locator('.batch-toolbar'),'Select page').click();await settle();await fb(page.locator('.batch-toolbar'),'Exclude').click();await settle();assert.match(await page.locator('.movie-table tbody').innerText(),/Excluded/);await fb(page.locator('.batch-toolbar'),'Filter rules').click();await settle();checks.push('search and bulk curation');
await page.locator('.movie-table .filename').click();await settle();assert.match(await page.locator('.exposure-heading').innerText(),/#1,000/);checks.push('table opens selected evidence');
await tab(page,'Picking');await page.waitForTimeout(800);const rangeRequests=[];page.on('request',r=>{if(r.url().includes('/api/file/')&&r.headers()['range'])rangeRequests.push(r.url())});await shot('picking-1440-wide');
other=await context.newPage();watch(other);await other.goto(new URL(route, baseUrl).href);await other.locator('.study-page').waitFor();await tab(other,'Monitor');await other.waitForTimeout(400);const corr=other.locator('.histogram-filters input').first();await corr.fill('0.35');await corr.press('Tab');await other.waitForTimeout(700);assert.equal(rangeRequests.length,0,'particle filter updates must not refetch loaded MRC');await corr.fill('0');await corr.press('Tab');await other.waitForTimeout(400);assert.equal(rangeRequests.length,0);checks.push('shared filters update picking overlay without MRC reload');await other.close();other=null;
await tab(page,'Monitor');await page.setViewportSize({width:3840,height:2160});await page.waitForTimeout(600);await shot('monitor-4k-wide');await page.getByRole('button',{name:'Expand right panel',exact:true}).click();await settle();await shot('monitor-4k-sidebar');
await tab(page,'Movies');await search.fill('');await search.press('Tab');await settle();await page.locator('.table-evidence').waitFor();await page.waitForTimeout(500);await shot('movies-4k-sidebar');assert.equal(await page.locator('.table-evidence').count(),1);checks.push('4K table evidence and responsive monitor');
await page.setViewportSize({width:1440,height:900});await settle();assert.equal(await page.locator('.table-evidence').count(),0);await shot('movies-1440-sidebar');checks.push('laptop table unmounts extra viewers');
console.log('checks',checks);console.log('errors',errors);console.log('overflow',await page.evaluate(()=>({innerWidth,scroll:document.documentElement.scrollWidth})));assert.deepEqual(errors,[]);
}catch(e){await shot('failure');console.log('body',(await page.locator('.study-page').innerText()).slice(-3000));throw e}finally{if(other)await other.close();await browser.close()}
