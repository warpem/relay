// Scrolls the virtualized jobs table so a row (picked in the treemap) sits in the upper third of the view.
export function scrollToRow(container, index, rowHeight) {
    if (!container) return;
    const header = container.querySelector('.st-header');
    const offset = (header ? header.offsetHeight : 0) + index * rowHeight;
    const target = Math.max(0, offset - container.clientHeight / 3);
    container.scrollTo({ top: target, behavior: Math.abs(container.scrollTop - target) > 4000 ? 'auto' : 'smooth' });
}
