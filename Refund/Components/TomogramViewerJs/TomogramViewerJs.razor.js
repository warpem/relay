/**
 * TomogramViewerJs - Fully client-side JavaScript tomogram viewer.
 *
 * All UI (toolbar, canvas rendering, interaction) is in JS.
 * Blazor wrapper is a thin shell that passes parameters and receives events.
 */

import { fetchMRC, fetchMRCHeader, fetchSlice } from '../../js/mrc-parser.js';
import {
    compileShader,
    buildOccupancyTexture,
    SH_EVALUATE_GLSL,
    initBakeResources,
    bakeSHCoefficients
} from '../../js/webgl-isosurface-shared.js';

// Instance state keyed by element ID
const instances = new Map();

const IDENTITY_MAT4 = new Float32Array([1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1]);

const DEFAULT_COLORS = ['#FFF700', '#00BFFF', '#FF6B6B', '#50FA7B', '#BD93F9', '#FFB86C', '#FF79C6', '#8BE9FD'];

function createSpeciesEntry(incoming, index) {
    return {
        name: incoming.name || `Species ${index + 1}`,
        particles: incoming.particles || [],
        particleRotations: null,

        settings: {
            visible: true,
            displayType: 'spheres',
            diameter: incoming.diameter ?? 100,
            color: incoming.color || DEFAULT_COLORS[index % DEFAULT_COLORS.length],
            strokeWidth: 2,
            shape: 'circle',
            flatShading: false,
            contourColor: '#000000',
        },

        model: {
            volumeUrl: incoming.modelVolumeUrl || null,
            volumeData: null,
            volumeDims: null,
            volumePixelSize: null,
            volumeStats: null,
            threshold: 0,
            thresholdPct: 30,
            lods: [],
            currentLOD: 0,
            boundingRadiusAngstroms: 0,
            bakeResources: null,
            occTexture: null,
            instanceBuffer: null,
            instanceRotations: null,
            _filterBuffer: null,
            _instanceBufferCapacity: 0,
            worker: null,
        },
    };
}

function getSelectedSpecies(state) {
    return state.species[state.selectedSpeciesIndex] || null;
}

// Forward declarations — defined below
// loadSpeciesModelVolume(state, speciesIndex)
// regenerateSpeciesModelMesh(state, speciesIndex)

// ── Exported functions ──────────────────────────────────────────────────────

/**
 * Initialize the viewer: build UI, setup events, start loading.
 */
export function initialize(container, dotNetRef, fileUrl, options) {
    const elementId = container.id;

    // Tear down previous instance if any
    if (instances.has(elementId)) dispose(elementId);

    const state = createState(elementId, container, dotNetRef, fileUrl, options);
    instances.set(elementId, state);

    createUI(state);
    setupMouseHandlers(state);
    setupResizeObserver(state);
    setupFullscreenListener(state);

    if (fileUrl) {
        loadVolume(state);
    }
}

/**
 * Load a new volume from URL.
 */
export function loadVolumeByUrl(elementId, url) {
    const state = instances.get(elementId);
    if (!state) return;

    state.fileUrl = url;
    state.isVolumeLoaded = false;
    state.volumeSlices = null;
    state.sliceCache = { x: -1, y: -1, z: -1, slab: -1 };
    showLoading(state, 0, 'Loading header...');
    loadVolume(state); // loadVolume handles aborting any in-flight load
}

/**
 * Set or update the list of particle species from Blazor.
 */
export function setSpecies(elementId, speciesList) {
    const state = instances.get(elementId);
    if (!state) return;

    const incoming = speciesList || [];
    const oldSpecies = state.species;

    const newSpecies = incoming.map((s, i) => {
        const old = oldSpecies[i];
        const entry = createSpeciesEntry(s, i);

        // Preserve loaded model state if volume URL unchanged
        if (old && old.model.volumeUrl === entry.model.volumeUrl && old.model.volumeData) {
            entry.model.volumeData = old.model.volumeData;
            entry.model.volumeDims = old.model.volumeDims;
            entry.model.volumePixelSize = old.model.volumePixelSize;
            entry.model.volumeStats = old.model.volumeStats;
            entry.model.threshold = old.model.threshold;
            entry.model.thresholdPct = old.model.thresholdPct;
            entry.model.lods = old.model.lods;
            entry.model.currentLOD = old.model.currentLOD;
            entry.model.boundingRadiusAngstroms = old.model.boundingRadiusAngstroms;
            entry.model.bakeResources = old.model.bakeResources;
            entry.model.occTexture = old.model.occTexture;
            entry.model.instanceBuffer = old.model.instanceBuffer;
            entry.model._instanceBufferCapacity = old.model._instanceBufferCapacity;
            entry.model.worker = old.model.worker;
        }

        // Preserve user-modified settings if species count/order unchanged
        if (old && oldSpecies.length === incoming.length) {
            entry.settings = { ...old.settings };
            if (s.color) entry.settings.color = s.color;
            if (s.diameter != null) entry.settings.diameter = s.diameter;
        }

        return entry;
    });

    // Clean up removed species
    for (let i = incoming.length; i < oldSpecies.length; i++) {
        cleanupSpeciesModel(state, oldSpecies[i]);
    }

    state.species = newSpecies;

    if (state.selectedSpeciesIndex >= newSpecies.length) {
        state.selectedSpeciesIndex = Math.max(0, newSpecies.length - 1);
    }

    for (let i = 0; i < newSpecies.length; i++) {
        precomputeSpeciesRotations(newSpecies[i]);
        if (newSpecies[i].model.volumeUrl && !newSpecies[i].model.volumeData) {
            loadSpeciesModelVolume(state, i);
        }
    }

    populateSpeciesDropdown(state);
    syncToolbarToSpecies(state);

    if (state.isVolumeLoaded) {
        renderAllOverlays(state);
        if (state.model.active) requestModelRender(state);
    }
}

function cleanupSpeciesModel(state, species) {
    const sm = species.model;
    if (sm.worker) sm.worker.terminate();
    const gl = state.model.gl;
    if (gl) {
        for (const lod of sm.lods) {
            if (lod.posBuffer) gl.deleteBuffer(lod.posBuffer);
            if (lod.normBuffer) gl.deleteBuffer(lod.normBuffer);
            if (lod.shBuffer) gl.deleteBuffer(lod.shBuffer);
        }
        if (sm.instanceBuffer) gl.deleteBuffer(sm.instanceBuffer);
        if (sm.occTexture) gl.deleteTexture(sm.occTexture);
    }
}

/**
 * Clean up an instance.
 */
export function dispose(elementId) {
    const state = instances.get(elementId);
    if (!state) return;

    if (state.abortController) state.abortController.abort();
    if (state.resizeObserver) state.resizeObserver.disconnect();
    if (state._onFullscreenChange) {
        document.removeEventListener('fullscreenchange', state._onFullscreenChange);
    }

    // Remove all event listeners
    for (const planeType of ['xy', 'xz', 'zy']) {
        const panel = state.panels[planeType];
        if (panel && panel.container) {
            panel.container.removeEventListener('mousedown', panel._onMouseDown);
            panel.container.removeEventListener('wheel', panel._onWheel);
            panel.container.removeEventListener('mousemove', panel._onMouseMove);
            panel.container.removeEventListener('mouseleave', panel._onMouseLeave);
            panel.container.removeEventListener('contextmenu', panel._onContextMenu);
        }
    }

    // Clean up 3D mouse handlers
    const m = state.model;
    if (m.canvas && m._3dHandlersAttached) {
        m.canvas.removeEventListener('mousedown', m._on3DMouseDown);
        m.canvas.removeEventListener('wheel', m._on3DWheel);
        m.canvas.removeEventListener('contextmenu', m._on3DContextMenu);
    }

    // Clean up all species
    for (const sp of state.species) {
        cleanupSpeciesModel(state, sp);
    }

    // Clean up model resources
    clearTimeout(m._settleTimer);
    const mgl = m.gl;
    if (mgl) {
        // Clean up primitive geometry
        for (const key of ['sphere', 'cube']) {
            const geom = state.primitiveGeometry[key];
            if (geom) {
                if (geom.posBuffer) mgl.deleteBuffer(geom.posBuffer);
                if (geom.normBuffer) mgl.deleteBuffer(geom.normBuffer);
                if (geom.shBuffer) mgl.deleteBuffer(geom.shBuffer);
            }
        }

        // Clean up slice resources
        if (m.sliceTextures) {
            for (const plane of ['xy', 'xz', 'zy']) {
                if (m.sliceTextures[plane]) mgl.deleteTexture(m.sliceTextures[plane]);
            }
        }
        if (m.sliceProgram) mgl.deleteProgram(m.sliceProgram);
        if (m.sliceVAO) mgl.deleteVertexArray(m.sliceVAO);
        if (m.sliceVertBuffer) mgl.deleteBuffer(m.sliceVertBuffer);
        if (m.lineProgram) mgl.deleteProgram(m.lineProgram);
        if (m.lineVAO) mgl.deleteVertexArray(m.lineVAO);
        if (m.lineVertBuffer) mgl.deleteBuffer(m.lineVertBuffer);

        // ID buffer contour resources
        if (m.idFBO) mgl.deleteFramebuffer(m.idFBO);
        if (m.idTexture) mgl.deleteTexture(m.idTexture);
        if (m.idDepthTex) mgl.deleteTexture(m.idDepthTex);
        if (m.idProgram) mgl.deleteProgram(m.idProgram);
        if (m.contourProgram) mgl.deleteProgram(m.contourProgram);
        if (m.contourVAO) mgl.deleteVertexArray(m.contourVAO);
    }
    if (m.canvas) m.canvas.remove();

    instances.delete(elementId);
}

// ── State factory ───────────────────────────────────────────────────────────

function createState(elementId, container, dotNetRef, fileUrl, options) {
    return {
        elementId,
        dotNetRef,
        container,
        fileUrl,
        header: null,
        volumeSlices: null,
        dims: { x: 0, y: 0, z: 0 },
        pixelSize: 1,

        viewPoint: { x: 0, y: 0, z: 0 },
        zoom: 1.0,
        translate: {
            xy: { x: 0, y: 0 },
            xz: { x: 0, y: 0 },
            zy: { x: 0, y: 0 }
        },
        minIntensity: 0,
        maxIntensity: 1,
        intensityMean: 0,
        intensityStd: 1,
        sigmaCutoff: 3,

        toolbar: {},
        panels: {},
        imageData: {},

        slabThickness: 1,
        xyBuffer: null,
        xzBuffer: null,
        zyBuffer: null,
        sliceCache: { x: -1, y: -1, z: -1, slab: -1 },

        species: [],
        selectedSpeciesIndex: 0,
        isPicking: false,

        isVolumeLoaded: false,
        abortController: null,
        resizeObserver: null,

        options: {
            minWidth: options?.minWidth || 800,
            minHeight: options?.minHeight || 600,
        },

        sliders: {},
        loadingOverlay: null,
        _hoverPlane: null,
        _hoverTomogramCoords: null,

        // 3D model rendering state
        model: {
            active: false,
            gl: null,
            canvas: null,
            program: null,
            vao: null,
            uniforms: {},
            _renderQueued: false,
            _interacting: false,
            _settleTimer: null,
            _3dHandlersAttached: false,

            // Slab & visibility (global)
            slabThickness: 50,
            slabFull: false,
            hideOrthoslices: false,
        },

        viewMode: 'orthoslices',  // 'orthoslices' | '3d'

        camera3d: {
            yaw: 0.45,            // radians — rotation around Z (up) axis
            pitch: 0.6,           // radians — elevation above XY plane
            distance: 0,          // auto-set on first activation from dims
            panX: 0, panY: 0, panZ: 0,  // world-space pan offsets
            projection: 'perspective',
            fov: Math.PI / 6,     // 30°
            orthoScale: 1.0,
        },

        primitiveGeometry: {
            sphere: null,   // { posBuffer, normBuffer, shBuffer, vertexCount }
            cube: null,
        },
    };
}

// ── UI construction ─────────────────────────────────────────────────────────

function createUI(state) {
    const c = state.container;
    c.innerHTML = '';
    c.style.display = 'flex';
    c.style.flexDirection = 'column';
    c.style.overflow = 'hidden';
    c.style.background = 'var(--neutral-fill-layer-rest)';
    c.style.userSelect = 'none';

    injectSliderStyles(c);

    // Toolbar
    const toolbar = createToolbar(state);
    c.appendChild(toolbar);

    // Viewport
    const viewport = document.createElement('div');
    viewport.style.cssText = 'display:flex; flex-direction:row; flex-grow:1; overflow:hidden; justify-content:center; align-items:flex-end; column-gap:10px; padding: 0 5px 5px 5px; position:relative;';
    c.appendChild(viewport);
    state._viewport = viewport;

    // Left column: XZ row on top, XY block on bottom
    const leftCol = document.createElement('div');
    leftCol.style.cssText = 'display:flex; flex-direction:column; row-gap:10px;';
    viewport.appendChild(leftCol);
    state._leftCol = leftCol;

    // XZ row: Z slider (vertical) + XZ panel
    const xzRow = document.createElement('div');
    xzRow.style.cssText = 'display:flex; flex-direction:row; align-items:center; gap:4px;';
    state.sliders.z = createAxisSlider(state, 'z', true);
    state.panels.xz = createPanel(state, 'xz');
    xzRow.append(state.sliders.z.element, state.panels.xz.container);
    leftCol.appendChild(xzRow);

    // XY block: (Y slider + XY panel) stacked above X slider
    const xyBlock = document.createElement('div');
    xyBlock.style.cssText = 'display:flex; flex-direction:column;';

    const xyRow = document.createElement('div');
    xyRow.style.cssText = 'display:flex; flex-direction:row; align-items:center; gap:4px;';
    state.sliders.y = createAxisSlider(state, 'y', true);
    state.panels.xy = createPanel(state, 'xy');
    xyRow.append(state.sliders.y.element, state.panels.xy.container);
    xyBlock.appendChild(xyRow);

    // X slider row: spacer + horizontal slider
    const xRow = document.createElement('div');
    xRow.style.cssText = 'display:flex; flex-direction:row; gap:4px; margin-top:4px;';
    const xSpacer = document.createElement('div');
    xSpacer.style.cssText = `width:${SLIDER_THICKNESS}px; flex-shrink:0;`;
    state.sliders.x = createAxisSlider(state, 'x', false);
    xRow.append(xSpacer, state.sliders.x.element);
    xyBlock.appendChild(xRow);
    state.sliders._xSpacer = xSpacer;

    leftCol.appendChild(xyBlock);

    // Right: ZY
    state.panels.zy = createPanel(state, 'zy');
    viewport.appendChild(state.panels.zy.container);
    state._zyContainer = state.panels.zy.container;

    // Loading overlay
    state.loadingOverlay = createLoadingOverlay();
    c.appendChild(state.loadingOverlay.root);
}

