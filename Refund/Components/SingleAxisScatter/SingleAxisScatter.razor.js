const scatterPlots = new Map();

/** Simple debounce helper */
function debounce(fn, wait) {
    let t;
    return function (...args) {
        clearTimeout(t);
        t = setTimeout(() => fn.apply(this, args), wait);
    };
}

/** Round the axis bounds to nice round numbers for labeling. */
function computeNiceAxisBounds(dataMin, dataMax) {
    if (dataMax <= dataMin) {
        return {min: dataMin - 1, max: dataMin + 1, center: dataMin};
    }
    const range = dataMax - dataMin;
    const order = Math.floor(Math.log10(range));
    const base = Math.pow(10, order);

    const niceMin = Math.floor(dataMin / base) * base;
    const niceMax = Math.ceil(dataMax / base) * base;
    const center = (niceMin + niceMax) * 0.5;
    return {min: niceMin, max: niceMax, center};
}

/** Simple numeric formatter for axis labels. */
function formatNumber(val) {
    const abs = Math.abs(val);
    if (abs < 10) return val.toFixed(2);
    if (abs < 100) return val.toFixed(1);
    return val.toFixed(0);
}

class ScatterPlot {
    constructor(
        canvasId,
        histogramId,
        highlightCircleId,
        rangeHighlightId,
        topLineId,
        centerLineId,
        bottomLineId,
        topLabelId,
        centerLabelId,
        bottomLabelId,
        dotNetRef
    ) {
        // DOM references
        this.canvas = document.getElementById(canvasId);
        this.ctx = this.canvas.getContext('2d');
        this.id = canvasId; // Store ID for referencing

        this.histogramSvg = document.getElementById(histogramId);
        this.highlightCircle = document.getElementById(highlightCircleId);
        this.rangeHighlight = document.getElementById(rangeHighlightId);

        // Set up debounced blazor calls
        this._debouncedPointHover = debounce((idx) => {
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync("OnPointHovered", idx);
            }
        }, 50);

        this.topLine = document.getElementById(topLineId);
        this.centerLine = document.getElementById(centerLineId);
        this.bottomLine = document.getElementById(bottomLineId);

        this.topLabel = document.getElementById(topLabelId);
        this.centerLabel = document.getElementById(centerLabelId);
        this.bottomLabel = document.getElementById(bottomLabelId);

        this.dotNetRef = dotNetRef;

        // Config from Blazor
        this.pointCollections = [];   // Array of collections of points
        this.pointRadius = 2.5;
        this.zoom = 1.0;
        this.rangeMin = 0;
        this.rangeMax = 1;
        this.opacity = 1.0;
        this.yAxisMin = null;  // Optional min Y value
        this.yAxisMax = null;  // Optional max Y value

        // Scroll position and state
        this.scrollPosition = 0;
        this.maxScrollPosition = 0;
        this.plotArea = null;

        // Data range
        this.dataMin = 0;
        this.dataMax = 1;
        // Plot range (with margin)
        this.plotMin = 0;
        this.plotMax = 1;
        // “Nice” axis range for labeling
        this.axisMin = 0;
        this.axisMax = 1;
        this.axisCenter = 0;

        // Dimensions
        this.currentWidth = 0;
        this.currentHeight = 0;

        this.highlightedIndex = -1;

        // Steps for layout
        this.stepX = 0;
        this.offsetX = 0;
        this.stepY = 0;

        // Debounced trackpad zoom
        this._accumWheelDelta = 0;
        this._debounceWheel = debounce(() => this.applyWheelZoom(), 100);

        // Bind methods
        this.handleMouseMove = this.handleMouseMove.bind(this);
        this.handleMouseLeave = this.handleMouseLeave.bind(this);
        this.handleClick = this.handleClick.bind(this);
        this.handleWheel = this.handleWheel.bind(this);
        this.handleResize = debounce(this.handleResize.bind(this), 100);
        this.updateScrollFromMousePosition = this.updateScrollFromMousePosition.bind(this);

