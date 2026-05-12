/**
 * Key used to store UI state in sessionStorage during reconnection attempts
 */
const sessionStorageKey = 'statefulReconnection.uiState';

/**
 * Flag to ensure init is only called once
 */
let isInitialized;

/**
 * Initializes the stateful reconnection handler.
 * This function overrides Blazor's default reconnection handler to provide:
 * 1. UI state preservation across reconnection attempts
 * 2. Enhanced reconnection display with better user messaging
 * 3. More aggressive reconnection parameters for better UX
 * 
 * @param {HTMLElement} overlayElem - The overlay DOM element to use for reconnection display
 * @throws Error if initialized more than once in a session
 */
export function init(overlayElem) {
    if (isInitialized) {
        throw new Error('Do not add more than one instance of <StatefulReconnection.Enable>');
    }

    isInitialized = true;
    loadUIState();

    Blazor.defaultReconnectionHandler._reconnectionDisplay = new BetterReconnectionDisplay(overlayElem);

    const origOnConnectionDown = Blazor.defaultReconnectionHandler.onConnectionDown;
    Blazor.defaultReconnectionHandler.onConnectionDown = function (options, error) {
        saveUIState();

        // If no custom options were set, change the defaults
        if (options.maxRetries === 8 && options.retryIntervalMilliseconds === 20000) {
            options.retryIntervalMilliseconds = 1000;
            options.maxRetries = 10 * 60; // 10 minutes
        }

        return origOnConnectionDown.call(this, options, error);
    }

    const origOnConnectionUp = Blazor.defaultReconnectionHandler.onConnectionUp;
    Blazor.defaultReconnectionHandler.onConnectionUp = function () {
        clearUIState();
        return origOnConnectionUp.apply(this, arguments);
    }
}

/**
 * Enhanced reconnection display handler that replaces Blazor's default implementation.
 * Provides a better user experience during reconnection attempts with:
 * 1. Visual overlay to indicate disconnection
 * 2. Delayed messaging about checking internet connection
 * 3. Automatic page reload on failure or rejection
 */
class BetterReconnectionDisplay {
    /**
     * Creates a new reconnection display
     * 
     * @param {HTMLElement} overlayElem - The DOM element to use as the reconnection overlay
     */
    constructor(overlayElem) {
        this.overlayElem = overlayElem;
        this.checkInternetElem = overlayElem.querySelector('.check-internet');
    }

    /**
     * Shows the reconnection overlay when a connection is lost.
     * After 5 seconds, displays an additional message suggesting to check internet connectivity.
     */
    show() {
        this.overlayElem.classList.add('reconnect-visible');
        this.checkInternetElem.style.display = 'none';
        clearTimeout(this.showCheckConnectionTimer);
        this.showCheckConnectionTimer = setTimeout(() => {
            this.checkInternetElem.style.display = 'block';
        }, 5000);
    }

    /**
     * Updates the display for each reconnection attempt.
     * Currently a no-op as we don't display attempt count.
     * 
     * @param {number} currentAttempt - The current reconnection attempt number
     */
    update(currentAttempt) {
        // No-op in this implementation
    }

    /**
     * Hides the reconnection overlay when connection is restored.
     */
    hide() {
        this.overlayElem.classList.remove('reconnect-visible');
        clearTimeout(this.showCheckConnectionTimer);
    }

    /**
     * Handles reconnection failure by reloading the page.
     * Called when all reconnection attempts have been exhausted.
     */
    failed() {
        location.reload();
    }

    /**
     * Handles reconnection rejection by reloading the page.
     * Called when the server explicitly rejects the reconnection attempt,
     * typically due to a version mismatch or authentication issue.
     */
    rejected() {
        location.reload();
    }
}

/**
 * Loads the saved UI state from sessionStorage and restores form field values.
 * Called during initialization to restore UI state after page reload or reconnection.
 */
function loadUIState() {
    const stateJson = sessionStorage.getItem(sessionStorageKey);
    if (stateJson) {
        clearUIState();
        const state = JSON.parse(stateJson);
        for (const [selector, value] of Object.entries(state)) {
            const elem = document.querySelector(selector);
            if (elem) {
                writeElementValue(elem, value);
            }
        }

        // Restore focus to the previously active element
        if (state.__activeElement) {
            const activeElem = document.querySelector(state.__activeElement);
            if (activeElem) {
                activeElem.focus();
            }
        }
    }
}

/**
 * Saves the current UI state to sessionStorage.
 * Captures all form input values, select dropdowns, and textareas,
 * as well as the currently focused element.
 * Called when the connection is lost to preserve user input.
 */
function saveUIState() {
    const editableElements = document.querySelectorAll(['input', 'textarea', 'select']);
    const selectorCacheMap = new Map();
    const uiState = {};
    editableElements.forEach(elem => {
        const selector = toQuerySelector(elem, selectorCacheMap);
        uiState[selector] = readElementValue(elem);
    });

    // Save the currently focused element
    if (document.activeElement) {
        uiState.__activeElement = toQuerySelector(document.activeElement, selectorCacheMap);
    }

    sessionStorage.setItem(sessionStorageKey, JSON.stringify(uiState));
}

/**
 * Clears the saved UI state from sessionStorage.
 * Called after successful reconnection or when loading a previously saved state.
 */
function clearUIState() {
    sessionStorage.removeItem(sessionStorageKey);
}

/**
 * Generates a CSS selector that uniquely identifies an element in the DOM.
 * Uses element IDs when available, and falls back to nth-of-type selectors
 * when necessary. Results are cached for performance.
 * 
 * @param {HTMLElement} elem - The element to generate a selector for
 * @param {Map} cacheMap - Cache of already computed selectors to avoid redundant calculations
 * @returns {string} A CSS selector string that uniquely identifies the element
 */
function toQuerySelector(elem, cacheMap) {
    if (cacheMap.has(elem)) {
        return cacheMap.get(elem);
    }

    let result;

    if (elem.id) {
        result = `#${elem.id}`; // No need to recurse into ancestors in this case
    } else {
        let nthOfTypeIndex = 1;
        let sibling = elem.parentNode.firstElementChild;
        while (sibling !== elem) {
            if (sibling.tagName === elem.tagName) {
                nthOfTypeIndex++;
            }
            sibling = sibling.nextElementSibling;
        }

        // Create a selector that identifies this element's position in its parent
        const selector = `${elem.tagName}:nth-of-type(${nthOfTypeIndex})`;
        result = elem === document.documentElement ? selector : `${toQuerySelector(elem.parentNode, cacheMap)} > ${selector}`;
    }

    cacheMap.set(elem, result);
    return result;
}

/**
 * Reads the current value from a form element, handling different types appropriately.
 * 
 * @param {HTMLElement} elem - The element to read the value from
 * @returns {any} The current value of the element (boolean for checkboxes, string for other inputs)
 */
function readElementValue(elem) {
    if (elem.type === 'checkbox') {
        return elem.checked;
    } else {
        return elem.value;
    }
}

/**
 * Writes a value to a form element, handling different types appropriately.
 * Also triggers input and change events to ensure Blazor components are notified.
 * 
 * @param {HTMLElement} elem - The element to write the value to
 * @param {any} value - The value to set (boolean for checkboxes, string for other inputs)
 */
function writeElementValue(elem, value) {
    if (elem.type === 'checkbox') {
        elem.checked = value;
    } else {
        elem.value = value;
    }

    // Trigger events to ensure Blazor components update
    elem.dispatchEvent(new Event('input', { 'bubbles': true }));
    elem.dispatchEvent(new Event('change', { 'bubbles': true }));
}