function createToolbar(state) {
    const toolbar = document.createElement('div');
    toolbar.style.cssText = `
        display: flex; align-items: flex-end; gap: 16px; padding: 6px 12px;
        background: var(--neutral-layer-1); border-radius: 4px; margin-bottom: 10px;
        flex-shrink: 0; min-height: 52px; box-shadow: var(--shadow-4); flex-wrap: wrap;
    `;

    // ── Coordinates group ──
    const coordGroup = createToolbarGroup('Coordinates');
    const coordRow = document.createElement('div');
    coordRow.style.cssText = 'display:flex; gap:4px; align-items:center;';

    const xInput = createCoordInput(state, 'X', () => state.dims.x - 1, v => {
        state.viewPoint.x = v;
        onViewPointChanged(state);
    });
    const yInput = createCoordInput(state, 'Y', () => state.dims.y - 1, v => {
        state.viewPoint.y = v;
        onViewPointChanged(state);
    });
    const zInput = createCoordInput(state, 'Z', () => state.dims.z - 1, v => {
        state.viewPoint.z = v;
        onViewPointChanged(state);
    });

    coordRow.append(xInput.wrapper, yInput.wrapper, zInput.wrapper);
    coordGroup.appendChild(coordRow);
    toolbar.appendChild(coordGroup);

    state.toolbar.xInput = xInput;
    state.toolbar.yInput = yInput;
    state.toolbar.zInput = zInput;

    // Separator
    toolbar.appendChild(createSeparator());

    // ── Zoom group ──
    const zoomGroup = createToolbarGroup('Zoom');
    const zoomRow = document.createElement('div');
    zoomRow.style.cssText = 'display:flex; gap:4px; align-items:center;';

    const zoomOutBtn = createSmallButton('\u2212', () => {
        zoomAroundViewPoint(state, Math.max(0.1, state.zoom / 1.2));
        renderAllOverlays(state);
        if (state.model.active) requestModelRender(state, true);
    });
    const zoomDisplay = document.createElement('span');
    zoomDisplay.style.cssText = 'min-width:48px; text-align:center; font-size:12px; font-family:monospace; color:var(--neutral-foreground-rest);';
    zoomDisplay.textContent = '100%';
    const zoomInBtn = createSmallButton('+', () => {
        zoomAroundViewPoint(state, Math.min(10, state.zoom * 1.2));
        renderAllOverlays(state);
        if (state.model.active) requestModelRender(state, true);
    });
    const fitBtn = createSmallButton('Fit', () => fitToViewport(state));
    fitBtn.style.fontSize = '11px';
    fitBtn.style.padding = '2px 6px';

    zoomRow.append(zoomOutBtn, zoomDisplay, zoomInBtn, fitBtn);
    zoomGroup.appendChild(zoomRow);
    toolbar.appendChild(zoomGroup);

    state.toolbar.zoomDisplay = zoomDisplay;

    // Separator
    toolbar.appendChild(createSeparator());

    // ── Ortho-slices group ──
    const slabGroup = createToolbarGroup('Ortho-slices');
    const slabRow = document.createElement('div');
    slabRow.style.cssText = 'display:flex; gap:4px; align-items:center;';

    const slabInput = document.createElement('input');
    slabInput.type = 'number';
    slabInput.value = '1';
    slabInput.min = '1';
    slabInput.step = '1';
    slabInput.title = 'Number of slices to average';
    slabInput.style.cssText = 'width:42px; height:24px; font-size:12px; border:1px solid var(--neutral-stroke-rest); border-radius:4px; padding:0 4px; text-align:right; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest);';
    slabInput.addEventListener('change', () => {
        const v = Math.max(1, Math.round(parseInt(slabInput.value) || 1));
        slabInput.value = v;
        state.slabThickness = v;
        state.sliceCache = { x: -1, y: -1, z: -1, slab: -1 };
        if (state.isVolumeLoaded) {
            renderAllSlices(state);
            renderAllOverlays(state);
            if (state.model.active) requestModelRender(state);
        }
    });

    const slabLabel = document.createElement('span');
    slabLabel.textContent = 'slices';
    slabLabel.style.cssText = 'font-size:11px; color:var(--neutral-foreground-hint);';

    // Show orthoslices toggle (starts active = slices visible)
    const showSlicesBtn = document.createElement('button');
    showSlicesBtn.textContent = 'Show';
    showSlicesBtn.title = 'Toggle orthoslice visibility';
    showSlicesBtn.style.cssText = `
        padding: 2px 6px; font-size: 11px; border: 1px solid var(--accent-fill-rest);
        border-radius: 4px; cursor: pointer; background: var(--accent-fill-rest);
        color: var(--foreground-on-accent-rest); margin-left: 8px;
    `;
    showSlicesBtn.addEventListener('click', () => {
        state.model.hideOrthoslices = !state.model.hideOrthoslices;
        const visible = !state.model.hideOrthoslices;
        showSlicesBtn.style.background = visible ? 'var(--accent-fill-rest)' : 'var(--neutral-layer-1)';
        showSlicesBtn.style.color = visible ? 'var(--foreground-on-accent-rest)' : 'var(--neutral-foreground-rest)';
        showSlicesBtn.style.borderColor = visible ? 'var(--accent-fill-rest)' : 'var(--neutral-stroke-rest)';
        setOrthosliceVisibility(state, visible);
    });

    slabRow.append(slabInput, slabLabel, showSlicesBtn);
    slabGroup.appendChild(slabRow);
    toolbar.appendChild(slabGroup);

    state.toolbar.slabInput = slabInput;
    state.toolbar.showSlicesBtn = showSlicesBtn;

    // Separator
    toolbar.appendChild(createSeparator());

    // ── Contrast (sigma cutoff) group ──
    const contrastGroup = createToolbarGroup('Contrast');
    const contrastRow = document.createElement('div');
    contrastRow.style.cssText = 'display:flex; gap:4px; align-items:center;';

    const sigmaInput = document.createElement('input');
    sigmaInput.type = 'number';
    sigmaInput.value = '3';
    sigmaInput.min = '0.1';
    sigmaInput.step = '0.5';
    sigmaInput.title = 'Standard deviations for intensity range';
    sigmaInput.style.cssText = 'width:42px; height:24px; font-size:12px; border:1px solid var(--neutral-stroke-rest); border-radius:4px; padding:0 4px; text-align:right; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest);';
    sigmaInput.addEventListener('change', () => {
        const v = Math.max(0.1, parseFloat(sigmaInput.value) || 3);
        sigmaInput.value = v;
        state.sigmaCutoff = v;
        applyIntensityRange(state);
        if (state.isVolumeLoaded) {
            state.sliceCache = { x: -1, y: -1, z: -1, slab: -1 };
            renderAllSlices(state);
            renderAllOverlays(state);
        }
    });

    const sigmaLabel = document.createElement('span');
    sigmaLabel.textContent = '\u03C3';
    sigmaLabel.style.cssText = 'font-size:11px; color:var(--neutral-foreground-hint);';

    contrastRow.append(sigmaInput, sigmaLabel);
    contrastGroup.appendChild(contrastRow);
    toolbar.appendChild(contrastGroup);

    state.toolbar.sigmaInput = sigmaInput;

    // Separator
    toolbar.appendChild(createSeparator());

    // ── View group (fullscreen + view mode + projection) ──
    const viewGroup = createToolbarGroup('View');
    const viewRow = document.createElement('div');
    viewRow.style.cssText = 'display:flex; gap:4px; align-items:center;';

    // View mode dropdown
    const viewModeSelect = document.createElement('select');
    viewModeSelect.title = 'View mode';
    viewModeSelect.style.cssText = 'height:24px; font-size:12px; border:1px solid var(--neutral-stroke-rest); border-radius:4px; padding:0 4px; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest); cursor:pointer;';
    const optOrthoslices = document.createElement('option');
    optOrthoslices.value = 'orthoslices'; optOrthoslices.textContent = 'Orthoslices';
    const opt3D = document.createElement('option');
    opt3D.value = '3d'; opt3D.textContent = '3D';
    viewModeSelect.append(optOrthoslices, opt3D);
    viewModeSelect.addEventListener('change', () => setViewMode(state, viewModeSelect.value));

    // Projection dropdown (hidden by default, shown in 3D mode)
    const projSelect = document.createElement('select');
    projSelect.title = 'Projection type';
    projSelect.style.cssText = 'height:24px; font-size:12px; border:1px solid var(--neutral-stroke-rest); border-radius:4px; padding:0 4px; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest); cursor:pointer; display:none;';
    const optPersp = document.createElement('option');
    optPersp.value = 'perspective'; optPersp.textContent = 'Perspective';
    const optOrtho = document.createElement('option');
    optOrtho.value = 'orthographic'; optOrtho.textContent = 'Orthographic';
    projSelect.append(optPersp, optOrtho);
    projSelect.addEventListener('change', () => {
        state.camera3d.projection = projSelect.value;
        requestModelRender(state);
    });

    const fullscreenBtn = document.createElement('button');
    fullscreenBtn.textContent = '\u26F6';
    fullscreenBtn.title = 'Toggle fullscreen';
    fullscreenBtn.style.cssText = `
        padding: 2px 8px; font-size: 14px; border: 1px solid var(--neutral-stroke-rest);
        border-radius: 4px; cursor: pointer; background: var(--neutral-layer-1);
        color: var(--neutral-foreground-rest);
    `;
    fullscreenBtn.addEventListener('click', () => toggleFullscreen(state));

    viewRow.append(viewModeSelect, projSelect, fullscreenBtn);
    viewGroup.appendChild(viewRow);
    toolbar.appendChild(viewGroup);

    state.toolbar.fullscreenBtn = fullscreenBtn;
    state.toolbar.viewModeSelect = viewModeSelect;
    state.toolbar.projSelect = projSelect;

    // ── Particle controls ──
    {
        // ── PARTICLES group: species select + show toggle + display type ──
        const partSep = createSeparator();
        toolbar.appendChild(partSep);

        const partGroup = createToolbarGroup('Particles');
        const partRow = document.createElement('div');
        partRow.style.cssText = 'display:flex; gap:4px; align-items:center;';

        // Species selector dropdown
        const speciesSelect = document.createElement('select');
        speciesSelect.title = 'Select particle species';
        speciesSelect.style.cssText = 'height:24px; font-size:12px; border:1px solid var(--neutral-stroke-rest); border-radius:4px; padding:0 4px; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest); cursor:pointer; max-width:120px;';
        speciesSelect.style.display = 'none';
        speciesSelect.addEventListener('change', () => {
            state.selectedSpeciesIndex = parseInt(speciesSelect.value) || 0;
            syncToolbarToSpecies(state);
        });

        // Show toggle
        const showToggle = document.createElement('button');
        showToggle.textContent = 'Show';
        showToggle.title = 'Toggle particle visibility';
        showToggle.style.cssText = `
            padding: 2px 8px; font-size: 12px; border: 1px solid var(--accent-fill-rest);
            border-radius: 4px; cursor: pointer; background: var(--accent-fill-rest);
            color: var(--foreground-on-accent-rest);
        `;
        showToggle.addEventListener('click', () => {
            const sp = getSelectedSpecies(state);
            if (!sp) return;
            sp.settings.visible = !sp.settings.visible;
            showToggle.style.background = sp.settings.visible ? 'var(--accent-fill-rest)' : 'var(--neutral-layer-1)';
            showToggle.style.color = sp.settings.visible ? 'var(--foreground-on-accent-rest)' : 'var(--neutral-foreground-rest)';
            showToggle.style.borderColor = sp.settings.visible ? 'var(--accent-fill-rest)' : 'var(--neutral-stroke-rest)';
            applyParticleVisibility(state);
        });

        // Display type dropdown
        const displaySelect = document.createElement('select');
        displaySelect.title = 'Particle display type';
        displaySelect.style.cssText = 'height:24px; font-size:12px; border:1px solid var(--neutral-stroke-rest); border-radius:4px; padding:0 4px; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest); cursor:pointer;';
        const optSpheres = document.createElement('option');
        optSpheres.value = 'spheres'; optSpheres.textContent = 'Spheres';
        const optCubes = document.createElement('option');
        optCubes.value = 'cubes'; optCubes.textContent = 'Cubes';
        const optModels = document.createElement('option');
        optModels.value = 'models'; optModels.textContent = 'Models';
        optModels.style.display = 'none';
        displaySelect.append(optSpheres, optCubes, optModels);
        displaySelect.addEventListener('change', () => {
            const sp = getSelectedSpecies(state);
            if (!sp) return;
            const oldType = sp.settings.displayType;
            const newType = displaySelect.value;
            if (oldType !== newType) {
                sp.settings.displayType = newType;
                onDisplayTypeChanged(state, oldType, newType);
            }
        });

        partRow.append(speciesSelect, showToggle, displaySelect);
        partGroup.appendChild(partRow);
        toolbar.appendChild(partGroup);

        // ── STYLE group: color, diameter ──
        const styleSep = createSeparator();
        toolbar.appendChild(styleSep);

        const styleGroup = createToolbarGroup('Style');
        const styleRow = document.createElement('div');
        styleRow.style.cssText = 'display:flex; gap:4px; align-items:center;';

        // Color picker
        const colorInput = document.createElement('input');
        colorInput.type = 'color';
        colorInput.value = '#FFF700';
        colorInput.style.cssText = 'width:24px; height:24px; border:none; cursor:pointer; padding:0;';
        colorInput.addEventListener('change', () => {
            const sp = getSelectedSpecies(state);
            if (sp) sp.settings.color = colorInput.value;
            if (state.isVolumeLoaded) renderAllOverlays(state);
            if (state.model.active) requestModelRender(state);
        });

        // Diameter/size group (visible for spheres and cubes)
        const diamGroup = document.createElement('span');
        diamGroup.style.cssText = 'display:flex; gap:4px; align-items:center;';
        const diamLabel = document.createElement('span');
        diamLabel.textContent = '\u00D8';
        diamLabel.style.cssText = 'font-size:12px; color:var(--neutral-foreground-hint);';
        diamLabel.title = 'Particle size (\u00C5)';
        const diamInput = document.createElement('input');
        diamInput.type = 'number';
        diamInput.value = '100';
        diamInput.min = '1';
        diamInput.step = '1';
        diamInput.style.cssText = 'width:50px; height:24px; font-size:12px; border:1px solid var(--neutral-stroke-rest); border-radius:4px; padding:0 4px; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest);';
        diamInput.addEventListener('change', () => {
            const sp = getSelectedSpecies(state);
            if (sp) sp.settings.diameter = parseFloat(diamInput.value) || 100;
            if (state.isVolumeLoaded) renderAllOverlays(state);
            if (state.model.active) requestModelRender(state);
        });
        const diamUnit = document.createElement('span');
        diamUnit.textContent = '\u00C5';
        diamUnit.style.cssText = 'font-size:11px; color:var(--neutral-foreground-hint);';
        diamGroup.append(diamLabel, diamInput, diamUnit);

        // Flat shading toggle
        const flatToggle = document.createElement('button');
        flatToggle.textContent = 'Flat';
        flatToggle.title = 'Toggle flat shading with contour lines';
        flatToggle.style.cssText = `
            padding: 2px 8px; font-size: 12px; border: 1px solid var(--neutral-stroke-rest);
            border-radius: 4px; cursor: pointer; background: var(--neutral-layer-1);
            color: var(--neutral-foreground-rest);
        `;

        // Contour color picker (hidden by default)
        const contourColorInput = document.createElement('input');
        contourColorInput.type = 'color';
        contourColorInput.value = '#000000';
        contourColorInput.title = 'Contour line color';
        contourColorInput.style.cssText = 'width:24px; height:24px; border:none; cursor:pointer; padding:0; display:none;';
        contourColorInput.addEventListener('change', () => {
            const sp = getSelectedSpecies(state);
            if (sp) sp.settings.contourColor = contourColorInput.value;
            if (state.isVolumeLoaded) renderAllOverlays(state);
            if (state.model.active) requestModelRender(state);
        });

        flatToggle.addEventListener('click', () => {
            const sp = getSelectedSpecies(state);
            if (!sp) return;
            sp.settings.flatShading = !sp.settings.flatShading;
            const active = sp.settings.flatShading;
            flatToggle.style.background = active ? 'var(--accent-fill-rest)' : 'var(--neutral-layer-1)';
            flatToggle.style.color = active ? 'var(--foreground-on-accent-rest)' : 'var(--neutral-foreground-rest)';
            flatToggle.style.borderColor = active ? 'var(--accent-fill-rest)' : 'var(--neutral-stroke-rest)';
            contourColorInput.style.display = active ? '' : 'none';
            if (state.isVolumeLoaded) renderAllOverlays(state);
            if (state.model.active) requestModelRender(state);
        });

        styleRow.append(colorInput, diamGroup, flatToggle, contourColorInput);
        styleGroup.appendChild(styleRow);
        toolbar.appendChild(styleGroup);

        // ── THRESHOLD group: slider + number input (models only) ──
        const threshSep = createSeparator();
        threshSep.style.display = 'none';
        toolbar.appendChild(threshSep);

        const threshGroup = createToolbarGroup('Threshold');
        threshGroup.style.display = 'none';
        const threshRow = document.createElement('div');
        threshRow.style.cssText = 'display:flex; gap:4px; align-items:center;';

        // Threshold slider
        const modelThreshSlider = document.createElement('input');
        modelThreshSlider.type = 'range';
        modelThreshSlider.min = '0';
        modelThreshSlider.max = '100';
        modelThreshSlider.value = '30';
        modelThreshSlider.title = 'Isosurface threshold';
        modelThreshSlider.style.cssText = 'width:100px; height:4px; cursor:pointer; accent-color:var(--accent-fill-rest);';

        modelThreshSlider.addEventListener('input', () => {
            const sp = getSelectedSpecies(state);
            if (!sp) return;
            const pct = parseFloat(modelThreshSlider.value);
            sp.model.thresholdPct = pct;
            if (sp.model.volumeStats) {
                const s = sp.model.volumeStats;
                sp.model.threshold = s.min + (pct / 100) * (s.max - s.min);
                if (state.toolbar.modelThreshInput) {
                    state.toolbar.modelThreshInput.value = formatModelThreshold(sp.model.threshold);
                }
            }
        });
        modelThreshSlider.addEventListener('change', () => {
            const sp = getSelectedSpecies(state);
            if (sp && sp.model.volumeData) regenerateSpeciesModelMesh(state, state.selectedSpeciesIndex);
        });

        // Threshold number input
        const modelThreshInput = document.createElement('input');
        modelThreshInput.type = 'number';
        modelThreshInput.step = '0.001';
        modelThreshInput.value = '0';
        modelThreshInput.title = 'Isosurface threshold value';
        modelThreshInput.style.cssText = 'width:75px; height:24px; font-size:11px; font-family:monospace; border:1px solid var(--neutral-stroke-rest); border-radius:4px; padding:0 4px; text-align:right; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest);';
        modelThreshInput.addEventListener('change', () => {
            const sp = getSelectedSpecies(state);
            if (!sp) return;
            const val = parseFloat(modelThreshInput.value);
            if (isNaN(val)) return;
            sp.model.threshold = val;
            if (sp.model.volumeStats) {
                const s = sp.model.volumeStats;
                const range = s.max - s.min;
                if (range > 0) {
                    sp.model.thresholdPct = ((val - s.min) / range) * 100;
                    modelThreshSlider.value = sp.model.thresholdPct;
                }
            }
            if (sp.model.volumeData) regenerateSpeciesModelMesh(state, state.selectedSpeciesIndex);
        });

        threshRow.append(modelThreshSlider, modelThreshInput);
        threshGroup.appendChild(threshRow);
        toolbar.appendChild(threshGroup);

        // ── SLAB group: thickness input + show-all button (models only) ──
        const slabSep = createSeparator();
        slabSep.style.display = 'none';
        toolbar.appendChild(slabSep);

        const slabGroup = createToolbarGroup('Slab');
        slabGroup.style.display = 'none';
        const slabRow = document.createElement('div');
        slabRow.style.cssText = 'display:flex; gap:4px; align-items:center;';

        const modelSlabInput = document.createElement('input');
        modelSlabInput.type = 'number';
        modelSlabInput.value = '50';
        modelSlabInput.min = '1';
        modelSlabInput.step = '1';
        modelSlabInput.title = 'Model slab thickness (slices)';
        modelSlabInput.style.cssText = 'width:54px; height:24px; font-size:12px; border:1px solid var(--neutral-stroke-rest); border-radius:4px; padding:0 4px; text-align:right; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest);';
        modelSlabInput.addEventListener('change', () => {
            state.model.slabThickness = Math.max(1, parseInt(modelSlabInput.value) || 50);
            modelSlabInput.value = state.model.slabThickness;
            if (state.model.active) requestModelRender(state);
        });
        const slabUnit = document.createElement('span');
        slabUnit.textContent = 'slices';
        slabUnit.style.cssText = 'font-size:11px; color:var(--neutral-foreground-hint);';

        // Show All button
        const fullSlabBtn = document.createElement('button');
        fullSlabBtn.textContent = 'Show All';
        fullSlabBtn.title = 'Show all particles (ignore slab)';
        fullSlabBtn.style.cssText = `
            padding: 2px 6px; font-size: 11px; border: 1px solid var(--neutral-stroke-rest);
            border-radius: 4px; cursor: pointer; background: var(--neutral-layer-1);
            color: var(--neutral-foreground-rest);
        `;
        fullSlabBtn.addEventListener('click', () => {
            state.model.slabFull = !state.model.slabFull;
            fullSlabBtn.style.background = state.model.slabFull ? 'var(--accent-fill-rest)' : 'var(--neutral-layer-1)';
            fullSlabBtn.style.color = state.model.slabFull ? 'var(--foreground-on-accent-rest)' : 'var(--neutral-foreground-rest)';
            fullSlabBtn.style.borderColor = state.model.slabFull ? 'var(--accent-fill-rest)' : 'var(--neutral-stroke-rest)';
            if (state.model.active) requestModelRender(state);
        });

        slabRow.append(modelSlabInput, slabUnit, fullSlabBtn);
        slabGroup.appendChild(slabRow);
        toolbar.appendChild(slabGroup);

        // Store toolbar references
        state.toolbar.speciesSelect = speciesSelect;
        state.toolbar.showToggle = showToggle;
        state.toolbar.displaySelect = displaySelect;
        state.toolbar.diamInput = diamInput;
        state.toolbar.colorInput = colorInput;
        state.toolbar.diamGroup = diamGroup;
        state.toolbar.threshGroup = threshGroup;
        state.toolbar.threshSep = threshSep;
        state.toolbar.slabGroup = slabGroup;
        state.toolbar.slabSep = slabSep;
        state.toolbar.modelThreshSlider = modelThreshSlider;
        state.toolbar.modelThreshInput = modelThreshInput;
        state.toolbar.modelSlabInput = modelSlabInput;
        state.toolbar.fullSlabBtn = fullSlabBtn;
        state.toolbar.flatToggle = flatToggle;
        state.toolbar.contourColorInput = contourColorInput;
        state.toolbar.partSep = partSep;
        state.toolbar.partGroup = partGroup;
        state.toolbar.styleSep = styleSep;
        state.toolbar.styleGroup = styleGroup;

        // Initially hide all particle controls until species are set
        partSep.style.display = 'none';
        partGroup.style.display = 'none';
        styleSep.style.display = 'none';
        styleGroup.style.display = 'none';
    }

    return toolbar;
}

function createToolbarGroup(label) {
    const group = document.createElement('div');
    group.style.cssText = 'display:flex; flex-direction:column;';
    const header = document.createElement('span');
    header.textContent = label;
    header.style.cssText = 'font-size:10px; color:var(--neutral-foreground-hint); text-transform:uppercase; letter-spacing:0.5px; margin-bottom:2px;';
    group.appendChild(header);
    return group;
}

function createSeparator() {
    const sep = document.createElement('div');
    sep.style.cssText = 'width:1px; height:36px; background:var(--neutral-stroke-rest); align-self:center;';
    return sep;
}

function populateSpeciesDropdown(state) {
    const sel = state.toolbar.speciesSelect;
    if (!sel) return;
    sel.innerHTML = '';
    for (let i = 0; i < state.species.length; i++) {
        const opt = document.createElement('option');
        opt.value = i;
        opt.textContent = state.species[i].name;
        sel.appendChild(opt);
    }
    sel.value = state.selectedSpeciesIndex;
    sel.style.display = state.species.length > 1 ? '' : 'none';

    // Show/hide all particle controls based on whether any species exist
    const hasSpecies = state.species.length > 0;
    const tb = state.toolbar;
    if (tb.partSep) tb.partSep.style.display = hasSpecies ? '' : 'none';
    if (tb.partGroup) tb.partGroup.style.display = hasSpecies ? 'flex' : 'none';
    if (tb.styleSep) tb.styleSep.style.display = hasSpecies ? '' : 'none';
    if (tb.styleGroup) tb.styleGroup.style.display = hasSpecies ? 'flex' : 'none';
}

function syncToolbarToSpecies(state) {
    const sp = getSelectedSpecies(state);
    if (!sp) return;
    const s = sp.settings;
    const sm = sp.model;
    const tb = state.toolbar;

    if (tb.colorInput) tb.colorInput.value = s.color;
    if (tb.diamInput) tb.diamInput.value = s.diameter;
    if (tb.displaySelect) {
        const modelsOpt = tb.displaySelect.querySelector('option[value="models"]');
        if (modelsOpt) modelsOpt.style.display = sm.volumeUrl ? '' : 'none';
        tb.displaySelect.value = s.displayType;
    }
    if (tb.showToggle) {
        tb.showToggle.style.background = s.visible ? 'var(--accent-fill-rest)' : 'var(--neutral-layer-1)';
        tb.showToggle.style.color = s.visible ? 'var(--foreground-on-accent-rest)' : 'var(--neutral-foreground-rest)';
        tb.showToggle.style.borderColor = s.visible ? 'var(--accent-fill-rest)' : 'var(--neutral-stroke-rest)';
    }
    if (tb.flatToggle) {
        tb.flatToggle.style.background = s.flatShading ? 'var(--accent-fill-rest)' : 'var(--neutral-layer-1)';
        tb.flatToggle.style.color = s.flatShading ? 'var(--foreground-on-accent-rest)' : 'var(--neutral-foreground-rest)';
        tb.flatToggle.style.borderColor = s.flatShading ? 'var(--accent-fill-rest)' : 'var(--neutral-stroke-rest)';
    }
    if (tb.contourColorInput) {
        tb.contourColorInput.value = s.contourColor;
        tb.contourColorInput.style.display = s.flatShading ? '' : 'none';
    }

    const isModels = s.displayType === 'models';
    if (tb.threshGroup) tb.threshGroup.style.display = isModels ? 'flex' : 'none';
    if (tb.threshSep) tb.threshSep.style.display = isModels ? '' : 'none';
    if (tb.slabGroup) tb.slabGroup.style.display = isModels ? 'flex' : 'none';
    if (tb.slabSep) tb.slabSep.style.display = isModels ? '' : 'none';
    if (tb.diamGroup) tb.diamGroup.style.display = isModels ? 'none' : 'flex';

    if (tb.modelThreshSlider) tb.modelThreshSlider.value = sm.thresholdPct;
    if (tb.modelThreshInput && sm.volumeStats) {
        tb.modelThreshInput.value = formatModelThreshold(sm.threshold);
    }
}

