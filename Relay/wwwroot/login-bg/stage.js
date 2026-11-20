// Login background stage: a thin shim around p5.js (instance mode) that runs one background sketch
// behind the login card. It provides what the sketches need and p5 doesn't:
//   * a simulation clock decoupled from frames (reduced motion slows it down)
//   * light/dark theme following the system preference, with an eased crossfade (h.mix)
//   * the login card rectangle (h.focus) so sketches can stay calm behind it
//   * shared analog overlays (paper, film grain, vignette) as cheap CSS layers
//
// Sketch module contract (<id>/sketch.js):
//   export const meta = { title, renderer: 'p2d'|'webgl', fps?, pixelDensity?, maxStep?,
//                         post?: { paper, grain, vignette } };
//   export default function sketch(p, h) {
//     return { setup() {}, step(dt) {}, render() {}, resize?() {}, theme?(name) {} };
//   }
// Sketches advance the simulation only in step(dt) and draw only in render().

const lerp = (a, b, t) => a + (b - a) * t;
const smooth = t => t * t * (3 - 2 * t);

function hexToRgb(hex) {
    let s = hex.replace('#', '');
    if (s.length === 3) s = s.split('').map(c => c + c).join('');
    const n = parseInt(s, 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function hash(str) {
    let h = 2166136261;
    for (let i = 0; i < str.length; i++) h = Math.imul(h ^ str.charCodeAt(i), 16777619);
    return (h >>> 0) / 4294967296;
}

// ---------- analog overlays (CSS only, nearly free at runtime) ----------
function makeNoiseTile(size, fn) {
    const c = document.createElement('canvas');
    c.width = c.height = size;
    const x = c.getContext('2d');
    const img = x.createImageData(size, size);
    for (let i = 0; i < size * size; i++) {
        const v = fn(i % size, (i / size) | 0);
        img.data[i * 4] = img.data[i * 4 + 1] = img.data[i * 4 + 2] = v;
        img.data[i * 4 + 3] = 255;
    }
    x.putImageData(img, 0, 0);
    return c.toDataURL();
}

function buildOverlays(root, post, reduced) {
    const grainUrl = makeNoiseTile(192, () => 128 + (Math.random() - 0.5) * 255);
    // Paper: seamless multi-octave value noise plus fibres and speckle, centred on mid-grey for soft-light.
    const S = 768, quint = t => t * t * t * (t * (t * 6 - 15) + 10);
    const octaves = [[3, 0.42], [7, 0.28], [17, 0.18], [41, 0.12]].map(([P, w]) => ({ P, w, lat: new Float32Array(P * P).map(() => Math.random()) }));
    const val = (x, y) => {
        let v = 0;
        for (const { P, w, lat } of octaves) {
            const gx = (x / S) * P, gy = (y / S) * P, ix = gx | 0, iy = gy | 0, fx = quint(gx - ix), fy = quint(gy - iy);
            const g = (i, j) => lat[((iy + j) % P) * P + ((ix + i) % P)];
            v += w * lerp(lerp(g(0, 0), g(1, 0), fx), lerp(g(0, 1), g(1, 1), fx), fy);
        }
        return v;
    };
    const fib = new Float32Array(S * S);
    for (let k = 0; k < 3600; k++) {
        let x = Math.random() * S, y = Math.random() * S, a = Math.random() * 6.283;
        const len = 8 + Math.random() * 30, d = (Math.random() - 0.5) * 0.25;
        for (let s = 0; s < len; s++) { fib[((((y | 0) % S) + S) % S) * S + ((((x | 0) % S) + S) % S)] += d; x += Math.cos(a); y += Math.sin(a); a += (Math.random() - 0.5) * 0.3; }
    }
    const paperUrl = makeNoiseTile(S, (x, y) => 128 + (val(x, y) - 0.5) * 70 + fib[y * S + x] * 255 + (Math.random() - 0.5) * 14);

    const nodes = [];
    const layer = (css) => {
        const d = document.createElement('div');
        d.style.cssText = 'position:absolute;inset:0;pointer-events:none;' + css;
        root.appendChild(d);
        nodes.push(d);
        return d;
    };
    const animName = 'relay-login-grain';
    const paper = layer(`background:url(${paperUrl});background-size:${S}px;mix-blend-mode:soft-light;`);
    const grain = layer(`inset:-200px;background:url(${grainUrl});background-size:192px;mix-blend-mode:overlay;` +
        (reduced ? '' : `animation:${animName} 0.6s steps(1) infinite;`));
    const vigL = layer('background:radial-gradient(ellipse at 50% 45%, transparent 45%, rgba(110,70,40,0.55) 125%);mix-blend-mode:multiply;');
    const vigD = layer('background:radial-gradient(ellipse at 50% 45%, transparent 35%, rgba(5,2,0,0.85) 120%);');
    const style = document.createElement('style');
    style.textContent = `@keyframes ${animName}{` + Array.from({ length: 8 }, (_, i) =>
        `${(i * 12.5).toFixed(1)}%{transform:translate(${(Math.random() * 190 - 95) | 0}px,${(Math.random() * 190 - 95) | 0}px)}`).join('') + '}';
    document.head.appendChild(style);
    nodes.push(style);
    return {
        apply(mix) {
            paper.style.opacity = (post.paper ?? 0.5) * lerp(0.9, 0.6, mix);
            grain.style.opacity = (post.grain ?? 0.35) * lerp(0.22, 0.32, mix);
            vigL.style.opacity = (post.vignette ?? 0.4) * (1 - mix);
            vigD.style.opacity = (post.vignette ?? 0.4) * mix;
        },
        set(next) { Object.assign(post, next); },
        remove() { for (const n of nodes) n.remove(); },
    };
}

/**
 * Mounts a background sketch into `container`, keeping it calm behind `focusEl`.
 * Returns a handle with dispose(). Rejects if the sketch fails during setup.
 */
export async function mount({ container, module, focusEl }) {
    const meta = { renderer: 'p2d', maxStep: 1 / 30, post: {}, ...module.meta };
    const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
    const darkQuery = matchMedia('(prefers-color-scheme: dark)');
    const seed = (Math.random() * 1e9) | 0;
    let theme = darkQuery.matches ? 'dark' : 'light';

    const h = {
        seed, theme, mix: theme === 'dark' ? 1 : 0, time: 0, dt: 0, speed: reduced ? 0.35 : 1,
        reducedMotion: reduced, preview: false, warping: false, meta,
        focus: { x: 0, y: 0, w: 0, h: 0, cx: 0, cy: 0 },
        /** Interpolates a light-theme and a dark-theme value by the eased crossfade h.mix.
         *  numbers → number; '#hex' or [r,g,b(,a)] → [r,g,b(,a)]. */
        byTheme(a, b) {
            const t = smooth(h.mix);
            if (typeof a === 'number') return lerp(a, b, t);
            const A = typeof a === 'string' ? hexToRgb(a) : a, B = typeof b === 'string' ? hexToRgb(b) : b;
            return A.map((v, i) => lerp(v, B[i] ?? v, t));
        },
        /** 1 inside the login card, easing to 0 at `pad` px outside (rounded-rect distance). */
        focusFalloff(x, y, pad = 120) {
            const f = h.focus, dx = Math.max(Math.abs(x - f.cx) - f.w / 2, 0), dy = Math.max(Math.abs(y - f.cy) - f.h / 2, 0);
            const d = Math.hypot(dx, dy);
            return d >= pad ? 0 : 1 - smooth(d / pad);
        },
        /** Slow, never-repeating value in [-1,1]: fractal noise of sim time. period ≈ seconds per swing. */
        drift(key, period = 60) {
            const o = hash(key + ':' + seed) * 1000, t = h.time / period;
            return (P.noise(t + o, o) * 0.65 + P.noise(t * 2.13 + o, o + 7) * 0.35) * 2 - 1;
        },
        /** Poisson scheduler on sim time: fn fires on average every `mean` seconds. */
        every(mean, fn) { timers.push({ mean, fn, next: -Math.log(1 - P.random()) * mean }); },
        setPost(next) { overlays.set(next); overlays.apply(h.mix); },
    };
    const timers = [];
    let p, P, impl, overlays, mixTarget = h.mix, failed = false, disposed = false;

    const measureFocus = () => {
        const cr = container.getBoundingClientRect();
        const r = focusEl && focusEl.isConnected && focusEl.offsetParent !== null ? focusEl.getBoundingClientRect() : null;
        Object.assign(h.focus, r ? { x: r.left - cr.left, y: r.top - cr.top, w: r.width, h: r.height }
            : { x: cr.width / 2 - 200, y: cr.height / 2 - 210, w: 400, h: 420 });
        h.focus.cx = h.focus.x + h.focus.w / 2;
        h.focus.cy = h.focus.y + h.focus.h / 2;
    };
    const advance = (sim, budgetMs, maxSteps = 400) => {
        const t0 = performance.now();
        let left = sim, steps = 0;
        while (left > 1e-7 && performance.now() - t0 < budgetMs && steps < maxSteps) {
            const d = Math.min(left, meta.maxStep);
            h.dt = d; h.time += d; left -= d; steps++;
            for (const tm of timers) { tm.next -= d; if (tm.next <= 0) { tm.next = -Math.log(1 - P.random()) * tm.mean; tm.fn(); } }
            impl.step(d);
        }
    };
    const setTheme = (name) => {
        theme = h.theme = name;
        mixTarget = name === 'dark' ? 1 : 0;
        impl?.theme?.(name);
    };
    const onScheme = (e) => setTheme(e.matches ? 'dark' : 'light');
    const onVisibility = () => { if (!failed) (document.hidden ? p.noLoop() : p.loop()); };
    const fail = (e) => {
        failed = true;
        p?.noLoop();
        console.warn('Login background stopped after an error:', e);
    };

    // The card changes height when switching between sign-in and registration, and the window can
    // resize: keep the canvas size and the focus rectangle current. Coalesce bursts into one frame.
    let resizeQueued = false;
    const onLayout = () => {
        if (resizeQueued) return;
        resizeQueued = true;
        requestAnimationFrame(() => {
            resizeQueued = false;
            if (!impl || failed || disposed) return;
            try {
                if (p.width !== container.clientWidth || p.height !== container.clientHeight)
                    p.resizeCanvas(container.clientWidth, container.clientHeight);
                measureFocus();
                impl.resize?.();
            } catch (e) { fail(e); }
        });
    };
    const observer = new ResizeObserver(onLayout);

    await new Promise((resolve, reject) => {
        p = new window.p5((pp) => {
            P = pp;
            pp.setup = async () => {
                try {
                    pp.createCanvas(container.clientWidth, container.clientHeight, meta.renderer === 'webgl' ? pp.WEBGL : pp.P2D);
                    pp.canvas.style.position = 'absolute';
                    pp.canvas.style.inset = '0';
                    pp.canvas.style.display = 'block';
                    pp.pixelDensity(Math.min(window.devicePixelRatio || 1, meta.pixelDensity ?? 1.5));
                    if (meta.fps) pp.frameRate(meta.fps);
                    pp.randomSeed(seed);
                    pp.noiseSeed(seed);
                    measureFocus();
                    setTheme(theme);
                    impl = module.default(pp, h);
                    await impl.setup?.();
                    overlays = buildOverlays(container, { ...meta.post }, reduced);
                    overlays.apply(h.mix);
                    resolve();
                } catch (e) { failed = true; reject(e); }
            };
            let last = performance.now();
            pp.draw = () => {
                if (!impl || failed) return;
                const now = performance.now(), elapsed = Math.min((now - last) / 1000, 0.1);
                last = now;
                try {
                    if (h.mix !== mixTarget) {
                        h.mix += Math.sign(mixTarget - h.mix) * Math.min(Math.abs(mixTarget - h.mix), elapsed / 1.4);
                        overlays.apply(h.mix);
                    }
                    advance(elapsed * h.speed, 14);
                    pp.push();
                    impl.render();
                    pp.pop();
                } catch (e) { fail(e); }
            };
        }, container);
    }).catch((e) => {
        p?.remove();
        throw e;
    });

    observer.observe(container);
    if (focusEl) observer.observe(focusEl);
    darkQuery.addEventListener('change', onScheme);
    document.addEventListener('visibilitychange', onVisibility);

    return {
        h,
        dispose() {
            if (disposed) return;
            disposed = true;
            observer.disconnect();
            darkQuery.removeEventListener('change', onScheme);
            document.removeEventListener('visibilitychange', onVisibility);
            overlays?.remove();
            p.remove();
        },
    };
}
