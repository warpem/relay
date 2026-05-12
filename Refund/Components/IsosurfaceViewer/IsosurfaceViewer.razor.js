/**
 * IsosurfaceViewer - Client-side WebGL2 isosurface renderer.
 *
 * Loads MRC volumes, generates isosurface meshes via marching cubes (Web Worker),
 * renders with Blinn-Phong shading. Quaternion-based orbit camera with free rotation
 * (no gimbal lock), camera and threshold sync across instances, Euler angle display
 * in cryo-EM convention.
 */

import { fetchMRC } from '../../js/mrc-parser.js';
import {
    compileShader,
    buildOccupancyTexture,
    BAKE_VERTEX_SHADER,
    BAKE_FRAGMENT_SHADER,
    SH_EVALUATE_GLSL,
    initBakeResources,
    bakeSHCoefficients
} from '../../js/webgl-isosurface-shared.js';

// ── Module-level sync ───────────────────────────────────────────────────────

const _defaultOrientation = [0, 0, 0, 1]; // identity = looking along -Z

const globalCamera = {
    orientation: [..._defaultOrientation],
    distance: 300, panX: 0, panY: 0
};
let globalThreshold = null;

const syncedCameraInstances = new Set();
const syncedThresholdInstances = new Set();
const instances = new Map();

// ── Settings persistence (localStorage) ──────────────────────────────────────

const STORAGE_PREFIX = 'isosurface-viewer:';

function saveSettings(state) {
    if (!state.storageKey) return;
    try {
        const data = {
            camera: {
                orientation: [...state.camera.orientation],
                distance: state.camera.distance,
                panX: state.camera.panX,
                panY: state.camera.panY
            },
            threshold: state.threshold,
            cameraSynced: state.cameraSynced,
            isoSynced: state.isoSynced
        };
        localStorage.setItem(STORAGE_PREFIX + state.storageKey, JSON.stringify(data));
    } catch { /* quota exceeded or unavailable — ignore */ }
}

function loadSettings(storageKey) {
    if (!storageKey) return null;
    try {
        const raw = localStorage.getItem(STORAGE_PREFIX + storageKey);
        if (!raw) return null;
        return JSON.parse(raw);
    } catch { return null; }
}

function scheduleSave(state) {
    clearTimeout(state._saveTimer);
    state._saveTimer = setTimeout(() => saveSettings(state), 500);
}

function broadcastCamera(sourceId, cam) {
    globalCamera.orientation = [...cam.orientation];
    globalCamera.distance = cam.distance;
    globalCamera.panX = cam.panX;
    globalCamera.panY = cam.panY;

    for (const id of syncedCameraInstances) {
        if (id === sourceId) continue;
        const s = instances.get(id);
        if (s) {
            s.camera.orientation = [...globalCamera.orientation];
            s.camera.distance = globalCamera.distance;
            s.camera.panX = globalCamera.panX;
            s.camera.panY = globalCamera.panY;
            requestRender(s);
        }
    }
}

function broadcastThreshold(sourceId, threshold) {
    globalThreshold = threshold;
    for (const id of syncedThresholdInstances) {
        if (id === sourceId) continue;
        const s = instances.get(id);
        if (s && s.header) {
            s.threshold = threshold;
            updateThresholdUI(s);
            regenerateMesh(s);
        }
    }
}

// ── Exported functions ──────────────────────────────────────────────────────

export function initialize(container, dotNetRef, fileUrl, options) {
    const elementId = container.id;
    if (instances.has(elementId)) dispose(elementId);

    const state = createState(elementId, container, dotNetRef, fileUrl, options);
    instances.set(elementId, state);
    syncedCameraInstances.add(elementId);
    syncedThresholdInstances.add(elementId);

    createUI(state);
    setupMouseHandlers(state);
    setupResizeObserver(state);

    if (fileUrl) {
        loadVolume(state);
    }
}

export function loadVolumeByUrl(elementId, url, storageKey) {
    const state = instances.get(elementId);
    if (!state) return;

    state.fileUrl = url;
    state.storageKey = storageKey || null;
    state.header = null;
    state.volumeData = null;
    state.mesh = null;
    showLoading(state, 0, 'Loading volume...');
    loadVolume(state);
}

export function dispose(elementId) {
    const state = instances.get(elementId);
    if (!state) return;

    if (state.abortController) state.abortController.abort();
    if (state.resizeObserver) state.resizeObserver.disconnect();
    if (state.worker) state.worker.terminate();
    if (state._onFullscreenChange) {
        document.removeEventListener('fullscreenchange', state._onFullscreenChange);
    }

    const canvas = state.canvas;
    if (canvas) {
        canvas.removeEventListener('mousedown', state._boundMouseDown);
        canvas.removeEventListener('wheel', state._boundWheel);
        canvas.removeEventListener('contextmenu', state._boundContextMenu);
        canvas.removeEventListener('mouseup', state._boundCanvasMouseUp);
        canvas.removeEventListener('click', state._boundCanvasClick);
        canvas.removeEventListener('mousemove', state._boundTooltipMove);
        canvas.removeEventListener('mouseleave', state._boundTooltipLeave);
    }
    document.removeEventListener('mousemove', state._boundMouseMove);
    document.removeEventListener('mouseup', state._boundMouseUp);
    if (state._tooltipRaf) cancelAnimationFrame(state._tooltipRaf);

    syncedCameraInstances.delete(elementId);
    syncedThresholdInstances.delete(elementId);
    instances.delete(elementId);
}

// ── State factory ───────────────────────────────────────────────────────────

