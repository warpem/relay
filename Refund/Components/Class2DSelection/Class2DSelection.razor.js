let worker = null;
let nextId = 0;
const roots = new Map();
const requests = new Map();

function status(state, text) { state.status.textContent = text; }
function ensureWorker() {
    if (worker) return;
    worker = new Worker(new URL('../../js/class2d-worker.js', import.meta.url), { type: 'module' });
    worker.onmessage = ({ data }) => {
        const state = requests.get(data.id);
        if (!state || data.generation !== state.generation) return;
        if (data.type === 'progress') {
            status(state, data.total > 0 ? `Loading class images: ${Math.round(100 * data.received / data.total)}%` : 'Loading class images…');
        } else if (data.type === 'image') {
            const old = state.urls.get(data.index);
            const url = URL.createObjectURL(data.blob);
            state.urls.set(data.index, url);
            const image = state.tiles.get(data.index + 1)?.querySelector('img');
            if (image) image.src = url;
            if (state.expandedParent != null) {
                const item = state.options.items.find(i => i.id === state.expandedParent);
                if (state.parentPreview && item?.number === data.index + 1) state.parentPreview.src = url;
            }
            if (old) URL.revokeObjectURL(old);
        } else if (data.type === 'done') status(state, '');
        else if (data.type === 'error') status(state, `Unable to load class images: ${data.message}`);
    };
    worker.onerror = event => {
        for (const state of requests.values()) status(state, `Unable to load class images: ${event.message}`);
    };
}

function releaseImages(state) {
    requests.delete(state.id);
    worker?.postMessage({ type: 'release', id: state.id });
    for (const url of state.urls.values()) URL.revokeObjectURL(url);
    state.urls.clear();
    if (requests.size === 0 && worker) { worker.terminate(); worker = null; }
}

function renderImages(state) {
    clearTimeout(state.timer);
    const { url, signed, items } = state.options;
    if (!url || items.length === 0) { status(state, ''); return; }
    if (state.sentGeneration === state.generation) return;
    state.sentGeneration = state.generation;
    refreshPanel(state);
    ensureWorker();
    requests.set(state.id, state);
    status(state, state.urls.size ? 'Updating contrast…' : 'Loading class images…');
    worker.postMessage({ type: 'render', id: state.id, generation: state.generation, url, signed,
        indices: items.map(item => item.number - 1), low: state.low, high: state.high });
}

function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
}

function sortTiles(state) {
    const field = state.sort.value;
    const items = [...state.options.items].sort((a, b) => {
        const av = a[field], bv = b[field];
        if (av == null || bv == null) return (av == null) - (bv == null) || a.number - b.number;
        return (state.ascending ? 1 : -1) * (av - bv) || a.number - b.number;
    });
    for (const item of items) state.grid.append(state.cards.get(item.number));
    placePanel(state);
}

function applySelection(state) {
    for (const [number, tile] of state.tiles) {
        const selected = state.selected.has(number);
        tile.classList.toggle('selected', selected);
        tile.disabled = !state.options.mutable;
        tile.setAttribute('aria-pressed', String(selected));
        const item = state.options.items.find(i => i.number === number);
        const group = state.options.subclasses?.find(g => g.parentId === item.id);
        const expand = state.cards.get(number).querySelector('.class2d-expand');
        if (group && expand) {
            const count = group.items.filter(i => state.selectedSubclasses.has(i.id)).length;
            expand.textContent = group.items.length ? `${state.expandedParent === group.parentId ? '▾' : '▸'} ${group.items.length}${count ? ` · ${count}✓` : ''}` : 'ⓘ';
        }
    }
    state.selectionTools.hidden = !state.options.mutable;
    state.selectionCount.textContent = `${state.selected.size} selected`;
    state.all.disabled = state.selected.size === state.options.items.length;
    state.clear.disabled = state.selected.size === 0;
    state.subclassTools.hidden = !(state.options.subclasses?.length && state.options.mutable);
    state.subclassCount.textContent = `${state.selectedSubclasses.size} subclasses selected`;
    state.clearSubclasses.disabled = state.selectedSubclasses.size === 0;
    state.allSubclasses.disabled = state.selectedSubclasses.size === (state.options.subclasses ?? []).reduce((n, g) => n + g.items.length, 0);
}

function select(state, number) {
    if (!state.options.mutable) return;
    if (!state.selected.delete(number)) state.selected.add(number);
    notifySelection(state);
}

function notifySelection(state, subclasses = false) {
    applySelection(state);
    const selected = [...(subclasses ? state.selectedSubclasses : state.selected)].sort((a, b) => a - b);
    const version = state.selectionVersion;
    // Serialize callbacks so rapid clicks cannot deliver an older selection last.
    state.notifications = state.notifications.then(() => {
        if (!state.disposed && version === state.selectionVersion)
            return state.callback?.invokeMethodAsync(subclasses ? 'ReceiveSubclassSelection' : 'ReceiveSelection', selected, version);
    }).catch(() => { if (!state.disposed) status(state, 'Unable to update class selection.'); });
}

