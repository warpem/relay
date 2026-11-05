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
const files = new Map([['/float.mrcs',stack(2)],['/half.mrcs',stack(12)],['/short.mrcs',stack(1)],['/signed.mrcs',stack(2,1,true)],['/sub-a.mrcs',stack(2,4)],['/sub-b.mrcs',stack(2,2)]]);
if (process.env.CLASS2D_TEST_STACK) files.set('/actual.mrcs', await readFile(process.env.CLASS2D_TEST_STACK));
const requests = new Map();
const html = `<!doctype html><style>body{margin:12px}section{margin-bottom:12px}</style>
<script>window.createdWorkers=0;window.terminatedWorkers=0;window.revokedUrls=0;
const RealWorker=Worker;window.Worker=class extends RealWorker{constructor(...a){super(...a);createdWorkers++}terminate(){terminatedWorkers++;super.terminate()}};
const realRevoke=URL.revokeObjectURL.bind(URL);URL.revokeObjectURL=u=>{revokedUrls++;realRevoke(u)};</script>
<script type=module>window.images=await import('/Components/Class2DSelection/Class2DSelection.razor.js');
window.configs=new Map();window.selections=[];
window.mount=(id,url,indices,signed=false,extras={})=>{
 let e=document.getElementById(id);if(!e){e=document.createElement('section');e.id=id;document.body.append(e)}
 const options={url,signed,items:indices.map(i=>({number:i+1,id:i+1,label:'#'+(i+1),resolution:20-i/10,size:(i+1)/1000})),
 imageSize:64,spacing:6,borderWidth:2,borderColor:'transparent',selectedColor:'mediumpurple',allowSorting:true,mutable:true,selected:[],selectionVersion:0,...extras};
 configs.set(id,options);images.update(e,options,{invokeMethodAsync:async(method,selection,version)=>{selections.push({id,method,selection,version})}},options.selected);
};window.refresh=(id,extras={})=>{const c={...configs.get(id),...extras};configs.set(id,c);images.update(document.getElementById(id),c,{invokeMethodAsync:async(method,selection,version)=>{selections.push({id,method,selection,version})}},c.selected)};
window.ready=true;</script>`;
const server = createServer(async (req,res) => {
    requests.set(req.url,(requests.get(req.url)??0)+1);
    try {
        if (req.url === '/') { res.setHeader('Content-Type','text/html');res.end(html);return; }
        if (files.has(req.url)) {const data=files.get(req.url);res.setHeader('Content-Length',data.length);res.setHeader('Cache-Control','no-store');res.end(data);return;}
        if (!req.url.startsWith('/Components/') && !req.url.startsWith('/js/') && !req.url.startsWith('/css/')) {res.writeHead(404);res.end();return;}
        res.setHeader('Content-Type',req.url.endsWith('.css')?'text/css':'text/javascript');
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
        return images.length===n&&images.every(i=>i.complete&&i.naturalWidth>0)&&e.querySelector('[data-image-status]').textContent===''&&e.getAttribute('aria-busy')!=='true';
    },{id,n});
    await page.evaluate(()=>mount('classes','/float.mrcs',Array.from({length:150},(_,i)=>i)));
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
    await page.evaluate(()=>mount('parent','/float.mrcs',[0,149]));
    await loaded('parent',2);
    assert.equal(requests.get('/float.mrcs'),1,'Parent comparisons must share the decoded stack');
    const before=await page.locator('#classes img[data-image-index="0"]').getAttribute('src');
    await page.locator('#classes select').selectOption('size');
    await page.locator('#classes .class2d-direction').click();
    assert.equal(await page.locator('#classes .class2d-grid button').first().getAttribute('data-image-number'),'150');
    assert.equal(await page.locator('#classes img[data-image-index="0"]').getAttribute('src'),before);
    assert.equal(requests.get('/float.mrcs'),1,'Sorting must not refetch or regenerate images');
    // Selection stays in JS across metadata refreshes, sorting and contrast updates.
    await page.locator('#classes [data-image-number="150"]').click();
    await page.locator('#classes [data-image-number="1"]').focus();
    await page.keyboard.press('Space');
    await page.waitForFunction(()=>selections.length===2);
    assert.deepEqual(await page.evaluate(()=>selections.at(-1)),{id:'classes',method:'ReceiveSelection',selection:[1,150],version:0});
    await page.evaluate(()=>refresh('classes'));
    assert.equal(await page.locator('#classes [aria-pressed="true"]').count(),2);
    // A server-side bulk selection overrides browser state without a callback loop.
    await page.evaluate(()=>refresh('classes',{selected:[2,150],selectionVersion:1}));
    assert.equal(await page.locator('#classes [data-image-number="2"]').getAttribute('aria-pressed'),'true');
    assert.equal(await page.locator('#classes [data-image-number="1"]').getAttribute('aria-pressed'),'false');
    assert.equal(await page.evaluate(()=>selections.length),2);
    const gridTop=await page.locator('#classes > .class2d-grid').evaluate(e=>e.getBoundingClientRect().top);
    const oldPreview=await page.locator('#classes img[data-image-index="0"]').getAttribute('src');
    // Rapid changes must show only the latest worker generation; no fetch or .NET callback.
    await page.evaluate(()=>{
        const b=document.querySelector('#classes [aria-label="Black percentile"]');
        const w=document.querySelector('#classes [aria-label="White percentile"]');
        for(let i=0;i<10;i++){b.value=i;b.dispatchEvent(new Event('input'))}
        b.value=30;b.dispatchEvent(new Event('input'));w.value=90;w.dispatchEvent(new Event('input'));
    });
    await page.waitForFunction(old=>document.querySelector('#classes img[data-image-index="0"]').src!==old&&document.querySelector('#classes').getAttribute('aria-busy')==='false',oldPreview);
    assert.equal(await page.locator('#classes > .class2d-grid').evaluate(e=>e.getBoundingClientRect().top),gridTop,'Contrast must not shift the grid');
    assert.equal(requests.get('/float.mrcs'),1,'Contrast must reuse the decoded stack');
    assert.equal(await page.evaluate(()=>selections.length),2,'Contrast never sends pixels or UI events to .NET');
    const changed=await page.evaluate(async()=>{const i=document.querySelector('#classes img[data-image-index="0"]');await i.decode();const c=document.createElement('canvas');c.width=c.height=8;const x=c.getContext('2d');x.drawImage(i,0,0);return [...x.getImageData(0,0,8,8).data]});
    assert.equal(changed[(7*8+7)*4],255,'White percentile must change the rendered pixels');
    assert.equal(changed[(7*8+1)*4],0,'Black percentile must change the rendered pixels');
    assert.equal(await page.locator('#classes [data-image-number="2"]').getAttribute('aria-pressed'),'true');
    // Accessible native range handles support keyboard changes and cannot cross.
    await page.locator('#classes [aria-label="Black percentile"]').focus();
    await page.keyboard.press('End');
    assert.equal(Number(await page.locator('#classes [aria-label="Black percentile"]').inputValue()),89.95);
    await page.locator('#classes .class2d-reset').click();await loaded('classes',150);
    assert.equal(await page.locator('#classes [aria-label="Black percentile"]').inputValue(),'0.5');
    assert.equal(await page.locator('#classes [aria-label="White percentile"]').inputValue(),'100');
    await page.locator('#classes [data-image-number="2"]').click();
    await page.waitForFunction(()=>selections.length===3);
    assert.deepEqual(await page.evaluate(()=>selections.at(-1).selection),[150]);
    assert.equal(await page.evaluate(()=>selections.at(-1).version),1);
    await page.locator('#classes .class2d-selection button', {hasText:'Select all'}).click();
    await page.waitForFunction(()=>selections.length===4);
    assert.equal(await page.locator('#classes [aria-pressed="true"]').count(),150);
    assert.equal(await page.evaluate(()=>selections.at(-1).selection.length),150);
    await page.locator('#classes .class2d-selection button', {hasText:'Clear'}).click();
    await page.waitForFunction(()=>selections.length===5);
    assert.equal(await page.locator('#classes [aria-pressed="true"]').count(),0);
    assert.deepEqual(await page.evaluate(()=>selections.at(-1).selection),[]);
    for(const url of ['/half.mrcs','/short.mrcs']){
        await page.evaluate(url=>mount('classes',url,Array.from({length:150},(_,i)=>i)),url);
        await loaded('classes',150);assert.equal(requests.get(url),1);
    }
    await page.evaluate(()=>mount('pc','/signed.mrcs',[0],true,{mutable:false}));await loaded('pc',1);
    const signed=await page.evaluate(async()=>{let i=document.querySelector('#pc img');await i.decode();let c=document.createElement('canvas');c.width=c.height=8;let x=c.getContext('2d');x.drawImage(i,0,0);return [...x.getImageData(0,0,8,8).data]});
    assert.equal(signed[0],128,'Signed zero must remain mid-gray');
    assert.equal(signed[(7*8+7)*4],255);
    assert.equal(await page.locator('#pc .class2d-tile').isDisabled(),true);
    // Subclasses expand one parent at a time and fetch their stack only on demand.
    await page.evaluate(()=>mount('hierarchy','/float.mrcs',[0,1,2],false,{imageSize:128,subclasses:[
        {parentId:1,url:'/sub-a.mrcs',items:Array.from({length:4},(_,i)=>({number:i+1,id:101+i,label:'#1.'+(i+1),size:0.25}))},
        {parentId:2,url:'/sub-b.mrcs',items:Array.from({length:2},(_,i)=>({number:i+1,id:201+i,label:'#2.'+(i+1),size:0.5}))},
        {parentId:3,items:[],reason:'Too few particles'}
    ]}));
    await loaded('hierarchy',3);
    assert.equal(requests.get('/sub-a.mrcs'),undefined);
    assert.equal(requests.get('/sub-b.mrcs'),undefined);
    await page.locator('#hierarchy [aria-label="Show subclasses for class 1"]').click();
    await loaded('hierarchy-subclasses',4);
    assert.equal(requests.get('/sub-a.mrcs'),1);
    assert.equal(await page.locator('#hierarchy .class2d-card').first().evaluate(e=>getComputedStyle(e).outlineStyle),'none','Open parents have no selection-like outline');
    assert.equal(await page.locator('#hierarchy .class2d-panel').count(),1);
    assert.equal(await page.locator('#hierarchy-subclasses .class2d-toolbar').isVisible(),false);
    await page.locator('#hierarchy-subclasses [data-image-number="3"]').click();
    await page.waitForFunction(()=>selections.at(-1)?.method==='ReceiveSubclassSelection');
    assert.deepEqual(await page.evaluate(()=>selections.at(-1).selection),[103]);
    assert.equal(await page.locator('#hierarchy > .class2d-grid > .class2d-card > [aria-pressed="true"]').count(),0);
    await page.locator('#hierarchy [aria-label="Show subclasses for class 2"]').click();
    await loaded('hierarchy-subclasses',2);
    assert.equal(await page.locator('#hierarchy .class2d-panel').count(),1,'Only one compact panel is open');
    await page.locator('#hierarchy-subclasses [data-image-number="2"]').click();
    await page.waitForFunction(()=>selections.at(-1)?.selection.includes(202));
    assert.deepEqual(await page.evaluate(()=>selections.at(-1).selection),[103,202]);
    await page.evaluate(()=>{
        const subclasses=configs.get('hierarchy').subclasses.map(g=>g.parentId===2?{...g,items:g.items.map(i=>({...i,id:i.id+300}))}:g);
        refresh('hierarchy',{subclasses});
    });
    await page.waitForFunction(()=>selections.at(-1)?.selection.includes(502));
    assert.deepEqual(await page.evaluate(()=>selections.at(-1).selection),[103,502],'Live metadata updates preserve parent/local identity when export IDs shift');
    await page.locator('#hierarchy [aria-label="Show subclasses for class 1"]').click();
    await loaded('hierarchy-subclasses',4);
    assert.equal(await page.locator('#hierarchy-subclasses [data-image-number="3"]').getAttribute('aria-pressed'),'true');
    const childFetches=requests.get('/sub-a.mrcs');
    const childUrl=await page.locator('#hierarchy-subclasses img').first().getAttribute('src');
    await page.evaluate(()=>{const w=document.querySelector('#hierarchy > .class2d-toolbar [aria-label="White percentile"]');w.value=90;w.dispatchEvent(new Event('input'))});
    await page.waitForFunction(old=>document.querySelector('#hierarchy-subclasses img').src!==old&&document.querySelector('#hierarchy-subclasses').getAttribute('aria-busy')==='false',childUrl);
    assert.equal(requests.get('/sub-a.mrcs'),childFetches,'Expanded children share the parent contrast without refetching');
    await page.locator('#hierarchy > .class2d-subclass-tools button',{hasText:'Select all subclasses'}).click();
    await page.waitForFunction(()=>selections.at(-1)?.selection.length===6);
    assert.deepEqual(await page.evaluate(()=>selections.at(-1).selection),[101,102,103,104,501,502]);
    assert.equal(await page.locator('#hierarchy-subclasses [aria-pressed="true"]').count(),4);
    await page.locator('#hierarchy > .class2d-subclass-tools button',{hasText:'Clear subclasses'}).click();
    await page.waitForFunction(()=>selections.at(-1)?.selection.length===0);
    assert.equal(await page.locator('#hierarchy-subclasses [aria-pressed="true"]').count(),0);
    await page.setViewportSize({width:560,height:1000});
    await page.waitForFunction(()=>document.documentElement.scrollWidth<=innerWidth);
    assert.equal(await page.locator('#hierarchy .class2d-panel').count(),1);
    await page.setViewportSize({width:1000,height:1000});
    await page.locator('#hierarchy').screenshot({path:'/private/tmp/relay-class2d-subclass-control.png'});
    await page.locator('#hierarchy [aria-label="Show subclasses for class 3"]').click();
    assert.equal(await page.locator('#hierarchy .class2d-panel').textContent(),'Parent #3 · subclassesCloseToo few particles');
    await page.locator('#hierarchy .class2d-panel-heading button').click();
    assert.equal(await page.locator('#hierarchy .class2d-panel').count(),0);
    if(files.has('/actual.mrcs')){
        await page.evaluate(()=>mount('actual','/actual.mrcs',Array.from({length:150},(_,i)=>i)));
        await loaded('actual',150);
        await page.screenshot({path:'/private/tmp/relay-class2d-client-mrc.png',fullPage:true});
    }
    await page.evaluate(()=>mount('missing','/missing.mrcs',[0]));
    await page.waitForFunction(()=>document.querySelector('#missing [data-image-status]').textContent.includes('Unable to load'));
    assert.equal(await page.evaluate(()=>createdWorkers),1,'All views use a single worker');
    await page.evaluate(()=>{for(const e of document.querySelectorAll('section')){images.dispose(e.id);e.remove()}});
    assert.equal(await page.evaluate(()=>terminatedWorkers),1,'Last disposal releases the worker and its buffers');
    assert.ok(await page.evaluate(()=>revokedUrls)>450,'Preview object URLs must be revoked on replacement/disposal');
    assert.deepEqual(errors,[]);
    console.log('150-class MRC views passed: float32/float16/int16, contrast slider, orientation, shared fetch, JS sorting/selection, metadata updates, lazy subclass panels, export ID remapping, responsive layout, disposal and visible errors.');
} finally {await browser?.close();server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