function createState(elementId, container, dotNetRef, fileUrl, options) {
    return {
        elementId, dotNetRef, container, fileUrl,
        storageKey: options?.storageKey || null,

        // MRC
        header: null, volumeData: null,
        stats: { min: 0, max: 1, mean: 0, std: 1 },
        dims: { x: 0, y: 0, z: 0 },
        pixelSize: { x: 1, y: 1, z: 1 },

        // Mesh
        threshold: 0,
        mesh: null,

        // WebGL
        gl: null, canvas: null, program: null,
        vao: null, posBuffer: null, normBuffer: null, shBuffer: null,
        uniforms: {},
        surfaceColor: [1.0, 1.0, 1.0],

        // Pick (position readback)
        pickFBO: null, pickColorRB: null, pickPosTex: null, pickDepthRB: null,
        _pickSize: { w: 0, h: 0 },
        _hasFloatColorBuffer: false,
        _tooltip: null, _tooltipRaf: null,

        // SH bake (GPU)
        bakeProgram: null, bakeVAO: null, transformFeedback: null,
        bakeUniforms: {}, occTexture: null,

        // Camera (quaternion-based, Angstrom units)
        camera: {
            orientation: [...globalCamera.orientation],
            distance: globalCamera.distance,
            panX: globalCamera.panX,
            panY: globalCamera.panY
        },
        cameraSynced: true,
        isoSynced: true,

        // Worker
        worker: null,

        // UI
        toolbar: {},
        loadingOverlay: null,

        // Interaction
        _isDragging: false,
        _dragButton: -1,
        _dragShift: false,
        _lastMouse: { x: 0, y: 0 },
        _arcballStart: [0, 0, 1],
        _arcballOrientation: [0, 0, 0, 1],

        // Cleanup
        abortController: null,
        resizeObserver: null,
        _renderQueued: false,
        _boundMouseDown: null, _boundMouseMove: null, _boundMouseUp: null,
        _boundWheel: null, _boundContextMenu: null,
        _boundTooltipMove: null, _boundTooltipLeave: null,

        options: {
            minWidth: options?.minWidth || 400,
            minHeight: options?.minHeight || 400,
            showEulerAngles: options?.showEulerAngles ?? true,
            miniMode: options?.miniMode ?? false
        }
    };
}

// ── UI Creation ─────────────────────────────────────────────────────────────

function createUI(state) {
    const c = state.container;
    c.innerHTML = '';
    c.style.display = 'flex';
    c.style.flexDirection = 'column';
    c.style.overflow = 'hidden';
    c.style.background = 'var(--neutral-fill-layer-rest)';
    c.style.userSelect = 'none';

    const toolbar = createToolbar(state);
    c.appendChild(toolbar);

    // Canvas container — theme-aware background shows through transparent WebGL
    const canvasContainer = document.createElement('div');
    canvasContainer.style.cssText = 'flex:1; position:relative; overflow:hidden; background:var(--neutral-fill-layer-rest);';
    c.appendChild(canvasContainer);

    const canvas = document.createElement('canvas');
    canvas.style.cssText = 'width:100%; height:100%; display:block;';
    canvasContainer.appendChild(canvas);
    state.canvas = canvas;

    const tooltip = document.createElement('div');
    tooltip.style.cssText = `
        position: absolute; pointer-events: none; display: none;
        background: var(--neutral-layer-1); border: 1px solid var(--neutral-stroke-rest);
        border-radius: 4px; padding: 4px 8px; font-size: 11px; font-family: monospace;
        color: var(--neutral-foreground-rest); white-space: nowrap; z-index: 10;
        box-shadow: var(--shadow-4); line-height: 1.4;
    `;
    canvasContainer.appendChild(tooltip);
    state._tooltip = tooltip;

    initWebGL(state);

    state.loadingOverlay = createLoadingOverlay();
    c.appendChild(state.loadingOverlay.root);

    setupFullscreenListener(state);
}

