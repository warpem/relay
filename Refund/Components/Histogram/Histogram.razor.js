const histograms = new Map();

/** Debounce helper with cancel support */
function debounce(fn, wait) {
    let t;
    function debounced(...args) {
        clearTimeout(t);
        t = setTimeout(() => fn.apply(this, args), wait);
    }
    
    // Add cancel method
    debounced.cancel = function() {
        clearTimeout(t);
    };
    
    return debounced;
}

class Histogram {
    constructor(histogramId, rangeId, rangeStartHandleId, rangeEndHandleId, dotNetRef) {
        // DOM references
        this.svg = document.getElementById(histogramId);
        this.id = histogramId;
        this.rangeSelection = document.getElementById(rangeId);
        this.rangeStartHandle = document.getElementById(rangeStartHandleId);
        this.rangeEndHandle = document.getElementById(rangeEndHandleId);
        this.dotNetRef = dotNetRef;

        // Config
        this.binSizes = [];
        this.secondaryBinSizes = [];
        this.minRange = 0;
        this.maxRange = 1;
        this.color = '#0078D4';
        this.secondaryColor = '#8A2BE2';
        this.rangeSelectionEnabled = false;
        this.selectedRangeStart = 0;
        this.selectedRangeEnd = 1;
        this.stepSize = 0.1;
        this.minGap = 1;
        this.disabled = false;
        
        // Debounced method for notifying range changes
        this._debouncedNotifyRangeChanged = debounce(() => {
            this.notifyRangeChanged();
        }, 50);

        // State
        this.isDraggingStart = false;
        this.isDraggingEnd = false;
        this.svgRect = null;
        this.barWidth = 0;

        // Bind event handlers
        this.handleMouseMove = this.handleMouseMove.bind(this);
        this.handleMouseUp = this.handleMouseUp.bind(this);
        this.handleMouseLeave = this.handleMouseLeave.bind(this);
        this.handleResize = debounce(this.handleResize.bind(this), 100);
        
        // Set up event listeners for range handles
        if (this.rangeStartHandle) {
            this.rangeStartHandle.addEventListener('mousedown', (e) => {
                if (this.disabled) return;
                this.isDraggingStart = true;
                this.rangeStartHandle.classList.add('dragging');
                e.preventDefault();
            });
        }
        
        if (this.rangeEndHandle) {
            this.rangeEndHandle.addEventListener('mousedown', (e) => {
                if (this.disabled) return;
                this.isDraggingEnd = true;
                this.rangeEndHandle.classList.add('dragging');
                e.preventDefault();
            });
        }
        
        // Global mouse events
        document.addEventListener('mousemove', this.handleMouseMove);
        document.addEventListener('mouseup', this.handleMouseUp);
        
        // Mouse leave event on the entire histogram container (parent of SVG)
        if (this.svg && this.svg.parentElement) {
            this.svg.parentElement.addEventListener('mouseleave', this.handleMouseLeave);
        }
        
        // Resize observer
        setTimeout(() => {
            this.resizeObserver = new ResizeObserver(() => {
                this.handleResize();
            });
            if (this.svg.parentElement) {
                this.resizeObserver.observe(this.svg.parentElement);
            }
        }, 30);
    }

    update(config) {
        if (!config) return;
        
        // Update configuration
        this.binSizes = config.binSizes || [];
        this.secondaryBinSizes = config.secondaryBinSizes || [];
        this.minRange = config.minRange;
        this.maxRange = config.maxRange;
        this.color = config.color || '#0078D4';
        this.secondaryColor = config.secondaryColor || '#8A2BE2';
        this.rangeSelectionEnabled = config.rangeSelectionEnabled || false;
        this.selectedRangeStart = config.selectedRangeStart;
        this.selectedRangeEnd = config.selectedRangeEnd;
        this.stepSize = config.stepSize || 0.1;
        this.minGap = config.minGap || 1;
        this.disabled = config.disabled || false;
        
        // Render histogram and range selection
        this.renderHistogram();
        this.updateRangeSelection();
    }