function createCoordInput(state, label, maxFn, onChange) {
    const wrapper = document.createElement('div');
    wrapper.style.cssText = `
        display:flex; align-items:center; background:var(--neutral-layer-1);
        min-width:50px; height:24px; border:1px solid var(--neutral-stroke-rest);
        padding:0 4px; border-radius:4px; gap:2px;
    `;
    const lbl = document.createElement('span');
    lbl.textContent = label;
    lbl.style.cssText = 'font-size:10px; color:var(--neutral-foreground-hint); font-weight:600;';
    const input = document.createElement('input');
    input.type = 'number';
    input.value = '0';
    input.min = '0';
    input.max = '0';
    input.style.cssText = `
        width:42px; border:none; outline:none; font-size:12px;
        text-align:right; background:transparent; padding:0;
        color:var(--neutral-foreground-rest); -moz-appearance:textfield;
    `;

    let debounceTimer = null;
    input.addEventListener('input', () => {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            const v = Math.max(0, Math.min(parseInt(input.value) || 0, maxFn()));
            input.value = v;
            onChange(v);
        }, 50);
    });

    wrapper.append(lbl, input);
    return { wrapper, input, updateMax: () => { input.max = maxFn(); } };
}

function createSmallButton(text, onClick) {
    const btn = document.createElement('button');
    btn.textContent = text;
    btn.style.cssText = `
        width:24px; height:24px; border:1px solid var(--neutral-stroke-rest); border-radius:4px;
        cursor:pointer; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest);
        font-size:14px; display:flex; align-items:center; justify-content:center; padding:0;
    `;
    btn.addEventListener('click', onClick);
    return btn;
}

// ── Coordinate sliders ──────────────────────────────────────────────────────

const SLIDER_THICKNESS = 20;
const SLIDER_GAP = 4;
const THUMB_SIZE = 10;

function injectSliderStyles(container) {
    const th = THUMB_SIZE;        // 10
    const half = THUMB_SIZE / 2;  // 5
    const style = document.createElement('style');
    style.textContent = `
        .tvjs-slider {
            -webkit-appearance: none;
            appearance: none;
            background: transparent;
            border: none;
            cursor: pointer;
            padding: 0;
        }
        .tvjs-slider:focus { outline: none; }

        /* ── base thumb ── */
        .tvjs-slider::-webkit-slider-thumb {
            -webkit-appearance: none;
            width: ${th}px; height: ${th}px;
            border-radius: 50%;
            background: var(--neutral-fill-strong-rest);
            border: none;
            cursor: pointer;
            transition: background 0.15s;
        }
        .tvjs-slider::-webkit-slider-thumb:hover { background: var(--accent-fill-rest); }
        .tvjs-slider::-moz-range-thumb {
            width: ${th}px; height: ${th}px;
            border-radius: 50%;
            background: var(--neutral-fill-strong-rest);
            border: none;
            cursor: pointer;
        }
        .tvjs-slider::-moz-range-thumb:hover { background: var(--accent-fill-rest); }

        /* ── vertical ── */
        .tvjs-slider-v {
            writing-mode: vertical-lr;
            direction: rtl;
            width: ${SLIDER_THICKNESS}px;
            flex-shrink: 0;
            margin-top: -${half}px;
            margin-bottom: -${half}px;
        }
        .tvjs-slider-v::-webkit-slider-runnable-track {
            width: 2px;
            border: none;
            border-radius: 1px;
            background: linear-gradient(
                transparent ${half}px,
                var(--neutral-stroke-rest) ${half}px,
                var(--neutral-stroke-rest) calc(100% - ${half}px),
                transparent calc(100% - ${half}px));
        }
        .tvjs-slider-v::-webkit-slider-thumb { margin-left: -${half - 1}px; }
        .tvjs-slider-v::-moz-range-track {
            width: 2px;
            border: none;
            border-radius: 1px;
            background: linear-gradient(
                transparent ${half}px,
                var(--neutral-stroke-rest) ${half}px,
                var(--neutral-stroke-rest) calc(100% - ${half}px),
                transparent calc(100% - ${half}px));
        }

        /* ── horizontal ── */
        .tvjs-slider-h {
            height: ${SLIDER_THICKNESS}px;
            margin-left: -${half}px;
            margin-right: -${half}px;
        }
        .tvjs-slider-h::-webkit-slider-runnable-track {
            height: 2px;
            border: none;
            border-radius: 1px;
            background: linear-gradient(to right,
                transparent ${half}px,
                var(--neutral-stroke-rest) ${half}px,
                var(--neutral-stroke-rest) calc(100% - ${half}px),
                transparent calc(100% - ${half}px));
        }
        .tvjs-slider-h::-webkit-slider-thumb { margin-top: -${half - 1}px; }
        .tvjs-slider-h::-moz-range-track {
            height: 2px;
            border: none;
            border-radius: 1px;
            background: linear-gradient(to right,
                transparent ${half}px,
                var(--neutral-stroke-rest) ${half}px,
                var(--neutral-stroke-rest) calc(100% - ${half}px),
                transparent calc(100% - ${half}px));
        }
    `;
    container.appendChild(style);
}

function createAxisSlider(state, axis, vertical) {
    const slider = document.createElement('input');
    slider.type = 'range';
    slider.min = '0';
    slider.max = '0';
    slider.value = '0';
    slider.className = vertical ? 'tvjs-slider tvjs-slider-v' : 'tvjs-slider tvjs-slider-h';

    slider.addEventListener('input', () => {
        state.viewPoint[axis] = parseInt(slider.value) || 0;
        onViewPointChanged(state);
    });

    return { element: slider, axis };
}

function createPanel(state, planeType) {
    const container = document.createElement('div');
    container.style.cssText = `
        position:relative; overflow:hidden;
        border:1px solid var(--neutral-stroke-rest); background:transparent;
    `;

    // Slice canvas (bottom) - use auto for bilinear interpolation when zoomed
    const sliceCanvas = document.createElement('canvas');
    sliceCanvas.style.cssText = 'position:absolute; top:0; left:0; transform-origin:0 0; image-rendering:auto;';

    // Overlay canvas (top, transparent)
    const overlayCanvas = document.createElement('canvas');
    overlayCanvas.style.cssText = 'position:absolute; top:0; left:0; transform-origin:0 0; image-rendering:auto;';

    container.append(sliceCanvas, overlayCanvas);

    return {
        container,
        sliceCanvas,
        sliceCtx: sliceCanvas.getContext('2d', { willReadFrequently: true }),
        overlayCanvas,
        overlayCtx: overlayCanvas.getContext('2d'),
        planeType,
        // Screen-space rect for WebGL viewport/scissor (set by fitToViewport)
        screenRect: { x: 0, y: 0, w: 0, h: 0 }
    };
}

function createLoadingOverlay() {
    const root = document.createElement('div');
    root.style.cssText = `
        position:absolute; top:0; left:0; width:100%; height:100%;
        display:flex; flex-direction:column; align-items:center; justify-content:center;
        background:rgb(from var(--neutral-fill-layer-rest) r g b / 85%); z-index:100; pointer-events:none;
        transition: opacity 0.3s;
    `;

    const text = document.createElement('div');
    text.style.cssText = 'font-size:13px; color:var(--neutral-foreground-hint); margin-bottom:8px;';
    text.textContent = 'Loading...';

    const barOuter = document.createElement('div');
    barOuter.style.cssText = 'width:200px; height:4px; background:var(--neutral-stroke-rest); border-radius:2px; overflow:hidden;';
    const barInner = document.createElement('div');
    barInner.style.cssText = 'width:0%; height:100%; background:var(--accent-fill-rest); transition:width 0.15s;';
    barOuter.appendChild(barInner);

    root.append(text, barOuter);
    root.style.display = 'none';

    return { root, text, barInner };
}

function showLoading(state, pct, msg) {
    const ol = state.loadingOverlay;
    ol.root.style.display = 'flex';
    ol.root.style.opacity = '1';
    ol.text.textContent = msg || 'Loading...';
    ol.barInner.style.width = `${Math.round(pct * 100)}%`;
}

function hideLoading(state) {
    const ol = state.loadingOverlay;
    ol.root.style.opacity = '0';
    setTimeout(() => { ol.root.style.display = 'none'; }, 300);
}

// ── Loading ─────────────────────────────────────────────────────────────────

async function loadVolume(state) {
    if (state.abortController) state.abortController.abort();
    state.abortController = new AbortController();
    const signal = state.abortController.signal;

    try {
        // Phase 1: Header
        showLoading(state, 0, 'Loading header...');
        const header = await fetchMRCHeader(state.fileUrl, { signal });
        state.header = header;
        state.dims = { x: header.dimensions.x, y: header.dimensions.y, z: header.dimensions.z };
        state.pixelSize = header.pixelSize.x;

        // Update coordinate input max values
        state.toolbar.xInput.updateMax();
        state.toolbar.yInput.updateMax();
        state.toolbar.zInput.updateMax();

        // Update slider max values
        if (state.sliders.x) state.sliders.x.element.max = state.dims.x - 1;
        if (state.sliders.y) state.sliders.y.element.max = state.dims.y - 1;
        if (state.sliders.z) state.sliders.z.element.max = state.dims.z - 1;

        // Set viewpoint to center
        state.viewPoint = {
            x: Math.floor(state.dims.x / 2),
            y: Math.floor(state.dims.y / 2),
            z: Math.floor(state.dims.z / 2)
        };
        updateCoordInputs(state);

        // Size panels
        resizePanels(state);

        // Phase 2: Central XY slice for quick preview
        showLoading(state, 0.05, 'Loading preview slice...');
        const centralZ = Math.floor(state.dims.z / 2);
        const centralSlice = await fetchSlice(state.fileUrl, header, centralZ, { signal });

        // Compute intensity stats from central slice
        computeIntensityStats(state, centralSlice);

        // Allocate ImageData objects
        allocateImageData(state);

        // Render XY preview
        renderSlice(centralSlice, state.imageData.xy, state.panels.xy.sliceCtx, state.dims.x, state.dims.y, state.minIntensity, state.maxIntensity);
        updateTransforms(state);

        // Phase 3: Full volume
        showLoading(state, 0.1, 'Loading full volume...');
        const { data } = await fetchMRC(state.fileUrl, {
            signal,
            onProgress: (received, total) => {
                const pct = 0.1 + 0.9 * (received / total);
                showLoading(state, pct, `Loading volume: ${Math.round(pct * 100)}%`);
            }
        });

        // Split into per-slice views
        state.volumeSlices = splitIntoSlices(data, header);

        // Allocate extraction buffers
        state.xyBuffer = new Float32Array(state.dims.x * state.dims.y);
        state.xzBuffer = new Float32Array(state.dims.x * state.dims.z);
        state.zyBuffer = new Float32Array(state.dims.z * state.dims.y);

        state.isVolumeLoaded = true;
        state.sliceCache = { x: -1, y: -1, z: -1, slab: -1 };

        hideLoading(state);
        renderAllSlices(state);
        renderAllOverlays(state);
        fitToViewport(state);

    } catch (err) {
        if (err.name === 'AbortError') return;
        console.error('TomogramViewerJs: load error', err);
        showLoading(state, 0, `Error: ${err.message}`);
    }
}

function splitIntoSlices(data, header) {
    const sliceVoxels = header.sliceVoxelCount;
    const slices = new Array(header.dimensions.z);
    for (let z = 0; z < header.dimensions.z; z++) {
        slices[z] = data.subarray(z * sliceVoxels, (z + 1) * sliceVoxels);
    }
    return slices;
}

function computeIntensityStats(state, sliceData) {
    // Use central 50% crop of the XY slice (25%-75% in each dimension)
    const { dims } = state;
    const x0 = Math.floor(dims.x * 0.25);
    const x1 = Math.floor(dims.x * 0.75);
    const y0 = Math.floor(dims.y * 0.25);
    const y1 = Math.floor(dims.y * 0.75);

    // Collect values from the central crop
    let sum = 0;
    let count = 0;
    for (let y = y0; y < y1; y++) {
        const rowOffset = y * dims.x;
        for (let x = x0; x < x1; x++) {
            sum += sliceData[rowOffset + x];
            count++;
        }
    }

    const mean = sum / count;

    let sumSqDiff = 0;
    for (let y = y0; y < y1; y++) {
        const rowOffset = y * dims.x;
        for (let x = x0; x < x1; x++) {
            const d = sliceData[rowOffset + x] - mean;
            sumSqDiff += d * d;
        }
    }
    const std = Math.sqrt(sumSqDiff / count);

    state.intensityMean = mean;
    state.intensityStd = std;
    applyIntensityRange(state);
}

function applyIntensityRange(state) {
    state.minIntensity = state.intensityMean - state.sigmaCutoff * state.intensityStd;
    state.maxIntensity = state.intensityMean + state.sigmaCutoff * state.intensityStd;
}

// ── Slice extraction ────────────────────────────────────────────────────────

function getXYSlice(state, z) {
    const slab = state.slabThickness;
    if (slab <= 1) return state.volumeSlices[z];

    const { dims, volumeSlices, xyBuffer } = state;
    const n = dims.x * dims.y;
    const z0 = Math.max(0, z - Math.floor((slab - 1) / 2));
    const z1 = Math.min(dims.z - 1, z0 + slab - 1);
    const count = z1 - z0 + 1;
    const invCount = 1 / count;

    // Start from first slice
    const first = volumeSlices[z0];
    for (let i = 0; i < n; i++) xyBuffer[i] = first[i];

    // Accumulate remaining slices
    for (let sz = z0 + 1; sz <= z1; sz++) {
        const src = volumeSlices[sz];
        for (let i = 0; i < n; i++) xyBuffer[i] += src[i];
    }

    // Divide by count
    for (let i = 0; i < n; i++) xyBuffer[i] *= invCount;

    return xyBuffer;
}

function extractXZSlice(state, y) {
    const { dims, volumeSlices, xzBuffer } = state;
    const slab = state.slabThickness;
    const y0 = Math.max(0, y - Math.floor((slab - 1) / 2));
    const y1 = Math.min(dims.y - 1, y0 + slab - 1);
    const count = y1 - y0 + 1;
    const invCount = 1 / count;

    for (let z = 0; z < dims.z; z++) {
        const dstOffset = z * dims.x;
        const slice = volumeSlices[z];

        // First row
        const srcOffset0 = y0 * dims.x;
        for (let xx = 0; xx < dims.x; xx++) {
            xzBuffer[dstOffset + xx] = slice[srcOffset0 + xx];
        }

        // Accumulate remaining rows
        for (let sy = y0 + 1; sy <= y1; sy++) {
            const srcOffset = sy * dims.x;
            for (let xx = 0; xx < dims.x; xx++) {
                xzBuffer[dstOffset + xx] += slice[srcOffset + xx];
            }
        }

        // Divide
        for (let xx = 0; xx < dims.x; xx++) {
            xzBuffer[dstOffset + xx] *= invCount;
        }
    }

    return xzBuffer;
}

function extractZYSlice(state, x) {
    const { dims, volumeSlices, zyBuffer } = state;
    const slab = state.slabThickness;
    const x0 = Math.max(0, x - Math.floor((slab - 1) / 2));
    const x1 = Math.min(dims.x - 1, x0 + slab - 1);
    const count = x1 - x0 + 1;
    const invCount = 1 / count;

    for (let z = 0; z < dims.z; z++) {
        const slice = volumeSlices[z];
        for (let yy = 0; yy < dims.y; yy++) {
            const rowBase = yy * dims.x;
            let sum = 0;
            for (let sx = x0; sx <= x1; sx++) {
                sum += slice[rowBase + sx];
            }
            zyBuffer[yy * dims.z + z] = sum * invCount;
        }
    }

    return zyBuffer;
}

// ── Rendering ───────────────────────────────────────────────────────────────

function allocateImageData(state) {
    const { dims } = state;
    state.imageData.xy = new ImageData(dims.x, dims.y);
    state.imageData.xz = new ImageData(dims.x, dims.z);
    state.imageData.zy = new ImageData(dims.z, dims.y);
}

function renderSlice(sliceData, imageData, ctx, w, h, minI, maxI) {
    const pixels = imageData.data;
    const scale = maxI > minI ? 255 / (maxI - minI) : 255;
    for (let yy = 0; yy < h; yy++) {
        const srcRow = (h - 1 - yy) * w; // Y-flip
        const dstRow = yy * w * 4;
        for (let xx = 0; xx < w; xx++) {
            const raw = (sliceData[srcRow + xx] - minI) * scale;
            const val = raw < 0 ? 0 : (raw > 255 ? 255 : raw);
            const idx = dstRow + xx * 4;
            pixels[idx] = val;
            pixels[idx + 1] = val;
            pixels[idx + 2] = val;
            pixels[idx + 3] = 255;
        }
    }
    ctx.putImageData(imageData, 0, 0);
}

function renderAllSlices(state) {
    if (!state.isVolumeLoaded) return;
    const { dims, viewPoint, slabThickness } = state;

    // If slab changed, invalidate everything
    const slabChanged = state.sliceCache.slab !== slabThickness;

    // XY
    if (slabChanged || state.sliceCache.z !== viewPoint.z) {
        const xyData = getXYSlice(state, viewPoint.z);
        renderSlice(xyData, state.imageData.xy, state.panels.xy.sliceCtx, dims.x, dims.y, state.minIntensity, state.maxIntensity);
        state.sliceCache.z = viewPoint.z;
    }

    // XZ
    if (slabChanged || state.sliceCache.y !== viewPoint.y) {
        const xzData = extractXZSlice(state, viewPoint.y);
        renderSlice(xzData, state.imageData.xz, state.panels.xz.sliceCtx, dims.x, dims.z, state.minIntensity, state.maxIntensity);
        state.sliceCache.y = viewPoint.y;
    }

    // ZY
    if (slabChanged || state.sliceCache.x !== viewPoint.x) {
        const zyData = extractZYSlice(state, viewPoint.x);
        renderSlice(zyData, state.imageData.zy, state.panels.zy.sliceCtx, dims.z, dims.y, state.minIntensity, state.maxIntensity);
        state.sliceCache.x = viewPoint.x;
    }

    state.sliceCache.slab = slabThickness;
}

function renderAllOverlays(state) {
    for (const planeType of ['xy', 'xz', 'zy']) {
        renderOverlay(state, planeType);
    }
}

function renderOverlay(state, planeType) {
    const panel = state.panels[planeType];
    if (!panel) return;
    const ctx = panel.overlayCtx;
    const canvas = panel.overlayCanvas;

    // Size overlay to container for native-resolution drawing
    const cw = panel.container.clientWidth;
    const ch = panel.container.clientHeight;
    if (canvas.width !== cw || canvas.height !== ch) {
        canvas.width = cw;
        canvas.height = ch;
    }

    // Clear at screen resolution, then set tomogram-space transform
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    const t = state.translate[planeType];
    ctx.setTransform(state.zoom, 0, 0, state.zoom, t.x, t.y);

    // Crosshairs at current viewpoint
    renderCrosshairs(state, ctx, planeType, state.viewPoint);

    // Particles (circles/squares — skip when model mode is active)
    if (!state.model.active) {
        for (const sp of state.species) {
            if (sp.particles.length > 0 && sp.settings.visible) {
                renderSpeciesParticles(state, sp, ctx, planeType);
            }
        }
    }

    // Preview circle if hovering and picking
    if (state._hoverPlane && state.isPicking && state._hoverTomogramCoords) {
        renderPreviewCircle(state, ctx, planeType, state._hoverTomogramCoords);
    }

    ctx.setTransform(1, 0, 0, 1, 0, 0);
}

