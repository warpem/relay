import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:http';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const root = new URL('../../', import.meta.url);

function stack(mode, count = 150, signed = false) {
    const width = 8, pixels = width * width;
    const buffer = Buffer.alloc(1024 + pixels * count * (mode === 2 ? 4 : 2));
    for (const [offset, value] of [[0,width],[4,width],[8,count],[12,mode],[28,width],[32,width],[36,1],[64,1],[68,2],[72,3]]) buffer.writeInt32LE(value, offset);
    buffer.writeFloatLE(width,40); buffer.writeFloatLE(width,44); buffer.writeFloatLE(1,48);
    buffer.write('MAP ',208); buffer[212] = 68; buffer[213] = 68;
    for (let i = 0; i < count; i++) for (let p = 0; p < pixels; p++) {
        let value = signed ? (p === 0 ? -2 : p === 7 ? 4 : 0) : (p === i % pixels ? 64 : p % 8);
        const offset = 1024 + (i*pixels+p)*(mode === 2 ? 4 : 2);
        if (mode === 2) buffer.writeFloatLE(value, offset);
        else if (mode === 12) { // All fixtures use exactly representable nonnegative integers.
            let bits = 0;
            if (value) { const power=Math.floor(Math.log2(value)); bits=((power+15)<<10)|Math.round((value/2**power-1)*1024); }
            buffer.writeUInt16LE(bits, offset);
        } else buffer.writeInt16LE(value, offset);
    }
    return buffer;
}
const files = new Map([['/float.mrcs',stack(2)],['/half.mrcs',stack(12)],['/short.mrcs',stack(1)],['/signed.mrcs',stack(2,1,true)]]);
if (process.env.CLASS2D_TEST_STACK) files.set('/actual.mrcs', await readFile(process.env.CLASS2D_TEST_STACK));
const requests = new Map();
const html = `<!doctype html><style>body{margin:0}section{display:flex;flex-wrap:wrap;gap:2px}img{width:48px;height:48px;background:#b44}img:not([src]){visibility:hidden}</style>
<script>window.createdWorkers=0;window.terminatedWorkers=0;window.revokedUrls=0;
const RealWorker=Worker;window.Worker=class extends RealWorker{constructor(...a){super(...a);createdWorkers++}terminate(){terminatedWorkers++;super.terminate()}};
const realRevoke=URL.revokeObjectURL.bind(URL);URL.revokeObjectURL=u=>{revokedUrls++;realRevoke(u)};</script>
<script type=module>window.images=await import('/Components/Class2DSelection/Class2DSelection.razor.js');
window.grid=(id,indices)=>{let e=document.getElementById(id);if(!e){e=document.createElement('section');e.id=id;document.body.append(e)}e.innerHTML='<div data-image-status></div>'+indices.map(i=>'<img data-image-index="'+i+'">').join('');return e};window.ready=true;</script>`;
const server = createServer(async (req,res) => {
    requests.set(req.url,(requests.get(req.url)??0)+1);
    try {
        if (req.url === '/') { res.setHeader('Content-Type','text/html');res.end(html);return; }
        if (files.has(req.url)) {const data=files.get(req.url);res.setHeader('Content-Length',data.length);res.setHeader('Cache-Control','no-store');res.end(data);return;}
        if (!req.url.startsWith('/Components/') && !req.url.startsWith('/js/')) {res.writeHead(404);res.end();return;}
        res.setHeader('Content-Type','text/javascript');
        res.end(await readFile(new URL(req.url.startsWith('/Components/')?'Refund'+req.url:'Refund/wwwroot'+req.url,root)));
    } catch(error) {res.writeHead(404);res.end(String(error));}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
let browser;
try {
    browser=await chromium.launch({headless:true,...(process.env.CHROMIUM_EXECUTABLE?{executablePath:process.env.CHROMIUM_EXECUTABLE}:{})});
    const page=await browser.newPage({viewport:{width:1000,height:1000},deviceScaleFactor:2});
    const errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.goto(`http://127.0.0.1:${server.address().port}`);await page.waitForFunction(()=>window.ready);
    const loaded = async (id,n)=>page.waitForFunction(({id,n})=>{
        const e=document.getElementById(id); const images=[...e.querySelectorAll('img')];
        return images.length===n&&images.every(i=>i.complete&&i.naturalWidth>0)&&e.querySelector('[data-image-status]').textContent==='';
    },{id,n});
    await page.evaluate(()=>images.update(grid('classes',Array.from({length:150},(_,i)=>i)),'/float.mrcs'));
    await loaded('classes',150);
    assert.equal(requests.get('/float.mrcs'),1);
    assert.equal(await page.locator('#classes img').count(),150);
    // Actual pixels prove origin='lower', per-class max contrast, and image identity.
    const pixels=await page.evaluate(async()=>{
        const img=document.querySelector('#classes img');await img.decode();
        const c=document.createElement('canvas');c.width=c.height=8;let x=c.getContext('2d');x.drawImage(img,0,0);
        return [...x.getImageData(0,0,8,8).data];
    });
    assert.equal(pixels[(7*8)*4],255); // Original row 0 moves to bottom.
    assert.equal(pixels[0],0);
    await page.evaluate(()=>images.update(grid('parent',[0,149]),'/float.mrcs'));
    await loaded('parent',2);
    assert.equal(requests.get('/float.mrcs'),1,'Parent comparisons must share the decoded stack');
    const before=await page.locator('#classes img[data-image-index="0"]').getAttribute('src');
    await page.evaluate(()=>{const e=document.getElementById('classes');e.append(e.querySelector('img'));images.update(e,'/float.mrcs')});
    assert.equal(await page.locator('#classes img[data-image-index="0"]').getAttribute('src'),before);
    assert.equal(requests.get('/float.mrcs'),1,'Sorting must not refetch or regenerate images');
    for(const url of ['/half.mrcs','/short.mrcs']){
        await page.evaluate(url=>images.update(grid('classes',Array.from({length:150},(_,i)=>i)),url),url);
        await loaded('classes',150);assert.equal(requests.get(url),1);
    }
    await page.evaluate(()=>images.update(grid('pc',[0]),'/signed.mrcs',true));await loaded('pc',1);
    const signed=await page.evaluate(async()=>{let i=document.querySelector('#pc img');await i.decode();let c=document.createElement('canvas');c.width=c.height=8;let x=c.getContext('2d');x.drawImage(i,0,0);return [...x.getImageData(0,0,8,8).data]});
    assert.equal(signed[0],128,'Signed zero must remain mid-gray');
    assert.equal(signed[(7*8+7)*4],255);
    if(files.has('/actual.mrcs')){
        await page.evaluate(()=>images.update(grid('actual',Array.from({length:150},(_,i)=>i)),'/actual.mrcs'));
        await loaded('actual',150);
        await page.screenshot({path:'/private/tmp/relay-class2d-client-mrc.png',fullPage:true});
    }
    await page.evaluate(()=>images.update(grid('missing',[0]),'/missing.mrcs'));
    await page.waitForFunction(()=>document.querySelector('#missing [data-image-status]').textContent.includes('Unable to load'));
    assert.equal(await page.evaluate(()=>createdWorkers),1,'All views use a single worker');
    await page.evaluate(()=>{for(const e of document.querySelectorAll('section')){images.dispose(e.id);e.remove()}});
    assert.equal(await page.evaluate(()=>terminatedWorkers),1,'Last disposal releases the worker and its buffers');
    assert.ok(await page.evaluate(()=>revokedUrls)>450,'Preview object URLs must be revoked on replacement/disposal');
    assert.deepEqual(errors,[]);
    console.log('150-class MRC views passed: float32/float16/int16, contrast, orientation, shared fetch, sorting, disposal and visible errors.');
} finally {await browser?.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
