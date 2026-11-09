import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const root = new URL('../../', import.meta.url);
function mrc(mode = 2, extended = 0) {
    const bytes = mode === 2 ? 4 : 2;
    const buffer = Buffer.alloc(1024 + extended + 8 * 6 * 3 * bytes);
    for (const [offset, value] of [[0,8],[4,6],[8,3],[12,mode],[28,8],[32,6],[36,3],[64,1],[68,2],[72,3],[92,extended]]) buffer.writeInt32LE(value, offset);
    buffer.writeFloatLE(16,40); buffer.writeFloatLE(12,44); buffer.writeFloatLE(6,48);
    buffer.write('MAP ',208); buffer[212] = 68; buffer[213] = 68;
    for (let i = 0; i < 144; i++) {
        const v = i % 48;
        const offset = 1024 + extended + i * bytes;
        if (mode === 2) buffer.writeFloatLE(v, offset);
        else if (mode === 12) { const power = v ? Math.floor(Math.log2(v)) : 0; buffer.writeUInt16LE(v ? ((power+15)<<10)|Math.round((v/2**power-1)*1024) : 0, offset); }
        else buffer.writeInt16LE(v, offset);
    }
    return buffer;
}
const requests = [];
const server = createServer(async (req,res) => {
    try {
        if (req.url === '/') {
            res.setHeader('Content-Type','text/html');
            res.end(`<div id="viewer" style="width:640px;height:480px"><div data-micrograph-surface></div></div><script type="module">
                window.viewer=await import('/Components/MicrographViewer/MicrographViewer.razor.js');
                window.options={url:'/float.mrc',layer:1,minWidth:100,minHeight:100,showScaleBar:false,showMask:true,particleDiameter:2,particleBoxSize:4,particleColor:'#ffff00',particleShape:1};
                window.calls=[]; window.callback={invokeMethodAsync:async(...a)=>calls.push(a)};
                window.refresh=(extras={})=>{Object.assign(options,extras);viewer.update(document.getElementById('viewer'),{...options},callback)};
                refresh();window.ready=true;
            </script>`); return;
        }
        if (req.url === '/particles.star') {res.end('data_particles\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n0.5 0.5\n');return;}
        if (req.url.endsWith('.mrc')) {
            if (req.url === '/missing.mrc') { res.writeHead(404);res.end();return; }
            const data=mrc(req.url.includes('half')?12:req.url.includes('short')?1:2,req.url.includes('extended')?128:0);
            requests.push({url:req.url,range:req.headers.range});
            const match=req.headers.range?.match(/bytes=(\d+)-(\d+)/);
            if(match && !req.url.includes('full')) {
                const start=Number(match[1]),end=Number(match[2]);
                res.writeHead(206,{'Content-Range':`bytes ${start}-${end}/${data.length}`});res.end(data.subarray(start,end+1));
            } else res.end(data);
            return;
        }
        res.setHeader('Content-Type','text/javascript');
        res.end(await readFile(new URL(req.url.startsWith('/Components/')?'Refund'+req.url:'Refund/wwwroot'+req.url,root)));
    } catch(error) {res.writeHead(404);res.end(String(error));}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
let browser;
try {
    browser=await chromium.launch({headless:true,...(process.env.CHROMIUM_EXECUTABLE?{executablePath:process.env.CHROMIUM_EXECUTABLE}:{})});
    const page=await browser.newPage({viewport:{width:1000,height:800},deviceScaleFactor:2});
    const errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.goto(`http://127.0.0.1:${server.address().port}`);
    await page.waitForFunction(()=>window.ready);
    const loaded=()=>page.waitForFunction(()=>document.getElementById('viewer').getAttribute('aria-busy')==='false' && document.querySelector('.micrograph-status').textContent==='');
    await loaded();console.log('initial plane loaded');
    const sample=()=>page.evaluate(()=>{
        const c=document.querySelector('canvas'),ctx=c.getContext('2d');
        return [ctx.getImageData(160,80,1,1).data[0],ctx.getImageData(160,880,1,1).data[0]];
    });
    const reference=await sample();assert.ok(reference[0]>reference[1],`Y must be flipped: ${reference}`);
    assert.ok(requests.some(r=>r.range==='bytes=1216-1407'),'only selected zero-based layer is fetched');
    for (const url of ['/half.mrc','/short.mrc','/extended.mrc','/full.mrc']) {
        await page.evaluate(url=>refresh({url}),url);await loaded();
        assert.deepEqual(await sample(),reference,url);console.log(url+' passed');
    }
    // All motion is browser-local; only a debounced viewport summary crosses the bridge.
    await page.locator('canvas').first().hover();await page.mouse.wheel(0,20);
    await page.waitForTimeout(200);
    assert.ok(await page.evaluate(()=>calls.at(-1)[1]<80));
    await page.evaluate(()=>viewer.command('viewer','fit'));await page.waitForTimeout(150);
    await page.evaluate(()=>refresh({particlesUrl:'/particles.star',maskUrl:'/short.mrc'}));await loaded();
    await page.waitForTimeout(200);
    assert.deepEqual(errors,[]);
    const overlay=await page.locator('canvas').first().evaluate(c=>{
        const ctx=c.getContext('2d'), p=ctx.getImageData(160,80,1,1).data;
        const data=ctx.getImageData(0,0,c.width,c.height).data;
        let yellow=false;
        for(let i=0;i<data.length;i+=4) if(data[i]>240 && data[i+1]>240 && data[i+2]<20) {yellow=true;break;}
        return {red:p[0]>p[1],yellow};
    });
    assert.deepEqual(overlay,{red:true,yellow:true},'mask and particle overlays are rendered');
    const settingsRequests=requests.length;
    await page.evaluate(()=>refresh({showMask:false,particleShape:0}));await page.waitForTimeout(100);
    assert.deepEqual(await sample(),reference,'overlay toggles restore the underlying image');
    assert.equal(requests.length,settingsRequests,'display settings do not refetch pixels');
    await page.locator('canvas').first().hover();await page.mouse.down({button:'middle'});
    await page.mouse.move(400,300);await page.mouse.up({button:'middle'});
    await page.evaluate(()=>viewer.command('viewer','fit'));await page.waitForTimeout(100);
    await page.locator('.micrograph-minimap').click({position:{x:20,y:20}});await page.waitForTimeout(100);
    assert.notDeepEqual(await sample(),reference,'minimap click moves the viewport');
    await page.evaluate(()=>viewer.command('viewer','fit'));await page.waitForTimeout(100);
    assert.deepEqual(await sample(),reference,'fit restores the image');
    console.log('overlays and minimap passed');
    const before=requests.length;
    await page.evaluate(()=>{for(let i=0;i<30;i++)refresh({url:`/rapid-${i}.mrc`,maskUrl:null,particlesUrl:null})});await loaded();
    assert.equal(requests.length-before,2,'rapid browsing fetches only final header and plane');
    await page.evaluate(()=>refresh({url:'/missing.mrc'}));
    await page.waitForFunction(()=>document.querySelector('.micrograph-status').textContent.includes('404'));
    await page.evaluate(()=>refresh({url:null,maskUrl:null,particlesUrl:null}));await loaded();
    assert.equal(await page.locator('canvas').first().evaluate(c=>c.getContext('2d').getImageData(10,10,1,1).data[3]),0);
    await page.evaluate(()=>{refresh({url:'/float.mrc'});viewer.dispose('viewer')});await page.waitForTimeout(200);
    assert.equal(await page.locator('canvas').count(),0);
    assert.deepEqual(errors,[]);
    console.log('Micrograph browser checks passed: range/layer loading, float/half/short, extended headers, full-response fallback, Y flip, zoom, overlays, rapid browsing, clearing, errors, disposal.');
} finally {await browser?.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