function renderCrosshairs(state, ctx, planeType, coords) {
    const { dims } = state;
    let hx1, hy1, hx2, hy2; // horizontal line
    let vx1, vy1, vx2, vy2; // vertical line

    if (planeType === 'xy') {
        // X horizontal, Y vertical (flipped)
        const svgX = coords.x;
        const svgY = dims.y - 1 - coords.y;
        hx1 = 0; hy1 = svgY; hx2 = dims.x; hy2 = svgY;
        vx1 = svgX; vy1 = 0; vx2 = svgX; vy2 = dims.y;
    } else if (planeType === 'xz') {
        const svgX = coords.x;
        const svgY = dims.z - 1 - coords.z;
        hx1 = 0; hy1 = svgY; hx2 = dims.x; hy2 = svgY;
        vx1 = svgX; vy1 = 0; vx2 = svgX; vy2 = dims.z;
    } else { // zy
        const svgX = coords.z;
        const svgY = dims.y - 1 - coords.y;
        hx1 = 0; hy1 = svgY; hx2 = dims.z; hy2 = svgY;
        vx1 = svgX; vy1 = 0; vx2 = svgX; vy2 = dims.y;
    }

    ctx.save();
    ctx.strokeStyle = '#00BFFF';
    ctx.lineWidth = 1 / state.zoom;
    ctx.setLineDash([]);

    ctx.beginPath();
    ctx.moveTo(hx1, hy1);
    ctx.lineTo(hx2, hy2);
    ctx.stroke();

    ctx.beginPath();
    ctx.moveTo(vx1, vy1);
    ctx.lineTo(vx2, vy2);
    ctx.stroke();

    ctx.restore();
}

function renderSpeciesParticles(state, species, ctx, planeType) {
    const { dims, viewPoint, pixelSize } = state;
    const settings = species.settings;
    const radius = settings.diameter / (2 * pixelSize);
    const cubeHalf = settings.diameter / (2 * pixelSize);

    ctx.save();
    ctx.strokeStyle = settings.color;
    ctx.lineWidth = settings.strokeWidth / state.zoom;
    ctx.setLineDash([]);

    for (const p of species.particles) {
        const px = p.x, py = p.y, pz = p.z;
        let d, cx, cy;

        if (planeType === 'xy') {
            d = viewPoint.z - pz;
            cx = px;
            cy = dims.y - 1 - py;
        } else if (planeType === 'xz') {
            d = viewPoint.y - py;
            cx = px;
            cy = dims.z - 1 - pz;
        } else { // zy
            d = viewPoint.x - px;
            cx = pz;
            cy = dims.y - 1 - py;
        }

        if (Math.abs(d) > radius) continue;
        const intersectionR = Math.sqrt(radius * radius - d * d);

        const shape = settings.shape;
        if (shape === 'circle') {
            ctx.beginPath();
            ctx.arc(cx, cy, intersectionR, 0, 2 * Math.PI);
            ctx.stroke();
        } else if (shape === 'square') {
            ctx.strokeRect(cx - cubeHalf, cy - cubeHalf, 2 * cubeHalf, 2 * cubeHalf);
        }
    }

    ctx.restore();
}

function renderPreviewCircle(state, ctx, planeType, coords) {
    const sp = getSelectedSpecies(state);
    if (!sp) return;
    const { dims, viewPoint, pixelSize } = state;
    const settings = sp.settings;
    const radius = settings.diameter / (2 * pixelSize);

    let d, cx, cy;
    if (planeType === 'xy') {
        d = viewPoint.z - coords.z;
        cx = coords.x;
        cy = dims.y - 1 - coords.y;
    } else if (planeType === 'xz') {
        d = viewPoint.y - coords.y;
        cx = coords.x;
        cy = dims.z - 1 - coords.z;
    } else { // zy
        d = viewPoint.x - coords.x;
        cx = coords.z;
        cy = dims.y - 1 - coords.y;
    }

    if (Math.abs(d) > radius) return;
    const r = Math.sqrt(radius * radius - d * d);

    ctx.save();
    ctx.strokeStyle = settings.color;
    ctx.lineWidth = settings.strokeWidth / state.zoom;
    const dashLen = 3 / state.zoom;
    ctx.setLineDash([dashLen, dashLen]);
    ctx.beginPath();
    ctx.arc(cx, cy, r, 0, 2 * Math.PI);
    ctx.stroke();
    ctx.restore();
}

// ── Layout & transforms ─────────────────────────────────────────────────────

function resizePanels(state) {
    const { dims } = state;
    if (dims.x === 0 || dims.y === 0 || dims.z === 0) return;

    // Set canvas native sizes (1 voxel = 1 pixel)
    const panels = state.panels;

    // XY: dimX x dimY
    setCanvasSize(panels.xy, dims.x, dims.y);
    // XZ: dimX x dimZ
    setCanvasSize(panels.xz, dims.x, dims.z);
    // ZY: dimZ x dimY
    setCanvasSize(panels.zy, dims.z, dims.y);

    // Calculate panel scale to fit viewport
    fitToViewport(state);
}

function setCanvasSize(panel, w, h) {
    panel.sliceCanvas.width = w;
    panel.sliceCanvas.height = h;
    // Overlay canvas is sized to container in render functions for native-resolution drawing
}

function fitToViewport(state) {
    const { dims, container } = state;
    if (dims.x === 0) return;

    const rect = container.getBoundingClientRect();
    const toolbarHeight = 62;
    const gutter = 10;
    const sliderSpace = SLIDER_THICKNESS + SLIDER_GAP;
    const viewW = rect.width - gutter;
    const viewH = rect.height - toolbarHeight - 2 * gutter;

    // Left column width includes vertical slider; height includes horizontal slider
    const scaleX = (viewW - sliderSpace) / (dims.x + dims.z + gutter);
    const scaleY = (viewH - sliderSpace) / (dims.y + dims.z + gutter);
    const scale = Math.min(scaleX, scaleY);

    state.zoom = scale;

    const panelWidthXY = dims.x * scale;
    const panelHeightXY = dims.y * scale;
    const panelWidthXZ = dims.x * scale;
    const panelHeightXZ = dims.z * scale;
    const panelWidthZY = dims.z * scale;
    const panelHeightZY = dims.y * scale;

    // Set panel sizes
    state.panels.xy.container.style.width = panelWidthXY + 'px';
    state.panels.xy.container.style.height = panelHeightXY + 'px';
    state.panels.xz.container.style.width = panelWidthXZ + 'px';
    state.panels.xz.container.style.height = panelHeightXZ + 'px';
    state.panels.zy.container.style.width = panelWidthZY + 'px';
    state.panels.zy.container.style.height = panelHeightZY + 'px';

    // Size sliders: thumb-size larger than panel so thumb center reaches panel edges
    // Negative margins in CSS compensate so layout size = panel size
    if (state.sliders.z) state.sliders.z.element.style.height = (panelHeightXZ + THUMB_SIZE) + 'px';
    if (state.sliders.y) state.sliders.y.element.style.height = (panelHeightXY + THUMB_SIZE) + 'px';
    if (state.sliders.x) state.sliders.x.element.style.width = (panelWidthXY + THUMB_SIZE) + 'px';

    // Align ZY panel bottom with XY panel bottom (offset by X slider row height)
    const xSliderRowHeight = SLIDER_THICKNESS + SLIDER_GAP;
    state.panels.zy.container.style.marginBottom = xSliderRowHeight + 'px';

    // Reset translates to 0 (CSS transform scale handles fitting)
    state.translate.xy = { x: 0, y: 0 };
    state.translate.xz = { x: 0, y: 0 };
    state.translate.zy = { x: 0, y: 0 };

    updateTransforms(state);
    updateZoomDisplay(state);
    if (state.model.active) requestModelRender(state);
}

function updateTransforms(state) {
    for (const planeType of ['xy', 'xz', 'zy']) {
        const panel = state.panels[planeType];
        const t = state.translate[planeType];
        const transform = `translate(${t.x}px, ${t.y}px) scale(${state.zoom})`;
        panel.sliceCanvas.style.transform = transform;
        // Overlay is drawn at native screen resolution via canvas context transform
        panel.overlayCanvas.style.transform = 'none';
    }
}

function clampTranslateAxis(t, imageDim, containerDim) {
    const scaled = imageDim;
    if (scaled >= containerDim) {
        // Image larger than container: keep image filling the panel
        return Math.max(containerDim - scaled, Math.min(0, t));
    } else {
        // Image smaller than container: keep image within panel
        return Math.max(0, Math.min(containerDim - scaled, t));
    }
}

function zoomAroundViewPoint(state, newZoom) {
    const oldZoom = state.zoom;
    const { dims, viewPoint } = state;
    state.zoom = newZoom;

    // For each panel, adjust translate so the viewPoint stays at panel center
    for (const planeType of ['xy', 'xz', 'zy']) {
        const panel = state.panels[planeType];
        const cw = panel.container.clientWidth;
        const ch = panel.container.clientHeight;

        // Canvas-space voxel coords of the viewPoint (matching the flip used by rendering)
        let vx, vy;
        if (planeType === 'xy') {
            vx = viewPoint.x;
            vy = dims.y - 1 - viewPoint.y;
        } else if (planeType === 'xz') {
            vx = viewPoint.x;
            vy = dims.z - 1 - viewPoint.z;
        } else { // zy
            vx = viewPoint.z;
            vy = dims.y - 1 - viewPoint.y;
        }

        // New translate so viewPoint maps to panel center
        state.translate[planeType].x = cw / 2 - newZoom * vx;
        state.translate[planeType].y = ch / 2 - newZoom * vy;
    }

    clampAllTranslates(state);
    updateTransforms(state);
    updateZoomDisplay(state);
}

function clampAllTranslates(state) {
    const zoom = state.zoom;
    const { dims } = state;

    for (const planeType of ['xy', 'xz', 'zy']) {
        const panel = state.panels[planeType];
        const t = state.translate[planeType];
        const cw = panel.container.clientWidth;
        const ch = panel.container.clientHeight;

        let imgW, imgH;
        if (planeType === 'xy')      { imgW = dims.x * zoom; imgH = dims.y * zoom; }
        else if (planeType === 'xz') { imgW = dims.x * zoom; imgH = dims.z * zoom; }
        else                         { imgW = dims.z * zoom; imgH = dims.y * zoom; }

        t.x = clampTranslateAxis(t.x, imgW, cw);
        t.y = clampTranslateAxis(t.y, imgH, ch);
    }
}

function updateZoomDisplay(state) {
    if (state.toolbar.zoomDisplay) {
        state.toolbar.zoomDisplay.textContent = `${Math.round(state.zoom * 100)}%`;
    }
}

function updateCoordInputs(state) {
    if (state.toolbar.xInput) state.toolbar.xInput.input.value = state.viewPoint.x;
    if (state.toolbar.yInput) state.toolbar.yInput.input.value = state.viewPoint.y;
    if (state.toolbar.zInput) state.toolbar.zInput.input.value = state.viewPoint.z;

    if (state.sliders.x) state.sliders.x.element.value = state.viewPoint.x;
    if (state.sliders.y) state.sliders.y.element.value = state.viewPoint.y;
    if (state.sliders.z) state.sliders.z.element.value = state.viewPoint.z;
}

function updateCursorStyle(state) {
    const cursor = state.isPicking ? 'crosshair' : 'default';
    for (const planeType of ['xy', 'xz', 'zy']) {
        state.panels[planeType].container.style.cursor = cursor;
    }
}

// ── Mouse interaction ───────────────────────────────────────────────────────

function setupMouseHandlers(state) {
    for (const planeType of ['xy', 'xz', 'zy']) {
        const panel = state.panels[planeType];

        panel._onMouseDown = (e) => handleMouseDown(state, planeType, e);
        panel._onWheel = (e) => handleWheel(state, planeType, e);
        panel._onMouseMove = (e) => handleMouseMove(state, planeType, e);
        panel._onMouseLeave = () => handleMouseLeave(state, planeType);

        // Suppress context menu for right-click panning
        panel._onContextMenu = (e) => e.preventDefault();

        panel.container.addEventListener('mousedown', panel._onMouseDown);
        panel.container.addEventListener('wheel', panel._onWheel, { passive: false });
        panel.container.addEventListener('mousemove', panel._onMouseMove);
        panel.container.addEventListener('mouseleave', panel._onMouseLeave);
        panel.container.addEventListener('contextmenu', panel._onContextMenu);
    }
}

function screenToTomogramCoords(screenX, screenY, planeType, state) {
    const t = state.translate[planeType];
    const canvasX = (screenX - t.x) / state.zoom;
    const canvasY = (screenY - t.y) / state.zoom;
    const { dims, viewPoint } = state;

    let x, y, z;
    if (planeType === 'xy') {
        x = canvasX;
        y = dims.y - 1 - canvasY; // reverse Y-flip
        z = viewPoint.z;
    } else if (planeType === 'xz') {
        x = canvasX;
        y = viewPoint.y;
        z = dims.z - 1 - canvasY; // reverse Z-flip
    } else { // zy
        x = viewPoint.x;
        y = dims.y - 1 - canvasY; // reverse Y-flip
        z = canvasX;
    }

    return {
        x: Math.round(Math.max(0, Math.min(x, dims.x - 1))),
        y: Math.round(Math.max(0, Math.min(y, dims.y - 1))),
        z: Math.round(Math.max(0, Math.min(z, dims.z - 1)))
    };
}

function handleMouseDown(state, planeType, e) {
    if (!state.isVolumeLoaded) return;

    // Right-click: start panning (synced across panels sharing axes)
    if (e.button === 2) {
        const panel = state.panels[planeType];
        panel._isPanning = true;
        panel._panStartX = e.clientX;
        panel._panStartY = e.clientY;

        const onMouseMovePan = (ev) => {
            if (!panel._isPanning) return;
            const dx = ev.clientX - panel._panStartX;
            const dy = ev.clientY - panel._panStartY;
            panel._panStartX = ev.clientX;
            panel._panStartY = ev.clientY;

            // Apply delta to the dragged panel
            state.translate[planeType].x += dx;
            state.translate[planeType].y += dy;

            // Sync shared axes to other panels:
            // XY: h=X, v=Y | XZ: h=X, v=Z | ZY: h=Z, v=Y
            if (planeType === 'xy') {
                state.translate.xz.x += dx;  // shared X axis (horizontal)
                state.translate.zy.y += dy;  // shared Y axis (vertical)
            } else if (planeType === 'xz') {
                state.translate.xy.x += dx;  // shared X axis (horizontal)
                state.translate.zy.x += dy;  // shared Z axis (xz.v=Z, zy.h=Z)
            } else { // zy
                state.translate.xz.y += dx;  // shared Z axis (zy.h=Z, xz.v=Z)
                state.translate.xy.y += dy;  // shared Y axis (vertical)
            }

            // Clamp all panels to tomogram boundaries
            clampAllTranslates(state);

            updateTransforms(state);
            renderAllOverlays(state);
            if (state.model.active) requestModelRender(state, true);
        };

        const onMouseUpPan = () => {
            panel._isPanning = false;
            document.removeEventListener('mousemove', onMouseMovePan);
            document.removeEventListener('mouseup', onMouseUpPan);
        };

        document.addEventListener('mousemove', onMouseMovePan);
        document.addEventListener('mouseup', onMouseUpPan);
        return;
    }

    if (e.button !== 0) return; // left click only

    const rect = state.panels[planeType].container.getBoundingClientRect();
    const coords = screenToTomogramCoords(e.clientX - rect.left, e.clientY - rect.top, planeType, state);

    if (state.isPicking) {
        // Add particle to selected species
        const sp = getSelectedSpecies(state);
        if (state.dotNetRef && sp) {
            state.dotNetRef.invokeMethodAsync('OnParticleAddedFromJs',
                state.selectedSpeciesIndex, coords.x, coords.y, coords.z);
        }
    } else {
        // Navigate
        if (planeType === 'xy') {
            state.viewPoint.x = coords.x;
            state.viewPoint.y = coords.y;
        } else if (planeType === 'xz') {
            state.viewPoint.x = coords.x;
            state.viewPoint.z = coords.z;
        } else { // zy
            state.viewPoint.z = coords.z;
            state.viewPoint.y = coords.y;
        }
        onViewPointChanged(state);
    }
}

function handleWheel(state, planeType, e) {
    e.preventDefault();
    if (!state.isVolumeLoaded) return;

    // Cmd/Ctrl+scroll → zoom centered on mouse position
    if (e.metaKey || e.ctrlKey) {
        handleZoomWheel(state, planeType, e);
        return;
    }

    // Accumulate scroll delta for smooth trackpad + crisp mouse wheel
    if (!state._sliceScrollAccum) state._sliceScrollAccum = 0;

    if (e.deltaMode === 1) {
        // Line mode: 1 slice per line
        state._sliceScrollAccum += Math.sign(e.deltaY);
    } else {
        // Pixel mode (trackpad & most macOS mouse wheels):
        // Normalize to ±1 max per event so a single wheel notch never exceeds 1 slice,
        // while trackpad's small deltas (~1-10px) accumulate gradually.
        state._sliceScrollAccum += Math.max(-1, Math.min(1, e.deltaY / 50));
    }

    const slices = Math.trunc(state._sliceScrollAccum);
    if (slices === 0) return;
    state._sliceScrollAccum -= slices;

    const { dims } = state;

    // Scroll changes the orthogonal axis for the hovered plane
    if (planeType === 'xy') {
        state.viewPoint.z = Math.max(0, Math.min(state.viewPoint.z + slices, dims.z - 1));
    } else if (planeType === 'xz') {
        state.viewPoint.y = Math.max(0, Math.min(state.viewPoint.y + slices, dims.y - 1));
    } else { // zy
        state.viewPoint.x = Math.max(0, Math.min(state.viewPoint.x + slices, dims.x - 1));
    }

    onViewPointChanged(state);
}

function handleZoomWheel(state, planeType, e) {
    // Normalize delta across input devices:
    // - Mouse wheel (deltaMode 1 = lines): large discrete steps, typically ±3
    // - Trackpad pinch (deltaMode 0 = pixels): small continuous values, typically ±1..10
    // ctrlKey is also set by trackpad pinch-to-zoom on most browsers
    let delta;
    if (e.deltaMode === 1) {
        // Line mode (mouse wheel): use fixed step per notch
        delta = -e.deltaY * 0.05;
    } else {
        // Pixel mode (trackpad): scale down for smooth feel
        delta = -e.deltaY * 0.005;
    }

    const factor = Math.pow(2, delta);
    const newZoom = Math.max(0.1, Math.min(10, state.zoom * factor));
    if (newZoom === state.zoom) return;

    const oldZoom = state.zoom;
    state.zoom = newZoom;

    // Mouse position relative to the hovered panel
    const panel = state.panels[planeType];
    const rect = panel.container.getBoundingClientRect();
    const mouseX = e.clientX - rect.left;
    const mouseY = e.clientY - rect.top;

    // Convert mouse position to canvas-space voxel in hovered panel
    const t = state.translate[planeType];
    const canvasVoxelX = (mouseX - t.x) / oldZoom;
    const canvasVoxelY = (mouseY - t.y) / oldZoom;

    // Convert to tomogram coordinates (canvas Y is flipped for Y/Z axes)
    const { dims, viewPoint } = state;
    let tomX = viewPoint.x, tomY = viewPoint.y, tomZ = viewPoint.z;
    if (planeType === 'xy') { tomX = canvasVoxelX; tomY = dims.y - 1 - canvasVoxelY; }
    else if (planeType === 'xz') { tomX = canvasVoxelX; tomZ = dims.z - 1 - canvasVoxelY; }
    else { tomZ = canvasVoxelX; tomY = dims.y - 1 - canvasVoxelY; } // zy

    // For ALL panels, anchor zoom on where this tomogram voxel appears
    for (const pt of ['xy', 'xz', 'zy']) {
        let cx, cy;
        if (pt === 'xy') { cx = tomX; cy = dims.y - 1 - tomY; }
        else if (pt === 'xz') { cx = tomX; cy = dims.z - 1 - tomZ; }
        else { cx = tomZ; cy = dims.y - 1 - tomY; }

        const tp = state.translate[pt];
        // screenPos = tp + zoom * cx → keep screenPos constant across zoom change
        // old: screenPos = tp.x + oldZoom * cx → new: screenPos = tp.x' + newZoom * cx
        // → tp.x' = tp.x + (oldZoom - newZoom) * cx
        tp.x += (oldZoom - newZoom) * cx;
        tp.y += (oldZoom - newZoom) * cy;
    }

    clampAllTranslates(state);
    updateTransforms(state);
    updateZoomDisplay(state);
    renderAllOverlays(state);
    if (state.model.active) requestModelRender(state, true);
}

