const activeTails = new Map();

async function fetchTail(url, signal) {
    try {
        const response = await fetch(`${url}?t=${Date.now()}`, {
            headers: { 'Range': 'bytes=-4096' },
            signal
        });

        if (!response.ok)
            return null;

        const text = await response.text();
        if (!text)
            return null;

        const lines = text.split('\n');

        // If we got a partial response (206), the first line is likely truncated
        const start = response.status === 206 ? 1 : 0;
        return lines.slice(start)
            // Resolve \r carriage returns: progress bars write to a single line
            // via \r, so keep only the last segment
            .map(l => { const parts = l.split('\r'); return parts[parts.length - 1]; })
            .filter(l => l.length > 0)
            .slice(-9)
            .join('\n');
    } catch {
        return null;
    }
}

async function updateElement(elementId, url, state) {
    const element = document.getElementById(elementId);
    if (!element)
        return;

    state.controller?.abort();
    state.controller = new AbortController();
    const content = await fetchTail(url, state.controller.signal);
    if (!state.disposed && content !== null)
        element.textContent = content;
}

function initializeFileTail(elementId, url, pollIntervalMs) {
    cleanupFileTail(elementId);

    const state = { intervalId: null, controller: null, disposed: false };
    activeTails.set(elementId, state);

    updateElement(elementId, url, state);

    if (pollIntervalMs > 0)
        state.intervalId = setInterval(() => updateElement(elementId, url, state), pollIntervalMs);
}

function cleanupFileTail(elementId) {
    const state = activeTails.get(elementId);
    if (!state)
        return;

    state.disposed = true;
    state.controller?.abort();
    if (state.intervalId)
        clearInterval(state.intervalId);
    activeTails.delete(elementId);
}

export { initializeFileTail, cleanupFileTail };