function closePanel(state) {
    if (state.childRoot) dispose(state.childRoot.id);
    state.childRoot = null;
    state.parentPreview = null;
    state.panel.remove();
    state.expandedParent = null;
    for (const card of state.cards.values()) {
        card.classList.remove('expanded');
        card.querySelector('.class2d-expand')?.setAttribute('aria-expanded', 'false');
    }
    applySelection(state);
}

function placePanel(state) {
    if (state.expandedParent == null) return;
    const item = state.options.items.find(i => i.id === state.expandedParent);
    const card = state.cards.get(item?.number);
    state.panel.remove();
    if (!card) return;
    // Keep the entire parent row together, then insert one full-width comparison.
    const rowTop = card.offsetTop;
    const row = [...state.grid.children].filter(c => c.offsetTop === rowTop);
    row.at(-1)?.after(state.panel);
}

function refreshPanel(state) {
    if (state.expandedParent == null) return;
    const group = (state.options.subclasses ?? []).find(g => g.parentId === state.expandedParent);
    if (!group) { closePanel(state); return; }
    if (!state.childRoot) return;
    const selected = group.items.filter(i => state.selectedSubclasses.has(i.id)).map(i => i.number);
    update(state.childRoot, { ...state.options, url: group.url, signed: false, items: group.items,
        subclasses: [], allowSorting: false, compact: true, contrast: [state.low, state.high],
        selectionVersion: state.subclassVersion }, {
        invokeMethodAsync(method, numbers, version) {
            if (state.disposed || version !== state.subclassVersion) return;
            for (const item of group.items) state.selectedSubclasses.delete(item.id);
            for (const number of numbers) {
                const item = group.items.find(i => i.number === number);
                if (item) state.selectedSubclasses.add(item.id);
            }
            notifySelection(state, true);
        }
    }, selected);
}

function openPanel(state, parentId) {
    const wasOpen = state.expandedParent === parentId;
    closePanel(state);
    if (wasOpen) return;
    const group = state.options.subclasses.find(g => g.parentId === parentId);
    if (!group) return;
    state.expandedParent = parentId;
    const item = state.options.items.find(i => i.id === parentId);
    const card = state.cards.get(item.number);
    card.classList.add('expanded');
    card.querySelector('.class2d-expand').setAttribute('aria-expanded', 'true');
    const heading = element('div', 'class2d-panel-heading');
    heading.append(element('span', null, `Parent #${parentId} · subclasses`));
    const close = element('button', null, 'Close'); close.type = 'button'; close.onclick = () => closePanel(state);
    heading.append(close);
    state.panel.replaceChildren(heading);
    if (group.items.length) {
        const body = element('div', 'class2d-panel-body');
        const parent = element('figure', 'class2d-parent-preview');
        state.parentPreview = element('img'); state.parentPreview.alt = `Parent class ${parentId}`;
        const url = state.urls.get(item.number - 1); if (url) state.parentPreview.src = url;
        parent.append(state.parentPreview, element('figcaption', null, `Parent #${parentId}`));
        state.childRoot = element('div', 'class2d-children'); state.childRoot.id = `${state.root.id}-subclasses`;
        body.append(parent, state.childRoot); state.panel.append(body);
        state.panel.append(element('div', 'class2d-panel-note', 'Percentages are relative to this parent.'));
    } else state.panel.append(element('p', null, group.reason ?? 'No subclasses available.'));
    applySelection(state); placePanel(state); refreshPanel(state);
}

function showRange(state) {
    state.black.value = state.low;
    state.white.value = state.high;
    state.readout.textContent = `${state.low.toFixed(2).replace(/\.?0+$/, '')}–${state.high.toFixed(2).replace(/\.?0+$/, '')}%`;
    state.range.style.setProperty('--low', `${state.low}%`);
    state.range.style.setProperty('--high', `${state.high}%`);
    state.black.setAttribute('aria-valuemax', String(state.high - 0.05));
    state.white.setAttribute('aria-valuemin', String(state.low + 0.05));
}

function defaultRange(state) {
    // Signed images use percentiles of the symmetric distribution of |pixels|.
    // These defaults reproduce ±99.5th-percentile magnitude and keep zero mid-gray.
    state.low = state.options.signed ? 0.25 : 0.5;
    state.high = state.options.signed ? 99.75 : 100;
    showRange(state);
}