    renderHistogram() {
        // Clear existing content
        while (this.svg.firstChild) {
            this.svg.removeChild(this.svg.firstChild);
        }
        
        // Get SVG dimensions
        this.svgRect = this.svg.getBoundingClientRect();
        const width = this.svgRect.width;
        const height = this.svgRect.height;
        
        // Skip if dimensions are too small or no data
        if (width < 10 || height < 10 || this.binSizes.length === 0) return;
        
        // Calculate bar width (no gaps)
        this.barWidth = width / this.binSizes.length;
        
        // Find the maximum bin size across both datasets for scaling
        const maxBinSize = Math.max(
            ...this.binSizes,
            ...(this.secondaryBinSizes.length > 0 ? this.secondaryBinSizes : [0])
        );
        
        // Default to 1 if all bins are empty
        const normFactor = maxBinSize > 0 ? maxBinSize : 1;
        
        // Create a group for primary bars
        const primaryGroup = document.createElementNS('http://www.w3.org/2000/svg', 'g');
        primaryGroup.setAttribute('class', 'histogram-bars-primary');
        this.svg.appendChild(primaryGroup);
        
        // Create primary bars
        for (let i = 0; i < this.binSizes.length; i++) {
            const barHeight = this.binSizes[i] / normFactor * height;
            const x = i * this.barWidth;
            const y = height - barHeight;
            
            const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
            rect.setAttribute('class', 'histogram-bar primary');
            rect.setAttribute('x', x);
            rect.setAttribute('y', y);
            rect.setAttribute('width', this.barWidth);
            rect.setAttribute('height', barHeight);
            rect.setAttribute('fill', this.color);
            rect.setAttribute('fill-opacity', '0.5'); // Make semi-transparent for overlapping
            rect.setAttribute('data-index', i);
            
            // Add tooltip for bar value range
            const binStart = this.getBinValue(i);
            const binEnd = this.getBinValue(i + 1);
            rect.setAttribute('title', `Primary: ${binStart} - ${binEnd}`);
            
            primaryGroup.appendChild(rect);
        }
        
        // Create secondary bars if they exist
        if (this.secondaryBinSizes.length > 0) {
            // Create a group for secondary bars
            const secondaryGroup = document.createElementNS('http://www.w3.org/2000/svg', 'g');
            secondaryGroup.setAttribute('class', 'histogram-bars-secondary');
            this.svg.appendChild(secondaryGroup);
            
            // Create secondary bars
            for (let i = 0; i < this.secondaryBinSizes.length && i < this.binSizes.length; i++) {
                const barHeight = this.secondaryBinSizes[i] / normFactor * height;
                const x = i * this.barWidth;
                const y = height - barHeight;
                
                const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
                rect.setAttribute('class', 'histogram-bar secondary');
                rect.setAttribute('x', x);
                rect.setAttribute('y', y);
                rect.setAttribute('width', this.barWidth);
                rect.setAttribute('height', barHeight);
                rect.setAttribute('fill', this.secondaryColor);
                rect.setAttribute('fill-opacity', '0.5'); // Make semi-transparent for overlapping
                rect.setAttribute('data-index', i);
                
                // Add tooltip for bar value range
                const binStart = this.getBinValue(i);
                const binEnd = this.getBinValue(i + 1);
                rect.setAttribute('title', `Secondary: ${binStart} - ${binEnd}`);
                
                secondaryGroup.appendChild(rect);
            }
        }
    }
    
    getBinValue(index) {
        if (this.binSizes.length === 0) return 0;
        
        // Calculate the value at the given bin index
        const range = this.maxRange - this.minRange;
        const binWidth = range / this.binSizes.length;
        return this.minRange + (binWidth * index);
    }
    
    // Get the bin width
    getBinWidth() {
        if (this.binSizes.length <= 1) return 1;
        
        const range = this.maxRange - this.minRange;
        return range / this.binSizes.length;
    }
    
    // Get effective step size, using bin width if step size is 0 or negative
    getEffectiveStepSize() {
        if (this.stepSize <= 0) {
            return this.getBinWidth();
        }
        return this.stepSize;
    }
    
    updateRangeSelection() {
        if (!this.rangeSelectionEnabled || !this.rangeSelection) return;
        
        // Get the range selection area dimensions
        const range = this.maxRange - this.minRange;
        if (range <= 0) return;
        
        // Calculate positions as percentages
        const startPct = (this.selectedRangeStart - this.minRange) / range;
        const endPct = (this.selectedRangeEnd - this.minRange) / range;
        
        // Apply to range background
        this.rangeSelection.style.display = 'block';
        this.rangeSelection.style.left = `${startPct * 100}%`;
        this.rangeSelection.style.width = `${(endPct - startPct) * 100}%`;
        
        // Update handle positions
        if (this.rangeStartHandle) {
            this.rangeStartHandle.style.left = `${startPct * 100}%`;
        }
        
        if (this.rangeEndHandle) {
            this.rangeEndHandle.style.right = `${(1 - endPct) * 100}%`;
        }
    }
    
