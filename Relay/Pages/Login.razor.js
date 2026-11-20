// Login page background: runs one randomly chosen animated background (wwwroot/login-bg) behind the card.

let stage = null;
let generation = 0;

async function run(gen) {
    const container = document.querySelector('.login-bg');
    if (!container) return;
    const focusEl = document.querySelector('.login-card, .sso-card');
    const { start: startBackground } = await import('../login-bg/index.js');
    if (gen !== generation) return;
    const started = await startBackground(container, focusEl);
    if (gen !== generation) { started?.dispose(); return; }
    stage = started;
}

/** Starts the background without waiting for it; failures leave the plain page colour. */
export function start() {
    stop();
    const gen = generation;
    run(gen).catch(e => console.warn('Login background failed to start:', e));
}

export function stop() {
    generation++;
    stage?.dispose();
    stage = null;
}
