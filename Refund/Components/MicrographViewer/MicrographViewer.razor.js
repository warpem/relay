/**
 * JavaScript interop functions for the MicrographViewer component
 */

// Store observer instances by element reference to allow cleanup
let resizeObservers = {};

/**
 * Initializes a ResizeObserver for the micrograph viewer element.
 * The observer will call the provided .NET callback when the element's size changes.
 * 
 * @param {HTMLElement} element The element to observe for resize events
 * @param {DotNet.DotNetObject} dotNetRef The .NET object reference for callbacks
 * @param {number} debounceMs The debounce time in milliseconds
 */
export function observeResize(element, dotNetRef, debounceMs = 100) {
    // Clean up any existing observer for this element
    disposeResizeObserver(element);
    
    const state = { observer: null, timeout: null, disposed: false };
    state.observer = new ResizeObserver(entries => {
        clearTimeout(state.timeout);
        state.timeout = setTimeout(() => {
            if (state.disposed || !element.isConnected) return;
            for (const entry of entries) {
                const width = Math.round(entry.contentRect.width);
                const height = Math.round(entry.contentRect.height);
                dotNetRef.invokeMethodAsync('OnResized', width, height).catch(error => {
                    // A callback may cross disposal while in flight. .NET owns the reference.
                    if (!state.disposed && element.isConnected) console.error(error);
                });
            }
        }, debounceMs);
    });
    resizeObservers[element.id] = state;
    state.observer.observe(element);
}

/**
 * Disposes a ResizeObserver for the given element
 * 
 * @param {HTMLElement} element The element to stop observing
 */
export function disposeResizeObserver(element) {
    if (element && element.id && resizeObservers[element.id]) {
        const state = resizeObservers[element.id];
        state.disposed = true;
        clearTimeout(state.timeout);
        state.observer.disconnect();
        // The .NET component disposes its DotNetObjectReference.
        delete resizeObservers[element.id];
    }
}
// Pixel processing runs in a cancellable worker; only GPU-ready bitmaps reach the UI.
const viewers = new Map();

function releaseImages(state) {
    clearTimeout(state.loadTimer);
    state.worker?.terminate();
    state.worker = null;
    for (const level of state.levels) level.close();
    state.levels = [];
    state.mask?.close();
    state.mask = null;
    state.particles = [];
    state.width = state.height = 0;
    state.thumbnail.onload = state.thumbnail.onerror = null;
    state.thumbnail.removeAttribute('src');
}

function notifyViewport(state) {
    clearTimeout(state.notifyTimer);
    state.notifyTimer = setTimeout(() => {
        if (state.disposed || !state.root.isConnected) return;
        state.callback.invokeMethodAsync('OnViewportChanged', state.zoom, state.viewWidth, state.viewHeight)
            .catch(error => { if (!state.disposed && state.root.isConnected) console.error(error); });
    }, 100);
}

