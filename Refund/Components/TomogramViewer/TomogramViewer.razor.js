/**
 * JavaScript interop functions for the TomogramViewer component
 */

// Store observer instances by element reference to allow cleanup
let resizeObservers = {};

// Store state for particle preview
let previewState = {
    isActive: false,             // Is picking mode active
    currentPlane: null,          // Which plane is currently being hovered (XY, XZ, ZY)
    planes: {},                  // Configuration for each plane
    viewPoint: { x: 0, y: 0, z: 0 }, // Current 3D viewpoint coordinates
    mousePosition: { x: 0, y: 0 },   // Current mouse position in screen coordinates
    particleColor: "#FFF700",     // Color for preview circles
    particleDiameter: 100,        // Diameter in tomogram units
    pixelSize: 1.0                // Pixel size for scaling
};

/**
 * Initializes a ResizeObserver for the tomogram viewer element.
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
        
        // Clean up references to avoid memory leaks
        dotNetRef.dispose();
        
        // Remove from the tracking object
        delete resizeObservers[element.id];
    }
}

/**
 * Clean up event handlers and references when component is disposed
 */
export function disposeParticlePreview() {
    // Remove event listeners
    for (const planeId in previewState.planes) {
        const planeConfig = previewState.planes[planeId];
        const containerElement = document.getElementById(planeId);
        
        if (containerElement && planeConfig.mouseMoveHandler && planeConfig.mouseLeaveHandler) {
            containerElement.removeEventListener('mousemove', planeConfig.mouseMoveHandler);
            containerElement.removeEventListener('mouseleave', planeConfig.mouseLeaveHandler);
        }
    }
    
    // Reset state
    previewState.planes = {};
    previewState.currentPlane = null;
}

/**
 * Initialize the particle preview system with plane configurations
 * 
 * @param {boolean} isPickingActive Whether particle picking mode is active
 * @param {Object} planeConfigs Configuration for each plane (id, type, dimensions)
 * @param {Object} viewPoint Current 3D coordinates where planes intersect
 * @param {string} particleColor Color to use for preview circles
 * @param {number} particleDiameter Diameter of particles in tomogram units
 * @param {number} pixelSize Pixel size for scaling
 */
export function initializeParticlePreview(isPickingActive, 
                                          planeConfigs, 
                                          viewPoint, 
                                          particleColor, 
                                          particleDiameter, 
                                          pixelSize) {
    // Set initial state
    previewState.isActive = isPickingActive;
    previewState.viewPoint = viewPoint;
    previewState.particleColor = particleColor;
    previewState.particleDiameter = particleDiameter;
    previewState.pixelSize = pixelSize;
    
    // Set up plane configurations
    previewState.planes = {};
    for (const planeId in planeConfigs) {
        const config = planeConfigs[planeId];
        
        // Find container element
        const containerElement = document.getElementById(planeId);
        if (!containerElement) {
            console.warn(`Could not find container element #${planeId}`);
            continue;
        }
        
        // Store plane configuration
        previewState.planes[planeId] = {
            id: planeId,
            type: config.type,
            width: config.width,
            height: config.height,
            translateX: config.translateX,
            translateY: config.translateY,
            zoom: config.zoom,
            dimensions: config.dimensions,
            // Find pre-defined SVG elements from the markup
            previewCircle: document.getElementById(`${planeId}-preview-circle`),
            crosshairH: document.getElementById(`${planeId}-crosshair-h`),
            crosshairV: document.getElementById(`${planeId}-crosshair-v`),
            overlayGroup: document.getElementById(`${planeId}-overlay`)?.querySelector('g')
        };
        
        // Update circle color and stroke width
        const previewCircle = previewState.planes[planeId].previewCircle;
        if (previewCircle) {
            previewCircle.setAttribute("stroke", particleColor);
            previewCircle.setAttribute("stroke-width", 2 / config.zoom);
        }
        
        // Add mouse event listeners
        // We need to store the event handler functions to be able to remove them later
        const mouseMoveHandler = (e) => handleMouseMove(planeId, e);
        const mouseLeaveHandler = () => handleMouseLeave(planeId);
        
        // Store handlers in the plane config
        previewState.planes[planeId].mouseMoveHandler = mouseMoveHandler;
        previewState.planes[planeId].mouseLeaveHandler = mouseLeaveHandler;
        
        // Add new listeners
        containerElement.addEventListener('mousemove', mouseMoveHandler);
        containerElement.addEventListener('mouseleave', mouseLeaveHandler);
    }
}

