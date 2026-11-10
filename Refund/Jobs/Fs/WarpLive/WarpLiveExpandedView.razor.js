const views = new Map();
export function observe(id, dotnet) {
    dispose(id);
    const element = document.getElementById(id); if (!element) return;
    let last;
    const observer = new ResizeObserver(entries => {
        const wide = entries[0].contentRect.width >= 2400;
        if (wide === last) return; last = wide;
        dotnet.invokeMethodAsync('WideScreenChanged', wide).catch(() => {});
    });
    views.set(id, observer); observer.observe(element);
}
export function dispose(id) { views.get(id)?.disconnect(); views.delete(id); }
