/**
 * Shared WebGL2 isosurface utilities.
 *
 * Extracted from IsosurfaceViewer so that TomogramViewerJs can reuse the same
 * marching-cubes mesh pipeline, SH baking, and occupancy texture code without
 * duplicating hundreds of lines of GPU setup.
 */

// ── Shader compilation ──────────────────────────────────────────────────────

export function compileShader(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        console.error('Shader compile error:', gl.getShaderInfoLog(shader));
        gl.deleteShader(shader);
        return null;
    }
    return shader;
}

// ── Occupancy texture (3D R8 with mip chain) ────────────────────────────────

export function buildOccupancyTexture(gl, volumeData, dims, threshold) {
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_3D, tex);

    const maxDim = Math.max(dims.x, dims.y, dims.z);
    const numLevels = Math.min(Math.floor(Math.log2(maxDim)) + 1, 5);

    gl.texStorage3D(gl.TEXTURE_3D, numLevels, gl.R8, dims.x, dims.y, dims.z);

    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);

    // Level 0: binary occupancy
    const n = dims.x * dims.y * dims.z;
    const level0 = new Uint8Array(n);
    for (let i = 0; i < n; i++) {
        level0[i] = volumeData[i] >= threshold ? 255 : 0;
    }
    gl.texSubImage3D(gl.TEXTURE_3D, 0, 0, 0, 0, dims.x, dims.y, dims.z,
                     gl.RED, gl.UNSIGNED_BYTE, level0);

    // Build mip levels by averaging 2x2x2 blocks
    let prevData = level0;
    let prevDims = { x: dims.x, y: dims.y, z: dims.z };

    for (let level = 1; level < numLevels; level++) {
        const w = Math.max(1, prevDims.x >> 1);
        const h = Math.max(1, prevDims.y >> 1);
        const d = Math.max(1, prevDims.z >> 1);
        const levelData = new Uint8Array(w * h * d);

        for (let iz = 0; iz < d; iz++) {
            for (let iy = 0; iy < h; iy++) {
                for (let ix = 0; ix < w; ix++) {
                    let sum = 0, count = 0;
                    for (let dz = 0; dz < 2; dz++) {
                        const pz = iz * 2 + dz;
                        if (pz >= prevDims.z) continue;
                        for (let dy = 0; dy < 2; dy++) {
                            const py = iy * 2 + dy;
                            if (py >= prevDims.y) continue;
                            for (let dx = 0; dx < 2; dx++) {
                                const px = ix * 2 + dx;
                                if (px >= prevDims.x) continue;
                                sum += prevData[pz * prevDims.y * prevDims.x + py * prevDims.x + px];
                                count++;
                            }
                        }
                    }
                    levelData[iz * h * w + iy * w + ix] = count > 0 ? Math.round(sum / count) : 0;
                }
            }
        }

        gl.texSubImage3D(gl.TEXTURE_3D, level, 0, 0, 0, w, h, d,
                         gl.RED, gl.UNSIGNED_BYTE, levelData);
        prevData = levelData;
        prevDims = { x: w, y: h, z: d };
    }

    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);

    gl.texParameteri(gl.TEXTURE_3D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
    gl.texParameteri(gl.TEXTURE_3D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_3D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_3D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_3D, gl.TEXTURE_WRAP_R, gl.CLAMP_TO_EDGE);

    gl.bindTexture(gl.TEXTURE_3D, null);
    return tex;
}

// ── SH bake shader sources ──────────────────────────────────────────────────

