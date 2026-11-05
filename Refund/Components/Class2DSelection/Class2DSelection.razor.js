let worker = null;
let nextId = 0;
const roots = new Map();
const requests = new Map();

function status(state, text) {
    const element = state.root.querySelector('[data-image-status]');
    if (element) element.textContent = text;
}

function apply(state) {
    for (const image of state.root.querySelectorAll('img[data-image-index]')) {
        const url = state.urls.get(Number(image.dataset.imageIndex));
        if (url && image.getAttribute('src') !== url) image.src = url;
    }
}

function ensureWorker() {
    if (worker) return;
    worker = new Worker(new URL('../../js/class2d-worker.js', import.meta.url), { type: 'module' });
    worker.onmessage = ({ data }) => {
        const state = requests.get(data.id);
        if (!state) return;
        if (data.type === 'progress') {
            status(state, `Loading class images: ${Math.round(100 * data.received / data.total)}%`);
        } else if (data.type === 'image') {
            const old = state.urls.get(data.index);
            if (old) URL.revokeObjectURL(old);
            state.urls.set(data.index, URL.createObjectURL(data.blob));
            apply(state);
        } else if (data.type === 'done') {
            status(state, '');
        } else if (data.type === 'error') {
            status(state, `Unable to load class images: ${data.message}`);
        }
    };
    worker.onerror = event => {
        for (const state of requests.values()) status(state, `Unable to load class images: ${event.message}`);
    };
}

function release(state) {
    if (!state) return;
    requests.delete(state.id);
    worker?.postMessage({ type: 'release', id: state.id });
    for (const url of state.urls.values()) URL.revokeObjectURL(url);
    state.urls.clear();
    // Termination also cancels outstanding fetches and releases all worker buffers.
    if (requests.size === 0 && worker) {
        worker.terminate();
        worker = null;
    }
}

export function update(root, url, signed = false) {
    const indices = [...new Set([...root.querySelectorAll('img[data-image-index]')]
        .map(image => Number(image.dataset.imageIndex)))];
    const key = JSON.stringify([url, signed, [...indices].sort((a, b) => a - b)]);
    const previous = roots.get(root.id);
    if (previous?.key === key) {
        apply(previous); // Sorting only moves keyed tiles; it never re-fetches the stack.
        return;
    }
    release(previous);
    for (const image of root.querySelectorAll('img[data-image-index]')) image.removeAttribute('src');
    const state = { root, id: ++nextId, key, urls: new Map() };
    roots.set(root.id, state);
    if (!url || indices.length === 0) {
        status(state, '');
        return;
    }
    ensureWorker();
    requests.set(state.id, state);
    status(state, 'Loading class images…');
    worker.postMessage({ type: 'render', id: state.id, url, signed, indices });
}

export function dispose(rootId) {
    release(roots.get(rootId));
    roots.delete(rootId);
}