        // Attach listeners
        this.canvas.addEventListener('mousemove', this.handleMouseMove);
        this.canvas.addEventListener('mouseleave', this.handleMouseLeave);
        this.canvas.addEventListener('click', this.handleClick);
        this.canvas.addEventListener('wheel', this.handleWheel, {passive: false});

        // Observe .canvas-container and .plot-area so any dimension change triggers handleResize
        setTimeout(() => {
            const container = this.canvas.closest('.canvas-container');
            this.plotArea = this.canvas.closest('.plot-area');

            if (container) {
                this.resizeObserver = new ResizeObserver(() => {
                    this.handleResize();
                });
                this.resizeObserver.observe(container);
            }

            if (this.plotArea) {
                this.resizeObserverPlotArea = new ResizeObserver(() => {
                    this.handleResize();
                });
                this.resizeObserverPlotArea.observe(this.plotArea);
            }
            
            // Register with the global sync system
            if (window.scatterHighlightSync) {
                window.scatterHighlightSync.registerScatterPlot(this.id, this);
            }
        }, 30);
    }

    /** Called by Blazor with updated config. */
    update(config) {
        if (!config) return;
        
        // Check if we have the legacy rawPoints or the new pointCollections
        if (config.rawPoints) {
            // If only rawPoints is provided (legacy mode), convert to collections format
            this.pointCollections = [config.rawPoints];
        } else if (config.pointCollections) {
            // Use the new multiple collections format
            this.pointCollections = config.pointCollections;
        } else {
            // No points provided
            this.pointCollections = [];
        }
        
        // Make sure names match the JSON from Blazor
        this.pointRadius = config.pointRadius;
        this.zoom = config.zoom;
        this.rangeMin = config.rangeMin;
        this.rangeMax = config.rangeMax;
        this.opacity = config.opacity;
        this.yAxisMin = config.yAxisMin;
        this.yAxisMax = config.yAxisMax;

        this.computeDataRange();
        this.computeNiceAxisBounds();  // First compute nice axis bounds for labels
        this.computePlotRange();       // Then add padding to get the plot range

        this.recalcSize();

        this.renderPoints();
        this.renderHistogram();
        this.renderAxisLinesAndLabels();
        this.renderRangeHighlight();
        
        // If we have an active highlight, check if it needs to be updated or cleared
        if (this.highlightedIndex >= 0) {
            // Check if the highlighted index is still valid in any collection
            let validIndex = false;
            for (const collection of this.pointCollections) {
                if (this.highlightedIndex < collection.length) {
                    validIndex = true;
                    break;
                }
            }
            
            if (!validIndex) {
                // Clear highlight if the highlighted index no longer exists in any collection
                this.clearHighlight();
            } else {
                // Update highlight position for all points at this index
                this.updateHighlightCirclePosition();
            }
        }
    }

    computeDataRange() {
        let mn = Number.POSITIVE_INFINITY;
        let mx = Number.NEGATIVE_INFINITY;
        
        // Calculate min/max from data across all collections
        if (this.pointCollections.length > 0) {
            for (const collection of this.pointCollections) {
                for (const p of collection) {
                    if (p.val !== null) {
                        if (p.val < mn) mn = p.val;
                        if (p.val > mx) mx = p.val;
                    }
                }
            }
            
            if (mn === Number.POSITIVE_INFINITY) {
                mn = 0;
                mx = 1;
            }
            
            // Special case: all points have the same Y value
            if (mx === mn) {
                // Store the original value to use for special axis handling
                this.singleValue = mn;
                
                // Create a range of ±1 from the single value
                mn = mn - 1;
                mx = mx + 1;
            } else {
                // Not all the same value, clear the flag
                this.singleValue = null;
            }
        } else {
            mn = 0;
            mx = 1;
            this.singleValue = null;
        }
        
        // Use provided values if specified
        this.dataMin = this.yAxisMin !== null ? this.yAxisMin : mn;
        this.dataMax = this.yAxisMax !== null ? this.yAxisMax : mx;
    }

    computeNiceAxisBounds() {
        // Special case: all points have the same Y value
        if (this.singleValue !== null) {
            // Use the exact value as the center, and ±1 as the min/max
            this.axisMin = this.singleValue - 1;
            this.axisMax = this.singleValue + 1;
            this.axisCenter = this.singleValue;
        } else {
            // Normal case: Get nice round bounds for the axis labels
            const {min, max, center} = computeNiceAxisBounds(this.dataMin, this.dataMax);
            this.axisMin = min;
            this.axisMax = max;
            this.axisCenter = center;
        }
    }

    computePlotRange() {
        // Add padding to the nice axis bounds to ensure grid lines are well inside the plot
        // Use 8% padding beyond the nice bounds
        const r = this.axisMax - this.axisMin;
        const margin = r * 0.08;
        this.plotMin = this.axisMin - margin;
        this.plotMax = this.axisMax + margin;
    }

    recalcSize() {
        const container = this.canvas.closest('.canvas-container');
        if (!container) return;

        // Use offsetHeight to account for scroll bars
        let w = container.offsetWidth;
        let h = container.offsetHeight;
        if (w < 10) w = 600;
        if (h < 10) h = 300;

        this.currentWidth = w;
        this.currentHeight = h;

        this.canvas.width = w * this.zoom;
        this.canvas.height = h;

        // Calculate max scroll position
        this.maxScrollPosition = Math.max(0, this.canvas.width - this.currentWidth);

        // If zoom is 1.0, always reset scroll position to 0
        if (this.zoom === 1.0) {
            this.scrollPosition = 0;
        } else {
            // Otherwise, ensure current scroll position is within bounds
            this.scrollPosition = Math.min(this.scrollPosition, this.maxScrollPosition);
        }

        // Apply scroll position to the canvas style
        this.updateCanvasPosition();
    }

    /**
     * Updates highlight circles for all points at the highlighted index
     * Creates a highlight for each valid point at the same index across all collections
     */
    updateHighlightCirclePosition() {
        // Skip if no highlight or no data
        if (this.highlightedIndex < 0 || this.pointCollections.length === 0) return;
        
        // Get the parent container for this plot
        const container = this.highlightCircle.parentNode;
        if (!container) return;
        
        // Clear existing temporary highlights in this plot
        this.clearTemporaryHighlights(container);
        
        // Collect all valid points at the highlighted index
        const validPoints = this.getValidPointsAtIndex(this.highlightedIndex);
        
        // Exit if no valid points found
        if (validPoints.length === 0) {
            this.highlightCircle.style.display = "none";
            return;
        }
        
        // Create highlight circles for each valid point
        this.createHighlightCircles(validPoints, container);
    }
    
    /**
     * Removes all temporary highlight circles in the specified container
     */
    clearTemporaryHighlights(container) {
        container.querySelectorAll('.temp-highlight-circle').forEach(el => el.remove());
    }
    
    /**
     * Returns all valid points at the specified index across all collections
     */
    getValidPointsAtIndex(index) {
        const validPoints = [];
        
        for (let collectionIndex = 0; collectionIndex < this.pointCollections.length; collectionIndex++) {
            const collection = this.pointCollections[collectionIndex];
            
            if (index < collection.length) {
                const point = collection[index];
                if (point.val !== null) {
                    validPoints.push({
                        point: point,
                        collectionIndex: collectionIndex
                    });
                }
            }
        }
        
        return validPoints;
    }
    
    /**
     * Creates highlight circles for all valid points
     * Uses the main highlight circle for the first point and creates temporary circles for the rest
     */
    createHighlightCircles(validPoints, container) {
        if (validPoints.length === 0) return;
        
        const diam = this.pointRadius * 2 + 4;
        let mainCircleUsed = false;
        
        validPoints.forEach(({ point }) => {
            // Calculate point position
            const px = point.index * this.stepX + this.offsetX;
            const py = (this.plotMax - point.val) * this.stepY;
            const left = (px - this.scrollPosition - diam / 2) + "px";
            const top = (py - diam / 2) + "px";
            
            if (!mainCircleUsed) {
                // Use main highlight circle for first point
                this.setHighlightCircleStyle(this.highlightCircle, left, top, diam);
                mainCircleUsed = true;
            } else {
                // Create temporary highlight for additional points
                const tempCircle = this.createTemporaryHighlightCircle(left, top, diam);
                container.appendChild(tempCircle);
            }
        });
    }
    
    /**
     * Updates the style of a highlight circle element
     */
    setHighlightCircleStyle(circle, left, top, diameter) {
        circle.style.display = "block";
        circle.style.left = left;
        circle.style.top = top;
        circle.style.width = diameter + "px";
        circle.style.height = diameter + "px";
    }
    
    /**
     * Creates a new temporary highlight circle
     */
    createTemporaryHighlightCircle(left, top, diameter) {
        const tempCircle = document.createElement('div');
        tempCircle.className = 'highlight-circle temp-highlight-circle';
        tempCircle.style.position = 'absolute';
        this.setHighlightCircleStyle(tempCircle, left, top, diameter);
        return tempCircle;
    }
    
    updateCanvasPosition() {
        if (!this.canvas) return;
        this.canvas.style.transform = `translateX(${-this.scrollPosition}px)`;
        
        // Update highlight circle position if there is an active highlight
        this.updateHighlightCirclePosition();
    }

    renderPoints() {
        const ctx = this.ctx;
        ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);

        if (this.pointCollections.length === 0) return;
        
        const dataRange = this.plotMax - this.plotMin;
        if (dataRange <= 0) return;

        // Calculate step sizes based on first collection's length
        // (assuming all collections have the same length)
        const n = this.pointCollections[0].length || 0;
        this.stepX = this.canvas.width / n;
        this.offsetX = this.stepX / 2;
        this.stepY = this.canvas.height / dataRange;

        // Draw each collection
        for (const collection of this.pointCollections) {
            for (const p of collection) {
                if (p.val === null) continue;
                const x = p.index * this.stepX + this.offsetX;
                
                // Calculate Y position - ensuring that when all points have the same value
                // they're centered in the plot
                const y = (this.plotMax - p.val) * this.stepY;
                
                ctx.beginPath();
                ctx.arc(x, y, this.pointRadius, 0, 2 * Math.PI);
                ctx.fillStyle = `rgba(${p.rgb[0]},${p.rgb[1]},${p.rgb[2]},${this.opacity})`;
                ctx.fill();
            }
        }
    }

    renderHistogram() {
        if (!this.histogramSvg) return;
        while (this.histogramSvg.firstChild) {
            this.histogramSvg.removeChild(this.histogramSvg.firstChild);
        }
        if (this.pointCollections.length === 0) return;

        // Collect all valid values from all collections
        const validVals = [];
        for (const collection of this.pointCollections) {
            validVals.push(...collection.filter(p => p.val !== null).map(p => p.val));
        }
        
        if (validVals.length === 0) return;

        const range = this.plotMax - this.plotMin;
        if (range <= 0) return;

        const binCount = 50;
        let bins = new Array(binCount).fill(0);
        validVals.forEach(v => {
            const t = (v - this.plotMin) / range;
            let idx = Math.floor(t * binCount);
            if (idx < 0) idx = 0;
            if (idx >= binCount) idx = binCount - 1;
            bins[idx]++;
        });

        // smoothing
        const kernel = [0.11, 0.37, 0.78, 1, 0.78, 0.37, 0.11];
        const smoothed = new Array(binCount).fill(0);
        for (let i = 0; i < binCount; i++) {
            let sum = 0;
            let w = 0;
            for (let j = 0; j < kernel.length; j++) {
                const ix = i - 3 + j;
                if (ix >= 0 && ix < binCount) {
                    sum += bins[ix] * kernel[j];
                    w += kernel[j];
                }
            }
            smoothed[i] = (w > 0) ? sum / w : 0;
        }

        const maxVal = Math.max(...smoothed);
        if (maxVal > 0) {
            for (let i = 0; i < binCount; i++) {
                smoothed[i] = (smoothed[i] / maxVal) * 16;
            }
        }

        const svgW = this.histogramSvg.clientWidth;
        const svgH = this.histogramSvg.clientHeight;
        if (svgW < 10 || svgH < 10) return;

        const poly = document.createElementNS("http://www.w3.org/2000/svg", "polygon");
        poly.setAttribute("style", "fill: gray; opacity: 0.2;");

        const pts = [];
        pts.push(`${svgW},${svgH}`);
        for (let i = 0; i < binCount; i++) {
            const val = smoothed[i];
            const y = svgH - (i / (binCount - 1)) * svgH;
            const x = Math.max(0, svgW - val);
            pts.push(`${x},${y}`);
        }
        pts.push(`${svgW},0`);
        poly.setAttribute("points", pts.join(" "));
        this.histogramSvg.appendChild(poly);
    }

    renderAxisLinesAndLabels() {
        if (!this.topLine || !this.centerLine || !this.bottomLine) return;
        if (!this.topLabel || !this.centerLabel || !this.bottomLabel) return;

        const totalWidth = Math.max(this.canvas.width, this.currentWidth);
        [this.topLine, this.centerLine, this.bottomLine].forEach(line => {
            line.style.width = totalWidth + "px";
        });

        const pRange = this.plotMax - this.plotMin;
        if (pRange <= 0) return;

        // place lines at axisMax/axisCenter/axisMin
        const topY = (this.plotMax - this.axisMax) / pRange * this.currentHeight;
        const centerY = (this.plotMax - this.axisCenter) / pRange * this.currentHeight;
        const bottomY = (this.plotMax - this.axisMin) / pRange * this.currentHeight;

        this.topLine.style.top = `${topY}px`;
        this.centerLine.style.top = `${centerY}px`;
        this.bottomLine.style.top = `${bottomY}px`;

        // labels
        this.topLabel.textContent = formatNumber(this.axisMax);
        this.centerLabel.textContent = formatNumber(this.axisCenter);
        this.bottomLabel.textContent = formatNumber(this.axisMin);
    }

    renderRangeHighlight() {
        if (!this.rangeHighlight) return;
        const pRange = this.plotMax - this.plotMin;
        if (pRange <= 0) {
            this.rangeHighlight.style.height = "0px";
            return;
        }
        const hiMin = Math.max(this.plotMin, this.rangeMin);
        const hiMax = Math.min(this.plotMax, this.rangeMax);
        const topPx = (this.plotMax - hiMax) / pRange * this.currentHeight;
        const heightPx = (hiMax - hiMin) / pRange * this.currentHeight;

        const totalWidth = Math.max(this.canvas.width, this.currentWidth);
        this.rangeHighlight.style.top = `${topPx}px`;
        this.rangeHighlight.style.height = `${Math.max(0, heightPx)}px`;
        this.rangeHighlight.style.width = `${totalWidth}px`;
    }

    // =================== EVENTS ===================
    // Helper method to set scroll position and update canvas
    applyScrollChange(newScrollPosition, notifySync = true) {
        // Always allow setting scroll position to 0, even at zoom level 1
        // (This fixes the bug when zooming back to 1.0)
        if (this.zoom <= 1 && newScrollPosition !== 0) return false;
        
        // Ensure scroll position is within valid range
        newScrollPosition = Math.max(0, Math.min(newScrollPosition, this.maxScrollPosition));
        
        // Only update if the change is significant
        if (Math.abs(this.scrollPosition - newScrollPosition) > 0.5) {
            this.scrollPosition = newScrollPosition;
            this.updateCanvasPosition();
            
            // Notify the global sync system about scroll position change if requested
            if (notifySync && window.scatterHighlightSync && window.scatterHighlightSync.setScrollPosition) {
                window.scatterHighlightSync.setScrollPosition(this.id, newScrollPosition);
            }
            
            return true;
        }
        
        return false;
    }
    
    updateScrollFromMousePosition(clientX) {
        if (this.zoom <= 1 || !this.plotArea) return; // Only apply when zoomed in

        const plotAreaRect = this.plotArea.getBoundingClientRect();

        // Get mouse position relative to plot area (0 to 1)
        const relativeX = (clientX - plotAreaRect.left) / plotAreaRect.width;

        // Clamp to 0-1 range
        const clampedX = Math.max(0, Math.min(1, relativeX));

        // Calculate new scroll position based on mouse position
        const newScrollPosition = clampedX * this.maxScrollPosition;

        // Apply the scroll change and notify sync system
        this.applyScrollChange(newScrollPosition, true);
    }

    handleMouseMove(e) {
        // Store last mouse clientX for zoom center calculation
        this._lastMouseClientX = e.clientX;

        // Update scroll position based on mouse position when zoomed in
        if (this.zoom > 1) {
            this.updateScrollFromMousePosition(e.clientX);
        }

        const rect = this.canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        const my = e.clientY - rect.top;

        const idx = this.findClosestPoint(mx, my);
        if (idx !== -1) {
            if (this.highlightedIndex !== idx) {
                this.highlightedIndex = idx;
                
                // Update highlight circle position
                this.updateHighlightCirclePosition();
                
                // Use global sync system for immediate JS updates to other plots
                window.scatterHighlightSync.setHighlight(this.id, idx);
                
                // Debounced Blazor update for tooltips
                this._debouncedPointHover(idx);
            }
        } else {
            this.clearHighlight();
        }
    }

    handleMouseLeave() {
        this.clearHighlight();
        this._lastMouseClientX = undefined; // Clear last mouse position when mouse leaves
    }

    /**
     * Clears all highlights in this plot and notifies the global sync system
     */
    clearHighlight() {
        if (this.highlightedIndex !== -1) {
            this.highlightedIndex = -1;
            
            // Use global sync system for immediate JS updates to other plots
            window.scatterHighlightSync.setHighlight(this.id, -1);
            
            // Debounced Blazor update for tooltips
            this._debouncedPointHover(-1);
        }
        
        // Hide the main highlight circle
        this.highlightCircle.style.display = "none";
        
        // Remove temporary highlight circles
        const container = this.highlightCircle.parentNode;
        if (container) {
            this.clearTemporaryHighlights(container);
        }
    }

    handleClick(e) {
        const rect = this.canvas.getBoundingClientRect();
        const mx = e.clientX - rect.left;
        const my = e.clientY - rect.top;
        
        // Get index and collection of closest point
        const result = this.findClosestPointWithCollection(mx, my);
        if (result.index !== -1) {
            // Pass both the index and collection index back to .NET
            this.dotNetRef.invokeMethodAsync("OnPointClicked", result.index, result.collectionIndex);
        }
    }

    /** Debounced trackpad zoom accumulation */
    handleWheel(e) {
        if (e.ctrlKey || e.shiftKey) {
            e.preventDefault();
            this._accumWheelDelta += e.deltaY;
            this._debounceWheel();
        }
    }

    // Helper method to apply zoom changes, used by both wheel zoom and sync zoom
    applyZoomChange(newZoom, useMousePosition = false) {
        if (newZoom === this.zoom) return; // No change needed
        
        // Store old zoom for comparison
        const oldZoom = this.zoom;
        this.zoom = newZoom;
        
        // Recalculate canvas width and max scroll position
        this.canvas.width = this.currentWidth * this.zoom;
        this.maxScrollPosition = Math.max(0, this.canvas.width - this.currentWidth);
        
        // Special case: when zooming to 1.0, always reset scroll position to 0
        if (newZoom === 1.0) {
            this.scrollPosition = 0;
            this.updateCanvasPosition();
        } 
        // Otherwise adjust scroll position to maintain center point when zooming
        else if (this.maxScrollPosition > 0) {
            // If mouse is over the canvas and we're asked to use it, use the mouse position to determine zoom center
            if (useMousePosition && this._lastMouseClientX !== undefined) {
                this.updateScrollFromMousePosition(this._lastMouseClientX);
            } else if (oldZoom > 1) {
                // Otherwise scale the scroll position proportionally
                const scrollRatio = this.scrollPosition / (this.currentWidth * oldZoom - this.currentWidth);
                this.applyScrollChange(scrollRatio * this.maxScrollPosition, false);
            } else {
                // Start at center when zooming in from 1.0
                this.applyScrollChange(this.maxScrollPosition / 2, false);
            }
        } else {
            this.applyScrollChange(0, false);
        }
        
        // Redraw everything after zoom change
        this.renderPoints();
        this.renderHistogram();
        this.renderAxisLinesAndLabels();
        this.renderRangeHighlight();
        
        return true; // Indicate zoom was changed
    }
    
    applyWheelZoom() {
        if (this._accumWheelDelta === 0) return;
        const direction = (this._accumWheelDelta < 0) ? 1 : -1;
        const zoomDelta = 2 ** direction;
        const newZoom = Math.max(1, Math.min(8, this.zoom * zoomDelta));
        this._accumWheelDelta = 0;

        // Apply the zoom change, using mouse position if available
        if (this.applyZoomChange(newZoom, true)) {
            // Sync zoom with other scatter plots using the global sync system
            if (window.scatterHighlightSync && window.scatterHighlightSync.setZoom) {
                window.scatterHighlightSync.setZoom(this.id, newZoom);
            }
        }
    }

    handleResize() {
        this.recalcSize();
        this.renderPoints();
        this.renderHistogram();
        this.renderAxisLinesAndLabels();
        this.renderRangeHighlight();
        //this.dotNetRef.invokeMethodAsync("RequestRedraw");
    }

    // Find the closest point with information about which collection it belongs to
    findClosestPointWithCollection(mx, my) {
        if (!this.pointCollections || this.pointCollections.length === 0) 
            return { index: -1, collectionIndex: -1 };
            
        // Check if we have any points at all
        const hasPoints = this.pointCollections.some(c => c.length > 0);
        if (!hasPoints) return { index: -1, collectionIndex: -1 };
            
        // Use the first collection length to determine the number of points
        const n = this.pointCollections[0].length;
            
        // The mouse coordinates (mx) are in the visible, transformed canvas space
        // The point positions are in the original canvas space
        // We need to convert between these two spaces

        // First calculate what index corresponds to this mouse x-position in the visible area
        const guessIndex = Math.round((mx - this.offsetX) / this.stepX);
        if (guessIndex < 0 || guessIndex >= n) return { index: -1, collectionIndex: -1 };

        // Look at a range of points around our initial guess
        const rangeSize = 100;
        const startIdx = Math.max(0, guessIndex - rangeSize);
        const endIdx = Math.min(n - 1, guessIndex + rangeSize);

        let closestIdx = -1;
        let closestCollectionIndex = -1;
        let minDist = this.pointRadius * 10; // Only consider points within this radius (more user-friendly)

        // Check all collections and points in range
        for (let collectionIndex = 0; collectionIndex < this.pointCollections.length; collectionIndex++) {
            const collection = this.pointCollections[collectionIndex];
            
            for (let i = startIdx; i <= endIdx; i++) {
                if (i >= collection.length) continue;
                const p = collection[i];
                if (!p || p.val === null) continue;

                // Calculate point position
                const px = p.index * this.stepX + this.offsetX;
                const py = (this.plotMax - p.val) * this.stepY;

                // Calculate distance to mouse
                const dist = Math.sqrt((px - mx) ** 2 + (py - my) ** 2);

                // Update if this is closer than what we've found
                if (dist < minDist) {
                    minDist = dist;
                    closestIdx = i;
                    closestCollectionIndex = collectionIndex;
                }
            }
        }

        return { index: closestIdx, collectionIndex: closestCollectionIndex };
    }

    // Find the closest point index without caring which collection it belongs to
    findClosestPoint(mx, my) {
        const result = this.findClosestPointWithCollection(mx, my);
        return result.index;
    }

    dispose() {
        this.canvas.removeEventListener("mousemove", this.handleMouseMove);
        this.canvas.removeEventListener("mouseleave", this.handleMouseLeave);
        this.canvas.removeEventListener("click", this.handleClick);
        this.canvas.removeEventListener("wheel", this.handleWheel);

        // We no longer use the scroll event listener

        if (this.resizeObserverPlotArea) {
            this.resizeObserverPlotArea.disconnect();
        }
        if (this.resizeObserver) {
            this.resizeObserver.disconnect();
        }
        
        // Unregister from global highlight sync system
        if (window.scatterHighlightSync) {
            window.scatterHighlightSync.unregisterScatterPlot(this.id);
        }
    }
}

