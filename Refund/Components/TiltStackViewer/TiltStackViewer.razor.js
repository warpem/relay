/**
 * JavaScript interop functions for the TiltStackViewer component
 */

// Store observer instances by element reference to allow cleanup
let resizeObservers = {};

/**
 * Initializes a ResizeObserver for the tilt stack viewer element.
 * The observer will call the provided .NET callback when the element's size changes.
 * 
 * @param {HTMLElement} element The element to observe for resize events
 * @param {DotNet.DotNetObject} dotNetRef The .NET object reference for callbacks
 * @param {number} debounceMs The debounce time in milliseconds
 */
export function observeResize(element, dotNetRef, debounceMs = 100) {
    // Clean up any existing observer for this element
    disposeResizeObserver(element);
    
    let timeout = null;
    const observer = new ResizeObserver(entries => {
        // Debounce the resize event to avoid overwhelming the server
        clearTimeout(timeout);
        timeout = setTimeout(() => {
            for (const entry of entries) {
                const width = Math.round(entry.contentRect.width);
                const height = Math.round(entry.contentRect.height);
                
                // Call back to the .NET component with the new dimensions
                dotNetRef.invokeMethodAsync('OnResized', width, height);
            }
        }, debounceMs);
    });
    
    // Start observing the element
    observer.observe(element);
    
    // Store the observer instance for later cleanup
    resizeObservers[element.id] = { 
        observer, 
        timeout,
        dotNetRef 
    };
}

/**
 * Disposes a ResizeObserver for the given element
 * 
 * @param {HTMLElement} element The element to stop observing
 */
export function disposeResizeObserver(element) {
    if (element && element.id && resizeObservers[element.id]) {
        const { observer, timeout, dotNetRef } = resizeObservers[element.id];
        
        // Clear any pending timeout
        if (timeout) {
            clearTimeout(timeout);
        }
        
        // Stop observing the element
        observer.disconnect();
        
        // Remove from the tracking object
        delete resizeObservers[element.id];
    }
}