/**
 * Update the preview system when parameters change
 * 
 * @param {boolean} isPickingActive Whether particle picking mode is active
 * @param {Object} viewPoint Current 3D coordinates where planes intersect
 * @param {string} particleColor Color to use for preview circles
 * @param {number} particleDiameter Diameter of particles in tomogram units
 * @param {number} pixelSize Pixel size for scaling
 */
export function updateParticlePreview(isPickingActive, viewPoint, particleColor, particleDiameter, pixelSize) {
    previewState.isActive = isPickingActive;
    previewState.viewPoint = viewPoint;
    previewState.particleColor = particleColor;
    previewState.particleDiameter = particleDiameter;
    previewState.pixelSize = pixelSize;
    
    // Update existing preview elements
    for (const planeId in previewState.planes) {
        const planeConfig = previewState.planes[planeId];
        const previewCircle = planeConfig.previewCircle;
        
        if (previewCircle) {
            previewCircle.setAttribute("stroke", particleColor);
        }
    }
    
    // If we have current plane and position, update all circles
    if (previewState.currentPlane) {
        updatePreviewCircles();
    }
}

/**
 * Update transforms for a specific plane
 * 
 * @param {string} planeId ID of the plane to update
 * @param {number} translateX X translation
 * @param {number} translateY Y translation
 * @param {number} zoom Zoom level
 */
export function updatePlaneTransform(planeId, translateX, translateY, zoom) {
    if (previewState.planes[planeId]) {
        const planeConfig = previewState.planes[planeId];
        
        // Update stored values
        planeConfig.translateX = translateX;
        planeConfig.translateY = translateY;
        planeConfig.zoom = zoom;
        
        // Update the overlay group transform (already exists in markup)
        if (planeConfig.overlayGroup) {
            planeConfig.overlayGroup.setAttribute(
                "transform", 
                `translate(${translateX}, ${translateY}) scale(${zoom})`
            );
        }
        
        // Update stroke widths based on zoom
        if (planeConfig.previewCircle) {
            planeConfig.previewCircle.setAttribute("stroke-width", 2 / zoom);
        }
        
        if (planeConfig.crosshairH && planeConfig.crosshairV) {
            planeConfig.crosshairH.setAttribute("stroke-width", 1 / zoom);
            planeConfig.crosshairV.setAttribute("stroke-width", 1 / zoom);
        }
        
        // If we have current plane and position, update all circles
        if (previewState.currentPlane) {
            updatePreviewCircles();
        }
    }
}

/**
 * Handle mouse movement over a plane
 * 
 * @param {string} planeId ID of the plane being hovered
 * @param {MouseEvent} event Mouse event
 */
function handleMouseMove(planeId, event) {
    const planeConfig = previewState.planes[planeId];
    if (!planeConfig) {
        return;
    }
    
    // Store current plane and mouse position
    previewState.currentPlane = planeId;
    previewState.mousePosition = { 
        x: event.offsetX, 
        y: event.offsetY 
    };
    
    // Convert mouse position to tomogram coordinates
    const tomogramCoords = screenToTomogramCoordinates(
        event.offsetX,
        event.offsetY,
        planeConfig
    );
    
    // Round and clamp to valid tomogram space
    const coords3D = {
        x: Math.round(Math.min(Math.max(0, tomogramCoords.x), planeConfig.dimensions.x - 1)),
        y: Math.round(Math.min(Math.max(0, tomogramCoords.y), planeConfig.dimensions.y - 1)),
        z: Math.round(Math.min(Math.max(0, tomogramCoords.z), planeConfig.dimensions.z - 1))
    };
    
    // Update crosshairs on all planes based on the 3D position
    updateAllCrosshairs(coords3D);
    
    // Update preview circles if picking is active
    if (previewState.isActive) {
        updatePreviewCircles();
    } else {
        hideAllPreviewCircles();
    }
}

/**
 * Handle mouse leaving a plane
 */
function handleMouseLeave(planeId) {
    previewState.currentPlane = null;
    hideAllPreviewCircles();
    
    // Only hide crosshairs if the user requests it
    // For our synchronized crosshairs, we'll keep them visible
    // hideAllCrosshairs();
}

/**
 * Hide all preview circles
 */
function hideAllPreviewCircles() {
    for (const planeId in previewState.planes) {
        const planeConfig = previewState.planes[planeId];
        if (planeConfig.previewCircle) {
            planeConfig.previewCircle.setAttribute("visibility", "hidden");
        }
    }
}

/**
 * Hide all crosshair lines
 */
