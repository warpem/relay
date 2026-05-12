/**
 * Initializes the log panel JavaScript functionality.
 * Sets up scroll tracking, event handlers, and data structures for managing log sections.
 * 
 * @param {Object} dotNetRef - Reference to the .NET component for callbacks
 * @param {HTMLElement} element - The log content container element
 */
export function initialize(dotNetRef, element) {
    if (!element) return;

    // Arrays to track log section heights and boundary positions
    element._logHeights = [];
    element._logBoundaries = [];
    element._scrollTimeout = null;

    // Add debounced scroll handler
    element._scrollHandler = () => {
        if (element._scrollTimeout) {
            clearTimeout(element._scrollTimeout);
        }

        element._scrollTimeout = setTimeout(() => {
            const scrollTop = element.scrollTop;

            // Check if scrolled to bottom (with small tolerance)
            const isAtBottom = Math.abs(element.scrollHeight - element.clientHeight - scrollTop) < 5;

            if (isAtBottom) {
                // If at bottom, always select the last iteration
                const lastIterationIndex = element._logBoundaries.length - 1;
                if (lastIterationIndex >= 0) {
                    dotNetRef.invokeMethodAsync('HandleScroll', lastIterationIndex);
                }
                return;
            }

            // Normal iteration detection logic - find which section the scroll position is within
            let currentIteration = -1;
            for (let i = 0; i < element._logBoundaries.length; i++) {
                if (scrollTop >= element._logBoundaries[i] &&
                    (i === element._logBoundaries.length - 1 || scrollTop < element._logBoundaries[i + 1])) {
                    currentIteration = i;
                    break;
                }
            }

            if (currentIteration !== -1) {
                dotNetRef.invokeMethodAsync('HandleScroll', currentIteration);
            }
        }, 200); // 200ms debounce to avoid excessive calls during fast scrolling
    };

    element.addEventListener('scroll', element._scrollHandler);
}

/**
 * Cleans up resources when the component is disposed.
 * Removes event listeners and clears data structures.
 * 
 * @param {HTMLElement} element - The log content container element
 */
export function dispose(element) {
    if (!element) return;

    if (element._scrollHandler) {
        element.removeEventListener('scroll', element._scrollHandler);
        delete element._scrollHandler;
    }

    if (element._scrollTimeout) {
        clearTimeout(element._scrollTimeout);
        delete element._scrollTimeout;
    }

    delete element._logHeights;
    delete element._logBoundaries;
}

/**
 * Updates the cached heights and boundary positions of log iteration sections.
 * Called when log content changes or when panel is expanded.
 * 
 * @param {HTMLElement} element - The log content container element
 */
export function updateLogHeights(element) {
    if (!element) return;

    const sections = element.getElementsByClassName('log-iteration');
    if (!sections.length) return;

    // Calculate heights and positions
    element._logHeights = [];
    element._logBoundaries = [];
    let totalHeight = 0;

    Array.from(sections).forEach((section, index) => {
        const height = section.offsetHeight;
        element._logHeights.push(height);
        element._logBoundaries.push(totalHeight);
        totalHeight += height;
    });
}

/**
 * Scrolls the log panel to display a specific iteration.
 * Can scroll to the beginning of an iteration or to the end of the last iteration.
 * 
 * @param {HTMLElement} element - The log content container element 
 * @param {number} iteration - The iteration index to scroll to
 * @param {boolean} scrollToEnd - Whether to scroll to the end of the content (for latest iteration)
 * @param {boolean} instant - Whether to use instant scrolling (true) or smooth animation (false)
 */
export function scrollToIteration(element, iteration, scrollToEnd = false, instant = false) {
    if (!element || iteration < 0 || iteration >= element._logBoundaries.length) return;

    // Temporarily remove scroll handler to prevent feedback loop
    element.removeEventListener('scroll', element._scrollHandler);

    // Calculate scroll position
    let scrollTop;
    if (scrollToEnd && iteration === element._logBoundaries.length - 1) {
        // For last iteration, scroll to the very end
        scrollTop = element.scrollHeight;
    } else {
        // Otherwise scroll to iteration boundary
        scrollTop = element._logBoundaries[iteration];
    }

    // Scroll with or without animation
    element.scrollTo({
        top: scrollTop,
        behavior: instant ? 'instant' : 'smooth'
    });

    // Re-add scroll handler after animation completes
    setTimeout(() => {
        element.addEventListener('scroll', element._scrollHandler);
    }, instant ? 0 : 500);
}