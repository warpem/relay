window.downloadFile = function (url, fileName) {
    return fetch(url)
        .then(response => response.blob())
        .then(blob => {
            const link = document.createElement('a');
            link.href = URL.createObjectURL(blob);
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            document.body.removeChild(link);
        })
        .catch(error => console.error('Download failed:', error));
};

window.parseDotNetFloat = function (num) {
    if (num === null) {
        return null;
    } else if (num === "null") {
        return null;
    } else if (num === "Infinity") {
        return Infinity;
    } else if (num === "-Infinity") {
        return -Infinity;
    } else if (num === "NaN") {
        return NaN;
    } else if (typeof num === "string") {
        return parseFloat(num);
    }
}

// Thumbnail animation system
window.thumbnailAnimations = (function() {
    // Store animation timers by element ID
    const animationTimers = new Map();
    
    return {
        // Start animation on a thumbnail
        startAnimation: function(animationElement, imageUrls, fps) {
            if (!animationElement || !imageUrls || imageUrls.length === 0) return;
            
            // Generate a unique ID for this animation if needed
            const elementId = animationElement.id || `thumbnail-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
            animationElement.id = elementId;
            
            // Stop any existing animation on this element
            this.stopAnimation(animationElement);
            
            // Make the animation element visible
            animationElement.style.display = 'block';
            
            // Calculate frame delay based on FPS
            const frameDelay = 1000 / fps;
            let currentFrame = 0;
            
            // Set initial frame
            if (imageUrls.length > 0) {
                animationElement.src = imageUrls[0];
            }
            
            // Create and store the animation timer
            const timerId = setInterval(() => {
                animationElement.src = imageUrls[currentFrame];
                currentFrame = (currentFrame + 1) % imageUrls.length;
            }, frameDelay);
            
            // Store the timer ID with the element's ID
            animationTimers.set(elementId, timerId);
        },
        
        // Stop animation on a thumbnail
        stopAnimation: function(animationElement) {
            if (!animationElement) return;
            
            // Find and clear the animation timer
            const elementId = animationElement.id;
            if (elementId && animationTimers.has(elementId)) {
                clearInterval(animationTimers.get(elementId));
                animationTimers.delete(elementId);
            }
            
            // Hide the animation element
            animationElement.style.display = 'none';
        }
    };
})();

// Shorthand methods for easier calling from Blazor
window.startThumbnailAnimation = function(animationElement, imageUrls, fps) {
    window.thumbnailAnimations.startAnimation(animationElement, imageUrls, fps);
};

window.stopThumbnailAnimation = function(animationElement) {
    window.thumbnailAnimations.stopAnimation(animationElement);
};

/**
 * Global scatter plot synchronization system
 * Keeps highlight index, zoom level, and scroll position synchronized across all scatter plots
 */
window.scatterHighlightSync = (function() {
    // Storage for registered scatter plots
    const scatterPlots = new Map();
    
    // Synchronized state
    const state = {
        highlightIndex: -1,
        zoom: 1.0,
        scrollPosition: 0
    };
    
    /**
     * Updates all registered plots with a specific property value
     * @param {string} property - The property to update ('highlightIndex', 'zoom', 'scrollPosition')
     * @param {any} value - The new value
     * @param {string|null} senderId - Optional ID of sender to exclude from update
     */
    function updateAllPlots(property, value, senderId = null) {
        const methodMap = {
            'highlightIndex': 'updateHighlight',
            'zoom': 'updateZoom',
            'scrollPosition': 'updateScrollPosition'
        };
        
        const method = methodMap[property];
        if (!method) return;
        
        scatterPlots.forEach((instance, id) => {
            // For highlight, always update all plots to ensure consistent state
            // For zoom and scroll, skip the sender to avoid feedback loops
            if (property === 'highlightIndex' || id !== senderId) {
                instance[method](value);
            }
        });
    }
    
    return {
        // Register a scatter plot with the sync system
        registerScatterPlot: function(scatterId, instance) {
            scatterPlots.set(scatterId, instance);
            
            // Apply current state to the new plot
            if (state.highlightIndex >= 0) {
                instance.updateHighlight(state.highlightIndex);
            }
            
            if (state.zoom > 1.0) {
                instance.updateZoom(state.zoom);
            }
            
            if (state.zoom > 1.0 && state.scrollPosition > 0) {
                instance.updateScrollPosition(state.scrollPosition);
            }
        },
        
        // Remove a scatter plot from the sync system
        unregisterScatterPlot: function(scatterId) {
            scatterPlots.delete(scatterId);
        },
        
        // Set highlight across all scatter plots
        setHighlight: function(senderId, pointIndex) {
            state.highlightIndex = pointIndex;
            updateAllPlots('highlightIndex', pointIndex);
        },
        
        // Set zoom level across all scatter plots
        setZoom: function(senderId, zoomLevel) {
            state.zoom = zoomLevel;
            updateAllPlots('zoom', zoomLevel, senderId);
        },
        
        // Set scroll position across all scatter plots
        setScrollPosition: function(senderId, scrollPos) {
            // Only sync scroll when zoomed in
            if (state.zoom <= 1.0) return;
            
            state.scrollPosition = scrollPos;
            updateAllPlots('scrollPosition', scrollPos, senderId);
        }
    };
})();