function fit(state) {
    if (!state.width) return;
    state.zoom = Math.min(state.viewWidth / state.width, state.viewHeight / state.height);
    state.tx = (state.viewWidth - state.width * state.zoom) / 2;
    state.ty = (state.viewHeight - state.height * state.zoom) / 2;
    state.fitted = true;
    schedule(state);
    notifyViewport(state);
}
function zoom(state, value) {
    if (!state.width) return;
    const fitZoom = Math.min(state.viewWidth / state.width, state.viewHeight / state.height);
    const next = Math.max(Math.min(.05, fitZoom), Math.min(Math.max(2, fitZoom), value));
    const x = (state.viewWidth / 2 - state.tx) / state.zoom, y = (state.viewHeight / 2 - state.ty) / state.zoom;
    state.zoom = next;
    state.tx = state.viewWidth / 2 - x * next;
    state.ty = state.viewHeight / 2 - y * next;
    state.fitted = false;
    schedule(state);
    notifyViewport(state);
}
function resize(state) {
    const box = state.root.getBoundingClientRect();
    const availableWidth = Math.max(state.options.minWidth, box.width);
    const availableHeight = Math.max(state.options.minHeight, box.height);
    const ratio = state.width ? state.width / state.height : availableWidth / availableHeight;
    const width = Math.round(Math.min(availableWidth, availableHeight * ratio));
    const height = Math.round(Math.min(availableHeight, availableWidth / ratio));
    const x = (state.viewWidth / 2 - state.tx) / state.zoom, y = (state.viewHeight / 2 - state.ty) / state.zoom;
    state.viewWidth = width;
    state.viewHeight = height;
    state.surface.style.width = `${width}px`;
    state.surface.style.height = `${height}px`;
    state.surface.style.margin = '0 auto';
    const dpr = window.devicePixelRatio || 1;
    state.canvas.width = Math.round(width * dpr);
    state.canvas.height = Math.round(height * dpr);
    state.canvas.style.width = `${width}px`;
    state.canvas.style.height = `${height}px`;
    if (state.fitted) fit(state);
    else {
        state.tx = width / 2 - x * state.zoom;
        state.ty = height / 2 - y * state.zoom;
        schedule(state);
        notifyViewport(state);
    }
}
function schedule(state) {
    if (!state.frame && !state.disposed) state.frame = requestAnimationFrame(() => { state.frame = 0; draw(state); });
}
function draw(state) {
    const ctx = state.canvas.getContext('2d');
    const { viewWidth: vw, viewHeight: vh, width, height, zoom: z, options: o } = state;
    ctx.setTransform(state.canvas.width / vw, 0, 0, state.canvas.height / vh, 0, 0);
    ctx.clearRect(0, 0, vw, vh);
    if (!state.levels.length) { state.mini.hidden = true; return; }
    ctx.save();
    ctx.translate(state.tx, state.ty);
    ctx.scale(z, z);
    const ideal = Math.min(state.levels.length - 1, Math.max(0, -Math.log2(z)));
    const lower = Math.floor(ideal), upper = Math.ceil(ideal), blend = ideal - lower;
    // Draw an opaque base before blending, so the background never leaks through.
    ctx.drawImage(state.levels[lower], 0, 0, width, height);
    if (blend > 0) {
        ctx.globalAlpha = blend;
        ctx.drawImage(state.levels[upper], 0, 0, width, height);
        ctx.globalAlpha = 1;
    }
    if (o.showMask && state.mask) ctx.drawImage(state.mask, 0, 0, width, height);
    ctx.strokeStyle = o.particleColor;
    ctx.lineWidth = 2 / z;
    const radius = o.particleDiameter / state.pixelSize / 2, box = o.particleBoxSize / state.pixelSize;
    for (const p of state.particles) {
        if ((o.particleShape & 1) !== 0) { ctx.beginPath(); ctx.arc(p.x, p.y, radius, 0, 2 * Math.PI); ctx.stroke(); }
        if ((o.particleShape & 2) !== 0) ctx.strokeRect(p.x - box / 2, p.y - box / 2, box, box);
    }
    ctx.restore();
    if (o.showScaleBar) {
        const nmPerPixel = state.pixelSize / z / 10;
        const log = Math.log10(80 * nmPerPixel), magnitude = 10 ** Math.floor(log);
        const nice = [1, 2, 3, 5, 8, 10].reduce((a, b) => Math.abs(Math.log10(a) - log % 1) < Math.abs(Math.log10(b) - log % 1) ? a : b);
        const nm = nice * magnitude, length = Math.round(nm / nmPerPixel);
        ctx.fillStyle = 'black'; ctx.strokeStyle = 'white'; ctx.lineWidth = 2;
        ctx.fillRect(vw - length - 10, vh - 30, length, 20);
        ctx.strokeRect(vw - length - 10, vh - 30, length, 20);
        ctx.fillStyle = 'white'; ctx.font = '14px sans-serif'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
        ctx.fillText(`${Number(nm.toPrecision(4))} nm`, vw - length / 2 - 10, vh - 19);
    }
    state.mini.hidden = false;
    const mw = 140 * width / Math.max(width, height), mh = 140 * height / Math.max(width, height);
    state.mini.width = Math.round(mw * 2); state.mini.height = Math.round(mh * 2);
    state.mini.style.width = `${mw}px`; state.mini.style.height = `${mh}px`;
    const mini = state.mini.getContext('2d');
    mini.scale(state.mini.width / width, state.mini.height / height);
    const image = state.thumbnail.complete && state.thumbnail.naturalWidth ? state.thumbnail : state.levels.at(-1);
    mini.drawImage(image, 0, 0, width, height);
    mini.strokeStyle = 'red'; mini.lineWidth = 2 * width / mw;
    mini.strokeRect(-state.tx / z, -state.ty / z, vw / z, vh / z);
}