/** Blazor export: create a new scatter plot instance */
export function initializeScatterPlot(
    canvasId,
    histogramId,
    highlightCircleId,
    rangeHighlightId,
    topLineId,
    centerLineId,
    bottomLineId,
    topLabelId,
    centerLabelId,
    bottomLabelId,
    dotNetRef,
    config
) {
    const sp = new ScatterPlot(
        canvasId,
        histogramId,
        highlightCircleId,
        rangeHighlightId,
        topLineId,
        centerLineId,
        bottomLineId,
        topLabelId,
        centerLabelId,
        bottomLabelId,
        dotNetRef
    );
    scatterPlots.set(canvasId, sp);
    sp.update(config);
}

/** Blazor export: update an existing scatter with new config */
export function updateScatterPlot(canvasId, config) {
    const sp = scatterPlots.get(canvasId);
    if (sp) sp.update(config);
}

/**
 * Updates the highlight state for this scatter plot
 * Called by the global sync system when highlighting should be updated
 */
ScatterPlot.prototype.updateHighlight = function(index) {
    if (index >= 0) {
        // Always update highlight position, even if the index is the same
        // This ensures proper highlighting when data changes or window is resized
        this.highlightedIndex = index;
        
        // Update the highlight circles for all points at this index
        this.updateHighlightCirclePosition();
    } else {
        // Clear all highlights
        this.highlightCircle.style.display = "none";
        this.highlightedIndex = -1;
        
        // Remove temporary highlight circles from this plot
        const container = this.highlightCircle.parentNode;
        if (container) {
            this.clearTemporaryHighlights(container);
        }
    }
};

// Add updateZoom as a method to ScatterPlot class
ScatterPlot.prototype.updateZoom = function(zoomLevel) {
    // Use our common helper method to apply the zoom change
    this.applyZoomChange(zoomLevel, false);
};

// Add updateScrollPosition as a method to ScatterPlot class
ScatterPlot.prototype.updateScrollPosition = function(scrollPos) {
    if (this.zoom <= 1) return; // Only apply when zoomed in
    
    // Scale scroll position relative to max scroll position
    // This ensures proper positioning across plots with different sizes
    const scaledScrollPos = (scrollPos / this.maxScrollPosition) * this.maxScrollPosition;
    
    // Apply the scroll change but don't notify sync (to avoid infinite loops)
    this.applyScrollChange(scaledScrollPos, false);
};

/** Blazor export: update highlight to specific point index */
export function updateHighlight(canvasId, index) {
    const sp = scatterPlots.get(canvasId);
    if (sp) {
        sp.updateHighlight(index);
    }
}

/** Blazor export: dispose a scatter plot */
export function disposeScatterPlot(canvasId) {
    const sp = scatterPlots.get(canvasId);
    if (sp) {
        sp.dispose();
        scatterPlots.delete(canvasId);
    }
}
