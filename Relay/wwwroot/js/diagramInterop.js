window.diagramInterop = {
    _state: null,

    initialize(viewportElement, canvasElement) {
        // Store state for this instance
        const state = {
            viewport: viewportElement,
            canvas: canvasElement,
            scale: 1,
            translateX: 0,
            translateY: 0,
            isPanning: false,
            startX: 0,
            startY: 0,
            startTranslateX: 0,
            startTranslateY: 0,
        };
        this._state = state;

        // Zoom via wheel — zoom toward cursor position
        state.onWheel = (e) => {
            e.preventDefault();
            const rect = state.viewport.getBoundingClientRect();
            const mouseX = e.clientX - rect.left;
            const mouseY = e.clientY - rect.top;

            const oldScale = state.scale;
            let delta;
            if (e.deltaMode === 1) {
                // Line mode (mouse wheel): use fixed step per notch
                delta = -e.deltaY * 0.05;
            } else {
                // Pixel mode (trackpad): scale down for smooth feel
                delta = -e.deltaY * 0.005;
            }
            const factor = Math.pow(2, delta);
            state.scale = Math.min(Math.max(state.scale * factor, 0.1), 2.0);

            // Adjust translate so the point under cursor stays fixed
            state.translateX = mouseX - (mouseX - state.translateX) * (state.scale / oldScale);
            state.translateY = mouseY - (mouseY - state.translateY) * (state.scale / oldScale);

            this._applyTransform();
        };

        // Pan via pointer drag on background (not on cards)
        state.onPointerDown = (e) => {
            if (e.button !== 0) return;  // only primary button
            // Don't pan if clicking on a card or interactive element
            if (e.target.closest('.card-common, .folder-card, .port-dot')) return;

            state.isPanning = true;
            state.startX = e.clientX;
            state.startY = e.clientY;
            state.startTranslateX = state.translateX;
            state.startTranslateY = state.translateY;
            state.viewport.style.cursor = 'grabbing';
            e.preventDefault();
        };

        state.onPointerMove = (e) => {
            if (!state.isPanning) return;
            state.translateX = state.startTranslateX + (e.clientX - state.startX);
            state.translateY = state.startTranslateY + (e.clientY - state.startY);
            this._applyTransform();
        };

        state.onPointerUp = () => {
            if (!state.isPanning) return;
            state.isPanning = false;
            state.viewport.style.cursor = '';
        };

        state.viewport.addEventListener('wheel', state.onWheel, { passive: false });
        state.viewport.addEventListener('pointerdown', state.onPointerDown);
        window.addEventListener('pointermove', state.onPointerMove);
        window.addEventListener('pointerup', state.onPointerUp);
    },

    _applyTransform() {
        const s = this._state;
        s.canvas.style.transform =
            `translate(${s.translateX}px, ${s.translateY}px) scale(${s.scale})`;
        // Expose inverse zoom so SVG strokes can maintain minimum visual thickness
        s.canvas.style.setProperty('--inv-zoom', Math.max(1, 1 / s.scale));
    },

    zoomToFit(graphWidth, graphHeight) {
        const s = this._state;
        const rect = s.viewport.getBoundingClientRect();
        const scaleX = rect.width / graphWidth;
        const scaleY = rect.height / graphHeight;
        s.scale = Math.min(scaleX, scaleY, 1.0);  // cap at 100%

        // Center the graph in the viewport
        const scaledW = graphWidth * s.scale;
        const scaledH = graphHeight * s.scale;
        s.translateX = (rect.width - scaledW) / 2;
        s.translateY = (rect.height - scaledH) / 2;

        this._applyTransform();
    },

    setZoom(scale) {
        const s = this._state;
        const rect = s.viewport.getBoundingClientRect();
        const centerX = rect.width / 2;
        const centerY = rect.height / 2;

        const oldScale = s.scale;
        s.scale = Math.min(Math.max(scale, 0.1), 2.0);

        s.translateX = centerX - (centerX - s.translateX) * (s.scale / oldScale);
        s.translateY = centerY - (centerY - s.translateY) * (s.scale / oldScale);

        this._applyTransform();
    },

    getTransform() {
        const s = this._state;
        return { x: s.translateX, y: s.translateY, scale: s.scale };
    },

    dispose() {
        const s = this._state;
        if (!s) return;
        s.viewport.removeEventListener('wheel', s.onWheel);
        s.viewport.removeEventListener('pointerdown', s.onPointerDown);
        window.removeEventListener('pointermove', s.onPointerMove);
        window.removeEventListener('pointerup', s.onPointerUp);
        this._state = null;
    }
};