function create(root, callback) {
    const surface = root.querySelector('[data-micrograph-surface]');
    const canvas = document.createElement('canvas');
    canvas.setAttribute('aria-label', 'Micrograph');
    const mini = document.createElement('canvas');
    mini.className = 'micrograph-minimap';
    mini.title = 'Click to center viewport';
    mini.setAttribute('aria-label', 'Micrograph minimap');
    const status = document.createElement('div');
    status.className = 'micrograph-status';
    status.setAttribute('role', 'status');
    surface.append(canvas, mini, status);
    const state = { root, surface, canvas, mini, status, callback, thumbnail: new Image(), levels: [], particles: [],
        width: 0, height: 0, viewWidth: 1, viewHeight: 1, zoom: 1, tx: 0, ty: 0, fitted: true,
        listeners: new AbortController(), generation: 0 };
    const signal = state.listeners.signal;
    canvas.addEventListener('wheel', event => {
        event.preventDefault();
        const factor = 1 + Math.abs(event.deltaY) / 50;
        zoom(state, state.zoom * (event.deltaY < 0 ? factor : 1 / factor));
    }, { passive: false, signal });
    canvas.addEventListener('pointerdown', event => {
        if (event.button !== 1) return;
        event.preventDefault();
        canvas.setPointerCapture(event.pointerId);
        state.pan = { x: event.clientX, y: event.clientY };
        canvas.style.cursor = 'grabbing';
    }, { signal });
    canvas.addEventListener('pointermove', event => {
        if (!state.pan) return;
        if (!(event.buttons & 4)) { state.pan = null; canvas.style.cursor = ''; return; }
        state.tx += event.clientX - state.pan.x;
        state.ty += event.clientY - state.pan.y;
        state.pan = { x: event.clientX, y: event.clientY };
        state.fitted = false;
        schedule(state);
    }, { signal });
    const stopPan = () => { state.pan = null; canvas.style.cursor = ''; };
    canvas.addEventListener('pointerup', stopPan, { signal });
    canvas.addEventListener('lostpointercapture', stopPan, { signal });
    mini.addEventListener('click', event => {
        const rect = mini.getBoundingClientRect();
        state.tx = state.viewWidth / 2 - (event.clientX - rect.left) / rect.width * state.width * state.zoom;
        state.ty = state.viewHeight / 2 - (event.clientY - rect.top) / rect.height * state.height * state.zoom;
        state.fitted = false;
        schedule(state);
    }, { signal });
    state.observer = new ResizeObserver(() => resize(state));
    viewers.set(root.id, state);
    return state;
}

export function update(root, options, callback) {
    const state = viewers.get(root.id) ?? create(root, callback);
    state.callback = callback;
    const previous = state.options;
    state.options = options;
    const key = JSON.stringify([options.url, options.layer, options.maskUrl, options.particlesUrl, options.thumbnailUrl]);
    if (key !== state.sourceKey) {
        state.sourceKey = key;
        releaseImages(state);
        state.generation++;
        state.root.setAttribute('aria-busy', String(!!options.url));
        state.status.textContent = options.url ? 'Loading micrograph…' : '';
        state.fitted = true;
        if (options.url) {
            const generation = state.generation;
            // Browsing quickly cancels both pending work and any in-progress fetch/processing.
            state.loadTimer = setTimeout(() => {
                const worker = state.worker = new Worker(new URL('../../js/micrograph-worker.js', import.meta.url), { type: 'module' });
                worker.onmessage = ({ data }) => {
                    if (state.disposed || generation !== state.generation) {
                        data.levels?.forEach(level => level.close()); data.mask?.close(); return;
                    }
                    if (data.type === 'image') {
                        state.levels = data.levels;
                        state.width = data.width; state.height = data.height; state.pixelSize = data.pixelSize;
                        state.status.textContent = '';
                        state.root.setAttribute('aria-busy', 'false');
                        resize(state);
                    } else if (data.type === 'mask') state.mask = data.mask;
                    else if (data.type === 'particles') state.particles = data.particles;
                    else {
                        state.status.textContent = data.message;
                        state.root.setAttribute('aria-busy', 'false');
                    }
                    schedule(state);
                };
                worker.onerror = event => {
                    if (state.disposed || generation !== state.generation) return;
                    state.status.textContent = `Unable to load micrograph: ${event.message}`;
                    state.root.setAttribute('aria-busy', 'false');
                };
                worker.postMessage(options);
                if (options.thumbnailUrl) {
                    state.thumbnail.onload = () => schedule(state);
                    state.thumbnail.onerror = () => schedule(state);
                    state.thumbnail.src = options.thumbnailUrl;
                }
            }, 50);
        }
    }
    if (!previous) state.observer.observe(root);
    if (!previous || previous.minWidth !== options.minWidth || previous.minHeight !== options.minHeight) resize(state);
    schedule(state);
}

export function command(id, name, value) {
    const state = viewers.get(id);
    if (!state) return;
    if (name === 'fit') fit(state);
    if (name === 'zoom') zoom(state, value);
}

export function dispose(id) {
    const state = viewers.get(id);
    if (!state) return;
    state.disposed = true;
    state.generation++;
    releaseImages(state);
    clearTimeout(state.notifyTimer);
    cancelAnimationFrame(state.frame);
    state.observer.disconnect();
    state.listeners.abort();
    state.surface.replaceChildren();
    viewers.delete(id);
}