export const BAKE_VERTEX_SHADER = `#version 300 es
precision highp float;
precision highp sampler3D;

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;

uniform vec3 uInvPixelSize;
uniform vec3 uHalfDims;
uniform vec3 uInvDims;
uniform float uMaxDist;
uniform sampler3D uOccupancy;

out vec4 vSH_A;
out vec4 vSH_B;
out float vSH_C;

const float PI = 3.14159265359;
const float GOLDEN_RATIO = 1.6180339887;
const int NUM_RAYS = 64;

vec3 hemisphereDir(int i) {
    float z = (float(i) + 0.5) / float(NUM_RAYS);
    float r = sqrt(1.0 - z * z);
    float theta = 2.0 * PI * float(i) / GOLDEN_RATIO;
    return vec3(r * cos(theta), r * sin(theta), z);
}

void shBasis(vec3 d, out float basis[9]) {
    basis[0] = 0.282095;
    basis[1] = 0.488603 * d.y;
    basis[2] = 0.488603 * d.z;
    basis[3] = 0.488603 * d.x;
    basis[4] = 1.092548 * d.x * d.y;
    basis[5] = 1.092548 * d.y * d.z;
    basis[6] = 0.315392 * (3.0 * d.z * d.z - 1.0);
    basis[7] = 1.092548 * d.x * d.z;
    basis[8] = 0.546274 * (d.x * d.x - d.y * d.y);
}

void main() {
    vec3 voxel = aPosition * uInvPixelSize + uHalfDims;
    vec3 N = normalize(aNormal);

    vec3 ref = abs(N.x) < 0.9 ? vec3(1, 0, 0) : vec3(0, 1, 0);
    vec3 T = normalize(cross(N, ref));
    vec3 B = cross(N, T);

    vec3 start = voxel + N * 0.5;

    float sh[9];
    for (int j = 0; j < 9; j++) sh[j] = 0.0;

    for (int ray = 0; ray < NUM_RAYS; ray++) {
        vec3 hdir = hemisphereDir(ray);
        vec3 dir = hdir.x * T + hdir.y * B + hdir.z * N;
        float visibility = 1.0;

        for (float d = 1.0; d <= 8.0; d += 1.0) {
            vec3 p = (start + dir * d + vec3(0.5)) * uInvDims;
            if (any(lessThan(p, vec3(0))) || any(greaterThanEqual(p, vec3(1)))) break;
            visibility *= 1.0 - textureLod(uOccupancy, p, 0.0).r;
            if (visibility < 0.01) break;
        }

        if (visibility >= 0.01) {
            for (float d = 10.0; d <= 32.0; d += 2.0) {
                vec3 p = (start + dir * d + vec3(0.5)) * uInvDims;
                if (any(lessThan(p, vec3(0))) || any(greaterThanEqual(p, vec3(1)))) break;
                visibility *= 1.0 - textureLod(uOccupancy, p, 1.0).r;
                if (visibility < 0.01) break;
            }
        }

        if (visibility >= 0.01) {
            for (float d = 36.0; d <= 96.0; d += 4.0) {
                vec3 p = (start + dir * d + vec3(0.5)) * uInvDims;
                if (any(lessThan(p, vec3(0))) || any(greaterThanEqual(p, vec3(1)))) break;
                visibility *= 1.0 - textureLod(uOccupancy, p, 2.0).r;
                if (visibility < 0.01) break;
            }
        }

        if (visibility >= 0.01) {
            for (float d = 104.0; d <= uMaxDist; d += 8.0) {
                vec3 p = (start + dir * d + vec3(0.5)) * uInvDims;
                if (any(lessThan(p, vec3(0))) || any(greaterThanEqual(p, vec3(1)))) break;
                visibility *= 1.0 - textureLod(uOccupancy, p, 3.0).r;
                if (visibility < 0.01) break;
            }
        }

        float basis[9];
        shBasis(dir, basis);
        for (int j = 0; j < 9; j++) {
            sh[j] += visibility * basis[j];
        }
    }

    float norm = 2.0 * PI / float(NUM_RAYS);
    for (int j = 0; j < 9; j++) sh[j] *= norm;

    vSH_A = vec4(sh[0], sh[1], sh[2], sh[3]);
    vSH_B = vec4(sh[4], sh[5], sh[6], sh[7]);
    vSH_C = sh[8];
    gl_Position = vec4(0.0);
}
`;

export const BAKE_FRAGMENT_SHADER = `#version 300 es
precision highp float;
out vec4 fragColor;
void main() { fragColor = vec4(0.0); }
`;

// ── GLSL snippet for evaluating 9-coefficient SH ────────────────────────────

export const SH_EVALUATE_GLSL = `
float evaluateSH(vec3 d, vec4 shA, vec4 shB, float shC) {
    float result = shA.x * 0.282095;
    result += shA.y * 0.488603 * d.y;
    result += shA.z * 0.488603 * d.z;
    result += shA.w * 0.488603 * d.x;
    result += shB.x * 1.092548 * d.x * d.y;
    result += shB.y * 1.092548 * d.y * d.z;
    result += shB.z * 0.315392 * (3.0 * d.z * d.z - 1.0);
    result += shB.w * 1.092548 * d.x * d.z;
    result += shC   * 0.546274 * (d.x * d.x - d.y * d.y);
    return clamp(result, 0.0, 1.0);
}
`;

// ── Generalized bake resource setup ─────────────────────────────────────────

/**
 * Creates the bake program, VAO, and transform feedback objects needed for
 * GPU-based SH coefficient baking.
 *
 * @param {WebGL2RenderingContext} gl
 * @param {WebGLBuffer} posBuffer  - vertex position buffer (location 0)
 * @param {WebGLBuffer} normBuffer - vertex normal buffer   (location 1)
 * @returns {{ program, vao, transformFeedback, uniforms }} or null on failure
 */