function createToolbar(state, miniOverride) {
    const mini = miniOverride !== undefined ? miniOverride : state.options.miniMode;

    const toolbar = document.createElement('div');
    toolbar.style.cssText = mini
        ? `display: flex; align-items: center; gap: 4px; padding: 3px 6px;
           background: var(--neutral-layer-1); box-shadow: var(--shadow-4); flex-shrink: 0;`
        : `display: flex; flex-wrap: wrap; align-items: flex-end; gap: 8px 16px; padding: 6px 12px;
           background: var(--neutral-layer-1); box-shadow: var(--shadow-4); flex-shrink: 0;`;

    // Stop clicks/mousedowns on toolbar from propagating to parent components
    toolbar.addEventListener('click', (e) => e.stopPropagation());
    toolbar.addEventListener('mousedown', (e) => e.stopPropagation());

    // ── Threshold slider ──
    const slider = document.createElement('input');
    slider.type = 'range';
    slider.min = '0';
    slider.max = '100';
    slider.value = '30';
    slider.style.cssText = mini
        ? 'flex:1; min-width:24px; height:4px; cursor:pointer; accent-color:var(--accent-fill-rest);'
        : 'width:60px; height:4px; cursor:pointer; accent-color:var(--accent-fill-rest);';

    // ── Threshold number input (full mode only) ──
    let threshInput = null;
    if (!mini) {
        threshInput = document.createElement('input');
        threshInput.type = 'number';
        threshInput.step = '0.001';
        threshInput.value = '0';
        threshInput.style.cssText = 'width:72px; height:24px; font-size:11px; font-family:monospace; border:1px solid var(--neutral-stroke-rest); border-radius:4px; padding:0 4px; text-align:right; background:var(--neutral-layer-1); color:var(--neutral-foreground-rest);';
    }

    // Slider drag → update display only
    slider.addEventListener('input', () => {
        const pct = parseFloat(slider.value) / 100;
        const val = state.stats.min + pct * (state.stats.max - state.stats.min);
        state.threshold = val;
        if (threshInput) threshInput.value = formatThresholdValue(val);
    });

    // Slider release → regenerate mesh + broadcast + save
    slider.addEventListener('change', () => {
        const pct = parseFloat(slider.value) / 100;
        state.threshold = state.stats.min + pct * (state.stats.max - state.stats.min);
        if (threshInput) threshInput.value = formatThresholdValue(state.threshold);
        if (state.header) regenerateMesh(state);
        if (state.isoSynced) broadcastThreshold(state.elementId, state.threshold);
        scheduleSave(state);
    });

    // Number input → regenerate + broadcast + save (full mode only)
    if (threshInput) {
        threshInput.addEventListener('change', () => {
            const val = parseFloat(threshInput.value);
            if (isNaN(val)) return;
            state.threshold = val;
            threshInput.value = formatThresholdValue(val);
            const range = state.stats.max - state.stats.min;
            if (range > 0) {
                slider.value = ((val - state.stats.min) / range) * 100;
            }
            if (state.header) regenerateMesh(state);
            if (state.isoSynced) broadcastThreshold(state.elementId, state.threshold);
            scheduleSave(state);
        });
    }

    // ── Threshold lock ──
    const isoLockBtn = createLockToggle(state.isoSynced, (active) => {
        state.isoSynced = active;
        if (active) {
            syncedThresholdInstances.add(state.elementId);
            if (globalThreshold !== null && state.header) {
                state.threshold = globalThreshold;
                updateThresholdUI(state);
                regenerateMesh(state);
            }
        } else {
            syncedThresholdInstances.delete(state.elementId);
        }
        scheduleSave(state);
    });
    isoLockBtn.title = 'Sync threshold across viewers';

    // ── Camera lock ──
    const camLockBtn = createLockToggle(state.cameraSynced, (active) => {
        state.cameraSynced = active;
        if (active) {
            syncedCameraInstances.add(state.elementId);
            state.camera.orientation = [...globalCamera.orientation];
            state.camera.distance = globalCamera.distance;
            state.camera.panX = globalCamera.panX;
            state.camera.panY = globalCamera.panY;
            requestRender(state);
        } else {
            syncedCameraInstances.delete(state.elementId);
        }
        scheduleSave(state);
    });
    camLockBtn.title = 'Sync camera across viewers';

    // ── Fullscreen button (shared by both modes) ──
    const fullscreenBtn = document.createElement('button');
    fullscreenBtn.textContent = '\u26F6';
    fullscreenBtn.title = 'Toggle fullscreen';
    fullscreenBtn.style.cssText = `
        width: 24px; height: 24px; font-size: 14px; line-height: 1;
        border: 1px solid var(--neutral-stroke-rest);
        border-radius: 4px; cursor: pointer; background: var(--neutral-layer-1);
        color: var(--neutral-foreground-rest); display: flex; align-items: center; justify-content: center;
        padding: 0;
    `;
    fullscreenBtn.addEventListener('click', () => toggleFullscreen(state));

    state.toolbar.slider = slider;
    state.toolbar.threshInput = threshInput;
    state.toolbar.isoLockBtn = isoLockBtn;
    state.toolbar.camLockBtn = camLockBtn;
    state.toolbar.fullscreenBtn = fullscreenBtn;

    if (mini) {
        // Mini mode: slider + iso lock + cam lock + fullscreen — flat, no groups/labels
        toolbar.append(slider, isoLockBtn, camLockBtn, fullscreenBtn);
    } else {
        // Full mode: grouped layout with all controls
        const threshGroup = createToolbarGroup('Threshold');
        const threshRow = document.createElement('div');
        threshRow.style.cssText = 'display:flex; gap:6px; align-items:center;';
        threshRow.append(slider, threshInput, isoLockBtn);
        threshGroup.appendChild(threshRow);
        toolbar.appendChild(threshGroup);

        toolbar.appendChild(createSeparator());

        const viewGroup = createToolbarGroup('View');
        const viewRow = document.createElement('div');
        viewRow.style.cssText = 'display:flex; gap:6px; align-items:center;';

        let eulerDisplay = null;
        if (state.options.showEulerAngles) {
            eulerDisplay = document.createElement('span');
            eulerDisplay.style.cssText = 'font-size:10px; font-family:monospace; color:var(--neutral-foreground-hint); white-space:nowrap;';
            eulerDisplay.textContent = '0.0, 0.0, 0.0';
        }

        const resetBtn = document.createElement('button');
        resetBtn.innerHTML = '&#x21bb;';
        resetBtn.title = 'Reset camera';
        resetBtn.style.cssText = `
            width: 24px; height: 24px; font-size: 14px; line-height: 1;
            border: 1px solid var(--neutral-stroke-rest);
            border-radius: 4px; cursor: pointer; background: var(--neutral-layer-1);
            color: var(--neutral-foreground-rest); display: flex; align-items: center; justify-content: center;
            padding: 0;
        `;
        resetBtn.addEventListener('click', () => {
            resetCamera(state);
            if (state.cameraSynced) broadcastCamera(state.elementId, state.camera);
            scheduleSave(state);
            requestRender(state);
        });

        if (eulerDisplay) viewRow.appendChild(eulerDisplay);
        viewRow.append(camLockBtn, resetBtn, fullscreenBtn);
        viewGroup.appendChild(viewRow);
        toolbar.appendChild(viewGroup);

        state.toolbar.eulerDisplay = eulerDisplay;
    }

    return toolbar;
}