function hideAllCrosshairs() {
    for (const planeId in previewState.planes) {
        const planeConfig = previewState.planes[planeId];
        if (planeConfig.crosshairH && planeConfig.crosshairV) {
            planeConfig.crosshairH.setAttribute("visibility", "hidden");
            planeConfig.crosshairV.setAttribute("visibility", "hidden");
        }
    }
}

/**
 * Update crosshair lines on all planes based on a 3D position
 * 
 * @param {Object} coords3D The 3D coordinates in tomogram space
 */
function updateAllCrosshairs(coords3D) {
    // Update crosshairs on all planes
    for (const planeId in previewState.planes) {
        const planeConfig = previewState.planes[planeId];
        if (!planeConfig.crosshairH || !planeConfig.crosshairV) continue;
        
        // Calculate SVG coordinates based on the 3D position and plane type
        let svgX, svgY;
        let width, height;
        
        if (planeConfig.type === 'XY') {
            // XY plane: X horizontal, Y vertical (flipped)
            width = planeConfig.dimensions.x;
            height = planeConfig.dimensions.y;
            svgX = coords3D.x;
            svgY = planeConfig.dimensions.y - 1 - coords3D.y;
        } else if (planeConfig.type === 'XZ') {
            // XZ plane: X horizontal, Z vertical (flipped)
            width = planeConfig.dimensions.x;
            height = planeConfig.dimensions.z;
            svgX = coords3D.x;
            svgY = planeConfig.dimensions.z - 1 - coords3D.z;
        } else { // ZY plane
            // ZY plane: Z horizontal, Y vertical (flipped)
            width = planeConfig.dimensions.z;
            height = planeConfig.dimensions.y;
            svgX = coords3D.z;
            svgY = planeConfig.dimensions.y - 1 - coords3D.y;
        }
        
        // Update horizontal line - goes across the full width
        planeConfig.crosshairH.setAttribute("x1", 0);
        planeConfig.crosshairH.setAttribute("y1", svgY);
        planeConfig.crosshairH.setAttribute("x2", width);
        planeConfig.crosshairH.setAttribute("y2", svgY);
        
        // Update vertical line - goes across the full height
        planeConfig.crosshairV.setAttribute("x1", svgX);
        planeConfig.crosshairV.setAttribute("y1", 0);
        planeConfig.crosshairV.setAttribute("x2", svgX);
        planeConfig.crosshairV.setAttribute("y2", height);
        
        // Make lines visible
        planeConfig.crosshairH.setAttribute("visibility", "visible");
        planeConfig.crosshairV.setAttribute("visibility", "visible");
    }
}

/**
 * Update all preview circles based on current mouse position
 */
function updatePreviewCircles() {
    if (!previewState.currentPlane || !previewState.isActive) {
        hideAllPreviewCircles();
        return;
    }
    
    // Get the current plane configuration
    const currentPlaneConfig = previewState.planes[previewState.currentPlane];
    if (!currentPlaneConfig) return;
    
    // Convert mouse position to tomogram coordinates
    const tomogramCoords = screenToTomogramCoordinates(
        previewState.mousePosition.x,
        previewState.mousePosition.y,
        currentPlaneConfig
    );
    
    // Round to nearest integer in tomogram space (enforcing boundaries)
    const roundedCoords = {
        x: Math.round(Math.min(Math.max(0, tomogramCoords.x), currentPlaneConfig.dimensions.x - 1)),
        y: Math.round(Math.min(Math.max(0, tomogramCoords.y), currentPlaneConfig.dimensions.y - 1)),
        z: Math.round(Math.min(Math.max(0, tomogramCoords.z), currentPlaneConfig.dimensions.z - 1))
    };
    
    // Update crosshairs on all planes with this 3D position
    updateAllCrosshairs(roundedCoords);
    
    // Calculate particle radius in tomogram units
    const radius = (previewState.particleDiameter / (2 * previewState.pixelSize));
    
    // Update preview circles on all planes
    for (const planeId in previewState.planes) {
        const planeConfig = previewState.planes[planeId];
        const previewCircle = planeConfig.previewCircle;
        
        if (!planeConfig || !previewCircle) continue;
        
        // Calculate circle position and size for this plane
        const { cx, cy, r } = calculateCircleForPlane(roundedCoords, radius, planeConfig);
        
        // Only show if intersection radius is positive
        if (r > 0) {
            previewCircle.setAttribute("cx", cx);
            previewCircle.setAttribute("cy", cy);
            previewCircle.setAttribute("r", r);
            previewCircle.setAttribute("visibility", "visible");
        } else {
            previewCircle.setAttribute("visibility", "hidden");
        }
    }
}