export function initBakeResources(gl, posBuffer, normBuffer) {
    const vs = compileShader(gl, gl.VERTEX_SHADER, BAKE_VERTEX_SHADER);
    const fs = compileShader(gl, gl.FRAGMENT_SHADER, BAKE_FRAGMENT_SHADER);
    if (!vs || !fs) { console.error('Failed to compile bake shaders'); return null; }

    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.transformFeedbackVaryings(program, ['vSH_A', 'vSH_B', 'vSH_C'], gl.INTERLEAVED_ATTRIBS);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        console.error('Bake shader link error:', gl.getProgramInfoLog(program));
        return null;
    }

    const uniforms = {
        invPixelSize: gl.getUniformLocation(program, 'uInvPixelSize'),
        halfDims:     gl.getUniformLocation(program, 'uHalfDims'),
        invDims:      gl.getUniformLocation(program, 'uInvDims'),
        maxDist:      gl.getUniformLocation(program, 'uMaxDist'),
        occupancy:    gl.getUniformLocation(program, 'uOccupancy')
    };

    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);

    gl.bindBuffer(gl.ARRAY_BUFFER, posBuffer);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);

    gl.bindBuffer(gl.ARRAY_BUFFER, normBuffer);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 0, 0);

    gl.bindVertexArray(null);

    const transformFeedback = gl.createTransformFeedback();

    return { program, vao, transformFeedback, uniforms };
}

// ── Generalized SH bake execution ───────────────────────────────────────────

/**
 * Bakes per-vertex SH visibility coefficients using GPU transform feedback.
 *
 * @param {WebGL2RenderingContext} gl
 * @param {{ program, vao, transformFeedback, uniforms }} bakeResources
 * @param {WebGLBuffer} shBuffer       - output buffer for SH data (9 floats/vertex)
 * @param {Float32Array} volumeData    - raw volume data
 * @param {{ x, y, z }} dims           - volume dimensions in voxels
 * @param {{ x, y, z }} pixelSize      - voxel size in Angstroms
 * @param {number} threshold           - isosurface threshold
 * @param {number} vertexCount         - number of mesh vertices
 * @param {WebGLTexture|null} existingOccTexture - if non-null, will be deleted before creating new one (unless reuseOccTexture)
 * @param {boolean} [reuseOccTexture=false] - when true, skip delete+rebuild of occupancy texture; use existingOccTexture directly
 * @returns {WebGLTexture} the occupancy texture (caller should store & delete later)
 */
export function bakeSHCoefficients(gl, bakeResources, shBuffer, volumeData, dims, pixelSize, threshold, vertexCount, existingOccTexture, reuseOccTexture = false) {
    if (!bakeResources || vertexCount === 0 || !volumeData) return existingOccTexture;

    // Build 3D occupancy texture with mip chain (or reuse existing one)
    let occTexture;
    if (reuseOccTexture && existingOccTexture) {
        occTexture = existingOccTexture;
    } else {
        if (existingOccTexture) gl.deleteTexture(existingOccTexture);
        occTexture = buildOccupancyTexture(gl, volumeData, dims, threshold);
    }

    // Allocate SH output buffer (9 floats = 36 bytes per vertex)
    gl.bindBuffer(gl.ARRAY_BUFFER, shBuffer);
    gl.bufferData(gl.ARRAY_BUFFER, vertexCount * 36, gl.STATIC_DRAW);
    gl.bindBuffer(gl.ARRAY_BUFFER, null);

    // Set up bake program
    gl.useProgram(bakeResources.program);
    gl.uniform3f(bakeResources.uniforms.invPixelSize,
                 1 / pixelSize.x, 1 / pixelSize.y, 1 / pixelSize.z);
    gl.uniform3f(bakeResources.uniforms.halfDims,
                 dims.x * 0.5, dims.y * 0.5, dims.z * 0.5);
    gl.uniform3f(bakeResources.uniforms.invDims,
                 1 / dims.x, 1 / dims.y, 1 / dims.z);
    gl.uniform1f(bakeResources.uniforms.maxDist,
                 0.5 * Math.sqrt(dims.x ** 2 + dims.y ** 2 + dims.z ** 2));

    // Bind occupancy texture
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_3D, occTexture);
    gl.uniform1i(bakeResources.uniforms.occupancy, 0);

    // Run transform feedback
    gl.bindVertexArray(bakeResources.vao);
    gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, bakeResources.transformFeedback);
    gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 0, shBuffer);

    gl.enable(gl.RASTERIZER_DISCARD);
    gl.beginTransformFeedback(gl.POINTS);
    gl.drawArrays(gl.POINTS, 0, vertexCount);
    gl.endTransformFeedback();
    gl.disable(gl.RASTERIZER_DISCARD);

    gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, null);
    gl.bindVertexArray(null);
    gl.bindTexture(gl.TEXTURE_3D, null);

    return occTexture;
}
