const observers = new Map();
export function observe(id, dotnet) {
    dispose(id);
    const query = matchMedia('(min-width: 2000px)');
    const listener = () => dotnet.invokeMethodAsync('SetupWidthChanged', query.matches).catch(() => {});
    query.addEventListener('change', listener);
    observers.set(id, { query, listener });
    listener();
}
export function dispose(id) {
    const state = observers.get(id);
    if (!state) return;
    state.query.removeEventListener('change', state.listener);
    observers.delete(id);
}
