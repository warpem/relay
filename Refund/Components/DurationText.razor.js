/**
 * JavaScript module for the DurationText component.
 * Handles real-time duration formatting and updates.
 */

/**
 * Map to track interval IDs for active duration updaters.
 * Key: Element ID, Value: setInterval ID
 */
const intervals = new Map();

/**
 * Formats a duration in milliseconds to a human-readable string.
 * Adaptively selects the most appropriate time units based on the duration.
 * 
 * @param {number} milliseconds - Duration in milliseconds
 * @returns {string} Formatted duration string
 */
function formatDuration(milliseconds) {
    const totalSeconds = Math.floor(milliseconds / 1000);

    const days = Math.floor(totalSeconds / (24 * 3600));
    const hours = Math.floor((totalSeconds % (24 * 3600)) / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;

    if (days >= 1) {
        return `${days} d ${hours} h`;
    }

    if (hours >= 1) {
        return `${hours}:${minutes < 10 ? '0' + minutes : minutes}:${seconds < 10 ? '0' + seconds : seconds}`;
    }

    return `${minutes < 10 ? '0' + minutes : minutes}:${seconds < 10 ? '0' + seconds : seconds}`;
}

/**
 * Initializes or resets a duration updater for a specific element.
 * Sets up a timer to update the duration display every second.
 * 
 * @param {string} elementId - ID of the element to update
 * @param {string|Date} timestamp - Starting timestamp
 */
function initializeDurationUpdater(elementId, timestamp) {
    const element = document.getElementById(elementId);
    if (!element) return;

    /**
     * Updates the duration displayed in the element.
     * Calculates the time difference between now and the starting timestamp.
     */
    function updateDuration() {
        const now = new Date().getTime();
        const startTime = new Date(timestamp).getTime();
        const duration = now - startTime;

        element.textContent = formatDuration(duration);
    }

    // Clear any existing interval for this element
    cleanupDurationUpdater(elementId);

    // Initial update
    updateDuration();

    // Update every second
    const intervalId = setInterval(updateDuration, 1000);
    intervals.set(elementId, intervalId);
}

/**
 * Cleans up a duration updater by clearing its interval.
 * 
 * @param {string} elementId - ID of the element whose updater should be cleaned up
 */
function cleanupDurationUpdater(elementId) {
    const intervalId = intervals.get(elementId);
    if (intervalId) {
        clearInterval(intervalId);
        intervals.delete(elementId);
    }
}

export {
    initializeDurationUpdater,
    cleanupDurationUpdater
};