function create(root) {
    if (!document.querySelector('link[data-class2d-style]')) {
        const style = element('link'); style.rel = 'stylesheet';
        style.href = new URL('../../css/class2d.css', import.meta.url);
        style.dataset.class2dStyle = '';
        document.head.append(style);
    }
    root.classList.add('relay-class2d');
    const toolbar = element('div', 'class2d-toolbar');
    const sorting = element('div', 'class2d-sorting');
    const sort = element('select'); sort.setAttribute('aria-label', 'Sort classes by');
    const direction = element('button', 'class2d-direction', '↑'); direction.type = 'button';
    direction.title = 'Reverse sort direction'; direction.setAttribute('aria-label', 'Reverse sort direction');
    sorting.append(sort, direction);
    const contrast = element('div', 'class2d-contrast');
    const caption = element('span', null, 'Contrast');
    const range = element('div', 'class2d-range');
    range.title = 'Black and white percentiles per image';
    const inputs = ['Black percentile', 'White percentile'].map(label => {
        const input = element('input'); input.type = 'range'; input.min = 0; input.max = 100; input.step = 0.05;
        input.setAttribute('aria-label', label); range.append(input); return input;
    });
    const readout = element('output');
    const reset = element('button', 'class2d-reset', 'Reset'); reset.type = 'button'; reset.title = 'Reset contrast';
    contrast.append(caption, range, readout, reset);
    const selectionTools = element('div', 'class2d-selection');
    const all = element('button', null, 'Select all'), clear = element('button', null, 'Clear');
    all.type = clear.type = 'button';
    const selectionCount = element('span');
    selectionTools.append(all, clear, selectionCount);
    toolbar.append(sorting, contrast, selectionTools);
    const message = element('div', 'class2d-status'); message.dataset.imageStatus = '';
    message.setAttribute('role', 'status'); message.setAttribute('aria-live', 'polite');
    const subclassTools = element('div', 'class2d-subclass-tools');
    const allSubclasses = element('button', null, 'Select all subclasses'), clearSubclasses = element('button', null, 'Clear subclasses');
    allSubclasses.type = clearSubclasses.type = 'button';
    const subclassCount = element('span');
    subclassTools.append(allSubclasses, clearSubclasses, subclassCount);
    const grid = element('div', 'class2d-grid');
    const panel = element('div', 'class2d-panel');
    root.replaceChildren(toolbar, subclassTools, message, grid);
    const state = { root, id: ++nextId, urls: new Map(), tiles: new Map(), cards: new Map(), selectedSubclasses: new Set(), subclassVersion: 0, generation: 0, ascending: true,
        sort, sorting, direction, toolbar, panel, subclassTools, allSubclasses, clearSubclasses, subclassCount, selectionTools, all, clear, selectionCount, range, black: inputs[0], white: inputs[1], readout, grid, status: message,
        notifications: Promise.resolve(), disposed: false };
    allSubclasses.onclick = () => {
        state.selectedSubclasses = new Set(state.options.subclasses.flatMap(g => g.items.map(i => i.id)));
        state.subclassVersion++; refreshPanel(state); notifySelection(state, true);
    };
    clearSubclasses.onclick = () => {
        state.selectedSubclasses.clear(); state.subclassVersion++; refreshPanel(state); notifySelection(state, true);
    };
    all.onclick = () => { state.selected = new Set(state.options.items.map(i => i.number)); notifySelection(state); };
    clear.onclick = () => { state.selected.clear(); notifySelection(state); };
    state.observer = new ResizeObserver(([entry]) => {
        if (entry.contentRect.width !== state.width) { state.width = entry.contentRect.width; placePanel(state); }
    });
    state.observer.observe(root);
    sort.onchange = () => sortTiles(state);
    direction.onclick = () => { state.ascending = !state.ascending; direction.textContent = state.ascending ? '↑' : '↓'; sortTiles(state); };
    for (const input of inputs) {
        input.onpointerdown = () => { for (const other of inputs) other.style.zIndex = other === input ? 2 : 1; };
        input.oninput = () => {
            if (input === state.black) state.low = Math.min(Number(input.value), state.high - 0.05);
            else state.high = Math.max(Number(input.value), state.low + 0.05);
            showRange(state);
            state.generation++; // Immediately ignore previews from an older contrast request.
            status(state, 'Updating contrast…');
            clearTimeout(state.timer);
            state.timer = setTimeout(() => renderImages(state), 60);
        };
        input.onchange = () => renderImages(state);
    }
    reset.onclick = () => { defaultRange(state); state.generation++; renderImages(state); };
    return state;
}

