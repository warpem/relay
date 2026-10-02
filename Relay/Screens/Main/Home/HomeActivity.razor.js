// Tracks the Home activity strip's horizontal scroll position and reports it to .NET:
// whether it can scroll back/forward (arrow buttons) and whether it is near the end
// (mount the next batch of cards).

// Start mounting the next batch when less than this many viewport widths remain
const NEAR_END_VIEWPORTS = 1.5;

export function attach(row, dotNetRef) {
    let lastState = null;
    let frame = 0;
    let disposed = false;

    const report = () => {
        frame = 0;
        if (disposed) return;
        const maxScroll = row.scrollWidth - row.clientWidth;
        const state = {
            back: row.scrollLeft > 1,
            forward: row.scrollLeft < maxScroll - 1,
            nearEnd: maxScroll - row.scrollLeft < row.clientWidth * NEAR_END_VIEWPORTS
        };

        // Only cross the interop boundary when something changed; nearEnd is re-sent after each
        // render through update(), so a newly mounted batch that still doesn't fill the strip loads the next one
        if (lastState &&
            lastState.back === state.back &&
            lastState.forward === state.forward &&
            lastState.nearEnd === state.nearEnd)
            return;

        lastState = state;
        dotNetRef.invokeMethodAsync('HandleScrollState', state.back, state.forward, state.nearEnd)
            .catch(error => { if (!disposed) console.warn('Activity scroll update failed', error); });
    };

    const schedule = () => {
        if (!disposed && !frame)
            frame = requestAnimationFrame(report);
    };

    const resizeObserver = new ResizeObserver(schedule);
    resizeObserver.observe(row);
    row.addEventListener('scroll', schedule, { passive: true });

    schedule();

    return {
        update: () => {
            lastState = null;
            schedule();
        },
        scrollPage: (direction) => {
            row.scrollBy({ left: direction * row.clientWidth * 0.8, behavior: 'smooth' });
        },
        scrollToStart: () => {
            row.scrollTo({ left: 0 });
        },
        reveal: (id) => {
            document.getElementById(id)?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        },
        dispose: () => {
            disposed = true;
            if (frame)
                cancelAnimationFrame(frame);
            resizeObserver.disconnect();
            row.removeEventListener('scroll', schedule);
        }
    };
}