function handleMouseMove(state, planeType, e) {
    if (!state.isVolumeLoaded) return;

    const rect = state.panels[planeType].container.getBoundingClientRect();
    const coords = screenToTomogramCoords(e.clientX - rect.left, e.clientY - rect.top, planeType, state);

    state._hoverPlane = planeType;
    state._hoverTomogramCoords = coords;

    // Re-render overlays to show crosshairs at hover position and preview circle
    renderAllOverlaysWithHover(state, coords);
}

function handleMouseLeave(state, planeType) {
    state._hoverPlane = null;
    state._hoverTomogramCoords = null;

    // Re-render overlays without hover
    renderAllOverlays(state);
}

function renderAllOverlaysWithHover(state, hoverCoords) {
    for (const planeType of ['xy', 'xz', 'zy']) {
        const panel = state.panels[planeType];
        if (!panel) continue;
        const ctx = panel.overlayCtx;
        const canvas = panel.overlayCanvas;

        // Size overlay to container for native-resolution drawing
        const cw = panel.container.clientWidth;
        const ch = panel.container.clientHeight;
        if (canvas.width !== cw || canvas.height !== ch) {
            canvas.width = cw;
            canvas.height = ch;
        }

        // Clear at screen resolution, then set tomogram-space transform
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        const t = state.translate[planeType];
        ctx.setTransform(state.zoom, 0, 0, state.zoom, t.x, t.y);

        // Crosshairs at hover position (across all planes)
        renderCrosshairs(state, ctx, planeType, hoverCoords);

        // Particles (circles/squares — skip when model mode is active)
        if (!state.model.active) {
            for (const sp of state.species) {
                if (sp.particles.length > 0 && sp.settings.visible) {
                    renderSpeciesParticles(state, sp, ctx, planeType);
                }
            }
        }

        // Preview circle if picking
        if (state.isPicking && state._hoverTomogramCoords) {
            renderPreviewCircle(state, ctx, planeType, state._hoverTomogramCoords);
        }

        ctx.setTransform(1, 0, 0, 1, 0, 0);
    }
}

function onViewPointChanged(state) {
    state.sliceCache = { x: -1, y: -1, z: -1, slab: -1 }; // invalidate all caches
    renderAllSlices(state);
    renderAllOverlays(state);
    updateCoordInputs(state);
    if (state.model.active) requestModelRender(state, true);
}

// ── Fullscreen ───────────────────────────────────────────────────────────────

function toggleFullscreen(state) {
    if (document.fullscreenElement === state.container) {
        document.exitFullscreen();
    } else {
        state.container.requestFullscreen();
    }
}

function setupFullscreenListener(state) {
    state._onFullscreenChange = () => {
        const isFs = document.fullscreenElement === state.container;
        const btn = state.toolbar.fullscreenBtn;
        if (btn) {
            btn.style.background = isFs ? 'var(--accent-fill-rest)' : 'var(--neutral-layer-1)';
            btn.style.color = isFs ? 'var(--foreground-on-accent-rest)' : 'var(--neutral-foreground-rest)';
            btn.style.borderColor = isFs ? 'var(--accent-fill-rest)' : 'var(--neutral-stroke-rest)';
        }
        // ResizeObserver will handle fitToViewport automatically
    };
    document.addEventListener('fullscreenchange', state._onFullscreenChange);
}

// ── Resize observer ─────────────────────────────────────────────────────────

function setupResizeObserver(state) {
    let timeout = null;
    state.resizeObserver = new ResizeObserver(() => {
        clearTimeout(timeout);
        timeout = setTimeout(() => {
            if (state.dims.x > 0) {
                if (state.viewMode === '3d') {
                    resizeModelCanvas(state);
                    requestModelRender(state);
                } else {
                    fitToViewport(state);
                    if (state.isVolumeLoaded) {
                        renderAllSlices(state);
                        renderAllOverlays(state);
                    }
                    if (state.model.active) {
                        resizeModelCanvas(state);
                        requestModelRender(state);
                    }
                }
            }
        }, 100);
    });
    state.resizeObserver.observe(state.container);
}

// ═══════════════════════════════════════════════════════════════════════════════
// 3D MODEL RENDERING
// ═══════════════════════════════════════════════════════════════════════════════

// ── Instanced rendering shaders ─────────────────────────────────────────────

const MODEL_VERTEX_SHADER = `#version 300 es
precision highp float;

// Per-vertex (template mesh)
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aSH_A;
layout(location = 3) in vec4 aSH_B;
layout(location = 4) in float aSH_C;

// Per-instance
layout(location = 5) in vec3 aInstanceTranslation;
layout(location = 6) in vec3 aInstanceRotCol0;
layout(location = 7) in vec3 aInstanceRotCol1;
layout(location = 8) in vec3 aInstanceRotCol2;

uniform mat4 uProjection;
uniform mat4 uView;
uniform vec3 uViewDir;         // view direction (eye toward target)
uniform vec3 uLight0;          // key light direction (toward light)
uniform vec3 uLight1;          // fill light 1
uniform vec3 uLight2;          // fill light 2
uniform float uModelScale;     // angstroms -> tomogram voxels
uniform vec3 uHalfDims;        // tomogram dims / 2

out vec3 vNormal;
out vec3 vViewDir;
out float vVis0;
out float vVis1;
out float vVis2;
out float vAmbientVis;
out float vSky;

${SH_EVALUATE_GLSL}

void main() {
    mat3 instanceRot = mat3(aInstanceRotCol0, aInstanceRotCol1, aInstanceRotCol2);

    // Rotate and scale template vertex into tomogram voxel space
    vec3 rotatedPos = instanceRot * (aPosition * uModelScale);
    vec3 worldPos = rotatedPos + (aInstanceTranslation - uHalfDims);
    // Normal in world space
    vec3 worldNormal = normalize(instanceRot * aNormal);
    vNormal = worldNormal;

    // SH was baked in template space — transform light dirs into template space
    mat3 invRot = transpose(instanceRot);

    vec3 L0_local = normalize(invRot * uLight0);
    vec3 L1_local = normalize(invRot * uLight1);
    vec3 L2_local = normalize(invRot * uLight2);
    vec3 N_local = normalize(invRot * worldNormal);

    vVis0 = evaluateSH(L0_local, aSH_A, aSH_B, aSH_C);
    vVis1 = evaluateSH(L1_local, aSH_A, aSH_B, aSH_C);
    vVis2 = evaluateSH(L2_local, aSH_A, aSH_B, aSH_C);
    vAmbientVis = evaluateSH(N_local, aSH_A, aSH_B, aSH_C);

    vSky = clamp(worldPos.y / uHalfDims.y * 0.5 + 0.5, 0.0, 1.0);
    vViewDir = uViewDir;

    gl_Position = uProjection * uView * vec4(worldPos, 1.0);
}
`;

const MODEL_FRAGMENT_SHADER = `#version 300 es
precision highp float;

in vec3 vNormal;
in vec3 vViewDir;
in float vVis0;
in float vVis1;
in float vVis2;
in float vAmbientVis;
in float vSky;

uniform vec3 uColor;
uniform vec3 uLight0;
uniform vec3 uLight1;
uniform vec3 uLight2;

out vec4 fragColor;

void main() {
    vec3 N = normalize(vNormal);
    vec3 V = normalize(-vViewDir);   // surface toward eye (vViewDir is eye toward target)
    if (dot(N, V) < 0.0) N = -N;

    vec3 L0 = uLight0;
    vec3 L1 = uLight1;
    vec3 L2 = uLight2;

    float diff0 = max(dot(N, L0), 0.0);
    float diff1 = max(dot(N, L1), 0.0);
    float diff2 = max(dot(N, L2), 0.0);

    float ambientLevel = mix(0.15, 0.35, vSky) * 0.9 + 0.1;

    vec3 color = uColor * ambientLevel * vAmbientVis
               + uColor * 0.65 * diff0 * vVis0
               + uColor * 0.20 * diff1 * vVis1
               + uColor * 0.40 * diff2 * vVis2;

    fragColor = vec4(color, 1.0);
}
`;

const SLICE_VERTEX_SHADER = `#version 300 es
precision highp float;

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aTexCoord;

uniform mat4 uMVP;

out vec2 vTexCoord;

void main() {
    vTexCoord = aTexCoord;
    gl_Position = uMVP * vec4(aPosition, 1.0);
}
`;

const SLICE_FRAGMENT_SHADER = `#version 300 es
precision highp float;

in vec2 vTexCoord;
uniform sampler2D uSliceTexture;

out vec4 fragColor;

void main() {
    vec3 color = texture(uSliceTexture, vTexCoord).rgb;
    fragColor = vec4(color, 1.0);
}
`;

const LINE_VERTEX_SHADER = `#version 300 es
precision highp float;

layout(location = 0) in vec3 aP0;      // line start
layout(location = 1) in vec3 aP1;      // line end
layout(location = 2) in vec2 aCorner;  // x: 0=start 1=end, y: -1/+1 side

uniform mat4 uMVP;
uniform vec2 uViewportSize;
uniform float uLineWidth;   // half-width in pixels

void main() {
    vec4 clip0 = uMVP * vec4(aP0, 1.0);
    vec4 clip1 = uMVP * vec4(aP1, 1.0);

    // Screen-space direction and perpendicular
    vec2 ndc0 = clip0.xy / clip0.w;
    vec2 ndc1 = clip1.xy / clip1.w;
    vec2 screenDir = (ndc1 - ndc0) * uViewportSize;
    float len = length(screenDir);
    if (len > 0.0) screenDir /= len;
    vec2 perp = vec2(-screenDir.y, screenDir.x);

    // Pick this vertex's endpoint
    vec4 clip = aCorner.x < 0.5 ? clip0 : clip1;

    // Offset in NDC by half-width pixels
    vec2 offset = perp * aCorner.y * uLineWidth * 2.0 / uViewportSize;
    clip.xy += offset * clip.w;

    gl_Position = clip;
}
`;

const LINE_FRAGMENT_SHADER = `#version 300 es
precision highp float;

uniform vec4 uLineColor;

out vec4 fragColor;

void main() {
    fragColor = uLineColor;
}
`;

// ── ID buffer contour shaders ───────────────────────────────────────────────

const ID_VERTEX_SHADER = `#version 300 es
precision highp float;

// Per-vertex
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;    // unused but bound in VAO
layout(location = 2) in vec4 aSH_A;      // unused but bound in VAO
layout(location = 3) in vec4 aSH_B;      // unused but bound in VAO
layout(location = 4) in float aSH_C;     // unused but bound in VAO

// Per-instance
layout(location = 5) in vec3 aInstanceTranslation;
layout(location = 6) in vec3 aInstanceRotCol0;
layout(location = 7) in vec3 aInstanceRotCol1;
layout(location = 8) in vec3 aInstanceRotCol2;

uniform mat4 uProjection;
uniform mat4 uView;
uniform float uModelScale;
uniform vec3 uHalfDims;

flat out int vInstanceId;

void main() {
    mat3 instanceRot = mat3(aInstanceRotCol0, aInstanceRotCol1, aInstanceRotCol2);
    vec3 rotatedPos = instanceRot * (aPosition * uModelScale);
    vec3 worldPos = rotatedPos + (aInstanceTranslation - uHalfDims);

    vInstanceId = gl_InstanceID + 1; // 0 = background
    gl_Position = uProjection * uView * vec4(worldPos, 1.0);
}
`;

const ID_FRAGMENT_SHADER = `#version 300 es
precision highp float;
precision highp int;

flat in int vInstanceId;

layout(location = 0) out int fragId;

void main() {
    fragId = vInstanceId;
}
`;

const CONTOUR_VERTEX_SHADER = `#version 300 es
precision highp float;

void main() {
    // Fullscreen triangle via gl_VertexID trick (no attributes needed)
    float x = float((gl_VertexID & 1) << 2) - 1.0;
    float y = float((gl_VertexID & 2) << 1) - 1.0;
    gl_Position = vec4(x, y, 0.0, 1.0);
}
`;

const CONTOUR_FRAGMENT_SHADER = `#version 300 es
precision highp float;
precision highp int;

uniform highp isampler2D uIdTex;
uniform sampler2D uDepthTex;
uniform vec3 uContourColor;
uniform vec3 uBaseColor;

out vec4 fragColor;

void main() {
    ivec2 coord = ivec2(gl_FragCoord.xy);
    int center = texelFetch(uIdTex, coord, 0).r;

    // Discard background pixels
    if (center == 0) discard;

    // 8-connected neighbor check — only emit contour on the lower-ID side
    // so particle-vs-particle boundaries are 1px, not 2px
    bool edge = false;
    for (int dy = -1; dy <= 1; dy++) {
        for (int dx = -1; dx <= 1; dx++) {
            if (dx == 0 && dy == 0) continue;
            int neighbor = texelFetch(uIdTex, coord + ivec2(dx, dy), 0).r;
            if (neighbor != center && (neighbor == 0 || center < neighbor)) {
                edge = true;
                break;
            }
        }
        if (edge) break;
    }

    fragColor = vec4(edge ? uContourColor : uBaseColor, 1.0);
    gl_FragDepth = texelFetch(uDepthTex, coord, 0).r;
}
`;

const ID_CLEAR_VALUE = new Int32Array([0, 0, 0, 0]);

// ── WebGL initialization (lazy — created on first model mode activation) ────

function initModelWebGL(state) {
    if (state.model.gl) return; // already initialized

    const m = state.model;

    // Create a single WebGL2 canvas covering the entire viewport area
    const viewport = state._viewport;
    m.canvas = document.createElement('canvas');
    m.canvas.style.cssText = 'position:absolute; top:0; left:0; width:100%; height:100%; pointer-events:none; z-index:1;';
    m.canvas.style.display = 'none';
    viewport.appendChild(m.canvas);

    const gl = m.canvas.getContext('webgl2', { antialias: true, alpha: true, premultipliedAlpha: true });
    if (!gl) { console.error('WebGL2 not supported for model rendering'); return; }
    m.gl = gl;

    gl.enable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);
    gl.clearColor(0, 0, 0, 0);

    // Compile shaders
    const vs = compileShader(gl, gl.VERTEX_SHADER, MODEL_VERTEX_SHADER);
    const fs = compileShader(gl, gl.FRAGMENT_SHADER, MODEL_FRAGMENT_SHADER);
    if (!vs || !fs) return;

    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        console.error('Model shader link error:', gl.getProgramInfoLog(program));
        return;
    }
    m.program = program;

    m.uniforms = {
        projection: gl.getUniformLocation(program, 'uProjection'),
        view:       gl.getUniformLocation(program, 'uView'),
        viewDir:    gl.getUniformLocation(program, 'uViewDir'),
        light0:     gl.getUniformLocation(program, 'uLight0'),
        light1:     gl.getUniformLocation(program, 'uLight1'),
        light2:     gl.getUniformLocation(program, 'uLight2'),
        modelScale: gl.getUniformLocation(program, 'uModelScale'),
        halfDims:   gl.getUniformLocation(program, 'uHalfDims'),
        color:      gl.getUniformLocation(program, 'uColor')
    };

    // Create instance buffer (shared across LODs)
    m.instanceBuffer = gl.createBuffer();

    // Create VAO — per-vertex attrs (loc 0-4) will be bound per-LOD via bindLODToVAO
    // Per-instance attrs (loc 5-8) are bound once here.
    m.vao = gl.createVertexArray();
    gl.bindVertexArray(m.vao);

    // Enable per-vertex attribute slots (bound later via bindLODToVAO)
    for (let i = 0; i <= 4; i++) gl.enableVertexAttribArray(i);

    // Per-instance: translation (loc 5) + rotation mat3 columns (loc 6,7,8)
    // Stride = 48 bytes (3 + 9 = 12 floats)
    gl.bindBuffer(gl.ARRAY_BUFFER, m.instanceBuffer);
    gl.enableVertexAttribArray(5);
    gl.vertexAttribPointer(5, 3, gl.FLOAT, false, 48, 0);
    gl.vertexAttribDivisor(5, 1);

    gl.enableVertexAttribArray(6);
    gl.vertexAttribPointer(6, 3, gl.FLOAT, false, 48, 12);
    gl.vertexAttribDivisor(6, 1);

    gl.enableVertexAttribArray(7);
    gl.vertexAttribPointer(7, 3, gl.FLOAT, false, 48, 24);
    gl.vertexAttribDivisor(7, 1);

    gl.enableVertexAttribArray(8);
    gl.vertexAttribPointer(8, 3, gl.FLOAT, false, 48, 36);
    gl.vertexAttribDivisor(8, 1);

    gl.bindVertexArray(null);

    // ── ID buffer program (for contour rendering) ──
    {
        const idVs = compileShader(gl, gl.VERTEX_SHADER, ID_VERTEX_SHADER);
        const idFs = compileShader(gl, gl.FRAGMENT_SHADER, ID_FRAGMENT_SHADER);
        if (idVs && idFs) {
            const idProg = gl.createProgram();
            gl.attachShader(idProg, idVs);
            gl.attachShader(idProg, idFs);
            gl.linkProgram(idProg);
            if (gl.getProgramParameter(idProg, gl.LINK_STATUS)) {
                m.idProgram = idProg;
                m.idUniforms = {
                    projection: gl.getUniformLocation(idProg, 'uProjection'),
                    view:       gl.getUniformLocation(idProg, 'uView'),
                    modelScale: gl.getUniformLocation(idProg, 'uModelScale'),
                    halfDims:   gl.getUniformLocation(idProg, 'uHalfDims')
                };
            } else {
                console.error('ID shader link error:', gl.getProgramInfoLog(idProg));
            }
        }
    }

    // ── Contour composite program ──
    {
        const cVs = compileShader(gl, gl.VERTEX_SHADER, CONTOUR_VERTEX_SHADER);
        const cFs = compileShader(gl, gl.FRAGMENT_SHADER, CONTOUR_FRAGMENT_SHADER);
        if (cVs && cFs) {
            const cProg = gl.createProgram();
            gl.attachShader(cProg, cVs);
            gl.attachShader(cProg, cFs);
            gl.linkProgram(cProg);
            if (gl.getProgramParameter(cProg, gl.LINK_STATUS)) {
                m.contourProgram = cProg;
                m.contourUniforms = {
                    idTex:        gl.getUniformLocation(cProg, 'uIdTex'),
                    depthTex:     gl.getUniformLocation(cProg, 'uDepthTex'),
                    contourColor: gl.getUniformLocation(cProg, 'uContourColor'),
                    baseColor:    gl.getUniformLocation(cProg, 'uBaseColor')
                };
            } else {
                console.error('Contour shader link error:', gl.getProgramInfoLog(cProg));
            }
        }
    }

    // Empty VAO for fullscreen triangle (contour composite pass)
    m.contourVAO = gl.createVertexArray();

    // ID buffer FBO resources (created/resized lazily in ensureIdBuffer)
    m.idFBO = null;
    m.idTexture = null;
    m.idDepthTex = null;
    m._idBufferSize = { w: 0, h: 0 };
}

// ── Model canvas sizing ─────────────────────────────────────────────────────

function resizeModelCanvas(state) {
    const m = state.model;
    if (!m.canvas) return;
    const parent = m.canvas.parentElement;
    if (!parent) return;
    const dpr = window.devicePixelRatio || 1;
    m.canvas.width = Math.round(parent.clientWidth * dpr);
    m.canvas.height = Math.round(parent.clientHeight * dpr);
}

// ── ID buffer (FBO) management ──────────────────────────────────────────────

function ensureIdBuffer(gl, m, w, h) {
    if (m._idBufferSize.w === w && m._idBufferSize.h === h && m.idFBO) return;

    // Delete old resources
    if (m.idFBO) gl.deleteFramebuffer(m.idFBO);
    if (m.idTexture) gl.deleteTexture(m.idTexture);
    if (m.idDepthTex) gl.deleteTexture(m.idDepthTex);

    // Color attachment: R32I integer texture
    m.idTexture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, m.idTexture);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.R32I, w, h, 0, gl.RED_INTEGER, gl.INT, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.bindTexture(gl.TEXTURE_2D, null);

    // Depth attachment: texture (so contour shader can sample particle depth)
    m.idDepthTex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, m.idDepthTex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.DEPTH_COMPONENT24, w, h, 0, gl.DEPTH_COMPONENT, gl.UNSIGNED_INT, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.bindTexture(gl.TEXTURE_2D, null);

    // Framebuffer
    m.idFBO = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, m.idFBO);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, m.idTexture, 0);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, m.idDepthTex, 0);

    const status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
    if (status !== gl.FRAMEBUFFER_COMPLETE) {
        console.error('ID framebuffer incomplete:', status);
    }

    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    m._idBufferSize = { w, h };
}

