// Store references to active tooltips and their position tracking info
const activeTooltips = new Map();

/**
 * Updates the position of all tooltip elements based on their target elements
 */
export function updateTooltipPositions() {
    const tooltips = document.querySelectorAll('.global-tooltip');
    
    // First, stop tracking for tooltips that are no longer visible
    for (const [tooltipId, trackingInfo] of activeTooltips.entries()) {
        const tooltip = document.getElementById(tooltipId);
        if (!tooltip) {// || tooltip.style.display === 'none') {
            // Tooltip is no longer visible, stop tracking
            if (trackingInfo.animationFrameId) {
                cancelAnimationFrame(trackingInfo.animationFrameId);
            }
            activeTooltips.delete(tooltipId);
        }
    }
    
    // Update/start tracking for visible tooltips
    tooltips.forEach(tooltip => {
        const tooltipId = tooltip.id;
        const targetId = tooltip.getAttribute('data-target-id');
        if (!targetId) return;
        
        const targetElement = document.getElementById(targetId);
        if (!targetElement) return;
        
        const placement = tooltip.getAttribute('data-placement') || 'auto';
        const offset = parseInt(tooltip.getAttribute('data-offset') || '8', 10);
        
        // Immediately position the tooltip
        positionTooltip(tooltip, targetElement, placement, offset);
        
        // If not already tracking this tooltip, start tracking
        if (!activeTooltips.has(tooltipId)) {
            const trackingInfo = {
                targetId,
                placement,
                offset,
                lastPositionX: targetElement.getBoundingClientRect().left,
                lastPositionY: targetElement.getBoundingClientRect().top,
                animationFrameId: null
            };
            
            // Start continuous position tracking
            function trackPosition() {
                const targetElement = document.getElementById(targetId);
                const tooltip = document.getElementById(tooltipId);
                
                if (!targetElement || !tooltip) {
                    // Target or tooltip is gone, stop tracking
                    activeTooltips.delete(tooltipId);
                    return;
                }
                
                // Check if target is visible using getComputedStyle
                const targetStyle = window.getComputedStyle(targetElement);
                if (targetStyle.display === 'none') {
                    // Hide tooltip when target is hidden (only if not already hidden)
                    if (tooltip.style.display !== 'none') {
                        tooltip.style.display = 'none';
                    }
                    
                    // Continue tracking - the element might become visible again
                    trackingInfo.animationFrameId = requestAnimationFrame(trackPosition);
                    return;
                }
                
                // Target is visible, show tooltip (positioning will be handled by positionTooltip)
                const targetRect = targetElement.getBoundingClientRect();

                // Check if position has changed
                if (targetRect.left !== trackingInfo.lastPositionX ||
                    targetRect.top !== trackingInfo.lastPositionY) {

                    // Update tooltip position
                    positionTooltip(tooltip, targetElement, placement, offset);

                    // Update last known position
                    trackingInfo.lastPositionX = targetRect.left;
                    trackingInfo.lastPositionY = targetRect.top;
                }

                // Continue tracking
                trackingInfo.animationFrameId = requestAnimationFrame(trackPosition);
            }
            
            // Start the tracking loop
            trackingInfo.animationFrameId = requestAnimationFrame(trackPosition);
            activeTooltips.set(tooltipId, trackingInfo);
        }
    });
}

/**
 * Positions a tooltip element relative to its target
 * @param {HTMLElement} tooltip - The tooltip element
 * @param {HTMLElement} target - The target element to position against
 * @param {string} placement - Placement preference (auto, top, right, bottom, left, horizontal, vertical)
 * @param {number} offset - Offset distance in pixels between target and tooltip
 */
