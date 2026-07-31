export function getScrollInfo(element) {
    if (!element) {
        return {
            scrollLeft: 0,
            clientWidth: 0,
            scrollWidth: 0
        };
    }
    return {
        scrollLeft: element.scrollLeft,
        clientWidth: element.clientWidth,
        scrollWidth: element.scrollWidth
    };
}

export function scrollLeft(element, amount) {
    if (element) {
        element.scrollBy({ left: -amount, behavior: 'smooth' });
    }
}

export function scrollRight(element, amount) {
    if (element) {
        element.scrollBy({ left: amount, behavior: 'smooth' });
    }
}

export function scrollBy(element, amount) {
    if (element) {
        element.scrollBy({ left: amount });
    }
}

export function scrollTo(element, left) {
    if (element) {
        element.scrollTo({ left: left, behavior: 'smooth' });
    }
}

export function getElementWidth(element) {
    return element ? element.clientWidth : 0;
}

let resizeObservers = new Map();

function stopObserving(element) {
    const observer = resizeObservers.get(element);
    if (observer) {
        observer.disconnect();
        resizeObservers.delete(element);
    }
}

export function observeResize(element, dotNetHelper) {
    if (element && !resizeObservers.has(element)) {
        const observer = new ResizeObserver(entries => {
            for (let entry of entries) {
                const width = entry.contentRect.width;
                // Removing the element from the DOM can queue a final resize notification that
                // is delivered after the .NET component has been disposed. Invoking the disposed
                // DotNetObjectReference then fails for every subsequent resize, so detach on the
                // first failure instead of leaking the observer and the reference.
                dotNetHelper.invokeMethodAsync('OnComponentResized', width)
                            .catch(() => stopObserving(element));
            }
        });
        observer.observe(element);
        resizeObservers.set(element, observer);
    }
}

export function unobserveResize(element) {
    if (element)
        stopObserving(element);
}