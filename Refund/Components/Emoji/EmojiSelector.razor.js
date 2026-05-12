/**
 * Timer for debouncing scroll events to prevent excessive callback invocations.
 * @type {number|null}
 */
let debounceTimer = null;

/**
 * Delay in milliseconds before invoking the scroll handler after scrolling stops.
 * Smaller values increase responsiveness but may reduce performance.
 * @type {number}
 */
const DEBOUNCE_DELAY = 200; // milliseconds

/**
 * Initializes scroll event tracking for the emoji selector grid.
 * Attaches a debounced scroll event listener to prevent excessive updates.
 * 
 * @param {HTMLElement} element - The scrollable grid container element.
 * @param {DotNetObjectReference} dotNetHelper - Reference to the .NET component for callbacks.
 */
export function initializeScroll(element, dotNetHelper) {
    element.addEventListener('scroll', () => {
        // Clear any pending debounce timer.
        if (debounceTimer !== null) {
            clearTimeout(debounceTimer);
        }
        // Set a new timer that fires 200ms after the last scroll event.
        debounceTimer = setTimeout(() => {
            dotNetHelper.invokeMethodAsync('OnScrollUpdate', Math.round(element.scrollTop));
            debounceTimer = null;
        }, DEBOUNCE_DELAY);
    });
}

/**
 * Gets the current scroll state of the emoji grid container.
 * Used to initialize virtualization calculations and determine visible items.
 * 
 * @param {HTMLElement} element - The scrollable grid container element.
 * @returns {Object} Object containing scrollTop, clientHeight, and scrollHeight values.
 */
export function getScrollInfo(element) {
    return {
        scrollTop: element.scrollTop,
        clientHeight: element.clientHeight,
        scrollHeight: element.scrollHeight
    };
}