// Run with PLAYWRIGHT_MODULE and CHROMIUM_EXECUTABLE when using an existing browser install.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const root = new URL('../../', import.meta.url);
const viewPath = 'Refund/Jobs/Refinement/Classes3D/BeamClass3D/BeamClass3DExpandedView.razor';
const razor = await readFile(new URL(viewPath, root), 'utf8');
const css = await readFile(new URL(`${viewPath}.css`, root), 'utf8');
const figureCss = await readFile(new URL('Refund/Components/ServerFigure.razor.css', root), 'utf8');
// Use the actual viewport markup and styles with the real viewer module/ResizeObserver.
const viewport = razor.match(/<div class="map-viewport">\s*<IsosurfaceViewer[\s\S]*?\/>\s*<\/div>/)?.[0];
assert.ok(viewport, 'Each percentage-height viewer needs a bounded viewport');
const cards = Array.from({ length: 8 }, (_, i) => `<section class="main-frame">
    <header>Class ${i + 1}</header>
    ${viewport.replace(/<IsosurfaceViewer[\s\S]*?\/>/, `<div id="viewer-${i}" style="position:relative;width:100%;height:100%;min-width:240px;min-height:250px"></div>`)}
    ${Array.from({ length: 4 }, () => `<figure class="theme-figure" style="max-width:18em"><figcaption>Figure</figcaption><img src="/figure.svg"></figure>`).join('')}
</section>`).join('');
const html = `<!doctype html><html><head><style>body{margin:0;font:16px sans-serif} .panel{width:100%;min-width:0} ${css} ${figureCss}</style></head>
<body><main class="panel"><div class="classification-view"><button>Export selection</button><p>Classification</p><div class="scroll-container">${cards}</div></div></main>
<script type="module">import {initialize} from '/Components/IsosurfaceViewer/IsosurfaceViewer.razor.js';
for(let i=0;i<8;i++) initialize(document.getElementById('viewer-'+i),null,null,{miniMode:true,minWidth:240,minHeight:250});
window.viewersReady=true;</script></body></html>`;
const server = createServer(async (req, res) => {
    try {
        if (req.url === '/') { res.setHeader('Content-Type', 'text/html'); res.end(html); return; }
        if (req.url === '/figure.svg') { res.setHeader('Content-Type', 'image/svg+xml'); res.end('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024"><rect width="1024" height="1024" fill="gray"/></svg>'); return; }
        const path = req.url.startsWith('/Components/') ? `Refund${req.url}` : `Refund/wwwroot${req.url}`;
        res.setHeader('Content-Type', 'text/javascript');
        res.end(await readFile(new URL(path, root)));
    } catch (error) { res.writeHead(404); res.end(String(error)); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
let browser;
try {
    browser = await chromium.launch({ headless: true, ...(process.env.CHROMIUM_EXECUTABLE ? { executablePath: process.env.CHROMIUM_EXECUTABLE } : {}) });
    for (const deviceScaleFactor of [1, 2]) {
        const page = await browser.newPage({ viewport: { width: 1000, height: 700 }, deviceScaleFactor });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.goto(`http://127.0.0.1:${server.address().port}`);
        await page.waitForFunction(() => window.viewersReady);
        const snapshots = [];
        for (const width of [1000, 600, 1200]) {
            await page.setViewportSize({ width, height: 700 });
            for (let sample = 0; sample < 5; sample++) {
                await page.waitForTimeout(120);
                snapshots.push(await page.evaluate(() => ({
                    height: document.documentElement.scrollHeight,
                    width: document.documentElement.scrollWidth,
                    viewports: [...document.querySelectorAll('.map-viewport')].map(e => e.getBoundingClientRect().height),
                    cards: [...document.querySelectorAll('.main-frame')].map(e => e.getBoundingClientRect().width),
                    canvases: [...document.querySelectorAll('canvas')].map(e => [e.width, e.height]),
                })));
            }
            assert.equal(snapshots.at(-1).width, width, 'Only the class strip should scroll horizontally');
        }
        assert.equal(new Set(snapshots.map(s => s.height)).size, 1, 'Canvas resizing must not enlarge the document');
        for (const snapshot of snapshots) {
            assert.ok(snapshot.height < 2000);
            assert.ok(snapshot.viewports.every(height => height === 250));
            assert.ok(snapshot.cards.every(width => width === 368));
            assert.ok(snapshot.canvases.every(([width, height]) => width > 0 && width < 750 && height > 0 && height <= 500));
        }
        assert.deepEqual(errors, []);
        console.log(`Stable eight-class layout at DPR ${deviceScaleFactor}: ${snapshots.at(-1).height}px document height`);
        await page.close();
    }
} finally {
    if (browser) await browser.close();
    await new Promise(resolve => server.close(resolve));
}