function createLockToggle(initialActive, onToggle) {
    const btn = document.createElement('button');
    let active = initialActive;

    function applyStyle() {
        btn.innerHTML = active ? '&#x1F512;' : '&#x1F513;';
        if (active) {
            btn.style.background = 'var(--accent-fill-rest)';
            btn.style.color = 'var(--foreground-on-accent-rest)';
            btn.style.borderColor = 'var(--accent-fill-rest)';
        } else {
            btn.style.background = 'var(--neutral-layer-1)';
            btn.style.color = 'var(--neutral-foreground-rest)';
            btn.style.borderColor = 'var(--neutral-stroke-rest)';
        }
    }

    btn.style.cssText = `
        width: 24px; height: 24px; font-size: 12px; line-height: 1;
        border: 1px solid; border-radius: 4px; cursor: pointer;
        display: flex; align-items: center; justify-content: center; padding: 0;
    `;
    applyStyle();

    btn.addEventListener('click', () => {
        active = !active;
        applyStyle();
        onToggle(active);
    });

    btn.setActive = (value) => {
        active = value;
        applyStyle();
        onToggle(active);
    };

    return btn;
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

// ── WebGL Setup ─────────────────────────────────────────────────────────────

const VERTEX_SHADER = `#version 300 es
precision highp float;

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aSH_A;
layout(location = 3) in vec4 aSH_B;
layout(location = 4) in float aSH_C;

uniform mat4 uModelViewProjection;
uniform mat4 uModelView;
uniform mat3 uNormalMatrix;
uniform vec3 uLightDir;
uniform float uModelExtent;

out vec3 vNormal;
out vec3 vPosition;
out vec3 vModelPosition;
out float vVis0;
out float vVis1;
out float vVis2;
out float vAmbientVis;
out float vSky;

${SH_EVALUATE_GLSL}

void main() {
    vec4 viewPos = uModelView * vec4(aPosition, 1.0);
    vPosition = viewPos.xyz;
    vNormal = uNormalMatrix * aNormal;
    vModelPosition = aPosition;

    mat3 viewToModel = transpose(uNormalMatrix);
    vec3 N_model = normalize(aNormal);
    vec3 L0_model = normalize(viewToModel * normalize(uLightDir));
    vec3 L1_model = normalize(viewToModel * normalize(vec3(-0.7, 0.5, 0.4)));
    vec3 L2_model = normalize(viewToModel * normalize(vec3(0.3, -0.2, -0.8)));

    vVis0 = evaluateSH(L0_model, aSH_A, aSH_B, aSH_C);
    vVis1 = evaluateSH(L1_model, aSH_A, aSH_B, aSH_C);
    vVis2 = evaluateSH(L2_model, aSH_A, aSH_B, aSH_C);
    vAmbientVis = evaluateSH(N_model, aSH_A, aSH_B, aSH_C);

    vSky = clamp(viewPos.y / uModelExtent * 0.5 + 0.5, 0.0, 1.0);
    gl_Position = uModelViewProjection * vec4(aPosition, 1.0);
}
`;

const FRAGMENT_SHADER = `#version 300 es
precision highp float;

in vec3 vNormal;
in vec3 vPosition;
in vec3 vModelPosition;
in float vVis0;
in float vVis1;
in float vVis2;
in float vAmbientVis;
in float vSky;

uniform vec3 uColor;
uniform vec3 uLightDir;

layout(location = 0) out vec4 fragColor;
layout(location = 1) out vec4 fragPosition;

void main() {
    vec3 V = normalize(-vPosition);
    vec3 N = normalize(vNormal);
    if (dot(N, V) < 0.0) N = -N;

    float diff0 = max(dot(N, normalize(uLightDir)), 0.0);
    float diff1 = max(dot(N, normalize(vec3(-0.7, 0.5, 0.4))), 0.0);
    float diff2 = max(dot(N, normalize(vec3(0.3, -0.2, -0.8))), 0.0);

    float ambientLevel = mix(0.15, 0.35, vSky) * 0.9 + 0.1;

    vec3 color = uColor * ambientLevel * vAmbientVis
               + uColor * 0.65 * diff0 * vVis0
               + uColor * 0.20 * diff1 * vVis1
               + uColor * 0.40 * diff2 * vVis2;

    fragColor = vec4(color, 1.0);
    fragPosition = vec4(vModelPosition, 1.0);
}
`;

function initWebGL(state) {
    const gl = state.canvas.getContext('webgl2', { antialias: true, alpha: true, premultipliedAlpha: true });
    if (!gl) { console.error('WebGL2 not supported'); return; }
    state.gl = gl;

    gl.enable(gl.DEPTH_TEST);
    gl.disable(gl.CULL_FACE);
    gl.clearColor(0, 0, 0, 0); // transparent — CSS background shows through

    state._hasFloatColorBuffer = !!gl.getExtension('EXT_color_buffer_float');

    const vs = compileShader(gl, gl.VERTEX_SHADER, VERTEX_SHADER);
    const fs = compileShader(gl, gl.FRAGMENT_SHADER, FRAGMENT_SHADER);
    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        console.error('Shader link error:', gl.getProgramInfoLog(program));
        return;
    }
    state.program = program;

    state.uniforms = {
        modelViewProjection: gl.getUniformLocation(program, 'uModelViewProjection'),
        modelView: gl.getUniformLocation(program, 'uModelView'),
        normalMatrix: gl.getUniformLocation(program, 'uNormalMatrix'),
        color: gl.getUniformLocation(program, 'uColor'),
        lightDir: gl.getUniformLocation(program, 'uLightDir'),
        modelExtent: gl.getUniformLocation(program, 'uModelExtent')
    };

    state.vao = gl.createVertexArray();
    gl.bindVertexArray(state.vao);

    state.posBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, state.posBuffer);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);

    state.normBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, state.normBuffer);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 0, 0);

    state.shBuffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, state.shBuffer);
    gl.enableVertexAttribArray(2);
    gl.vertexAttribPointer(2, 4, gl.FLOAT, false, 36, 0);   // SH_A at offset 0
    gl.enableVertexAttribArray(3);
    gl.vertexAttribPointer(3, 4, gl.FLOAT, false, 36, 16);  // SH_B at offset 16
    gl.enableVertexAttribArray(4);
    gl.vertexAttribPointer(4, 1, gl.FLOAT, false, 36, 32);  // SH_C at offset 32

    gl.bindVertexArray(null);
    resizeCanvas(state);
    setupPickFBO(state);

    initBakeProgram(state);
}

// compileShader, buildOccupancyTexture, BAKE shaders, initBakeResources,
// bakeSHCoefficients — all imported from webgl-isosurface-shared.js

function initBakeProgram(state) {
    const gl = state.gl;
    if (!gl) return;
    const res = initBakeResources(gl, state.posBuffer, state.normBuffer);
    if (!res) return;
    state.bakeProgram = res.program;
    state.bakeUniforms = res.uniforms;
    state.bakeVAO = res.vao;
    state.transformFeedback = res.transformFeedback;
}

function bakeSH(state) {
    const gl = state.gl;
    if (!gl || !state.bakeProgram || !state.mesh || state.mesh.vertexCount === 0 || !state.volumeData) return;
    state.occTexture = bakeSHCoefficients(
        gl, { program: state.bakeProgram, vao: state.bakeVAO, transformFeedback: state.transformFeedback, uniforms: state.bakeUniforms },
        state.shBuffer, state.volumeData, state.dims, state.pixelSize, state.threshold,
        state.mesh.vertexCount, state.occTexture
    );
}

// ── Rendering ───────────────────────────────────────────────────────────────

function requestRender(state) {
    if (state._renderQueued) return;
    state._renderQueued = true;
    requestAnimationFrame(() => {
        state._renderQueued = false;
        renderScene(state);
    });
}

