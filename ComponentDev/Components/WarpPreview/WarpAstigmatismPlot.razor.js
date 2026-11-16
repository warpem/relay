const states = new Map();
export function initialize(canvas, dotnet, id) {
    if (!canvas) return;
    dispose(id);
    const s = {canvas,dotnet,points:[],radius:.1,selected:-1,hover:-1,base:document.createElement('canvas'),size:0,dpr:1,ready:false,disposed:false};
    states.set(id,s);
    s.resize = new ResizeObserver(() => drawBase(s)); s.resize.observe(canvas);
    s.move = e => { if(!s.ready || s.disposed)return; const r=canvas.getBoundingClientRect(), x=e.clientX-r.left,y=e.clientY-r.top; let closest=-1,best=64;
        for(let i=0;i<s.points.length;i++){const p=s.points[i];if(Math.hypot(p[1],p[2])>s.radius)continue;const d=(s.cx+p[1]*s.scale-x)**2+(s.cy+p[2]*s.scale-y)**2;if(d<best){best=d;closest=i;}}
        if(s.hover!==closest){s.hover=closest;const p=s.points[closest];canvas.title=p?`#${(p[0]+1).toLocaleString()} · ${p[5].toFixed(3)} µm · ${p[4].toFixed(1)}°`: 'Astigmatism · 0° and 180° are equivalent';paint(s);}
    };
    s.leave=()=>{s.hover=-1;paint(s);};
    s.click=()=>{const p=s.points[s.hover];if(!s.disposed && s.ready && p)s.dotnet.invokeMethodAsync('SelectMovie',p[0]).catch(()=>{});};
    canvas.addEventListener('pointermove',s.move);canvas.addEventListener('pointerleave',s.leave);canvas.addEventListener('click',s.click);
}
export function setData(id, points, radius, selected){const s=states.get(id);if(!s)return;s.points=points;s.hover=-1;s.radius=radius;s.selected=selected;drawBase(s);}
export function setView(id,radius,selected){const s=states.get(id);if(!s)return;const changed=s.radius!==radius;s.radius=radius;s.selected=selected;changed?drawBase(s):paint(s);}
function drawBase(s){
    if(s.disposed) return;
    const size=s.canvas.getBoundingClientRect().width;
    if(!Number.isFinite(size)||size<60){s.ready=false;return;}
    s.ready=true;
    const dpr=Math.min(2,window.devicePixelRatio||1);s.size=size;s.dpr=dpr;
    for(const c of [s.canvas,s.base]){c.width=Math.round(size*dpr);c.height=Math.round(size*dpr);}
    const ctx=s.base.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,size,size);
    const style=getComputedStyle(s.canvas),fg=style.getPropertyValue('--colorNeutralForeground3').trim()||'#666',line=style.getPropertyValue('--colorNeutralStroke2').trim()||'#ddd';
    s.cx=s.cy=size/2;const r=size/2-24;s.scale=r/s.radius;ctx.strokeStyle=line;ctx.lineWidth=1;
    for(const f of [.5,1]){ctx.beginPath();ctx.arc(s.cx,s.cy,r*f,0,Math.PI*2);ctx.stroke();}
    ctx.globalAlpha=.5;ctx.beginPath();ctx.moveTo(s.cx-r,s.cy);ctx.lineTo(s.cx+r,s.cy);ctx.moveTo(s.cx,s.cy-r);ctx.lineTo(s.cx,s.cy+r);ctx.stroke();ctx.globalAlpha=1;
    ctx.fillStyle=fg;ctx.font='11px system-ui';ctx.textAlign='center';ctx.textBaseline='middle';ctx.fillText('135°',s.cx,11);ctx.fillText('45°',s.cx,size-10);ctx.textAlign='left';ctx.fillText('0°',size-21,s.cy);ctx.textAlign='right';ctx.fillText('90°',22,s.cy);ctx.fillText(`${s.radius.toFixed(2)} µm`,size-2,size-10);
    ctx.save();ctx.beginPath();ctx.arc(s.cx,s.cy,r,0,Math.PI*2);ctx.clip();
    const groups=new Map();for(const p of s.points){let path=groups.get(p[3]);if(!path){path=new Path2D();groups.set(p[3],path);}const x=s.cx+p[1]*s.scale,y=s.cy+p[2]*s.scale;path.moveTo(x+1.7,y);path.arc(x,y,1.7,0,Math.PI*2);}
    ctx.globalAlpha=.52;for(const [color,path] of groups){ctx.fillStyle=color;ctx.fill(path);}ctx.restore();paint(s);
}
function paint(s){if(!s.ready||s.disposed)return;const ctx=s.canvas.getContext('2d');ctx.setTransform(1,0,0,1,0,0);ctx.clearRect(0,0,s.canvas.width,s.canvas.height);ctx.drawImage(s.base,0,0);ctx.setTransform(s.dpr,0,0,s.dpr,0,0);
    for(const [index,color] of [[s.points.findIndex(p=>p[0]===s.selected),'#0078d4'],[s.hover,'#333']]){const p=s.points[index];if(!p||Math.hypot(p[1],p[2])>s.radius)continue;const x=s.cx+p[1]*s.scale,y=s.cy+p[2]*s.scale;ctx.beginPath();ctx.arc(x,y,5,0,Math.PI*2);ctx.strokeStyle='#fff';ctx.lineWidth=4;ctx.stroke();ctx.strokeStyle=color;ctx.lineWidth=2;ctx.stroke();}
}
export function dispose(id){const s=states.get(id);if(!s)return;s.disposed=true;s.ready=false;const canvas=s.canvas;s.resize.disconnect();canvas.removeEventListener('pointermove',s.move);canvas.removeEventListener('pointerleave',s.leave);canvas.removeEventListener('click',s.click);states.delete(id);}