/**
 * Convert screen coordinates to tomogram coordinates
 * 
 * @param {number} screenX X position in screen coordinates
 * @param {number} screenY Y position in screen coordinates
 * @param {Object} planeConfig Configuration for the plane
 * @returns {Object} 3D coordinates in tomogram space
 */
function screenToTomogramCoordinates(screenX, screenY, planeConfig) {
    // Apply inverse of the transforms to get SVG coordinates (unscaled)
    const svgX = (screenX - planeConfig.translateX) / planeConfig.zoom;
    const svgY = (screenY - planeConfig.translateY) / planeConfig.zoom;
    
    // Initialize 3D coordinates in tomogram space
    let x = 0, y = 0, z = 0;
    
    // Get original tomogram dimensions from the plane configuration
    const volX = planeConfig.dimensions.x;
    const volY = planeConfig.dimensions.y;
    const volZ = planeConfig.dimensions.z;
    
    // Convert from SVG coordinates to tomogram coordinates based on plane type
    if (planeConfig.type === 'XY') {
        // XY plane: X is horizontal, Y is vertical (flipped due to SVG coordinate system)
        // Z is fixed at the current view point
        x = Math.min(Math.max(0, svgX), volX - 1);
        y = Math.min(Math.max(0, volY - 1 - svgY), volY - 1); // Flip Y and clamp
        z = previewState.viewPoint.z;
    } else if (planeConfig.type === 'XZ') {
        // XZ plane: X is horizontal, Z is vertical (flipped)
        // Y is fixed at the current view point
        x = Math.min(Math.max(0, svgX), volX - 1);
        y = previewState.viewPoint.y;
        z = Math.min(Math.max(0, volZ - 1 - svgY), volZ - 1); // Flip Z and clamp
    } else if (planeConfig.type === 'ZY') {
        // ZY plane: Z is horizontal, Y is vertical (flipped)
        // X is fixed at the current view point
        x = previewState.viewPoint.x;
        y = Math.min(Math.max(0, volY - 1 - svgY), volY - 1); // Flip Y and clamp
        z = Math.min(Math.max(0, svgX), volZ - 1);
    }
    
    return { x, y, z };
}

/**
 * Calculate circle parameters for a specific plane
 * 
 * @param {Object} coords 3D coordinates in tomogram space
 * @param {number} radius Particle radius in tomogram units
 * @param {Object} planeConfig Configuration for the plane
 * @returns {Object} Circle parameters for this plane (cx, cy, r)
 */
function calculateCircleForPlane(coords, radius, planeConfig) {
    let cx = 0, cy = 0;
    let intersectionSize = radius;
    
    // Get original tomogram dimensions from the plane configuration
    const volY = planeConfig.dimensions.y;
    const volZ = planeConfig.dimensions.z;
    
    // Calculate position on this plane in SVG coordinates
    if (planeConfig.type === 'XY') {
        // For XY plane, we need coords.x and coords.y (already in tomogram space)
        cx = coords.x;
        cy = volY - 1 - coords.y; // Convert Y from tomogram space to SVG space
        
        // Calculate intersection based on distance from Z plane
        const d = previewState.viewPoint.z - coords.z;
        if (Math.abs(d) <= radius) {
            intersectionSize = Math.sqrt(radius * radius - d * d);
        } else {
            intersectionSize = 0;
        }
    } else if (planeConfig.type === 'XZ') {
        // For XZ plane, we need coords.x and coords.z
        cx = coords.x;
        cy = volZ - 1 - coords.z; // Convert Z from tomogram space to SVG space
        
        // Calculate intersection based on distance from Y plane
        const d = previewState.viewPoint.y - coords.y;
        if (Math.abs(d) <= radius) {
            intersectionSize = Math.sqrt(radius * radius - d * d);
        } else {
            intersectionSize = 0;
        }
    } else if (planeConfig.type === 'ZY') {
        // For ZY plane, we need coords.z and coords.y
        cx = coords.z;
        cy = volY - 1 - coords.y; // Convert Y from tomogram space to SVG space
        
        // Calculate intersection based on distance from X plane
        const d = previewState.viewPoint.x - coords.x;
        if (Math.abs(d) <= radius) {
            intersectionSize = Math.sqrt(radius * radius - d * d);
        } else {
            intersectionSize = 0;
        }
    }
    
    return { cx, cy, r: intersectionSize };
}