function positionTooltip(tooltip, target, placement, offset) {
    // Check if target is visible using getComputedStyle (more reliable than just checking style.display)
    const targetStyle = window.getComputedStyle(target);
    if (targetStyle.display === 'none') {
        // Only hide if not already hidden, to prevent flickering
        if (tooltip.style.display !== 'none') {
            tooltip.style.display = 'none';
        }
        return;
    }
    
    // Determine if we need to make the tooltip temporarily visible for measurement
    const isHidden = tooltip.style.display === 'none' || window.getComputedStyle(tooltip).display === 'none';
    const hasNoDimensions = !tooltip.offsetWidth;
    const needsMeasurement = isHidden || hasNoDimensions;
    
    // Save current state
    let originalOpacity = null;
    
    if (needsMeasurement) {
        // Remember original opacity for hidden tooltips (for non-hidden, we'll keep the original)
        if (isHidden) {
            originalOpacity = tooltip.style.opacity;
            tooltip.style.opacity = '0'; // Only temporarily hide if it wasn't already visible
        }
        
        // Make visible for measurement
        tooltip.style.display = 'block';
        
        // Force layout/reflow to get proper dimensions
        tooltip.offsetHeight;
    }
    
    // Get dimensions
    const targetRect = target.getBoundingClientRect();
    const tooltipRect = tooltip.getBoundingClientRect();
    const windowWidth = window.innerWidth;
    const windowHeight = window.innerHeight;
    
    // Restore opacity for previously hidden tooltips
    if (originalOpacity !== null) {
        tooltip.style.opacity = originalOpacity || '1';
    }
    
    // Remove any existing positioning
    tooltip.style.top = '';
    tooltip.style.right = '';
    tooltip.style.bottom = '';
    tooltip.style.left = '';
    
    // Determine best placement based on placement preference
    let effectivePlacement = placement;
    
    // Available space in each direction
    const spaces = {
        top: targetRect.top,
        right: windowWidth - targetRect.right,
        bottom: windowHeight - targetRect.bottom,
        left: targetRect.left
    };
    
    // Required space for tooltip in each direction (with offset)
    const requiredSpace = {
        top: tooltipRect.height + offset,
        right: tooltipRect.width + offset,
        bottom: tooltipRect.height + offset,
        left: tooltipRect.width + offset
    };
    
    // Pairs of opposite sides for automatic flipping
    const opposites = {
        top: 'bottom',
        right: 'left',
        bottom: 'top',
        left: 'right'
    };
    
    if (placement === 'auto' || placement === 'horizontal' || placement === 'vertical') {
        // Special placement logic for auto/horizontal/vertical
        if (placement === 'horizontal') {
            // Only consider left and right placements
            if (spaces.right >= spaces.left) {
                effectivePlacement = 'right';
            } else {
                effectivePlacement = 'left';
            }
        } else if (placement === 'vertical') {
            // Only consider top and bottom placements
            if (spaces.bottom >= spaces.top) {
                effectivePlacement = 'bottom';
            } else {
                effectivePlacement = 'top';
            }
        } else {
            // Auto placement - choose direction with most space
            let maxSpace = 0;
            Object.entries(spaces).forEach(([side, space]) => {
                if (space > maxSpace) {
                    maxSpace = space;
                    effectivePlacement = side;
                }
            });
        }
    } else if (['top', 'right', 'bottom', 'left'].includes(placement)) {
        // For specific placement preferences, check if there's enough space
        // If not, flip to the opposite side
        if (spaces[placement] < requiredSpace[placement]) {
            // Not enough space on preferred side, check opposite side
            const opposite = opposites[placement];
            if (spaces[opposite] >= requiredSpace[opposite]) {
                // Use opposite side if it has enough space
                effectivePlacement = opposite;
            }
            // If opposite side doesn't have enough space either, stick with original placement
            // and let the viewport clamping logic handle it
        }
    }
    
    // Position based on placement
    let top, left;
    
    switch (effectivePlacement) {
        case 'top':
            top = targetRect.top - tooltipRect.height - offset;
            left = targetRect.left + (targetRect.width - tooltipRect.width) / 2;
            break;
            
        case 'right':
            top = targetRect.top + (targetRect.height - tooltipRect.height) / 2;
            left = targetRect.right + offset;
            break;
            
        case 'bottom':
            top = targetRect.bottom + offset;
            left = targetRect.left + (targetRect.width - tooltipRect.width) / 2;
            break;
            
        case 'left':
            top = targetRect.top + (targetRect.height - tooltipRect.height) / 2;
            left = targetRect.left - tooltipRect.width - offset;
            break;
    }
    
    // Apply position, ensuring tooltip stays within viewport
    top = Math.max(0, Math.min(windowHeight - tooltipRect.height, top));
    left = Math.max(0, Math.min(windowWidth - tooltipRect.width, left));
    
    tooltip.style.position = 'fixed';
    tooltip.style.top = `${top}px`;
    tooltip.style.left = `${left}px`;
    
    // Add appropriate class for styling arrow
    tooltip.className = tooltip.className.replace(/\bplacement-\w+\b/g, '');
    tooltip.classList.add(`placement-${effectivePlacement}`);
    
    // Make tooltip visible
    tooltip.style.opacity = '1';
}

/**
 * Cleans up all tooltip tracking when component is unmounted or page is unloaded
 */
export function cleanupTooltipTracking() {
    for (const [tooltipId, trackingInfo] of activeTooltips.entries()) {
        if (trackingInfo.animationFrameId) {
            cancelAnimationFrame(trackingInfo.animationFrameId);
        }
    }
    activeTooltips.clear();
}