const states = new Map();
const palette = [[18,40,55],[36,109,134],[87,211,183],[243,231,150]];
function color(t){const k=Math.min(2,Math.floor(t*3)),f=t*3-k;return `rgb(${palette[k].map((v,i)=>Math.round(v+(palette[k+1][i]-v)*f)).join(',')})`;}
const clamp=(v,min,max)=>Math.max(min,Math.min(max,v));
function finishDrag(s) {
    const pointerId = s.pointerId;
    s.start = s.end = null;
    s.pointerId = null;
    if (pointerId != null && s.canvas.hasPointerCapture?.(pointerId))
        s.canvas.releasePointerCapture(pointerId);
}
function point(s, event, insideOnly = false) {
    const rect = s.canvas.getBoundingClientRect();
    if (rect.width < 60 || rect.height < 60) return null;
    const x = (event.clientX - rect.left - s.l) / s.w;
    const y = 1 - (event.clientY - rect.top - s.t) / s.h;
    if (insideOnly && (x < 0 || x > 1 || y < 0 || y > 1)) return null;
    return [clamp(x, 0, 1), clamp(y, 0, 1) * s.powerMaximum];
}
export function initialize(canvas, id, dotnet) {
    dispose(id);
    const s = { canvas, dotnet, bins: [], cols: 64, rows: 48, powerMaximum: 1, bounds: [0, 1, 0, 1], pointerId: null, disposed: false };
    states.set(id, s);
    s.observer = new ResizeObserver(() => draw(s));
    s.observer.observe(canvas);
    s.move = event => {
        if (s.disposed || !s.w || (s.pointerId != null && s.pointerId !== event.pointerId)) return;
        const position = point(s, event);
        if (!position) return;
        const [corr, power] = position;
        if (s.start) { s.end = position; draw(s); return; }
        const x = clamp(Math.floor(corr * s.cols), 0, s.cols - 1);
        const y = clamp(Math.floor(power / s.powerMaximum * s.rows), 0, s.rows - 1);
        canvas.title = `Correlation ${(x / s.cols).toFixed(2)}–${((x + 1) / s.cols).toFixed(2)} · Power ${(y / s.rows * s.powerMaximum).toPrecision(3)}–${((y + 1) / s.rows * s.powerMaximum).toPrecision(3)} · ${(s.bins[y * s.cols + x] || 0).toLocaleString()} particles. Drag to filter.`;
    };
    s.down = event => {
        if (s.disposed || !s.w || event.button !== 0 || s.pointerId != null) return;
        const position = point(s, event, true);
        if (!position) return;
        s.start = s.end = position;
        s.pointerId = event.pointerId;
        try { canvas.setPointerCapture(event.pointerId); }
        catch { finishDrag(s); }
    };
    s.up = event => {
        if (s.disposed || !s.start || s.pointerId !== event.pointerId) return;
        const a = s.start, b = point(s, event);
        finishDrag(s);
        draw(s);
        if (!b || Math.abs(a[0] - b[0]) * s.w < 4 || Math.abs(a[1] - b[1]) / s.powerMaximum * s.h < 4) return;
        try {
            Promise.resolve(dotnet.invokeMethodAsync('SelectRange', Math.min(a[0], b[0]), Math.max(a[0], b[0]), Math.min(a[1], b[1]), Math.max(a[1], b[1]))).catch(() => {});
        } catch { /* The .NET reference may have been disposed before this event arrived. */ }
    };
    s.cancel = event => {
        if (s.disposed || (event && event.pointerId !== s.pointerId)) return;
        finishDrag(s);
        draw(s);
    };
    canvas.addEventListener('pointermove', s.move);
    canvas.addEventListener('pointerdown', s.down);
    canvas.addEventListener('pointerup', s.up);
    canvas.addEventListener('pointercancel', s.cancel);
}
export function update(id, bins, cols, rows, powerMaximum, bounds) {
    const s = states.get(id);
    if (!s || s.disposed) return;
    if (bins) s.bins = bins;
    s.cols = cols; s.rows = rows; s.powerMaximum = powerMaximum; s.bounds = bounds;
    draw(s);
}
function draw(s){
    if (s.disposed) return;
    const r=s.canvas.getBoundingClientRect();
    if(r.width<60||r.height<60) { s.w=s.h=0;finishDrag(s);return; }
    const dpr=Math.min(2,window.devicePixelRatio||1),ctx=s.canvas.getContext('2d');s.canvas.width=Math.round(r.width*dpr);s.canvas.height=Math.round(r.height*dpr);ctx.setTransform(dpr,0,0,dpr,0,0);
    const style=getComputedStyle(s.canvas),fontSize=parseFloat(style.fontSize)||12;
    // The plot area spans the astigmatism circle's height (same --plot-inset); tick labels keep the
    // polar plot's 6 px gap, so "0.0 0.5 1.0" share a line with its "45°".
    const gap=6,line=fontSize*1.2,inset=parseFloat(style.getPropertyValue('--plot-inset'))||8;
    const l=s.l=52,t=s.t=inset,w=s.w=r.width-l,h=s.h=r.height-t-gap-2*line;
    ctx.fillStyle='#122837';ctx.fillRect(l,t,w,h);
    const max=Math.max(1,...s.bins),scale=Math.log1p(max);
    for(let y=0;y<s.rows;y++)for(let x=0;x<s.cols;x++){const n=s.bins[y*s.cols+x];if(!n)continue;ctx.fillStyle=color(Math.log1p(n)/scale);ctx.fillRect(l+x*w/s.cols,t+(s.rows-1-y)*h/s.rows,Math.ceil(w/s.cols),Math.ceil(h/s.rows));}
    let b=s.bounds;if(s.start)b=[Math.min(s.start[0],s.end[0]),Math.max(s.start[0],s.end[0]),Math.min(s.start[1],s.end[1]),Math.max(s.start[1],s.end[1])];
    const x1=l+clamp(b[0],0,1)*w,x2=l+clamp(b[1],0,1)*w,y1=t+(1-clamp(b[3]/s.powerMaximum,0,1))*h,y2=t+(1-clamp(b[2]/s.powerMaximum,0,1))*h;
    ctx.fillStyle='#0009';ctx.fillRect(l,t,x1-l,h);ctx.fillRect(x2,t,l+w-x2,h);ctx.fillRect(x1,t,x2-x1,y1-t);ctx.fillRect(x1,y2,x2-x1,t+h-y2);ctx.strokeStyle='#fff';ctx.lineWidth=1;ctx.strokeRect(x1+.5,y1+.5,Math.max(0,x2-x1-1),Math.max(0,y2-y1-1));
    ctx.fillStyle=style.getPropertyValue('--colorNeutralForeground3').trim()||'#666';ctx.font=`${style.fontSize} ${style.fontFamily}`;
    ctx.textAlign='right';ctx.textBaseline='middle';for(const y of [0,.5,1])ctx.fillText((y*s.powerMaximum).toLocaleString(undefined,{maximumSignificantDigits:3}),l-gap,t+(1-y)*h);
    // Alphabetic baseline at the glyph top plus its ascent, so the gap is measured to the digits.
    ctx.textBaseline='alphabetic';const ascent=ctx.measureText('0.5').actualBoundingBoxAscent;
    for(const x of [0,.5,1]){ctx.textAlign=x===1?'right':x===0?'left':'center';ctx.fillText(x.toFixed(1),l+x*w,t+h+gap+ascent);}
    ctx.textAlign='center';ctx.fillText('Correlation',l+w/2,t+h+gap+line+ascent);
    ctx.textBaseline='middle';ctx.save();ctx.translate(9,t+h/2);ctx.rotate(-Math.PI/2);ctx.fillText('Power',0,0);ctx.restore();
    if(!s.bins.some(n=>n>0)){ctx.fillStyle='#ddd';ctx.textBaseline='middle';ctx.fillText('No up-to-date picks',l+w/2,t+h/2);}
}
export function dispose(id) {
    const s = states.get(id);
    if (!s) return;
    s.disposed = true;
    finishDrag(s);
    s.observer.disconnect();
    for (const [event, handler] of [['pointermove', s.move], ['pointerdown', s.down], ['pointerup', s.up], ['pointercancel', s.cancel]])
        s.canvas.removeEventListener(event, handler);
    states.delete(id);
}