// ── Euler angles to rotation matrix (ZYZ convention, degrees) ───────────────

function eulerToRotationMatrix(rotDeg, tiltDeg, psiDeg) {
    const d2r = Math.PI / 180;
    const r = rotDeg * d2r;
    const t = tiltDeg * d2r;
    const p = psiDeg * d2r;

    const cr = Math.cos(r), sr = Math.sin(r);
    const ct = Math.cos(t), st = Math.sin(t);
    const cp = Math.cos(p), sp = Math.sin(p);

    // R = Rz(rot) * Ry(tilt) * Rz(psi)  — column-major
    return [
        cr*ct*cp - sr*sp,  sr*ct*cp + cr*sp,  -st*cp,
        -cr*ct*sp - sr*cp, -sr*ct*sp + cr*cp,  st*sp,
        cr*st,              sr*st,              ct
    ];
}

function precomputeSpeciesRotations(species) {
    const particles = species.particles;
    if (!particles || particles.length === 0) {
        species.model.instanceRotations = null;
        species.model._filterBuffer = null;
        return;
    }

    const data = new Float32Array(particles.length * 12);
    for (let i = 0; i < particles.length; i++) {
        const p = particles[i];
        const off = i * 12;
        data[off]     = p.x;
        data[off + 1] = p.y;
        data[off + 2] = p.z;
        const rot = eulerToRotationMatrix(p.rot || 0, p.tilt || 0, p.psi || 0);
        for (let j = 0; j < 9; j++) data[off + 3 + j] = rot[j];
    }
    species.model.instanceRotations = data;
    species.model._filterBuffer = new Float32Array(particles.length * 12);
    species.model._instanceBufferCapacity = 0;
}

// ── LOD VAO binding ─────────────────────────────────────────────────────────

function bindLODToVAO(gl, m, sm, lodIndex) {
    const lod = sm.lods[lodIndex];
    if (!lod) return;

    gl.bindVertexArray(m.vao);

    // Per-vertex: position (loc 0)
    gl.bindBuffer(gl.ARRAY_BUFFER, lod.posBuffer);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);

    // Per-vertex: normal (loc 1)
    gl.bindBuffer(gl.ARRAY_BUFFER, lod.normBuffer);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 0, 0);

    // Per-vertex: SH coefficients (loc 2, 3, 4) — 36 bytes stride
    gl.bindBuffer(gl.ARRAY_BUFFER, lod.shBuffer);
    gl.vertexAttribPointer(2, 4, gl.FLOAT, false, 36, 0);
    gl.vertexAttribPointer(3, 4, gl.FLOAT, false, 36, 16);
    gl.vertexAttribPointer(4, 1, gl.FLOAT, false, 36, 32);

    gl.bindVertexArray(null);
    sm.currentLOD = lodIndex;
}

function bindPrimitiveGeomToVAO(gl, m, geom) {
    gl.bindVertexArray(m.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, geom.posBuffer);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, geom.normBuffer);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ARRAY_BUFFER, geom.shBuffer);
    gl.vertexAttribPointer(2, 4, gl.FLOAT, false, 36, 0);
    gl.vertexAttribPointer(3, 4, gl.FLOAT, false, 36, 16);
    gl.vertexAttribPointer(4, 1, gl.FLOAT, false, 36, 32);
    gl.bindVertexArray(null);
}

// ── LOD selection ───────────────────────────────────────────────────────────

function selectSpeciesLOD(state, sm) {
    if (sm.lods.length === 0) return 0;

    const screenPixelSize = state.pixelSize / state.zoom; // Angstroms per screen pixel
    const tps = sm.volumePixelSize;
    if (!tps) return 0;
    const templatePixelSize = Math.max(tps.x, tps.y, tps.z);

    if (templatePixelSize <= 0) return 0;

    const k = Math.floor(Math.log2(screenPixelSize / templatePixelSize));
    return Math.max(0, Math.min(k, sm.lods.length - 1));
}

// ── Template volume loading ─────────────────────────────────────────────────

async function loadSpeciesModelVolume(state, speciesIndex) {
    const species = state.species[speciesIndex];
    if (!species) return;
    const sm = species.model;
    const gl = state.model.gl;
    if (!sm.volumeUrl || !gl) return;

    try {
        showLoading(state, 0, `Loading ${species.name} volume...`);

        const { header, data } = await fetchMRC(sm.volumeUrl, {
            onProgress: (received, total) => {
                if (total > 0) showLoading(state, received / total * 0.7, `Loading ${species.name}...`);
            }
        });

        sm.volumeData = data;
        sm.volumeDims = { x: header.dimensions.x, y: header.dimensions.y, z: header.dimensions.z };
        sm.volumePixelSize = { x: header.pixelSize.x, y: header.pixelSize.y, z: header.pixelSize.z };

        let min = Infinity, max = -Infinity, sum = 0;
        const n = data.length;
        for (let i = 0; i < n; i++) {
            const v = data[i];
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        const mean = sum / n;
        let sqSum = 0;
        for (let i = 0; i < n; i++) { const d = data[i] - mean; sqSum += d * d; }
        sm.volumeStats = { min, max, mean, std: Math.sqrt(sqSum / n) };

        sm.threshold = 0.3 * max;
        sm.thresholdPct = (max - min) > 0 ? ((sm.threshold - min) / (max - min)) * 100 : 30;

        showLoading(state, 0.75, `Generating ${species.name} mesh...`);

        if (sm.worker) sm.worker.terminate();
        sm.worker = new Worker('_content/Refund/js/marching-cubes-worker.js?v=2');

        sm.worker.onerror = (e) => {
            console.error(`Marching cubes worker error for ${species.name}:`, e.message);
            hideLoading(state);
        };

        sm.worker.onmessage = (e) => {
            const msg = e.data;
            if (msg.type === 'progress') {
                showLoading(state, 0.75 + 0.2 * (msg.percent / 100), `Generating ${species.name} mesh...`);
            } else if (msg.type === 'result') {
                try {
                    // Verify species is still in the array (may have been removed by setSpecies)
                    const currentIndex = state.species.indexOf(species);
                    if (currentIndex === -1) return;

                    uploadSpeciesModelLODs(state, currentIndex, msg.lods);
                    showLoading(state, 0.95, 'Baking occlusion...');
                    setTimeout(() => {
                        const idx = state.species.indexOf(species);
                        if (idx === -1) return;
                        bakeSpeciesModelSH(state, idx);
                        hideLoading(state);
                        if (state.selectedSpeciesIndex === idx) {
                            updateModelThresholdUI(state);
                        }
                        requestModelRender(state);
                    }, 0);
                } catch (err) {
                    console.error(`Failed to process mesh for ${species.name}:`, err);
                    hideLoading(state);
                }
            }
        };

        sm.worker.postMessage({
            type: 'setVolume', volume: sm.volumeData,
            dims: sm.volumeDims, pixelSize: sm.volumePixelSize
        });
        sm.worker.postMessage({ type: 'generate', threshold: sm.threshold });

    } catch (err) {
        console.error(`Failed to load model volume for ${species.name}:`, err);
        hideLoading(state);
    }
}

function uploadSpeciesModelLODs(state, speciesIndex, lodsData) {
    const sm = state.species[speciesIndex].model;
    const gl = state.model.gl;
    if (!gl) return;

    // Delete old LOD GPU buffers
    for (const lod of sm.lods) {
        if (lod.posBuffer) gl.deleteBuffer(lod.posBuffer);
        if (lod.normBuffer) gl.deleteBuffer(lod.normBuffer);
        if (lod.shBuffer) gl.deleteBuffer(lod.shBuffer);
    }
    sm.lods = [];

    const dc = Math.sqrt(4.0 * Math.PI);

    for (let i = 0; i < lodsData.length; i++) {
        const { positions, normals } = lodsData[i];
        const vertexCount = positions.length / 3;

        const posBuffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, posBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, positions, gl.STATIC_DRAW);

        const normBuffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, normBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, normals, gl.STATIC_DRAW);

        // Default SH: fully lit (DC = sqrt(4*PI))
        const defaultSH = new Float32Array(vertexCount * 9);
        for (let v = 0; v < vertexCount; v++) defaultSH[v * 9] = dc;
        const shBuffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, shBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, defaultSH, gl.STATIC_DRAW);

        sm.lods.push({ posBuffer, normBuffer, shBuffer, vertexCount });
    }

    // Compute bounding radius from LOD 0
    if (sm.lods.length > 0 && lodsData[0].positions.length > 0) {
        const pos = lodsData[0].positions;
        let maxR2 = 0;
        for (let v = 0; v < pos.length; v += 3) {
            const r2 = pos[v] * pos[v] + pos[v + 1] * pos[v + 1] + pos[v + 2] * pos[v + 2];
            if (r2 > maxR2) maxR2 = r2;
        }
        sm.boundingRadiusAngstroms = Math.sqrt(maxR2);
    } else {
        sm.boundingRadiusAngstroms = 0;
    }

    // Init bake resources from LOD 0 if needed
    if (sm.lods.length > 0 && sm.lods[0].vertexCount > 0) {
        const lod0 = sm.lods[0];
        if (!sm.bakeResources) {
            sm.bakeResources = initBakeResources(gl, lod0.posBuffer, lod0.normBuffer);
        } else {
            gl.bindVertexArray(sm.bakeResources.vao);
            gl.bindBuffer(gl.ARRAY_BUFFER, lod0.posBuffer);
            gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
            gl.bindBuffer(gl.ARRAY_BUFFER, lod0.normBuffer);
            gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 0, 0);
            gl.bindVertexArray(null);
        }
    }

    // Log LOD summary
    console.log(`Model LODs generated for ${state.species[speciesIndex].name}:`);
    for (let i = 0; i < sm.lods.length; i++) {
        const l = sm.lods[i];
        console.log(`  LOD ${i}: ${l.vertexCount} verts (${l.vertexCount / 3} tris)`);
    }

    // Bind LOD 0 to render VAO
    if (sm.lods.length > 0) bindLODToVAO(gl, state.model, sm, 0);
}

function bakeSpeciesModelSH(state, speciesIndex) {
    const sm = state.species[speciesIndex].model;
    const gl = state.model.gl;
    if (!gl || !sm.bakeResources || sm.lods.length === 0 || !sm.volumeData) return;

    // Bake SH for each LOD level, reusing occupancy texture after the first
    for (let i = 0; i < sm.lods.length; i++) {
        const lod = sm.lods[i];
        if (lod.vertexCount === 0) continue;

        // Rebind bake VAO to this LOD's pos/norm buffers
        gl.bindVertexArray(sm.bakeResources.vao);
        gl.bindBuffer(gl.ARRAY_BUFFER, lod.posBuffer);
        gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
        gl.bindBuffer(gl.ARRAY_BUFFER, lod.normBuffer);
        gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 0, 0);
        gl.bindVertexArray(null);

        // Always use full-res volume data for SH baking
        sm.occTexture = bakeSHCoefficients(
            gl, sm.bakeResources, lod.shBuffer, sm.volumeData,
            sm.volumeDims, sm.volumePixelSize, sm.threshold,
            lod.vertexCount, sm.occTexture,
            i > 0 // reuseOccTexture for LODs after the first
        );
    }
}

function regenerateSpeciesModelMesh(state, speciesIndex) {
    const sm = state.species[speciesIndex].model;
    if (!sm.worker || !sm.volumeData) return;
    sm.worker.postMessage({ type: 'generate', threshold: sm.threshold });
}

// ── Model rendering ─────────────────────────────────────────────────────────

const LOD_SETTLE_MS = 200; // ms after last interaction before switching to quality LOD

function requestModelRender(state, isInteraction = false) {
    const m = state.model;

    if (isInteraction) {
        m._interacting = true;
        clearTimeout(m._settleTimer);
        m._settleTimer = setTimeout(() => {
            m._interacting = false;
            // Final quality render at zoom-appropriate LOD
            m._renderQueued = false; // allow a new frame
            requestModelRender(state, false);
        }, LOD_SETTLE_MS);
    }

    if (m._renderQueued) return;
    m._renderQueued = true;
    requestAnimationFrame(() => {
        m._renderQueued = false;
        renderModels(state);
    });
}

function renderModels(state) {
    const m = state.model;
    if (!m.gl || !m.active) return;

    if (state.viewMode === '3d') {
        renderModels3D(state);
    } else {
        if (!m.program || m.lods.length === 0) return;
        renderModelsOrthoslice(state);
    }
}

function renderSpeciesModelPanel(state, species, gl, m, planeType, canvasW, canvasH, vertexCount, uniforms) {
    uniforms = uniforms || m.uniforms;

    const panel = state.panels[planeType];
    const rect = panel.container.getBoundingClientRect();
    const viewportParent = m.canvas.parentElement.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;

    // Convert panel screen position to WebGL canvas pixels (bottom-left origin)
    const px = Math.round((rect.left - viewportParent.left) * dpr);
    const py = Math.round((viewportParent.bottom - rect.bottom) * dpr);
    const pw = Math.round(rect.width * dpr);
    const ph = Math.round(rect.height * dpr);

    if (pw <= 0 || ph <= 0) return;

    gl.viewport(px, py, pw, ph);
    gl.scissor(px, py, pw, ph);

    // Build orthographic projection for this panel
    const zoom = state.zoom;
    const t = state.translate[planeType];
    const { dims } = state;

    let projMatrix;
    let viewDir;

    // Compute visible voxel range from canvas transform
    const panelW = (pw / dpr) / zoom;
    const panelH = (ph / dpr) / zoom;
    const voxelLeft = -t.x / zoom;
    const voxelRight = voxelLeft + panelW;
    const voxelTop = -t.y / zoom;
    const voxelBottom = voxelTop + panelH;

    if (planeType === 'xy') {
        const l = voxelLeft - dims.x * 0.5;
        const rr = voxelRight - dims.x * 0.5;
        const tt = dims.y * 0.5 - voxelTop;
        const b = dims.y * 0.5 - voxelBottom;
        projMatrix = orthoMatrix(l, rr, b, tt, -dims.z, dims.z);
        viewDir = [0, 0, -1];
    } else if (planeType === 'xz') {
        const l = voxelLeft - dims.x * 0.5;
        const rr = voxelRight - dims.x * 0.5;
        const tt = dims.z * 0.5 - voxelTop;
        const b = dims.z * 0.5 - voxelBottom;
        projMatrix = orthoMatrixXZ(l, rr, b, tt, -dims.y, dims.y);
        viewDir = [0, -1, 0];
    } else { // zy
        const l = voxelLeft - dims.z * 0.5;
        const rr = voxelRight - dims.z * 0.5;
        const tt = dims.y * 0.5 - voxelTop;
        const b = dims.y * 0.5 - voxelBottom;
        projMatrix = orthoMatrixZY(l, rr, b, tt, -dims.x, dims.x);
        viewDir = [1, 0, 0];
    }

    gl.uniformMatrix4fv(uniforms.projection, false, projMatrix);

    // Lighting only applies to the lit shader (ID shader has no viewDir/light uniforms)
    if (uniforms.viewDir) {
        gl.uniform3fv(uniforms.viewDir, viewDir);

        // Compute per-panel camera basis, then offset lights the same way as 3D mode
        let right, up;
        if (planeType === 'xy')      { right = [1,0,0]; up = [0,1,0]; }
        else if (planeType === 'xz') { right = [1,0,0]; up = [0,0,1]; }
        else                         { right = [0,0,1]; up = [0,1,0]; }
        const tc = [-viewDir[0], -viewDir[1], -viewDir[2]]; // toward camera
        function setLight(loc, a0, ar, au) {
            const x = a0 * tc[0] + ar * right[0] + au * up[0];
            const y = a0 * tc[1] + ar * right[1] + au * up[1];
            const z = a0 * tc[2] + ar * right[2] + au * up[2];
            const len = Math.sqrt(x * x + y * y + z * z) || 1;
            gl.uniform3f(loc, x / len, y / len, z / len);
        }
        setLight(uniforms.light0, 1.0,  0.36,  0.58);  // key: ~30° above-right of camera
        setLight(uniforms.light1, 1.0, -0.5,  -0.3);   // fill: opposite side, slightly below
        setLight(uniforms.light2, -1.0, 0.2,   0.7);   // rim: behind, above
    }

    // Filter particles by slab + frustum and upload visible instances
    const visibleData = filterSpeciesParticlesForPanel(state, species, planeType, voxelLeft, voxelRight, voxelTop, voxelBottom);
    if (visibleData.count === 0) return;

    gl.bindBuffer(gl.ARRAY_BUFFER, species.model.instanceBuffer);
    gl.bufferSubData(gl.ARRAY_BUFFER, 0, visibleData.data);

    gl.drawArraysInstanced(gl.TRIANGLES, 0, vertexCount, visibleData.count);
}

function filterSpeciesParticlesForPanel(state, species, planeType, voxelLeft, voxelRight, voxelTop, voxelBottom) {
    const m = state.model;
    const sm = species.model;
    const particles = species.particles;
    const rotData = sm.instanceRotations;
    if (!particles || particles.length === 0 || !rotData) {
        return { data: new Float32Array(0), count: 0 };
    }

    const { viewPoint, dims } = state;
    const slabHalf = m.slabThickness * 0.5;
    const doSlab = !m.slabFull;

    // Margin in voxels for frustum test (model bounding sphere or primitive radius)
    const tps = sm.volumePixelSize;
    let margin;
    if (tps) {
        const modelScale = 1.0 / state.pixelSize;
        margin = sm.boundingRadiusAngstroms * modelScale;
    } else {
        margin = species.settings.diameter / (2 * state.pixelSize);
    }

    // Convert visible canvas-voxel range to tomogram coordinates
    // Canvas axes are flipped for Y/Z relative to tomogram coords
    let hMin, hMax, vMin, vMax;
    if (planeType === 'xy') {
        hMin = voxelLeft - margin;
        hMax = voxelRight + margin;
        vMin = (dims.y - 1 - voxelBottom) - margin;
        vMax = (dims.y - 1 - voxelTop) + margin;
    } else if (planeType === 'xz') {
        hMin = voxelLeft - margin;
        hMax = voxelRight + margin;
        vMin = (dims.z - 1 - voxelBottom) - margin;
        vMax = (dims.z - 1 - voxelTop) + margin;
    } else { // zy
        hMin = voxelLeft - margin;
        hMax = voxelRight + margin;
        vMin = (dims.y - 1 - voxelBottom) - margin;
        vMax = (dims.y - 1 - voxelTop) + margin;
    }

    const result = sm._filterBuffer || new Float32Array(particles.length * 12);
    let count = 0;

    for (let i = 0; i < particles.length; i++) {
        const p = particles[i];

        // Slab test (depth axis)
        if (doSlab) {
            let d;
            if (planeType === 'xy') d = Math.abs(viewPoint.z - p.z);
            else if (planeType === 'xz') d = Math.abs(viewPoint.y - p.y);
            else d = Math.abs(viewPoint.x - p.x);
            if (d > slabHalf) continue;
        }

        // Frustum test (in-plane axes)
        let h, v;
        if (planeType === 'xy') { h = p.x; v = p.y; }
        else if (planeType === 'xz') { h = p.x; v = p.z; }
        else { h = p.z; v = p.y; }

        if (h < hMin || h > hMax || v < vMin || v > vMax) continue;

        // Copy 12 floats (translation + rotation)
        const srcOff = i * 12;
        const dstOff = count * 12;
        for (let j = 0; j < 12; j++) result[dstOff + j] = rotData[srcOff + j];
        count++;
    }

    return { data: result.subarray(0, count * 12), count };
}

function parseHexColor(hex) {
    return new Float32Array([
        parseInt(hex.substr(1, 2), 16) / 255,
        parseInt(hex.substr(3, 2), 16) / 255,
        parseInt(hex.substr(5, 2), 16) / 255
    ]);
}

// ── Contour composite pass ──────────────────────────────────────────────────

function compositeContour(gl, m, baseColor, contourColor, w, h) {
    gl.useProgram(m.contourProgram);
    gl.bindVertexArray(m.contourVAO);

    // Bind ID texture to unit 0
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, m.idTexture);
    gl.uniform1i(m.contourUniforms.idTex, 0);

    // Bind depth texture to unit 1 (for gl_FragDepth output)
    gl.activeTexture(gl.TEXTURE1);
    gl.bindTexture(gl.TEXTURE_2D, m.idDepthTex);
    gl.uniform1i(m.contourUniforms.depthTex, 1);

    gl.uniform3fv(m.contourUniforms.baseColor, baseColor);
    gl.uniform3fv(m.contourUniforms.contourColor, contourColor);

    gl.viewport(0, 0, w, h);
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    gl.bindVertexArray(null);
    gl.activeTexture(gl.TEXTURE0);
}

