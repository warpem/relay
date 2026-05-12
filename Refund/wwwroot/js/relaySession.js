/**
 * JavaScript interop library for the RelaySession component.
 * Provides browser-specific functionality for window sizing, theme detection,
 * mouse position tracking, and OS detection.
 */
window.relaySessionInterop = {
    /**
     * Reference to the .NET object for invoking C# methods
     * @type {DotNetObjectReference}
     */
    dotNetReference: null,
    
    /**
     * Timeout ID for debouncing resize events
     * @type {number|null}
     */
    resizeTimeout: null,

    /**
     * Utility function to debounce function calls
     * @param {Function} func - The function to debounce
     * @param {number} wait - Milliseconds to delay
     */
    debounce: function(func, wait) {
        clearTimeout(this.resizeTimeout);
        this.resizeTimeout = setTimeout(() => func(), wait);
    },

    /**
     * Initializes the session interop with event listeners
     * @param {DotNetObjectReference} dotNetRef - Reference to the .NET object
     */
    initialize: function (dotNetRef) {
        this.dotNetReference = dotNetRef;
        window.addEventListener('resize', this.handleResize.bind(this));
        // Add listener for system theme changes (both dark -> light and light -> dark)
        // matches will be true when changing TO dark mode, false when changing TO light mode
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', this.handleThemeChange.bind(this));
    },

    /**
     * Handles window resize events and notifies .NET
     * Debounces the event to avoid excessive calls during resize operations
     */
    handleResize: function () {
        if (this.dotNetReference) {
            // Debounce the resize handler with 200ms delay
            this.debounce(() => {
                this.dotNetReference.invokeMethodAsync('HandleWindowResize');
            }, 200);
        }
    },

    /**
     * Handles system theme changes and notifies .NET
     * @param {MediaQueryListEvent} e - The media query change event
     */
    handleThemeChange: function (e) {
        if (this.dotNetReference) {
            // e.matches will be:
            // - true when system switches TO dark mode
            // - false when system switches TO light mode
            this.dotNetReference.invokeMethodAsync('HandleSystemThemeChange', e.matches);
        }
    },

    /**
     * Gets the current window width in pixels
     * @returns {number} The window inner width
     */
    getWindowWidth: function () {
        return window.innerWidth;
    },

    /**
     * Gets the current window height in pixels
     * @returns {number} The window inner height
     */
    getWindowHeight: function () {
        return window.innerHeight;
    },

    /**
     * Detects the client operating system from the user agent
     * @returns {string} The detected OS name ("Windows", "MacOS", "Linux", "Android", "iOS", or "Unknown")
     */
    getClientOS: function() {
        const userAgent = window.navigator.userAgent;
        let os = "Unknown";
        if (userAgent.indexOf("Win") > -1) os = "Windows";
        else if (userAgent.indexOf("Mac") > -1) os = "MacOS";
        else if (userAgent.indexOf("Linux") > -1) os = "Linux";
        else if (userAgent.indexOf("Android") > -1) os = "Android";
        else if (userAgent.indexOf("iOS") > -1) os = "iOS";
        return os;
    },

    /**
     * Detects if the system prefers dark mode
     * @returns {boolean} True if the system prefers dark mode, false otherwise
     */
    getSystemThemePreference: function() {
        return window.matchMedia('(prefers-color-scheme: dark)').matches;
    },

    /**
     * Calculates mouse coordinates relative to a specific element
     * @param {Object} coordinates - Object with clientX and clientY properties
     * @param {string} elementId - ID of the element to calculate position relative to
     * @returns {Object|null} Object with x and y properties, or null if element not found
     */
    getRelativeMousePosition: function(coordinates, elementId) {
        const element = document.getElementById(elementId);
        if (!element) return null;
        const rect = element.getBoundingClientRect();
        return {
            x: coordinates.clientX - rect.left,
            y: coordinates.clientY - rect.top
        };
    },

    /**
     * Cleans up resources and event listeners
     */
    dispose: function () {
        window.removeEventListener('resize', this.handleResize.bind(this));
        window.matchMedia('(prefers-color-scheme: dark)').removeEventListener('change', this.handleThemeChange.bind(this));
        // Clear any pending resize timeout
        if (this.resizeTimeout) {
            clearTimeout(this.resizeTimeout);
            this.resizeTimeout = null;
        }
        this.dotNetReference = null;
    }
};