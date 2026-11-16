// Selection is a shared original-index interval; drawing the brush never ships plot data.
const instances = new Map();

function clamp(value, min, max) { return Math.max(min, Math.min(max, value)); }

function paint(state, start, end) {
    const count = state.end - state.start;
    const left = Math.max(state.start, start ?? state.start);
    const right = Math.min(state.end, end ?? state.start);
    const visible = count > 0 && start != null && end != null && right > left;
    state.element.classList.toggle('has-interval', visible);
    state.element.style.setProperty('--interval-left', `${visible ? (left - state.start) / count * 100 : 0}%`);
    state.element.style.setProperty('--interval-width', `${visible ? (right - left) / count * 100 : 0}%`);
}

function indexAt(state, clientX, frame) {
    const bounds = frame.getBoundingClientRect();
    const axisWidth = 34;
    const fraction = clamp((clientX - bounds.left - axisWidth) / Math.max(1, bounds.width - axisWidth), 0, .99999999);
    return state.start + Math.floor(fraction * Math.max(1, state.end - state.start));
}

export function initialize(element, dotNet) {
    dispose(element.id);
    const state = { element, dotNet, start: 0, end: 0, rangeStart: null, rangeEnd: null, drag: null, disposed: false, suppressClick: false };
    state.down = event => {
        state.suppressClick = false;
        const frame = event.target.closest('.plot-frame');
        if (event.button !== 0 || !frame || !element.contains(frame) || state.end <= state.start) return;
        const bounds = frame.getBoundingClientRect();
        if (event.clientX < bounds.left + 34) return;
        state.drag = { frame, pointerId: event.pointerId, x: event.clientX, first: indexAt(state, event.clientX, frame), active: false };
    };
    state.move = event => {
        const drag = state.drag;
        if (!drag || drag.pointerId !== event.pointerId) return;
        if (!drag.active && Math.abs(event.clientX - drag.x) < 5) return;
        drag.active = true;
        element.classList.add('is-brushing');
        event.preventDefault();
        const current = indexAt(state, event.clientX, drag.frame);
        paint(state, Math.min(drag.first, current), Math.max(drag.first, current) + 1);
    };
    state.up = event => {
        const drag = state.drag;
        if (!drag || drag.pointerId !== event.pointerId) return;
        state.drag = null;
        element.classList.remove('is-brushing');
        if (!drag.active) return;
        const last = indexAt(state, event.clientX, drag.frame);
        const start = Math.min(drag.first, last), end = Math.max(drag.first, last) + 1;
        state.rangeStart = start;
        state.rangeEnd = end;
        paint(state, start, end);
        state.suppressClick = true;
        state.dotNet.invokeMethodAsync('SelectInterval', start, end).catch(error => {
            if (!state.disposed && element.isConnected) console.error(error);
        });
    };
    state.cancel = () => {
        state.drag = null;
        element.classList.remove('is-brushing');
        paint(state, state.rangeStart, state.rangeEnd);
    };
    state.click = event => {
        if (!state.suppressClick) return;
        state.suppressClick = false;
        event.preventDefault();
        event.stopImmediatePropagation();
    };
    element.addEventListener('pointerdown', state.down, true);
    element.addEventListener('click', state.click, true);
    window.addEventListener('pointermove', state.move, { passive: false });
    window.addEventListener('pointerup', state.up);
    window.addEventListener('pointercancel', state.cancel);
    instances.set(element.id, state);
}

export function update(element, config) {
    const state = instances.get(element?.id);
    if (!state) return;
    const domainChanged = state.start !== config.start || state.end !== config.end;
    state.start = config.start;
    state.end = config.end;
    state.rangeStart = config.rangeStart;
    state.rangeEnd = config.rangeEnd;
    if (domainChanged) state.cancel();
    if (!state.drag?.active) paint(state, config.rangeStart, config.rangeEnd);
}

export function dispose(elementOrId) {
    const id = typeof elementOrId === "string" ? elementOrId : elementOrId?.id;
    const state = instances.get(id);
    if (!state) return;
    state.disposed = true;
    const element = state.element;
    element.removeEventListener('pointerdown', state.down, true);
    element.removeEventListener('click', state.click, true);
    window.removeEventListener('pointermove', state.move);
    window.removeEventListener('pointerup', state.up);
    window.removeEventListener('pointercancel', state.cancel);
    instances.delete(id);
}