function renderScene(state) {
    const gl = state.gl;
    if (!gl || !state.program) return;

    const w = state.canvas.width;
    const h = state.canvas.height;
    if (w === 0 || h === 0) return;

    gl.viewport(0, 0, w, h);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

    updateEulerDisplay(state);

    if (!state.mesh || state.mesh.vertexCount === 0) return;

    gl.useProgram(state.program);

    const aspect = w / h;
    const fov = Math.PI / 6;
    const near = state.camera.distance * 0.01;
    const far = state.camera.distance * 10;
    const projection = mat4_perspective(fov, aspect, near, far);

    const viewMatrix = computeViewMatrix(state.camera);
    const mvp = mat4_multiply(projection, viewMatrix);
    const normalMat = mat3_normalFromMat4(viewMatrix);

    gl.uniformMatrix4fv(state.uniforms.modelViewProjection, false, mvp);
    gl.uniformMatrix4fv(state.uniforms.modelView, false, viewMatrix);
    gl.uniformMatrix3fv(state.uniforms.normalMatrix, false, normalMat);
    gl.uniform3fv(state.uniforms.color, state.surfaceColor);
    gl.uniform3fv(state.uniforms.lightDir, [0, 0, 1]);
    const maxExtent = Math.max(
        state.dims.x * state.pixelSize.x,
        state.dims.y * state.pixelSize.y,
        state.dims.z * state.pixelSize.z
    );
    gl.uniform1f(state.uniforms.modelExtent, maxExtent * 0.5);

    gl.bindVertexArray(state.vao);
    gl.drawArrays(gl.TRIANGLES, 0, state.mesh.vertexCount);
    gl.bindVertexArray(null);

    // Pick pass — only when not dragging (tooltip is hidden during drag)
    if (state.pickFBO && !state._isDragging) {
        gl.bindFramebuffer(gl.FRAMEBUFFER, state.pickFBO);
        gl.viewport(0, 0, w, h);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        gl.bindVertexArray(state.vao);
        gl.drawArrays(gl.TRIANGLES, 0, state.mesh.vertexCount);
        gl.bindVertexArray(null);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }
}

// ── Camera ──────────────────────────────────────────────────────────────────

function computeViewMatrix(cam) {
    const rot = quat_toMatrix3(cam.orientation);
    // Columns of the 3x3: right, up, back (camera axes in world space)
    const r0 = rot[0], r1 = rot[1], r2 = rot[2];
    const u0 = rot[3], u1 = rot[4], u2 = rot[5];
    const b0 = rot[6], b1 = rot[7], b2 = rot[8];

    // Eye = pan offset (in screen-aligned plane) + back * distance
    const ex = r0 * cam.panX + u0 * cam.panY + b0 * cam.distance;
    const ey = r1 * cam.panX + u1 * cam.panY + b1 * cam.distance;
    const ez = r2 * cam.panX + u2 * cam.panY + b2 * cam.distance;

    // View matrix = [R^T | -R^T * eye] in column-major
    return new Float32Array([
        r0, u0, b0, 0,
        r1, u1, b1, 0,
        r2, u2, b2, 0,
        -(r0 * ex + r1 * ey + r2 * ez),
        -(u0 * ex + u1 * ey + u2 * ez),
        -(b0 * ex + b1 * ey + b2 * ez),
        1
    ]);
}

function resetCamera(state) {
    const maxExtent = Math.max(
        state.dims.x * state.pixelSize.x,
        state.dims.y * state.pixelSize.y,
        state.dims.z * state.pixelSize.z
    );
    state.camera.distance = maxExtent > 0 ? maxExtent * 1.5 : 300;
    state.camera.orientation = [..._defaultOrientation];
    state.camera.panX = 0;
    state.camera.panY = 0;
}

function updateEulerDisplay(state) {
    if (!state.toolbar.eulerDisplay) return;
    const rot3 = quat_toMatrix3(state.camera.orientation);
    const [alpha, beta, gamma] = eulerFromMatrix3(rot3);
    const toDeg = 180 / Math.PI;
    state.toolbar.eulerDisplay.textContent =
        `${(alpha * toDeg).toFixed(1)}, ${(beta * toDeg).toFixed(1)}, ${(gamma * toDeg).toFixed(1)}`;
}

// ── Arcball (Shoemake) ──────────────────────────────────────────────────────

function mapToSphere(cx, cy, width, height) {
    // Map pixel position to normalized coords using min(w,h) as sphere diameter
    const dim = Math.min(width, height);
    const x = (2 * cx - width) / dim;
    const y = (height - 2 * cy) / dim;

    const r2 = x * x + y * y;
    let z;
    if (r2 <= 0.5) {
        // On the sphere
        z = Math.sqrt(1.0 - r2);
    } else {
        // Hyperbolic sheet — C1 continuous at r² = 0.5
        z = 0.5 / Math.sqrt(r2);
    }

    // Normalize to unit sphere (sphere branch is already unit length,
    // hyperbolic branch is not)
    const len = Math.sqrt(r2 + z * z);
    return [x / len, y / len, z / len];
}

// ── Mouse interaction ───────────────────────────────────────────────────────