export function update(root, options, callback, selected) {
    let state = roots.get(root.id);
    if (!state) { state = create(root); roots.set(root.id, state); }
    const old = state.options;
    state.options = options;
    state.callback = callback;
    const previousSubclasses = state.selectedSubclasses;
    if (old?.subclasses?.length) {
        // Combined export IDs may shift as earlier parents finish. Preserve the
        // stable parent/local-image identity while accepting the new export IDs.
        state.selectedSubclasses = new Set(old.subclasses.flatMap(group => {
            const current = options.subclasses?.find(g => g.parentId === group.parentId);
            return group.items.filter(i => previousSubclasses.has(i.id))
                .map(i => current?.items.find(c => c.number === i.number)?.id).filter(id => id != null);
        }));
    }
    const subclassesChanged = state.selectedSubclasses.size !== previousSubclasses.size ||
        [...state.selectedSubclasses].some(id => !previousSubclasses.has(id));
    state.toolbar.hidden = !!options.compact;
    if (!old || state.selectionVersion !== options.selectionVersion) {
        state.selectionVersion = options.selectionVersion;
        state.selected = new Set(selected);
    }
    const oldLow = state.low, oldHigh = state.high;
    if (!old || old.signed !== options.signed) defaultRange(state);
    if (options.contrast) { [state.low, state.high] = options.contrast; showRange(state); }
    const contrastChanged = oldLow !== state.low || oldHigh !== state.high;
    root.style.setProperty('--image-size', `${options.imageSize}px`);
    root.style.setProperty('--spacing', `${options.spacing}px`);
    root.style.setProperty('--border-width', `${options.borderWidth}px`);
    root.style.setProperty('--border-color', options.borderColor);
    root.style.setProperty('--selected-color', options.selectedColor);
    state.sorting.hidden = !options.allowSorting;
    const fields = [['id', 'ID']];
    if (options.items.some(i => i.resolution != null)) fields.push(['resolution', 'Resolution']);
    if (options.items.some(i => i.size != null)) fields.push(['size', 'Size']);
    const oldSort = state.sort.value;
    state.sort.replaceChildren(...fields.map(([value, text]) => { const o = element('option', null, text); o.value = value; return o; }));
    if (fields.some(([value]) => value === oldSort)) state.sort.value = oldSort;
    const numbers = new Set(options.items.map(i => i.number));
    for (const [number, tile] of state.tiles) if (!numbers.has(number)) { state.cards.get(number).remove(); state.cards.delete(number); state.tiles.delete(number); }
    for (const item of options.items) {
        let tile = state.tiles.get(item.number);
        if (!tile) {
            const card = element('div', 'class2d-card');
            state.cards.set(item.number, card);
            tile = element('button', 'class2d-tile'); tile.type = 'button'; tile.dataset.imageNumber = item.number;
            const img = element('img'); img.alt = `Class image ${item.number}`; img.dataset.imageIndex = item.number - 1; img.draggable = false;
            const url = state.urls.get(item.number - 1); if (url) img.src = url;
            tile.append(img); card.append(tile); tile.onclick = () => select(state, item.number); state.tiles.set(item.number, tile);
        }
        const card = state.cards.get(item.number);
        const group = options.subclasses?.find(g => g.parentId === item.id);
        let expand = card.querySelector('.class2d-expand');
        if (group) {
            if (!expand) { expand = element('button', 'class2d-expand'); expand.type = 'button'; card.append(expand); }
            expand.textContent = group.items.length ? `▸ ${group.items.length}` : 'ⓘ';
            expand.setAttribute('aria-label', `Show subclasses for class ${item.id}`);
            expand.title = group.items.length ? `Show ${group.items.length} subclasses` : group.reason;
            expand.setAttribute('aria-expanded', String(state.expandedParent === item.id));
            expand.onclick = () => openPanel(state, item.id);
        } else expand?.remove();
        tile.setAttribute('aria-label', item.label ?? `Class ${item.id}`);
        for (const label of tile.querySelectorAll('span')) label.remove();
        if (item.label != null) tile.append(element('span', 'class2d-label', item.label));
        if (item.resolution != null) tile.append(element('span', 'class2d-resolution', `${Math.round(item.resolution)} Å`));
        if (item.size != null) tile.append(element('span', 'class2d-size', `${(item.size * 100).toFixed(1)} %`));
    }
    applySelection(state); sortTiles(state);
    const key = JSON.stringify([options.url, options.signed, [...numbers].sort((a, b) => a - b)]);
    if (state.key !== key) {
        if (state.key) releaseImages(state);
        for (const img of state.grid.querySelectorAll('img')) img.removeAttribute('src');
        state.key = key; state.generation++; renderImages(state);
    } else if (contrastChanged) { state.generation++; renderImages(state); }
    refreshPanel(state);
    if (subclassesChanged) notifySelection(state, true);
}

export function dispose(rootId) {
    const state = roots.get(rootId);
    if (!state) return;
    state.disposed = true; clearTimeout(state.timer);
    state.observer.disconnect();
    closePanel(state);
    releaseImages(state); roots.delete(rootId);
    state.root.replaceChildren();
}
