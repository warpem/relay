const views = new Map();
export function observe(id, dotnet) {
    dispose(id);
    const query = matchMedia('(min-width: 2400px)');
    const listener = () => dotnet.invokeMethodAsync('WideScreenChanged', query.matches).catch(() => {});
    views.set(id, { query, listener });
    query.addEventListener('change', listener);
    listener();
}
export function dispose(id) {
    const state = views.get(id);
    if (!state) return;
    state.query.removeEventListener('change', state.listener);
    views.delete(id);
}