function setupMouseHandlers(state) {
    const canvas = state.canvas;

    state._boundMouseDown = (e) => {
        state._isDragging = true;
        state._dragButton = e.button;
        state._dragShift = e.shiftKey;
        state._lastMouse = { x: e.clientX, y: e.clientY };

        // Capture arcball start for left-button rotation
        if (e.button === 0 && !e.shiftKey) {
            const rect = state.canvas.getBoundingClientRect();
            state._arcballStart = mapToSphere(
                e.clientX - rect.left, e.clientY - rect.top,
                rect.width, rect.height
            );
            state._arcballOrientation = [...state.camera.orientation];
        }

        if (state._tooltip) state._tooltip.style.display = 'none';
        e.preventDefault();
        e.stopPropagation();
    };

    state._boundMouseMove = (e) => {
        if (!state._isDragging) return;

        const dx = e.clientX - state._lastMouse.x;
        const dy = e.clientY - state._lastMouse.y;
        state._lastMouse = { x: e.clientX, y: e.clientY };

        const isPan = state._dragButton === 2 || (state._dragButton === 0 && state._dragShift);

        if (isPan) {
            const fov = Math.PI / 6;
            const scale = state.camera.distance * Math.tan(fov / 2) * 2 / state.canvas.clientHeight;
            state.camera.panX -= dx * scale;
            state.camera.panY += dy * scale;
        } else if (state._dragButton === 0) {
            // Shoemake arcball — no precession from circular mouse motion
            const rect = state.canvas.getBoundingClientRect();
            const p1 = mapToSphere(
                e.clientX - rect.left, e.clientY - rect.top,
                rect.width, rect.height
            );
            const p0 = state._arcballStart;

            // Inverse arcball rotation (p1 → p0) so the object follows the cursor
            const drag = quat_normalize([
                p1[1] * p0[2] - p1[2] * p0[1],
                p1[2] * p0[0] - p1[0] * p0[2],
                p1[0] * p0[1] - p1[1] * p0[0],
                p0[0] * p1[0] + p0[1] * p1[1] + p0[2] * p1[2]
            ]);

            state.camera.orientation = quat_normalize(
                quat_multiply(state._arcballOrientation, drag)
            );
        }

        if (state.cameraSynced) broadcastCamera(state.elementId, state.camera);
        requestRender(state);
    };

    state._boundMouseUp = () => {
        if (state._isDragging) scheduleSave(state);
        state._isDragging = false;
        state._dragButton = -1;
    };

    // Canvas-level mouseup: stop propagation when finishing a drag so parent
    // components don't react to it. Fires before the document-level handler
    // in the bubbling phase; stopPropagation prevents the document handler
    // from running, so we duplicate the cleanup here.
    state._boundCanvasMouseUp = (e) => {
        if (state._isDragging) {
            e.stopPropagation();
            state._wasDragging = true;
            scheduleSave(state);
        }
        state._isDragging = false;
        state._dragButton = -1;
    };

    // Suppress the click event that fires after a drag release
    state._boundCanvasClick = (e) => {
        if (state._wasDragging) {
            e.stopPropagation();
            state._wasDragging = false;
        }
    };

    state._boundWheel = (e) => {
        e.preventDefault();
        e.stopPropagation();
        const factor = Math.pow(1.1, -e.deltaY / 100);
        state.camera.distance = Math.max(1, state.camera.distance * factor);
        if (state.cameraSynced) broadcastCamera(state.elementId, state.camera);
        scheduleSave(state);
        requestRender(state);
    };

    state._boundContextMenu = (e) => { e.preventDefault(); e.stopPropagation(); };

    // Tooltip: read position under cursor when not dragging
    state._boundTooltipMove = (e) => {
        const tooltip = state._tooltip;
        if (!tooltip) return;

        if (state._isDragging || !state.pickFBO || !state.mesh) {
            tooltip.style.display = 'none';
            return;
        }

        if (state._tooltipRaf) return; // already scheduled

        const clientX = e.clientX;
        const clientY = e.clientY;

        state._tooltipRaf = requestAnimationFrame(() => {
            state._tooltipRaf = null;
            if (!state.gl || !state.pickFBO) return;

            const rect = state.canvas.getBoundingClientRect();
            const cssX = clientX - rect.left;
            const cssY = clientY - rect.top;
            const dpr = window.devicePixelRatio || 1;
            const px = Math.round(cssX * dpr);
            const py = state.canvas.height - Math.round(cssY * dpr) - 1;

            if (px < 0 || py < 0 || px >= state.canvas.width || py >= state.canvas.height) {
                tooltip.style.display = 'none';
                return;
            }

            const gl = state.gl;
            const buf = new Float32Array(4);
            gl.bindFramebuffer(gl.FRAMEBUFFER, state.pickFBO);
            gl.readBuffer(gl.COLOR_ATTACHMENT1);
            gl.readPixels(px, py, 1, 1, gl.RGBA, gl.FLOAT, buf);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);

            if (buf[3] < 0.5) { // background — no geometry
                tooltip.style.display = 'none';
                return;
            }

            // Model-space position (angstroms, centered on volume)
            const posA = [buf[0], buf[1], buf[2]];

            // Voxel coordinates
            const vx = buf[0] / state.pixelSize.x + state.dims.x * 0.5;
            const vy = buf[1] / state.pixelSize.y + state.dims.y * 0.5;
            const vz = buf[2] / state.pixelSize.z + state.dims.z * 0.5;

            tooltip.innerHTML =
                `<span style="color:var(--neutral-foreground-hint)">\u00C5</span> ${posA[0].toFixed(1)}, ${posA[1].toFixed(1)}, ${posA[2].toFixed(1)}<br>` +
                `<span style="color:var(--neutral-foreground-hint)">voxel</span> ${Math.round(vx)}, ${Math.round(vy)}, ${Math.round(vz)}`;
            tooltip.style.display = 'block';

            // Position tooltip, flip if near right/bottom edge
            const containerRect = state.canvas.parentElement.getBoundingClientRect();
            const cw = containerRect.width;
            const ch = containerRect.height;
            let tipX = cssX + 16;
            let tipY = cssY + 16;
            if (tipX + 150 > cw) tipX = cssX - 160;
            if (tipY + 40 > ch) tipY = cssY - 44;
            tooltip.style.left = tipX + 'px';
            tooltip.style.top = tipY + 'px';
        });
    };

    state._boundTooltipLeave = () => {
        if (state._tooltip) state._tooltip.style.display = 'none';
        if (state._tooltipRaf) {
            cancelAnimationFrame(state._tooltipRaf);
            state._tooltipRaf = null;
        }
    };

    canvas.addEventListener('mousedown', state._boundMouseDown);
    document.addEventListener('mousemove', state._boundMouseMove);
    document.addEventListener('mouseup', state._boundMouseUp);
    canvas.addEventListener('wheel', state._boundWheel, { passive: false });
    canvas.addEventListener('contextmenu', state._boundContextMenu);
    canvas.addEventListener('mouseup', state._boundCanvasMouseUp);
    canvas.addEventListener('click', state._boundCanvasClick);
    canvas.addEventListener('mousemove', state._boundTooltipMove);
    canvas.addEventListener('mouseleave', state._boundTooltipLeave);
}

// ── Resize ──────────────────────────────────────────────────────────────────

function setupResizeObserver(state) {
    let resizeTimeout = null;
    state.resizeObserver = new ResizeObserver(() => {
        clearTimeout(resizeTimeout);
        resizeTimeout = setTimeout(() => {
            resizeCanvas(state);
            requestRender(state);
        }, 50);
    });
    state.resizeObserver.observe(state.canvas.parentElement);
}

function resizeCanvas(state) {
    const canvas = state.canvas;
    const parent = canvas.parentElement;
    if (!parent) return;
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.round(parent.clientWidth * dpr);
    canvas.height = Math.round(parent.clientHeight * dpr);
    setupPickFBO(state);
}