    handleMouseMove(e) {
        if (this.disabled || (!this.isDraggingStart && !this.isDraggingEnd)) return;
        if (!this.svgRect) this.svgRect = this.svg.getBoundingClientRect();
        
        // Get mouse position relative to SVG
        const relX = (e.clientX - this.svgRect.left) / this.svgRect.width;
        const clampedX = Math.max(0, Math.min(1, relX));
        
        // Convert to value space
        const range = this.maxRange - this.minRange;
        let newValue = this.minRange + (clampedX * range);
        
        // Apply step size (snap to grid)
        const effectiveStepSize = this.getEffectiveStepSize();
        newValue = Math.round(newValue / effectiveStepSize) * effectiveStepSize;
        
        // Handle dragging of different edges
        if (this.isDraggingStart) {
            // Ensure start doesn't exceed end - minGap
            const minGapValue = this.minGap * this.getEffectiveStepSize();
            const maxStart = this.selectedRangeEnd - minGapValue;
            newValue = Math.min(newValue, maxStart);
            
            if (newValue !== this.selectedRangeStart) {
                this.selectedRangeStart = newValue;
                this.updateRangeSelection();
                // Don't notify during drag - only on mouse up or mouse leave
            }
        } else if (this.isDraggingEnd) {
            // Ensure end doesn't go below start + minGap
            const minGapValue = this.minGap * this.getEffectiveStepSize();
            const minEnd = this.selectedRangeStart + minGapValue;
            newValue = Math.max(newValue, minEnd);
            
            if (newValue !== this.selectedRangeEnd) {
                this.selectedRangeEnd = newValue;
                this.updateRangeSelection();
                // Don't notify during drag - only on mouse up or mouse leave
            }
        }
    }
    
    handleMouseUp() {
        let wasDragging = this.isDraggingStart || this.isDraggingEnd;
        
        if (this.isDraggingStart) {
            this.isDraggingStart = false;
            if (this.rangeStartHandle) {
                this.rangeStartHandle.classList.remove('dragging');
            }
        }
        
        if (this.isDraggingEnd) {
            this.isDraggingEnd = false;
            if (this.rangeEndHandle) {
                this.rangeEndHandle.classList.remove('dragging');
            }
        }
        
        // If we were dragging, send a final non-debounced update
        if (wasDragging) {
            // Cancel any pending debounced updates
            if (this._debouncedNotifyRangeChanged.cancel) {
                this._debouncedNotifyRangeChanged.cancel();
            }
            
            // Send the final update immediately
            this.notifyRangeChanged();
        }
    }
    
    handleMouseLeave() {
        // Stop dragging when mouse leaves the histogram component
        let wasDragging = this.isDraggingStart || this.isDraggingEnd;
        
        if (this.isDraggingStart) {
            this.isDraggingStart = false;
            if (this.rangeStartHandle) {
                this.rangeStartHandle.classList.remove('dragging');
            }
        }
        
        if (this.isDraggingEnd) {
            this.isDraggingEnd = false;
            if (this.rangeEndHandle) {
                this.rangeEndHandle.classList.remove('dragging');
            }
        }
        
        // If we were dragging, send a final update
        if (wasDragging) {
            // Cancel any pending debounced updates
            if (this._debouncedNotifyRangeChanged.cancel) {
                this._debouncedNotifyRangeChanged.cancel();
            }
            
            // Send the final update immediately
            this.notifyRangeChanged();
        }
    }
    
    handleResize() {
        this.svgRect = null; // Force recalculation of dimensions
        this.renderHistogram();
        this.updateRangeSelection();
    }
    
    notifyRangeChanged() {
        if (this.dotNetRef) {
            this.dotNetRef.invokeMethodAsync('OnRangeChanged', 
                this.selectedRangeStart, 
                this.selectedRangeEnd);
        }
    }
    
    dispose() {
        // Remove event listeners
        document.removeEventListener('mousemove', this.handleMouseMove);
        document.removeEventListener('mouseup', this.handleMouseUp);
        
        // Remove mouse leave event listener
        if (this.svg && this.svg.parentElement) {
            this.svg.parentElement.removeEventListener('mouseleave', this.handleMouseLeave);
        }
        
        // Disconnect resize observer
        if (this.resizeObserver) {
            this.resizeObserver.disconnect();
        }
    }
}

/** Blazor export: initialize a new histogram */
export function initializeHistogram(
    histogramId,
    rangeId,
    rangeStartHandleId,
    rangeEndHandleId,
    dotNetRef,
    config
) {
    const histogram = new Histogram(
        histogramId,
        rangeId,
        rangeStartHandleId,
        rangeEndHandleId,
        dotNetRef
    );
    histograms.set(histogramId, histogram);
    histogram.update(config);
}

/** Blazor export: update an existing histogram */
export function updateHistogram(histogramId, config) {
    const histogram = histograms.get(histogramId);
    if (histogram) histogram.update(config);
}

/** Blazor export: dispose a histogram */
export function disposeHistogram(histogramId) {
    const histogram = histograms.get(histogramId);
    if (histogram) {
        histogram.dispose();
        histograms.delete(histogramId);
    }
}