// ── Orthographic projection matrices (column-major) ─────────────────────────

function orthoMatrix(left, right, bottom, top, near, far) {
    // Standard orthographic: X right, Y up, -Z into screen
    const rl = 1 / (right - left);
    const tb = 1 / (top - bottom);
    const fn = 1 / (far - near);
    return new Float32Array([
        2 * rl,  0,      0,       0,
        0,       2 * tb, 0,       0,
        0,       0,     -2 * fn,  0,
        -(right + left) * rl, -(top + bottom) * tb, -(far + near) * fn, 1
    ]);
}

function orthoMatrixXZ(left, right, bottom, top, near, far) {
    // XZ panel: projX = worldX, projY = worldZ, depth = -worldY
    const rl = 1 / (right - left);
    const tb = 1 / (top - bottom);
    const fn = 1 / (far - near);
    return new Float32Array([
        2 * rl, 0,       0,       0,
        0,      0,       -2 * fn, 0,
        0,      2 * tb,  0,       0,
        -(right + left) * rl, -(top + bottom) * tb, -(far + near) * fn, 1
    ]);
}

function orthoMatrixZY(left, right, bottom, top, near, far) {
    // ZY panel: projX = worldZ, projY = worldY, depth = worldX
    const rl = 1 / (right - left);
    const tb = 1 / (top - bottom);
    const fn = 1 / (far - near);
    return new Float32Array([
        0,      0,       2 * fn,  0,
        0,      2 * tb,  0,       0,
        2 * rl, 0,       0,       0,
        -(right + left) * rl, -(top + bottom) * tb, -(far + near) * fn, 1
    ]);
}

// ── Matrix utilities for 3D view ─────────────────────────────────────────────

function mat4_perspective(fov, aspect, near, far) {
    const f = 1 / Math.tan(fov / 2);
    const nf = 1 / (near - far);
    return new Float32Array([
        f / aspect, 0, 0, 0,
        0, f, 0, 0,
        0, 0, (far + near) * nf, -1,
        0, 0, 2 * far * near * nf, 0
    ]);
}

function mat4_multiply(a, b) {
    const out = new Float32Array(16);
    for (let i = 0; i < 4; i++) {
        for (let j = 0; j < 4; j++) {
            out[j * 4 + i] =
                a[0 * 4 + i] * b[j * 4 + 0] +
                a[1 * 4 + i] * b[j * 4 + 1] +
                a[2 * 4 + i] * b[j * 4 + 2] +
                a[3 * 4 + i] * b[j * 4 + 3];
        }
    }
    return out;
}

function lookAtMatrix(ex, ey, ez, cx, cy, cz) {
    // Z-up look-at matrix
    let fx = cx - ex, fy = cy - ey, fz = cz - ez;
    let len = Math.sqrt(fx * fx + fy * fy + fz * fz);
    if (len > 0) { fx /= len; fy /= len; fz /= len; }

    // right = normalize(cross(forward, worldUp=[0,0,1]))
    // cross([fx,fy,fz], [0,0,1]) = (fy*1 - fz*0, fz*0 - fx*1, fx*0 - fy*0) = (fy, -fx, 0)
    let rx = fy, ry = -fx, rz = 0;
    len = Math.sqrt(rx * rx + ry * ry);
    if (len > 0) { rx /= len; ry /= len; }

    // up = cross(right, forward)
    const ux = ry * fz - rz * fy;
    const uy = rz * fx - rx * fz;
    const uz = rx * fy - ry * fx;

    return new Float32Array([
        rx,  ux,  -fx, 0,
        ry,  uy,  -fy, 0,
        rz,  uz,  -fz, 0,
        -(rx * ex + ry * ey + rz * ez),
        -(ux * ex + uy * ey + uz * ez),
        (fx * ex + fy * ey + fz * ez),
        1
    ]);
}

function computeCamera3dMatrices(state) {
    const cam = state.camera3d;
    const dist = cam.distance;

    // Z-up spherical coordinates: yaw rotates in XY plane, pitch elevates toward +Z
    const cosP = Math.cos(cam.pitch);
    const eyeX = cam.panX + dist * cosP * Math.cos(cam.yaw);
    const eyeY = cam.panY + dist * cosP * Math.sin(cam.yaw);
    const eyeZ = cam.panZ + dist * Math.sin(cam.pitch);

    const targetX = cam.panX;
    const targetY = cam.panY;
    const targetZ = cam.panZ;

    const viewMatrix = lookAtMatrix(eyeX, eyeY, eyeZ, targetX, targetY, targetZ);

    // View direction (from eye toward target)
    let vdx = targetX - eyeX, vdy = targetY - eyeY, vdz = targetZ - eyeZ;
    const vdLen = Math.sqrt(vdx * vdx + vdy * vdy + vdz * vdz);
    if (vdLen > 0) { vdx /= vdLen; vdy /= vdLen; vdz /= vdLen; }

    const canvas = state.model.canvas;
    const aspect = canvas.width / canvas.height;

    let projMatrix;
    if (cam.projection === 'perspective') {
        projMatrix = mat4_perspective(cam.fov, aspect, dist * 0.01, dist * 10);
    } else {
        const halfH = cam.orthoScale * dist * 0.5;
        const halfW = halfH * aspect;
        projMatrix = orthoMatrix(-halfW, halfW, -halfH, halfH, -dist * 5, dist * 5);
    }

    return { projMatrix, viewMatrix, viewDir: [vdx, vdy, vdz] };
}

// ── Geometry generators ──────────────────────────────────────────────────────

function generateSphereGeometry(subdivisions) {
    // Start with icosahedron
    const t = (1 + Math.sqrt(5)) / 2;
    let verts = [
        -1, t, 0,   1, t, 0,   -1,-t, 0,    1,-t, 0,
         0,-1, t,   0, 1, t,    0,-1,-t,     0, 1,-t,
         t, 0,-1,   t, 0, 1,   -t, 0,-1,    -t, 0, 1
    ];
    // Normalize initial vertices
    for (let i = 0; i < verts.length; i += 3) {
        const len = Math.sqrt(verts[i]*verts[i] + verts[i+1]*verts[i+1] + verts[i+2]*verts[i+2]);
        verts[i] /= len; verts[i+1] /= len; verts[i+2] /= len;
    }

    let faces = [
        0,11,5,  0,5,1,   0,1,7,   0,7,10,  0,10,11,
        1,5,9,   5,11,4,  11,10,2, 10,7,6,   7,1,8,
        3,9,4,   3,4,2,   3,2,6,   3,6,8,    3,8,9,
        4,9,5,   2,4,11,  6,2,10,  8,6,7,    9,8,1
    ];

    const midpointCache = new Map();
    function getMidpoint(a, b) {
        const key = Math.min(a, b) * 65536 + Math.max(a, b);
        if (midpointCache.has(key)) return midpointCache.get(key);
        const ax = verts[a*3], ay = verts[a*3+1], az = verts[a*3+2];
        const bx = verts[b*3], by = verts[b*3+1], bz = verts[b*3+2];
        let mx = (ax+bx)*0.5, my = (ay+by)*0.5, mz = (az+bz)*0.5;
        const len = Math.sqrt(mx*mx+my*my+mz*mz);
        mx /= len; my /= len; mz /= len;
        const idx = verts.length / 3;
        verts.push(mx, my, mz);
        midpointCache.set(key, idx);
        return idx;
    }

    for (let s = 0; s < subdivisions; s++) {
        const newFaces = [];
        for (let i = 0; i < faces.length; i += 3) {
            const a = faces[i], b = faces[i+1], c = faces[i+2];
            const ab = getMidpoint(a, b);
            const bc = getMidpoint(b, c);
            const ca = getMidpoint(c, a);
            newFaces.push(a,ab,ca, b,bc,ab, c,ca,bc, ab,bc,ca);
        }
        faces = newFaces;
        midpointCache.clear();
    }

    // Expand indexed to non-indexed
    const vertexCount = faces.length;
    const positions = new Float32Array(vertexCount * 3);
    const normals = new Float32Array(vertexCount * 3);
    for (let i = 0; i < faces.length; i++) {
        const vi = faces[i];
        positions[i*3]   = verts[vi*3];
        positions[i*3+1] = verts[vi*3+1];
        positions[i*3+2] = verts[vi*3+2];
        // Normals = positions for unit sphere
        normals[i*3]   = verts[vi*3];
        normals[i*3+1] = verts[vi*3+1];
        normals[i*3+2] = verts[vi*3+2];
    }

    return { positions, normals, vertexCount };
}

function generateCubeGeometry() {
    // Unit cube centered at origin (side=1)
    const h = 0.5;
    // 6 faces, 2 tris each, 3 verts each = 36 verts
    // Each face: [v0,v1,v2, v0,v2,v3] with face normal
    const faceData = [
        // +Z face
        { n: [0,0,1], v: [[-h,-h,h],[h,-h,h],[h,h,h],[-h,h,h]] },
        // -Z face
        { n: [0,0,-1], v: [[h,-h,-h],[-h,-h,-h],[-h,h,-h],[h,h,-h]] },
        // +X face
        { n: [1,0,0], v: [[h,-h,h],[h,-h,-h],[h,h,-h],[h,h,h]] },
        // -X face
        { n: [-1,0,0], v: [[-h,-h,-h],[-h,-h,h],[-h,h,h],[-h,h,-h]] },
        // +Y face
        { n: [0,1,0], v: [[-h,h,h],[h,h,h],[h,h,-h],[-h,h,-h]] },
        // -Y face
        { n: [0,-1,0], v: [[-h,-h,-h],[h,-h,-h],[h,-h,h],[-h,-h,h]] },
    ];

    const positions = new Float32Array(36 * 3);
    const normals = new Float32Array(36 * 3);
    let vi = 0;
    for (const face of faceData) {
        const [v0, v1, v2, v3] = face.v;
        const tris = [v0, v1, v2, v0, v2, v3];
        for (const vert of tris) {
            positions[vi*3]   = vert[0];
            positions[vi*3+1] = vert[1];
            positions[vi*3+2] = vert[2];
            normals[vi*3]     = face.n[0];
            normals[vi*3+1]   = face.n[1];
            normals[vi*3+2]   = face.n[2];
            vi++;
        }
    }

    return { positions, normals, vertexCount: 36 };
}

function initPrimitiveGeometry(state) {
    const m = state.model;
    const gl = m.gl;
    if (!gl) return;

    const dc = Math.sqrt(4.0 * Math.PI);

    for (const [key, genFn] of [['sphere', () => generateSphereGeometry(2)], ['cube', generateCubeGeometry]]) {
        const { positions, normals, vertexCount } = genFn();

        const posBuffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, posBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, positions, gl.STATIC_DRAW);

        const normBuffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, normBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, normals, gl.STATIC_DRAW);

        // Default SH: fully lit (DC only)
        const defaultSH = new Float32Array(vertexCount * 9);
        for (let v = 0; v < vertexCount; v++) defaultSH[v * 9] = dc;
        const shBuffer = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, shBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, defaultSH, gl.STATIC_DRAW);

        state.primitiveGeometry[key] = { posBuffer, normBuffer, shBuffer, vertexCount };
    }
}

function initSliceProgram(state) {
    const m = state.model;
    const gl = m.gl;
    if (!gl || m.sliceProgram) return;

    const vs = compileShader(gl, gl.VERTEX_SHADER, SLICE_VERTEX_SHADER);
    const fs = compileShader(gl, gl.FRAGMENT_SHADER, SLICE_FRAGMENT_SHADER);
    if (!vs || !fs) return;

    const prog = gl.createProgram();
    gl.attachShader(prog, vs);
    gl.attachShader(prog, fs);
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
        console.error('Slice shader link error:', gl.getProgramInfoLog(prog));
        return;
    }

    m.sliceProgram = prog;
    m.sliceUniforms = {
        mvp: gl.getUniformLocation(prog, 'uMVP'),
        texture: gl.getUniformLocation(prog, 'uSliceTexture'),
    };

    // VAO with position (loc 0) + texcoord (loc 1)
    m.sliceVAO = gl.createVertexArray();
    gl.bindVertexArray(m.sliceVAO);

    // Interleaved buffer: 3 pos + 2 tex = 5 floats per vert, 6 verts per quad, 3 quads
    m.sliceVertBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, m.sliceVertBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, 6 * 5 * 4 * 3, gl.DYNAMIC_DRAW); // 3 quads max

    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 20, 0);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 2, gl.FLOAT, false, 20, 12);

    gl.bindVertexArray(null);

    // Create 3 textures for slices
    m.sliceTextures = {};
    for (const plane of ['xy', 'xz', 'zy']) {
        const tex = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        m.sliceTextures[plane] = tex;
    }

    // Line shader for slice plane edges and intersection lines
    const lvs = compileShader(gl, gl.VERTEX_SHADER, LINE_VERTEX_SHADER);
    const lfs = compileShader(gl, gl.FRAGMENT_SHADER, LINE_FRAGMENT_SHADER);
    if (!lvs || !lfs) return;
    const lineProg = gl.createProgram();
    gl.attachShader(lineProg, lvs);
    gl.attachShader(lineProg, lfs);
    gl.linkProgram(lineProg);
    if (!gl.getProgramParameter(lineProg, gl.LINK_STATUS)) {
        console.error('Line shader link error:', gl.getProgramInfoLog(lineProg));
        return;
    }
    m.lineProgram = lineProg;
    m.lineUniforms = {
        mvp: gl.getUniformLocation(lineProg, 'uMVP'),
        viewportSize: gl.getUniformLocation(lineProg, 'uViewportSize'),
        lineWidth: gl.getUniformLocation(lineProg, 'uLineWidth'),
        color: gl.getUniformLocation(lineProg, 'uLineColor'),
    };

    // Line VAO: per-vertex = P0(3) + P1(3) + corner(2) = 8 floats = 32 bytes
    // 15 line segments × 6 verts = 90 vertices
    m.lineVAO = gl.createVertexArray();
    gl.bindVertexArray(m.lineVAO);
    m.lineVertBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, m.lineVertBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, 90 * 32, gl.DYNAMIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 32, 0);   // aP0
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 32, 12);  // aP1
    gl.enableVertexAttribArray(2);
    gl.vertexAttribPointer(2, 2, gl.FLOAT, false, 32, 24);  // aCorner
    gl.bindVertexArray(null);
}

// ── 3D View mode ─────────────────────────────────────────────────────────────

function setViewMode(state, mode) {
    if (state.viewMode === mode) return;
    state.viewMode = mode;
    if (mode === '3d') enterMode3D(state);
    else exitMode3D(state);
}

function enterMode3D(state) {
    // Hide 3-panel layout
    if (state._leftCol) state._leftCol.style.display = 'none';
    if (state._zyContainer) state._zyContainer.style.display = 'none';
    // Hide sliders
    for (const key of ['x', 'y', 'z']) {
        if (state.sliders[key]) state.sliders[key].element.style.display = 'none';
    }
    if (state.sliders._xSpacer) state.sliders._xSpacer.style.display = 'none';

    // Show projection dropdown
    if (state.toolbar.projSelect) state.toolbar.projSelect.style.display = '';

    // Init WebGL if needed
    initModelWebGL(state);
    if (!state.model.gl) return;

    initPrimitiveGeometry(state);
    initSliceProgram(state);

    // Auto-set distance from dims
    const cam = state.camera3d;
    if (cam.distance === 0) {
        cam.distance = Math.max(state.dims.x, state.dims.y, state.dims.z) * 1.5;
    }

    // Show model canvas and make it interactive
    const m = state.model;
    m.canvas.style.display = 'block';
    m.canvas.style.pointerEvents = 'auto';
    m.active = true;

    setup3DMouseHandlers(state);
    resizeModelCanvas(state);
    requestModelRender(state);
}

function exitMode3D(state) {
    // Restore panel layout
    if (state._leftCol) state._leftCol.style.display = '';
    if (state._zyContainer) state._zyContainer.style.display = '';
    for (const key of ['x', 'y', 'z']) {
        if (state.sliders[key]) state.sliders[key].element.style.display = '';
    }
    if (state.sliders._xSpacer) state.sliders._xSpacer.style.display = '';

    // Hide projection dropdown
    if (state.toolbar.projSelect) state.toolbar.projSelect.style.display = 'none';

    // Reset model canvas interactivity
    const m = state.model;
    if (m.canvas) m.canvas.style.pointerEvents = 'none';

    // If no species uses models, deactivate model mode
    const anyModels = state.species.some(s => s.settings.visible && s.settings.displayType === 'models');
    if (!anyModels) {
        m.active = false;
        if (m.canvas) m.canvas.style.display = 'none';
    }

    // Restore standard view
    fitToViewport(state);
    if (state.isVolumeLoaded) {
        renderAllSlices(state);
        renderAllOverlays(state);
    }
}

// ── 3D mouse handlers ────────────────────────────────────────────────────────

function setup3DMouseHandlers(state) {
    const m = state.model;
    if (!m.canvas || m._3dHandlersAttached) return;
    m._3dHandlersAttached = true;

    m._on3DMouseDown = (e) => handle3DMouseDown(state, e);
    m._on3DWheel = (e) => handle3DWheel(state, e);
    m._on3DContextMenu = (e) => e.preventDefault();

    m.canvas.addEventListener('mousedown', m._on3DMouseDown);
    m.canvas.addEventListener('wheel', m._on3DWheel, { passive: false });
    m.canvas.addEventListener('contextmenu', m._on3DContextMenu);
}

function handle3DMouseDown(state, e) {
    if (state.viewMode !== '3d') return;

    const cam = state.camera3d;

    if (e.button === 0) {
        // Orbit: left-drag maps dx→yaw, dy→pitch
        let lastX = e.clientX, lastY = e.clientY;
        const onMove = (ev) => {
            const dx = ev.clientX - lastX;
            const dy = ev.clientY - lastY;
            lastX = ev.clientX;
            lastY = ev.clientY;
            cam.yaw -= dx * 0.005;
            cam.pitch += dy * 0.005;
            const limit = Math.PI / 2 - 0.01;
            cam.pitch = Math.max(-limit, Math.min(limit, cam.pitch));
            requestModelRender(state, true);
        };
        const onUp = () => {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
        };
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    } else if (e.button === 2) {
        // Pan: right-drag moves in screen plane
        let lastX = e.clientX, lastY = e.clientY;
        const onMove = (ev) => {
            const dx = ev.clientX - lastX;
            const dy = ev.clientY - lastY;
            lastX = ev.clientX;
            lastY = ev.clientY;

            const canvas = state.model.canvas;
            const dpr = window.devicePixelRatio || 1;
            let wupp;
            if (cam.projection === 'perspective') {
                wupp = 2 * cam.distance * Math.tan(cam.fov / 2) / (canvas.height / dpr);
            } else {
                wupp = cam.orthoScale * cam.distance / (canvas.height / dpr);
            }

            // Z-up: camera right vector lies in XY plane
            // right = (-sin(yaw), cos(yaw), 0)
            const rightX = -Math.sin(cam.yaw);
            const rightY = Math.cos(cam.yaw);

            // Screen-X pans along camera right (in XY plane)
            // Screen-Y pans along world Z (up)
            cam.panX -= dx * rightX * wupp;
            cam.panY -= dx * rightY * wupp;
            cam.panZ += dy * wupp;

            requestModelRender(state, true);
        };
        const onUp = () => {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
        };
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    }
}

function handle3DWheel(state, e) {
    if (state.viewMode !== '3d') return;
    e.preventDefault();

    const cam = state.camera3d;
    const factor = Math.pow(1.1, -e.deltaY / 100);

    if (cam.projection === 'perspective') {
        cam.distance = Math.max(1, cam.distance / factor);
    } else {
        cam.orthoScale = Math.max(0.01, cam.orthoScale / factor);
    }

    requestModelRender(state, true);
}

// ── 3D rendering ─────────────────────────────────────────────────────────────