function setupPickFBO(state) {
    const gl = state.gl;
    if (!gl || !state._hasFloatColorBuffer) return;

    const w = state.canvas.width;
    const h = state.canvas.height;
    if (w === 0 || h === 0) return;
    if (state._pickSize.w === w && state._pickSize.h === h) return;

    // Clean up old resources
    if (state.pickFBO) {
        gl.deleteFramebuffer(state.pickFBO);
        gl.deleteRenderbuffer(state.pickColorRB);
        gl.deleteTexture(state.pickPosTex);
        gl.deleteRenderbuffer(state.pickDepthRB);
    }

    // Dummy color renderbuffer at COLOR_ATTACHMENT0 (receives fragColor, never read)
    state.pickColorRB = gl.createRenderbuffer();
    gl.bindRenderbuffer(gl.RENDERBUFFER, state.pickColorRB);
    gl.renderbufferStorage(gl.RENDERBUFFER, gl.RGBA8, w, h);

    // Position texture at COLOR_ATTACHMENT1 (RGBA32F — model-space position + alpha flag)
    state.pickPosTex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, state.pickPosTex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA32F, w, h, 0, gl.RGBA, gl.FLOAT, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);

    // Depth renderbuffer
    state.pickDepthRB = gl.createRenderbuffer();
    gl.bindRenderbuffer(gl.RENDERBUFFER, state.pickDepthRB);
    gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, w, h);

    // Framebuffer — standard MRT: location 0 → attachment 0, location 1 → attachment 1
    state.pickFBO = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, state.pickFBO);
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.RENDERBUFFER, state.pickColorRB);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT1, gl.TEXTURE_2D, state.pickPosTex, 0);
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, state.pickDepthRB);
    gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1]);

    const status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
    if (status !== gl.FRAMEBUFFER_COMPLETE) {
        console.warn('Pick FBO incomplete:', status);
        gl.deleteFramebuffer(state.pickFBO);
        state.pickFBO = null;
    }

    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    state._pickSize = { w, h };
}

// ── Fullscreen ───────────────────────────────────────────────────────────────

function toggleFullscreen(state) {
    if (document.fullscreenElement === state.container) {
        document.exitFullscreen();
    } else {
        state._preFullscreenWidth = state.container.offsetWidth;
        state._preFullscreenHeight = state.container.offsetHeight;
        state.container.requestFullscreen();
    }
}

function rebuildToolbar(state, miniOverride) {
    const oldToolbar = state.container.firstChild;
    if (oldToolbar) state.container.removeChild(oldToolbar);

    const newToolbar = createToolbar(state, miniOverride);
    state.container.insertBefore(newToolbar, state.container.firstChild);

    updateThresholdUI(state);
}

function setupFullscreenListener(state) {
    state._onFullscreenChange = () => {
        const isFs = document.fullscreenElement === state.container;

        if (isFs) {
            // Entering fullscreen: always show full UI
            if (state.options.miniMode) {
                rebuildToolbar(state, false);
            }
        } else {
            // Exiting fullscreen: restore original UI mode
            if (state.options.miniMode) {
                rebuildToolbar(state, true);
            }
            // Restore pre-fullscreen dimensions
            if (state._preFullscreenWidth != null) {
                state.container.style.width = state._preFullscreenWidth + 'px';
                state.container.style.height = state._preFullscreenHeight + 'px';
                state._preFullscreenWidth = null;
                state._preFullscreenHeight = null;
            }
        }

        // Update fullscreen button style (after potential toolbar rebuild)
        const btn = state.toolbar.fullscreenBtn;
        if (btn) {
            btn.style.background = isFs ? 'var(--accent-fill-rest)' : 'var(--neutral-layer-1)';
            btn.style.color = isFs ? 'var(--foreground-on-accent-rest)' : 'var(--neutral-foreground-rest)';
            btn.style.borderColor = isFs ? 'var(--accent-fill-rest)' : 'var(--neutral-stroke-rest)';
        }
    };
    document.addEventListener('fullscreenchange', state._onFullscreenChange);
}

// ── Volume loading ──────────────────────────────────────────────────────────

async function loadVolume(state) {
    if (state.abortController) state.abortController.abort();
    state.abortController = new AbortController();
    const signal = state.abortController.signal;

    try {
        showLoading(state, 0, 'Loading volume...');

        const { header, data } = await fetchMRC(state.fileUrl, {
            signal,
            onProgress: (received, total) => {
                if (total > 0) showLoading(state, received / total, 'Downloading volume...');
            }
        });
        if (signal.aborted) return;

        state.header = header;
        state.dims = { x: header.dimensions.x, y: header.dimensions.y, z: header.dimensions.z };
        state.pixelSize = { x: header.pixelSize.x, y: header.pixelSize.y, z: header.pixelSize.z };

        showLoading(state, 0.8, 'Computing statistics...');
        const stats = computeStats(data);
        state.stats = stats;

        // Restore settings: stored > current (re-load) > defaults
        const isFirstLoad = !state._hasLoadedVolume;
        state._hasLoadedVolume = true;
        const saved = loadSettings(state.storageKey);
        if (saved) {
            state.threshold = saved.threshold;
            if (saved.camera) {
                // Apply to both instance and global so the sync
                // callback doesn't overwrite with stale defaults
                state.camera.orientation = [...saved.camera.orientation];
                state.camera.distance = saved.camera.distance;
                state.camera.panX = saved.camera.panX;
                state.camera.panY = saved.camera.panY;
                globalCamera.orientation = [...saved.camera.orientation];
                globalCamera.distance = saved.camera.distance;
                globalCamera.panX = saved.camera.panX;
                globalCamera.panY = saved.camera.panY;
            }
            if (saved.cameraSynced !== undefined)
                state.toolbar.camLockBtn.setActive(saved.cameraSynced);
            if (saved.isoSynced !== undefined)
                state.toolbar.isoLockBtn.setActive(saved.isoSynced);
        } else if (isFirstLoad) {
            // First load with no stored settings — use defaults
            state.threshold = 0.3 * stats.max;
            resetCamera(state);
        }
        // else: re-load in same viewer — keep current camera & threshold

        updateThresholdUI(state);
        if (state.cameraSynced) broadcastCamera(state.elementId, state.camera);

        showLoading(state, 0.85, 'Preparing worker...');
        if (state.worker) state.worker.terminate();
        state.worker = new Worker('_content/Refund/js/marching-cubes-worker.js');

        state.worker.onmessage = (e) => {
            const msg = e.data;
            if (msg.type === 'progress') {
                showLoading(state, 0.85 + 0.12 * (msg.percent / 100), 'Generating surface...');
            } else if (msg.type === 'result') {
                const lod = msg.lods[0];
                uploadMesh(state, lod.positions, lod.normals);
                showLoading(state, 0.97, 'Baking directional occlusion...');
                // Allow browser to paint loading text, then bake synchronously on GPU
                setTimeout(() => {
                    bakeSH(state);
                    hideLoading(state);
                    requestRender(state);
                }, 0);
            }
        };

        state.volumeData = data; // Keep on main thread for GPU SH bake
        state.worker.postMessage(
            { type: 'setVolume', volume: data, dims: state.dims, pixelSize: state.pixelSize }
        );

        showLoading(state, 0.85, 'Generating surface...');
        state.worker.postMessage({ type: 'generate', threshold: state.threshold });

    } catch (err) {
        if (err.name === 'AbortError') return;
        console.error('Failed to load volume:', err);
        showLoading(state, 0, 'Error: ' + err.message);
    }
}

