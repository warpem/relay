import fs from 'node:fs';
import test from 'node:test';
test('astigmatism handles hidden canvases and cleans up after disposal', async () => {
let arcs=0, observers=[];
const context=new Proxy({},{get:(_,key)=>key==='arc'?((x,y,r)=>{if(r<0)throw Error('negative radius');arcs++;}):()=>{}});
function canvas(width=0){const handlers=new Map();return {width:0,height:0,measure:width,handlers,title:'',getBoundingClientRect(){return {width:this.measure,left:0,top:0}},getContext(){return context},addEventListener(k,v){handlers.set(k,v)},removeEventListener(k){handlers.delete(k)}};}
globalThis.document={createElement:()=>canvas(0)};globalThis.window={devicePixelRatio:2};globalThis.getComputedStyle=()=>({getPropertyValue:()=>''});globalThis.Path2D=class {moveTo(){} arc(x,y,r){if(r<0)throw Error('negative path radius');}};
globalThis.ResizeObserver=class {constructor(fn){this.fn=fn;observers.push(this)}observe(){}disconnect(){this.disconnected=true}};
const source=fs.readFileSync(new URL('../../ComponentDev/Components/WarpPreview/WarpAstigmatismPlot.razor.js', import.meta.url),'utf8');const mod=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
const c=canvas(0);let calls=0;mod.initialize(c,{invokeMethodAsync(){calls++;return Promise.reject(Error('disposed dotnet'))}},'test');mod.setData('test',[[4,0,0,'#999',0,0]],.1,4);mod.setView('test',.05,4);if(arcs)throw Error('hidden canvas drew');
c.measure=200;observers[0].fn();if(!arcs)throw Error('visible canvas did not redraw');c.handlers.get('pointermove')({clientX:100,clientY:100});c.handlers.get('click')();await Promise.resolve();if(calls!==1)throw Error('click not dispatched');
c.measure=0;observers[0].fn();mod.dispose('test');if(c.handlers.size||!observers[0].disconnected)throw Error('cleanup incomplete');mod.setView('test',.1,4);mod.dispose('test');console.log('PASS hidden → visible draw, disposed callback rejection, ID cleanup, repeated dispose');

});