function renderModelsOrthoslice(state) {
    const m = state.model;
    const gl = m.gl;
    const canvas = m.canvas;
    const w = canvas.width;
    const h = canvas.height;
    if (w === 0 || h === 0) return;

    // Standard lit rendering with per-species loop
    gl.viewport(0, 0, w, h);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    gl.enable(gl.SCISSOR_TEST);

    gl.useProgram(m.program);
    gl.uniformMatrix4fv(m.uniforms.view, false, IDENTITY_MAT4);
    gl.uniform3f(m.uniforms.halfDims,
        state.dims.x * 0.5, state.dims.y * 0.5, state.dims.z * 0.5);

    for (const sp of state.species) {
        if (!sp.settings.visible || sp.particles.length === 0) continue;
        const settings = sp.settings;
        const sm = sp.model;

        let vertexCount, modelScale;
        if (settings.displayType === 'models' && sm.lods.length > 0) {
            const lodIndex = m._interacting ? sm.lods.length - 1 : selectSpeciesLOD(state, sm);
            const lod = sm.lods[lodIndex];
            if (!lod || lod.vertexCount === 0) continue;
            bindLODToVAO(gl, m, sm, lodIndex);
            vertexCount = lod.vertexCount;
            modelScale = 1.0 / state.pixelSize;
        } else if (settings.displayType === 'spheres' && state.primitiveGeometry.sphere) {
            bindPrimitiveGeomToVAO(gl, m, state.primitiveGeometry.sphere);
            vertexCount = state.primitiveGeometry.sphere.vertexCount;
            modelScale = settings.diameter / (2 * state.pixelSize);
        } else if (settings.displayType === 'cubes' && state.primitiveGeometry.cube) {
            bindPrimitiveGeomToVAO(gl, m, state.primitiveGeometry.cube);
            vertexCount = state.primitiveGeometry.cube.vertexCount;
            modelScale = settings.diameter / (2 * state.pixelSize);
        } else {
            continue;
        }

        gl.uniform1f(m.uniforms.modelScale, modelScale);
        const color = parseHexColor(settings.color);
        gl.uniform3f(m.uniforms.color, color[0], color[1], color[2]);

        // Ensure instance buffer
        if (!sm.instanceBuffer) sm.instanceBuffer = gl.createBuffer();
        const particleCount = sp.particles.length;
        if (particleCount > 0 && sm._instanceBufferCapacity !== particleCount) {
            gl.bindBuffer(gl.ARRAY_BUFFER, sm.instanceBuffer);
            gl.bufferData(gl.ARRAY_BUFFER, particleCount * 48, gl.DYNAMIC_DRAW);
            sm._instanceBufferCapacity = particleCount;
        }

        gl.bindVertexArray(m.vao);
        for (const planeType of ['xy', 'xz', 'zy']) {
            renderSpeciesModelPanel(state, sp, gl, m, planeType, w, h, vertexCount, m.uniforms);
        }
    }

    gl.disable(gl.SCISSOR_TEST);
    gl.bindVertexArray(null);
}

function renderModels3D(state) {
    const m = state.model;
    const gl = m.gl;
    const canvas = m.canvas;
    const w = canvas.width;
    const h = canvas.height;
    if (w === 0 || h === 0) return;

    gl.viewport(0, 0, w, h);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    gl.disable(gl.SCISSOR_TEST);

    const { projMatrix, viewMatrix, viewDir } = computeCamera3dMatrices(state);

    // Render slice planes first (opaque, depth write on)
    if (!m.hideOrthoslices && m.sliceProgram && state.isVolumeLoaded) {
        renderSlicePlanes3D(state, gl, projMatrix, viewMatrix);
    }

    // Render particles for each species
    for (const sp of state.species) {
        if (sp.particles.length === 0 || !sp.settings.visible) continue;
        const useFlatShading = sp.settings.flatShading && m.idProgram && m.contourProgram;
        if (useFlatShading) {
            renderSpeciesParticlesId3D(state, sp, gl, m, projMatrix, viewMatrix, w, h);
        } else {
            renderSpeciesParticles3D(state, sp, gl, m, projMatrix, viewMatrix, viewDir);
        }
    }
}

function renderSpeciesParticles3D(state, species, gl, m, projMatrix, viewMatrix, viewDir) {
    if (!m.program) return;

    const settings = species.settings;
    const sm = species.model;
    const displayType = settings.displayType;

    // Determine geometry and scale
    let vertexCount;
    let modelScale;

    if (displayType === 'models' && sm.lods.length > 0) {
        const lodIndex = m._interacting ? sm.lods.length - 1 : 0;
        const lod = sm.lods[lodIndex];
        if (!lod || lod.vertexCount === 0) return;
        bindLODToVAO(gl, m, sm, lodIndex);
        vertexCount = lod.vertexCount;
        modelScale = 1.0 / state.pixelSize;
    } else if (displayType === 'spheres' && state.primitiveGeometry.sphere) {
        bindPrimitiveGeomToVAO(gl, m, state.primitiveGeometry.sphere);
        vertexCount = state.primitiveGeometry.sphere.vertexCount;
        modelScale = settings.diameter / (2 * state.pixelSize);
    } else if (displayType === 'cubes' && state.primitiveGeometry.cube) {
        bindPrimitiveGeomToVAO(gl, m, state.primitiveGeometry.cube);
        vertexCount = state.primitiveGeometry.cube.vertexCount;
        modelScale = settings.diameter / (2 * state.pixelSize);
    } else {
        return;
    }

    gl.useProgram(m.program);
    gl.uniformMatrix4fv(m.uniforms.projection, false, projMatrix);
    gl.uniformMatrix4fv(m.uniforms.view, false, viewMatrix);
    gl.uniform3fv(m.uniforms.viewDir, viewDir);

    // Camera-relative lighting for 3D mode
    const cam = state.camera3d;
    const cosP = Math.cos(cam.pitch), sinP = Math.sin(cam.pitch);
    const cosY = Math.cos(cam.yaw), sinY = Math.sin(cam.yaw);
    const crx = -sinY, cry = cosY, crz = 0;
    const cux = -sinP * cosY, cuy = -sinP * sinY, cuz = cosP;
    const kx = -viewDir[0] + 0.36 * crx + 0.58 * cux;
    const ky = -viewDir[1] + 0.36 * cry + 0.58 * cuy;
    const kz = -viewDir[2] + 0.36 * crz + 0.58 * cuz;
    const klen = Math.sqrt(kx * kx + ky * ky + kz * kz) || 1;
    gl.uniform3f(m.uniforms.light0, kx / klen, ky / klen, kz / klen);
    const f1x = -viewDir[0] - 0.5 * crx - 0.3 * cux;
    const f1y = -viewDir[1] - 0.5 * cry - 0.3 * cuy;
    const f1z = -viewDir[2] - 0.5 * crz - 0.3 * cuz;
    const f1len = Math.sqrt(f1x * f1x + f1y * f1y + f1z * f1z) || 1;
    gl.uniform3f(m.uniforms.light1, f1x / f1len, f1y / f1len, f1z / f1len);
    const f2x = viewDir[0] + 0.2 * crx + 0.7 * cux;
    const f2y = viewDir[1] + 0.2 * cry + 0.7 * cuy;
    const f2z = viewDir[2] + 0.2 * crz + 0.7 * cuz;
    const f2len = Math.sqrt(f2x * f2x + f2y * f2y + f2z * f2z) || 1;
    gl.uniform3f(m.uniforms.light2, f2x / f2len, f2y / f2len, f2z / f2len);

    gl.uniform1f(m.uniforms.modelScale, modelScale);
    gl.uniform3f(m.uniforms.halfDims,
        state.dims.x * 0.5, state.dims.y * 0.5, state.dims.z * 0.5);

    const color = parseHexColor(settings.color);
    gl.uniform3f(m.uniforms.color, color[0], color[1], color[2]);

    gl.bindVertexArray(m.vao);

    // Upload all particles (no slab filtering in 3D mode)
    const rotData = sm.instanceRotations;
    if (!rotData) return;

    if (!sm.instanceBuffer) {
        sm.instanceBuffer = gl.createBuffer();
    }
    const particleCount = species.particles.length;
    if (particleCount > 0 && sm._instanceBufferCapacity !== particleCount) {
        gl.bindBuffer(gl.ARRAY_BUFFER, sm.instanceBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, particleCount * 48, gl.DYNAMIC_DRAW);
        sm._instanceBufferCapacity = particleCount;
    }

    gl.bindBuffer(gl.ARRAY_BUFFER, sm.instanceBuffer);
    gl.bufferSubData(gl.ARRAY_BUFFER, 0, rotData);

    gl.drawArraysInstanced(gl.TRIANGLES, 0, vertexCount, particleCount);
    gl.bindVertexArray(null);
}

function renderSpeciesParticlesId3D(state, species, gl, m, projMatrix, viewMatrix, w, h) {
    const settings = species.settings;
    const sm = species.model;
    const displayType = settings.displayType;

    // Determine geometry and scale
    let vertexCount;
    let modelScale;

    if (displayType === 'models' && sm.lods.length > 0) {
        const lodIndex = m._interacting ? sm.lods.length - 1 : 0;
        const lod = sm.lods[lodIndex];
        if (!lod || lod.vertexCount === 0) return;
        bindLODToVAO(gl, m, sm, lodIndex);
        vertexCount = lod.vertexCount;
        modelScale = 1.0 / state.pixelSize;
    } else if (displayType === 'spheres' && state.primitiveGeometry.sphere) {
        bindPrimitiveGeomToVAO(gl, m, state.primitiveGeometry.sphere);
        vertexCount = state.primitiveGeometry.sphere.vertexCount;
        modelScale = settings.diameter / (2 * state.pixelSize);
    } else if (displayType === 'cubes' && state.primitiveGeometry.cube) {
        bindPrimitiveGeomToVAO(gl, m, state.primitiveGeometry.cube);
        vertexCount = state.primitiveGeometry.cube.vertexCount;
        modelScale = settings.diameter / (2 * state.pixelSize);
    } else {
        return;
    }

    const rotData = sm.instanceRotations;
    if (!rotData) return;

    if (!sm.instanceBuffer) {
        sm.instanceBuffer = gl.createBuffer();
    }
    const particleCount = species.particles.length;
    if (particleCount > 0 && sm._instanceBufferCapacity !== particleCount) {
        gl.bindBuffer(gl.ARRAY_BUFFER, sm.instanceBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, particleCount * 48, gl.DYNAMIC_DRAW);
        sm._instanceBufferCapacity = particleCount;
    }

    // ── Pass 1: render IDs to offscreen FBO ──
    ensureIdBuffer(gl, m, w, h);
    gl.bindFramebuffer(gl.FRAMEBUFFER, m.idFBO);
    gl.clearBufferiv(gl.COLOR, 0, ID_CLEAR_VALUE);
    gl.clear(gl.DEPTH_BUFFER_BIT);
    gl.viewport(0, 0, w, h);

    gl.useProgram(m.idProgram);
    gl.uniformMatrix4fv(m.idUniforms.projection, false, projMatrix);
    gl.uniformMatrix4fv(m.idUniforms.view, false, viewMatrix);
    gl.uniform1f(m.idUniforms.modelScale, modelScale);
    gl.uniform3f(m.idUniforms.halfDims,
        state.dims.x * 0.5, state.dims.y * 0.5, state.dims.z * 0.5);

    gl.bindVertexArray(m.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, sm.instanceBuffer);
    gl.bufferSubData(gl.ARRAY_BUFFER, 0, rotData);
    gl.drawArraysInstanced(gl.TRIANGLES, 0, vertexCount, particleCount);
    gl.bindVertexArray(null);

    // ── Pass 2: contour composite to default framebuffer ──
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);

    const baseColor = parseHexColor(settings.color);
    const contourColor = parseHexColor(settings.contourColor);
    compositeContour(gl, m, baseColor, contourColor, w, h);
}

function renderSlicePlanes3D(state, gl, projMatrix, viewMatrix) {
    const m = state.model;

    // Ensure slices are up to date
    renderAllSlices(state);

    gl.useProgram(m.sliceProgram);
    gl.bindVertexArray(m.sliceVAO);

    const mvp = mat4_multiply(projMatrix, viewMatrix);
    gl.uniformMatrix4fv(m.sliceUniforms.mvp, false, mvp);
    gl.uniform1i(m.sliceUniforms.texture, 0);

    gl.activeTexture(gl.TEXTURE0);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);

    const { dims, viewPoint } = state;
    const hx = dims.x * 0.5, hy = dims.y * 0.5, hz = dims.z * 0.5;

    const planes = [
        {
            plane: 'xy',
            // XY plane at z=viewPoint.z
            corners: [
                [-hx, -hy, viewPoint.z - hz],
                [ hx, -hy, viewPoint.z - hz],
                [ hx,  hy, viewPoint.z - hz],
                [-hx,  hy, viewPoint.z - hz],
            ],
        },
        {
            plane: 'xz',
            // XZ plane at y=viewPoint.y
            corners: [
                [-hx, viewPoint.y - hy, -hz],
                [ hx, viewPoint.y - hy, -hz],
                [ hx, viewPoint.y - hy,  hz],
                [-hx, viewPoint.y - hy,  hz],
            ],
        },
        {
            plane: 'zy',
            // ZY plane at x=viewPoint.x
            corners: [
                [viewPoint.x - hx, -hy, -hz],
                [viewPoint.x - hx, -hy,  hz],
                [viewPoint.x - hx,  hy,  hz],
                [viewPoint.x - hx,  hy, -hz],
            ],
        },
    ];

    for (const { plane, corners } of planes) {
        const canvas = state.panels[plane].sliceCanvas;

        // Upload slice canvas to texture
        gl.bindTexture(gl.TEXTURE_2D, m.sliceTextures[plane]);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, canvas);

        // Build 6 vertices (2 triangles) with texcoords
        const [c0, c1, c2, c3] = corners;
        // Texcoords: c0=(0,0), c1=(1,0), c2=(1,1), c3=(0,1)
        const verts = new Float32Array([
            c0[0], c0[1], c0[2], 0, 0,
            c1[0], c1[1], c1[2], 1, 0,
            c2[0], c2[1], c2[2], 1, 1,
            c0[0], c0[1], c0[2], 0, 0,
            c2[0], c2[1], c2[2], 1, 1,
            c3[0], c3[1], c3[2], 0, 1,
        ]);

        gl.bindBuffer(gl.ARRAY_BUFFER, m.sliceVertBuffer);
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, verts);

        gl.drawArrays(gl.TRIANGLES, 0, 6);
    }

    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
    gl.bindVertexArray(null);

    // Draw edge lines and intersection lines as screen-space quads
    if (m.lineProgram) {
        gl.useProgram(m.lineProgram);
        gl.uniformMatrix4fv(m.lineUniforms.mvp, false, mvp);
        gl.uniform2f(m.lineUniforms.viewportSize,
            m.canvas.width, m.canvas.height);
        gl.uniform1f(m.lineUniforms.lineWidth, 1.5); // half-width in pixels (total = 3px)
        gl.bindVertexArray(m.lineVAO);
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
        gl.enable(gl.POLYGON_OFFSET_FILL);
        gl.polygonOffset(-1, -1); // bias toward camera to avoid z-fighting with planes

        // Collect all line segments: edges + intersections
        const segments = [];

        // Edge lines for each plane (4 edges per plane)
        for (const { corners } of planes) {
            const [c0, c1, c2, c3] = corners;
            segments.push([c0, c1], [c1, c2], [c2, c3], [c3, c0]);
        }

        // Intersection lines
        const vx = viewPoint.x - hx, vy = viewPoint.y - hy, vz = viewPoint.z - hz;
        segments.push(
            [[-hx, vy, vz], [ hx, vy, vz]],  // XY ∩ XZ
            [[vx, -hy, vz], [vx,  hy, vz]],   // XY ∩ ZY
            [[vx, vy, -hz], [vx, vy,  hz]],    // XZ ∩ ZY
        );

        // Build quad vertices: 6 verts per segment, 8 floats per vert (P0 + P1 + corner)
        const lineVerts = new Float32Array(segments.length * 6 * 8);
        let vi = 0;
        for (const [p0, p1] of segments) {
            // 2 triangles: (0,-1)(1,-1)(1,1) and (0,-1)(1,1)(0,1)
            const corners = [[0,-1],[1,-1],[1,1], [0,-1],[1,1],[0,1]];
            for (const [cx, cy] of corners) {
                lineVerts[vi++] = p0[0]; lineVerts[vi++] = p0[1]; lineVerts[vi++] = p0[2];
                lineVerts[vi++] = p1[0]; lineVerts[vi++] = p1[1]; lineVerts[vi++] = p1[2];
                lineVerts[vi++] = cx;    lineVerts[vi++] = cy;
            }
        }

        gl.bindBuffer(gl.ARRAY_BUFFER, m.lineVertBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, lineVerts, gl.DYNAMIC_DRAW);

        // Edge lines (12 segments = 72 verts): semi-transparent white
        gl.uniform4f(m.lineUniforms.color, 0.7, 0.7, 0.7, 0.8);
        gl.drawArrays(gl.TRIANGLES, 0, 72);

        // Intersection lines (3 segments = 18 verts): bright yellow
        gl.uniform4f(m.lineUniforms.color, 1.0, 1.0, 0.4, 0.9);
        gl.drawArrays(gl.TRIANGLES, 72, 18);

        gl.disable(gl.POLYGON_OFFSET_FILL);
        gl.disable(gl.BLEND);
        gl.bindVertexArray(null);
    }
}

// ── Display type & visibility ────────────────────────────────────────────────

function onDisplayTypeChanged(state, oldType, newType) {
    const sp = getSelectedSpecies(state);
    if (!sp) return;

    if (newType === 'spheres') sp.settings.shape = 'circle';
    else if (newType === 'cubes') sp.settings.shape = 'square';

    syncToolbarToSpecies(state);

    if (state.viewMode === '3d') {
        if (newType === 'models' && sp.model.volumeUrl && !sp.model.volumeData) {
            loadSpeciesModelVolume(state, state.selectedSpeciesIndex);
        }
        requestModelRender(state);
        return;
    }

    // Orthoslice mode
    const anyModels = state.species.some(s => s.settings.visible && s.settings.displayType === 'models');

    if (anyModels && !state.model.active) {
        activateModelMode(state);
    } else if (!anyModels && state.model.active) {
        deactivateModelMode(state);
    } else {
        if (newType === 'models' && sp.model.volumeUrl && !sp.model.volumeData) {
            loadSpeciesModelVolume(state, state.selectedSpeciesIndex);
        }
        if (state.model.active) requestModelRender(state);
    }

    renderAllOverlays(state);
}

function applyParticleVisibility(state) {
    if (state.viewMode === '3d') {
        requestModelRender(state);
        return;
    }

    const anyModels = state.species.some(s => s.settings.visible && s.settings.displayType === 'models');

    if (anyModels && !state.model.active) {
        activateModelMode(state);
    } else if (!anyModels && state.model.active) {
        deactivateModelMode(state);
    }

    renderAllOverlays(state);
    if (state.model.active) requestModelRender(state);
}

// ── Model mode toggle ───────────────────────────────────────────────────────

function activateModelMode(state) {
    const m = state.model;
    m.active = true;
    initModelWebGL(state);
    if (!m.gl) return;
    m.canvas.style.display = 'block';
    resizeModelCanvas(state);
    if (m.hideOrthoslices) setOrthosliceVisibility(state, false);

    // Load model volumes for all species that need them
    for (let i = 0; i < state.species.length; i++) {
        const sm = state.species[i].model;
        if (sm.volumeUrl && !sm.volumeData) {
            loadSpeciesModelVolume(state, i);
        }
    }

    requestModelRender(state);
    renderAllOverlays(state);
}

function deactivateModelMode(state) {
    const m = state.model;
    m.active = false;

    if (m.canvas) m.canvas.style.display = 'none';

    // Restore orthoslices only if user hasn't explicitly hidden them
    if (!m.hideOrthoslices) setOrthosliceVisibility(state, true);

    // Re-render overlays (circles visible again)
    renderAllOverlays(state);
}

function setOrthosliceVisibility(state, visible) {
    // In 3D mode, skip CSS toggle on 2D canvases — just re-render
    if (state.viewMode === '3d') {
        requestModelRender(state);
        return;
    }
    for (const planeType of ['xy', 'xz', 'zy']) {
        state.panels[planeType].sliceCanvas.style.display = visible ? '' : 'none';
    }
}

// ── Model threshold UI update ───────────────────────────────────────────────

function updateModelThresholdUI(state) {
    const sp = getSelectedSpecies(state);
    if (!sp) return;
    const sm = sp.model;
    if (state.toolbar.modelThreshSlider) {
        state.toolbar.modelThreshSlider.value = sm.thresholdPct;
    }
    if (state.toolbar.modelThreshInput && sm.volumeStats) {
        state.toolbar.modelThreshInput.value = formatModelThreshold(sm.threshold);
    }
}

function formatModelThreshold(val) {
    if (val === 0) return '0';
    const abs = Math.abs(val);
    if (abs >= 100) return val.toFixed(1);
    if (abs >= 1) return val.toFixed(3);
    return val.toFixed(4);
}