function computeStats(data) {
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
    return { min, max, mean, std: Math.sqrt(sqSum / n) };
}

function uploadMesh(state, positions, normals) {
    const gl = state.gl;
    if (!gl) return;
    gl.bindBuffer(gl.ARRAY_BUFFER, state.posBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, positions, gl.STATIC_DRAW);
    gl.bindBuffer(gl.ARRAY_BUFFER, state.normBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, normals, gl.STATIC_DRAW);
    // Default SH: DC = sqrt(4*PI) makes evaluateSH() return 1.0 for any direction (fully lit)
    const vertexCount = positions.length / 3;
    const dc = Math.sqrt(4.0 * Math.PI);
    const defaultSH = new Float32Array(vertexCount * 9);
    for (let i = 0; i < vertexCount; i++) defaultSH[i * 9] = dc;
    gl.bindBuffer(gl.ARRAY_BUFFER, state.shBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, defaultSH, gl.STATIC_DRAW);
    state.mesh = { vertexCount };
}

function regenerateMesh(state) {
    if (!state.worker) return;
    showLoading(state, 0.85, 'Generating surface...');
    state.worker.postMessage({ type: 'generate', threshold: state.threshold });
}

function updateThresholdUI(state) {
    if (!state.toolbar.slider) return;
    const range = state.stats.max - state.stats.min;
    if (range > 0) {
        state.toolbar.slider.value = ((state.threshold - state.stats.min) / range) * 100;
    }
    if (state.toolbar.threshInput) {
        state.toolbar.threshInput.value = formatThresholdValue(state.threshold);
    }
}

function formatThresholdValue(val) {
    if (val === 0) return '0';
    const abs = Math.abs(val);
    if (abs >= 100) return val.toFixed(1);
    if (abs >= 1) return val.toFixed(3);
    if (abs >= 0.001) return val.toFixed(4);
    // For very small values, find enough decimal places to show 3 significant figures
    const digits = Math.max(1, -Math.floor(Math.log10(abs)) + 2);
    return val.toFixed(digits);
}

// ── Quaternion math ─────────────────────────────────────────────────────────

function quat_fromAxisAngle(axis, angle) {
    const ha = angle * 0.5;
    const s = Math.sin(ha);
    return [axis[0] * s, axis[1] * s, axis[2] * s, Math.cos(ha)];
}

function quat_multiply(a, b) {
    return [
        a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
        a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
        a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
        a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]
    ];
}

function quat_normalize(q) {
    const len = Math.sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
    if (len < 1e-10) return [0, 0, 0, 1];
    const inv = 1 / len;
    return [q[0] * inv, q[1] * inv, q[2] * inv, q[3] * inv];
}

function quat_rotateVector(q, v) {
    // Efficient q*v*q^-1 via: result = v + 2*w*(qxyz x v) + 2*(qxyz x (qxyz x v))
    const qx = q[0], qy = q[1], qz = q[2], qw = q[3];
    const tx = 2 * (qy * v[2] - qz * v[1]);
    const ty = 2 * (qz * v[0] - qx * v[2]);
    const tz = 2 * (qx * v[1] - qy * v[0]);
    return [
        v[0] + qw * tx + qy * tz - qz * ty,
        v[1] + qw * ty + qz * tx - qx * tz,
        v[2] + qw * tz + qx * ty - qy * tx
    ];
}

function quat_toMatrix3(q) {
    // Returns column-major 3x3 rotation matrix
    const [x, y, z, w] = q;
    const xx = x * x, yy = y * y, zz = z * z;
    const xy = x * y, xz = x * z, yz = y * z;
    const wx = w * x, wy = w * y, wz = w * z;
    return [
        1 - 2 * (yy + zz), 2 * (xy + wz), 2 * (xz - wy),       // column 0: right
        2 * (xy - wz), 1 - 2 * (xx + zz), 2 * (yz + wx),         // column 1: up
        2 * (xz + wy), 2 * (yz - wx), 1 - 2 * (xx + yy)          // column 2: back
    ];
}

// ── Euler extraction (cryo-EM ZYZ convention, from WarpLib Matrix3) ─────────

function eulerFromMatrix3(m) {
    // Column-major m[]: m[0]=M11, m[1]=M21, m[2]=M31, m[3]=M12, m[4]=M22, m[5]=M32,
    //                    m[6]=M13, m[7]=M23, m[8]=M33
    // Returns [rot, tilt, psi] in radians (ZYZ Euler angles)
    const M13 = m[6], M23 = m[7], M33 = m[8];
    const M31 = m[2], M32 = m[5];
    const M11 = m[0], M21 = m[1];

    let alpha, beta, gamma;
    const abs_sb = Math.sqrt(M13 * M13 + M23 * M23);

    if (abs_sb > 16 * 1.192092896e-07) {
        gamma = Math.atan2(M23, -M13);
        alpha = Math.atan2(M32, M31);
        let sign_sb;
        if (Math.abs(Math.sin(gamma)) < 1.192092896e-07)
            sign_sb = Math.sign(-M13 / Math.cos(gamma));
        else
            sign_sb = Math.sin(gamma) > 0 ? Math.sign(M23) : -Math.sign(M23);
        beta = Math.atan2(sign_sb * abs_sb, M33);
    } else {
        if (Math.sign(M33) > 0) {
            alpha = 0;
            beta = 0;
            gamma = Math.atan2(-M21, M11);
        } else {
            alpha = 0;
            beta = Math.PI;
            gamma = Math.atan2(M21, -M11);
        }
    }

    return [alpha, beta, gamma];
}

// ── Matrix math ─────────────────────────────────────────────────────────────

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

function mat3_normalFromMat4(m) {
    return new Float32Array([
        m[0], m[1], m[2],
        m[4], m[5], m[6],
        m[8], m[9], m[10]
    ]);
}
