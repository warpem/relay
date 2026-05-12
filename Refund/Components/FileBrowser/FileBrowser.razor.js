/**
 * Scrolls the given element to its rightmost position.
 * Used in the FileBrowser component to ensure the most recently opened folder column
 * is visible to the user after navigation actions.
 *
 * @param {HTMLElement} element - The scrollable container element (typically the columns container)
 */
export function scrollToRight(element) {
    if (element) {
        element.scrollTo({ left: element.scrollWidth, behavior: 'smooth' });
    }
}

/**
 * Scrolls a single folder column vertically so the selected item is centered,
 * but only if the selected item is outside the visible area.
 *
 * @param {HTMLElement} col - The .folder-column element
 */
function scrollColumnToSelected(col) {
    const selected = col.querySelector('.item.selected');
    if (!selected) return;

    const colRect = col.getBoundingClientRect();
    const itemRect = selected.getBoundingClientRect();

    // Skip if already visible
    if (itemRect.top >= colRect.top && itemRect.bottom <= colRect.bottom) return;

    const targetScrollTop = selected.offsetTop - col.clientHeight / 2 + selected.offsetHeight / 2;
    col.scrollTo({ top: Math.max(0, targetScrollTop) });
}

/**
 * Observes the columns container for new content and scrolls each column
 * to show its selected item. Uses a MutationObserver to handle columns
 * that load asynchronously.
 *
 * @param {HTMLElement} container - The .columns-container element
 */
export function observeAndScrollSelected(container) {
    if (!container) return;

    // Scroll any columns that already have their content
    for (const col of container.querySelectorAll('.folder-column')) {
        scrollColumnToSelected(col);
    }

    // Watch for new content appearing (columns loading async)
    const scrolled = new Set();
    const observer = new MutationObserver(() => {
        for (const col of container.querySelectorAll('.folder-column')) {
            if (scrolled.has(col)) continue;
            const selected = col.querySelector('.item.selected');
            if (!selected) continue;
            scrollColumnToSelected(col);
            scrolled.add(col);
        }
    });

    observer.observe(container, { childList: true, subtree: true });

    // Safety disconnect — no need to observe forever
    setTimeout(() => observer.disconnect(), 5000);
}