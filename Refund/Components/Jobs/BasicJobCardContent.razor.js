const activeTails = new Map();

async function fetchTail(url) {
    try {
        const response = await fetch(`${url}?t=${Date.now()}`, {
            headers: { 'Range': 'bytes=-4096' }
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

async function updateElement(elementId, url) {
    const element = document.getElementById(elementId);
    if (!element)
        return;

    const content = await fetchTail(url);
    if (content !== null)
        element.textContent = content;
}

function initializeLogTail(elementId, url, pollIntervalMs) {
    cleanupLogTail(elementId);

    updateElement(elementId, url);

    if (pollIntervalMs > 0) {
        const intervalId = setInterval(() => updateElement(elementId, url), pollIntervalMs);
        activeTails.set(elementId, intervalId);
    }
}

function cleanupLogTail(elementId) {
    const intervalId = activeTails.get(elementId);
    if (intervalId) {
        clearInterval(intervalId);
        activeTails.delete(elementId);
    }
}

export { initializeLogTail, cleanupLogTail };
