/**
 * Marching Cubes Web Worker
 *
 * Receives volume data and threshold, generates isosurface mesh
 * with positions and normals in physical Angstrom coordinates.
 *
 * Messages received:
 *   { type: 'setVolume', volume: Float32Array, dims: {x,y,z}, pixelSize: {x,y,z} }
 *   { type: 'generate', threshold: number }
 *
 * Messages sent:
 *   { type: 'result', lods: [{ positions: Float32Array, normals: Float32Array }, ...] }
 *   { type: 'progress', percent: number }
 */

let volumeData = null;
let dims = null;
let pixelSize = null;
let blockMinMax = null;
let blocksX = 0, blocksY = 0, blocksZ = 0;
const BLOCK_SIZE = 4;

self.onmessage = function (e) {
    const msg = e.data;

    if (msg.type === 'setVolume') {
        volumeData = msg.volume;
        dims = msg.dims;
        pixelSize = msg.pixelSize;
        buildBlockMinMax(volumeData, dims);
        return;
    }

    if (msg.type === 'generate') {
        if (!volumeData || !dims) {
            self.postMessage({ type: 'result', lods: [{ positions: new Float32Array(0), normals: new Float32Array(0) }] });
            return;
        }
        const result = marchingCubes(volumeData, dims, pixelSize, msg.threshold);

        // Generate LOD levels; fall back to LOD 0 only if decimation fails (e.g. OOM)
        let lods;
        try {
            lods = generateLODs(result.positions, result.normals, pixelSize);
        } catch (e) {
            console.warn('LOD generation failed, using full-res only:', e.message);
            lods = [{ positions: result.positions, normals: result.normals }];
        }

        // Transfer all LOD buffers
        const transferList = [];
        for (const lod of lods) {
            transferList.push(lod.positions.buffer, lod.normals.buffer);
        }
        self.postMessage({ type: 'result', lods }, transferList);
    }
};

// ── Block min-max for empty region skipping ─────────────────────────────────

function buildBlockMinMax(data, dims) {
    const DX = dims.x, DY = dims.y, DZ = dims.z;
    const cellsX = DX - 1, cellsY = DY - 1, cellsZ = DZ - 1;
    const rowStride = DX;
    const sliceStride = DX * DY;
    const BS = BLOCK_SIZE;

    blocksX = Math.ceil(cellsX / BS);
    blocksY = Math.ceil(cellsY / BS);
    blocksZ = Math.ceil(cellsZ / BS);

    const count = blocksX * blocksY * blocksZ;
    blockMinMax = new Float32Array(count * 2);

    for (let bz = 0; bz < blocksZ; bz++) {
        const vz0 = bz * BS;
        const vz1 = Math.min(vz0 + BS, cellsZ);
        for (let by = 0; by < blocksY; by++) {
            const vy0 = by * BS;
            const vy1 = Math.min(vy0 + BS, cellsY);
            for (let bx = 0; bx < blocksX; bx++) {
                const vx0 = bx * BS;
                const vx1 = Math.min(vx0 + BS, cellsX);

                let bMin = Infinity, bMax = -Infinity;

                // Scan voxels: cells [v0, v1) read corners up to v1 inclusive
                for (let z = vz0; z <= vz1; z++) {
                    for (let y = vy0; y <= vy1; y++) {
                        const base = z * sliceStride + y * rowStride;
                        for (let x = vx0; x <= vx1; x++) {
                            const v = data[base + x];
                            if (v < bMin) bMin = v;
                            if (v > bMax) bMax = v;
                        }
                    }
                }

                const idx = (bz * blocksY * blocksX + by * blocksX + bx) * 2;
                blockMinMax[idx] = bMin;
                blockMinMax[idx + 1] = bMax;
            }
        }
    }
}

// ── Marching Cubes ──────────────────────────────────────────────────────────

function marchingCubes(data, dims, ps, threshold) {
    const DX = dims.x, DY = dims.y, DZ = dims.z;
    const rowStride = DX;
    const sliceStride = DX * DY;
    const hx = DX * 0.5, hy = DY * 0.5, hz = DZ * 0.5;
    const psx = ps.x, psy = ps.y, psz = ps.z;
    const maxX = DX - 1, maxY = DY - 1, maxZ = DZ - 1;

    // Growable typed output buffers (avoids JS array + push + final copy)
    let capacity = 65536;
    let posArr = new Float32Array(capacity * 3);
    let normArr = new Float32Array(capacity * 3);
    let vertCount = 0;

    // Per-cell vertex cache: 12 edges × 6 floats (px, py, pz, nx, ny, nz)
    const vc = new Float32Array(72);

    // Inline edge interpolation: writes position + normal into vc at offset.
    // Takes pre-read corner values (val0, val1) to avoid re-reading the volume.
    function interpEdge(off, x0, y0, z0, val0, x1, y1, z1, val1) {
        let t = 0.5;
        const denom = val1 - val0;
        if (denom > 1e-10 || denom < -1e-10) {
            t = (threshold - val0) / denom;
            if (t < 0) t = 0; else if (t > 1) t = 1;
        }

        // Position → Angstrom
        vc[off]     = (x0 + t * (x1 - x0) - hx) * psx;
        vc[off + 1] = (y0 + t * (y1 - y0) - hy) * psy;
        vc[off + 2] = (z0 + t * (z1 - z0) - hz) * psz;

        // Gradient at endpoint 0 (direct index, clamped at boundaries)
        const i0 = z0 * sliceStride + y0 * rowStride + x0;
        const g0x = data[x0 < maxX ? i0 + 1 : i0] - data[x0 > 0 ? i0 - 1 : i0];
        const g0y = data[y0 < maxY ? i0 + rowStride : i0] - data[y0 > 0 ? i0 - rowStride : i0];
        const g0z = data[z0 < maxZ ? i0 + sliceStride : i0] - data[z0 > 0 ? i0 - sliceStride : i0];

        // Gradient at endpoint 1
        const i1 = z1 * sliceStride + y1 * rowStride + x1;
        const g1x = data[x1 < maxX ? i1 + 1 : i1] - data[x1 > 0 ? i1 - 1 : i1];
        const g1y = data[y1 < maxY ? i1 + rowStride : i1] - data[y1 > 0 ? i1 - rowStride : i1];
        const g1z = data[z1 < maxZ ? i1 + sliceStride : i1] - data[z1 > 0 ? i1 - sliceStride : i1];

        // Interpolated normal (negative gradient = outward)
        let nx = -(g0x + t * (g1x - g0x));
        let ny = -(g0y + t * (g1y - g0y));
        let nz = -(g0z + t * (g1z - g0z));
        const len = Math.sqrt(nx * nx + ny * ny + nz * nz);
        if (len > 1e-10) {
            const inv = 1 / len;
            nx *= inv; ny *= inv; nz *= inv;
        } else {
            nx = 0; ny = 0; nz = 1;
        }
        vc[off + 3] = nx;
        vc[off + 4] = ny;
        vc[off + 5] = nz;
    }

    const cellsX = maxX, cellsY = maxY, cellsZ = maxZ;
    const totalCells = cellsX * cellsY * cellsZ;
    let cellCount = 0;
    let lastProgress = 0;

    const BS = BLOCK_SIZE;
    const bxN = blocksX, byN = blocksY, bzN = blocksZ;

    for (let bz = 0; bz < bzN; bz++) {
        const cz0 = bz * BS;
        const cz1 = cz0 + BS < cellsZ ? cz0 + BS : cellsZ;
        for (let by = 0; by < byN; by++) {
            const cy0 = by * BS;
            const cy1 = cy0 + BS < cellsY ? cy0 + BS : cellsY;
            for (let bx = 0; bx < bxN; bx++) {
                const cx0 = bx * BS;
                const cx1 = cx0 + BS < cellsX ? cx0 + BS : cellsX;

                // Skip blocks where all voxels are on the same side of threshold
                if (blockMinMax) {
                    const bIdx = (bz * byN * bxN + by * bxN + bx) * 2;
                    const bMax = blockMinMax[bIdx + 1];
                    if (bMax < threshold) {
                        cellCount += (cx1 - cx0) * (cy1 - cy0) * (cz1 - cz0);
                        continue;
                    }
                    const bMin = blockMinMax[bIdx];
                    if (bMin >= threshold) {
                        cellCount += (cx1 - cx0) * (cy1 - cy0) * (cz1 - cz0);
                        continue;
                    }
                }

                for (let iz = cz0; iz < cz1; iz++) {
                    for (let iy = cy0; iy < cy1; iy++) {
                        const baseIdx = iz * sliceStride + iy * rowStride;
                        for (let ix = cx0; ix < cx1; ix++) {
                            const idx = baseIdx + ix;

                            const v0 = data[idx];
                            const v1 = data[idx + 1];
                            const v2 = data[idx + 1 + rowStride];
                            const v3 = data[idx + rowStride];
                            const v4 = data[idx + sliceStride];
                            const v5 = data[idx + sliceStride + 1];
                            const v6 = data[idx + sliceStride + 1 + rowStride];
                            const v7 = data[idx + sliceStride + rowStride];

                            let cubeIndex = 0;
                            if (v0 >= threshold) cubeIndex |= 1;
                            if (v1 >= threshold) cubeIndex |= 2;
                            if (v2 >= threshold) cubeIndex |= 4;
                            if (v3 >= threshold) cubeIndex |= 8;
                            if (v4 >= threshold) cubeIndex |= 16;
                            if (v5 >= threshold) cubeIndex |= 32;
                            if (v6 >= threshold) cubeIndex |= 64;
                            if (v7 >= threshold) cubeIndex |= 128;

                            if (cubeIndex === 0 || cubeIndex === 255) {
                                cellCount++;
                                continue;
                            }

                            const edgeMask = EDGE_TABLE[cubeIndex];
                            const x1 = ix + 1, y1 = iy + 1, z1 = iz + 1;

                            if (edgeMask & 1)    interpEdge(0,  ix,iy,iz, v0, x1,iy,iz, v1);
                            if (edgeMask & 2)    interpEdge(6,  x1,iy,iz, v1, x1,y1,iz, v2);
                            if (edgeMask & 4)    interpEdge(12, x1,y1,iz, v2, ix,y1,iz, v3);
                            if (edgeMask & 8)    interpEdge(18, ix,y1,iz, v3, ix,iy,iz, v0);
                            if (edgeMask & 16)   interpEdge(24, ix,iy,z1, v4, x1,iy,z1, v5);
                            if (edgeMask & 32)   interpEdge(30, x1,iy,z1, v5, x1,y1,z1, v6);
                            if (edgeMask & 64)   interpEdge(36, x1,y1,z1, v6, ix,y1,z1, v7);
                            if (edgeMask & 128)  interpEdge(42, ix,y1,z1, v7, ix,iy,z1, v4);
                            if (edgeMask & 256)  interpEdge(48, ix,iy,iz, v0, ix,iy,z1, v4);
                            if (edgeMask & 512)  interpEdge(54, x1,iy,iz, v1, x1,iy,z1, v5);
                            if (edgeMask & 1024) interpEdge(60, x1,y1,iz, v2, x1,y1,z1, v6);
                            if (edgeMask & 2048) interpEdge(66, ix,y1,iz, v3, ix,y1,z1, v7);

                            const triRow = TRI_TABLE[cubeIndex];
                            for (let t = 0; triRow[t] !== -1; t += 3) {
                                if (vertCount + 3 > capacity) {
                                    capacity *= 2;
                                    const newPos = new Float32Array(capacity * 3);
                                    newPos.set(posArr);
                                    posArr = newPos;
                                    const newNorm = new Float32Array(capacity * 3);
                                    newNorm.set(normArr);
                                    normArr = newNorm;
                                }

                                const out = vertCount * 3;
                                for (let v = 0; v < 3; v++) {
                                    const e = triRow[t + v] * 6;
                                    const o = out + v * 3;
                                    posArr[o]     = vc[e];
                                    posArr[o + 1] = vc[e + 1];
                                    posArr[o + 2] = vc[e + 2];
                                    normArr[o]     = vc[e + 3];
                                    normArr[o + 1] = vc[e + 4];
                                    normArr[o + 2] = vc[e + 5];
                                }
                                vertCount += 3;
                            }

                            cellCount++;
                        }
                    }
                }

                // Progress per block (fewer postMessage calls than per-cell)
                const pct = Math.floor((cellCount / totalCells) * 20);
                if (pct > lastProgress) {
                    lastProgress = pct;
                    self.postMessage({ type: 'progress', percent: pct * 5 });
                }
            }
        }
    }

    return {
        positions: posArr.slice(0, vertCount * 3),
        normals: normArr.slice(0, vertCount * 3)
    };
}

// ── Vertex-clustering LOD generation ────────────────────────────────────────

function generateLODs(positions, normals, ps) {
    const vertCount = positions.length / 3;
    // LOD 0 = full-resolution mesh
    const lods = [{ positions, normals }];
    if (vertCount === 0) return lods;

    const maxPixelSize = Math.max(ps.x, ps.y, ps.z);

    // LOD levels 1-3 with cell sizes 2, 4, 8 times maxPixelSize
    for (let level = 1; level <= 3; level++) {
        const cellSize = (1 << level) * maxPixelSize;
        const invCell = 1 / cellSize;

        // Hash vertices into grid cells
        // Key: "ix,iy,iz" -> { sx, sy, sz, snx, sny, snz, count, newIndex }
        const cellMap = new Map();
        const vertexToCell = new Int32Array(vertCount); // cell index per vertex
        const cellKeys = []; // ordered cell keys
        let cellCount = 0;

        for (let v = 0; v < vertCount; v++) {
            const off = v * 3;
            const px = positions[off], py = positions[off + 1], pz = positions[off + 2];
            // floor that handles negatives correctly
            const ix = Math.floor(px * invCell);
            const iy = Math.floor(py * invCell);
            const iz = Math.floor(pz * invCell);
            const key = ix + ',' + iy + ',' + iz;

            let cell = cellMap.get(key);
            if (!cell) {
                cell = { sx: 0, sy: 0, sz: 0, snx: 0, sny: 0, snz: 0, count: 0, newIndex: cellCount };
                cellMap.set(key, cell);
                cellKeys.push(key);
                cellCount++;
            }

            cell.sx += px;
            cell.sy += py;
            cell.sz += pz;
            cell.snx += normals[off];
            cell.sny += normals[off + 1];
            cell.snz += normals[off + 2];
            cell.count++;
            vertexToCell[v] = cell.newIndex;
        }

        // Compute averaged positions and renormalized normals per cell
        const cellPos = new Float32Array(cellCount * 3);
        const cellNorm = new Float32Array(cellCount * 3);
        for (const [, cell] of cellMap) {
            const ci = cell.newIndex;
            const off = ci * 3;
            const inv = 1 / cell.count;
            cellPos[off] = cell.sx * inv;
            cellPos[off + 1] = cell.sy * inv;
            cellPos[off + 2] = cell.sz * inv;

            let nx = cell.snx, ny = cell.sny, nz = cell.snz;
            const len = Math.sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-10) {
                const invL = 1 / len;
                nx *= invL; ny *= invL; nz *= invL;
            } else {
                nx = 0; ny = 0; nz = 1;
            }
            cellNorm[off] = nx;
            cellNorm[off + 1] = ny;
            cellNorm[off + 2] = nz;
        }

        // Emit non-degenerate triangles (all 3 vertices map to distinct cells)
        const triCount = vertCount / 3;
        let outCapacity = Math.max(1024, triCount) * 3;
        let outPos = new Float32Array(outCapacity * 3);
        let outNorm = new Float32Array(outCapacity * 3);
        let outVerts = 0;

        for (let t = 0; t < triCount; t++) {
            const v0 = t * 3, v1 = v0 + 1, v2 = v0 + 2;
            const c0 = vertexToCell[v0], c1 = vertexToCell[v1], c2 = vertexToCell[v2];
            if (c0 === c1 || c1 === c2 || c0 === c2) continue;

            if (outVerts + 3 > outCapacity) {
                outCapacity *= 2;
                const newP = new Float32Array(outCapacity * 3);
                newP.set(outPos);
                outPos = newP;
                const newN = new Float32Array(outCapacity * 3);
                newN.set(outNorm);
                outNorm = newN;
            }

            for (const ci of [c0, c1, c2]) {
                const src = ci * 3;
                const dst = outVerts * 3;
                outPos[dst] = cellPos[src];
                outPos[dst + 1] = cellPos[src + 1];
                outPos[dst + 2] = cellPos[src + 2];
                outNorm[dst] = cellNorm[src];
                outNorm[dst + 1] = cellNorm[src + 1];
                outNorm[dst + 2] = cellNorm[src + 2];
                outVerts++;
            }
        }

        lods.push({
            positions: outPos.slice(0, outVerts * 3),
            normals: outNorm.slice(0, outVerts * 3)
        });

        // If this LOD produced no triangles, skip coarser levels
        if (outVerts === 0) break;
    }

    return lods;
}

// ── Lookup Tables ───────────────────────────────────────────────────────────
// Standard marching cubes edge table (256 entries, 12-bit bitmask)
// and triangle table (256 × up to 16 entries, -1 terminated).

const EDGE_TABLE = [
    0x0,   0x109, 0x203, 0x30a, 0x406, 0x50f, 0x605, 0x70c,
    0x80c, 0x905, 0xa0f, 0xb06, 0xc0a, 0xd03, 0xe09, 0xf00,
    0x190, 0x99,  0x393, 0x29a, 0x596, 0x49f, 0x795, 0x69c,
    0x99c, 0x895, 0xb9f, 0xa96, 0xd9a, 0xc93, 0xf99, 0xe90,
    0x230, 0x339, 0x33,  0x13a, 0x636, 0x73f, 0x435, 0x53c,
    0xa3c, 0xb35, 0x83f, 0x936, 0xe3a, 0xf33, 0xc39, 0xd30,
    0x3a0, 0x2a9, 0x1a3, 0xaa,  0x7a6, 0x6af, 0x5a5, 0x4ac,
    0xbac, 0xaa5, 0x9af, 0x8a6, 0xfaa, 0xea3, 0xda9, 0xca0,
    0x460, 0x569, 0x663, 0x76a, 0x66,  0x16f, 0x265, 0x36c,
    0xc6c, 0xd65, 0xe6f, 0xf66, 0x86a, 0x963, 0xa69, 0xb60,
    0x5f0, 0x4f9, 0x7f3, 0x6fa, 0x1f6, 0xff,  0x3f5, 0x2fc,
    0xdfc, 0xcf5, 0xfff, 0xef6, 0x9fa, 0x8f3, 0xbf9, 0xaf0,
    0x650, 0x759, 0x453, 0x55a, 0x256, 0x35f, 0x55,  0x15c,
    0xe5c, 0xf55, 0xc5f, 0xd56, 0xa5a, 0xb53, 0x859, 0x950,
    0x7c0, 0x6c9, 0x5c3, 0x4ca, 0x3c6, 0x2cf, 0x1c5, 0xcc,
    0xfcc, 0xec5, 0xdcf, 0xcc6, 0xbca, 0xac3, 0x9c9, 0x8c0,
    0x8c0, 0x9c9, 0xac3, 0xbca, 0xcc6, 0xdcf, 0xec5, 0xfcc,
    0xcc,  0x1c5, 0x2cf, 0x3c6, 0x4ca, 0x5c3, 0x6c9, 0x7c0,
    0x950, 0x859, 0xb53, 0xa5a, 0xd56, 0xc5f, 0xf55, 0xe5c,
    0x15c, 0x55,  0x35f, 0x256, 0x55a, 0x453, 0x759, 0x650,
    0xaf0, 0xbf9, 0x8f3, 0x9fa, 0xef6, 0xfff, 0xcf5, 0xdfc,
    0x2fc, 0x3f5, 0xff,  0x1f6, 0x6fa, 0x7f3, 0x4f9, 0x5f0,
    0xb60, 0xa69, 0x963, 0x86a, 0xf66, 0xe6f, 0xd65, 0xc6c,
    0x36c, 0x265, 0x16f, 0x66,  0x76a, 0x663, 0x569, 0x460,
    0xca0, 0xda9, 0xea3, 0xfaa, 0x8a6, 0x9af, 0xaa5, 0xbac,
    0x4ac, 0x5a5, 0x6af, 0x7a6, 0xaa,  0x1a3, 0x2a9, 0x3a0,
    0xd30, 0xc39, 0xf33, 0xe3a, 0x936, 0x83f, 0xb35, 0xa3c,
    0x53c, 0x435, 0x73f, 0x636, 0x13a, 0x33,  0x339, 0x230,
    0xe90, 0xf99, 0xc93, 0xd9a, 0xa96, 0xb9f, 0x895, 0x99c,
    0x69c, 0x795, 0x49f, 0x596, 0x29a, 0x393, 0x99,  0x190,
    0xf00, 0xe09, 0xd03, 0xc0a, 0xb06, 0xa0f, 0x905, 0x80c,
    0x70c, 0x605, 0x50f, 0x406, 0x30a, 0x203, 0x109, 0x0
];

const TRI_TABLE = [
    [-1],
    [0, 8, 3, -1],
    [0, 1, 9, -1],
    [1, 8, 3, 9, 8, 1, -1],
    [1, 2, 10, -1],
    [0, 8, 3, 1, 2, 10, -1],
    [9, 2, 10, 0, 2, 9, -1],
    [2, 8, 3, 2, 10, 8, 10, 9, 8, -1],
    [3, 11, 2, -1],
    [0, 11, 2, 8, 11, 0, -1],
    [1, 9, 0, 2, 3, 11, -1],
    [1, 11, 2, 1, 9, 11, 9, 8, 11, -1],
    [3, 10, 1, 11, 10, 3, -1],
    [0, 10, 1, 0, 8, 10, 8, 11, 10, -1],
    [3, 9, 0, 3, 11, 9, 11, 10, 9, -1],
    [9, 8, 10, 10, 8, 11, -1],
    [4, 7, 8, -1],
    [4, 3, 0, 7, 3, 4, -1],
    [0, 1, 9, 8, 4, 7, -1],
    [4, 1, 9, 4, 7, 1, 7, 3, 1, -1],
    [1, 2, 10, 8, 4, 7, -1],
    [3, 4, 7, 3, 0, 4, 1, 2, 10, -1],
    [9, 2, 10, 9, 0, 2, 8, 4, 7, -1],
    [2, 10, 9, 2, 9, 7, 2, 7, 3, 7, 9, 4, -1],
    [8, 4, 7, 3, 11, 2, -1],
    [11, 4, 7, 11, 2, 4, 2, 0, 4, -1],
    [9, 0, 1, 8, 4, 7, 2, 3, 11, -1],
    [4, 7, 11, 9, 4, 11, 9, 11, 2, 9, 2, 1, -1],
    [3, 10, 1, 3, 11, 10, 7, 8, 4, -1],
    [1, 11, 10, 1, 4, 11, 1, 0, 4, 7, 11, 4, -1],
    [4, 7, 8, 9, 0, 11, 9, 11, 10, 11, 0, 3, -1],
    [4, 7, 11, 4, 11, 9, 9, 11, 10, -1],
    [9, 5, 4, -1],
    [9, 5, 4, 0, 8, 3, -1],
    [0, 5, 4, 1, 5, 0, -1],
    [8, 5, 4, 8, 3, 5, 3, 1, 5, -1],
    [1, 2, 10, 9, 5, 4, -1],
    [3, 0, 8, 1, 2, 10, 4, 9, 5, -1],
    [5, 2, 10, 5, 4, 2, 4, 0, 2, -1],
    [2, 10, 5, 3, 2, 5, 3, 5, 4, 3, 4, 8, -1],
    [9, 5, 4, 2, 3, 11, -1],
    [0, 11, 2, 0, 8, 11, 4, 9, 5, -1],
    [0, 5, 4, 0, 1, 5, 2, 3, 11, -1],
    [2, 1, 5, 2, 5, 8, 2, 8, 11, 4, 8, 5, -1],
    [10, 3, 11, 10, 1, 3, 9, 5, 4, -1],
    [4, 9, 5, 0, 8, 1, 8, 10, 1, 8, 11, 10, -1],
    [5, 4, 0, 5, 0, 11, 5, 11, 10, 11, 0, 3, -1],
    [5, 4, 8, 5, 8, 10, 10, 8, 11, -1],
    [9, 7, 8, 5, 7, 9, -1],
    [9, 3, 0, 9, 5, 3, 5, 7, 3, -1],
    [0, 7, 8, 0, 1, 7, 1, 5, 7, -1],
    [1, 5, 3, 3, 5, 7, -1],
    [9, 7, 8, 9, 5, 7, 10, 1, 2, -1],
    [10, 1, 2, 9, 5, 0, 5, 3, 0, 5, 7, 3, -1],
    [8, 0, 2, 8, 2, 5, 8, 5, 7, 10, 5, 2, -1],
    [2, 10, 5, 2, 5, 3, 3, 5, 7, -1],
    [7, 9, 5, 7, 8, 9, 3, 11, 2, -1],
    [9, 5, 7, 9, 7, 2, 9, 2, 0, 2, 7, 11, -1],
    [2, 3, 11, 0, 1, 8, 1, 7, 8, 1, 5, 7, -1],
    [11, 2, 1, 11, 1, 7, 7, 1, 5, -1],
    [9, 5, 8, 8, 5, 7, 10, 1, 3, 10, 3, 11, -1],
    [5, 7, 0, 5, 0, 9, 7, 11, 0, 1, 0, 10, 11, 10, 0, -1],
    [11, 10, 0, 11, 0, 3, 10, 5, 0, 8, 0, 7, 5, 7, 0, -1],
    [11, 10, 5, 7, 11, 5, -1],
    [10, 6, 5, -1],
    [0, 8, 3, 5, 10, 6, -1],
    [9, 0, 1, 5, 10, 6, -1],
    [1, 8, 3, 1, 9, 8, 5, 10, 6, -1],
    [1, 6, 5, 2, 6, 1, -1],
    [1, 6, 5, 1, 2, 6, 3, 0, 8, -1],
    [9, 6, 5, 9, 0, 6, 0, 2, 6, -1],
    [5, 9, 8, 5, 8, 2, 5, 2, 6, 3, 2, 8, -1],
    [2, 3, 11, 10, 6, 5, -1],
    [11, 0, 8, 11, 2, 0, 10, 6, 5, -1],
    [0, 1, 9, 2, 3, 11, 5, 10, 6, -1],
    [5, 10, 6, 1, 9, 2, 9, 11, 2, 9, 8, 11, -1],
    [6, 3, 11, 6, 5, 3, 5, 1, 3, -1],
    [0, 8, 11, 0, 11, 5, 0, 5, 1, 5, 11, 6, -1],
    [3, 11, 6, 0, 3, 6, 0, 6, 5, 0, 5, 9, -1],
    [6, 5, 9, 6, 9, 11, 11, 9, 8, -1],
    [5, 10, 6, 4, 7, 8, -1],
    [4, 3, 0, 4, 7, 3, 6, 5, 10, -1],
    [1, 9, 0, 5, 10, 6, 8, 4, 7, -1],
    [10, 6, 5, 1, 9, 7, 1, 7, 3, 7, 9, 4, -1],
    [6, 1, 2, 6, 5, 1, 4, 7, 8, -1],
    [1, 2, 5, 5, 2, 6, 3, 0, 4, 3, 4, 7, -1],
    [8, 4, 7, 9, 0, 5, 0, 6, 5, 0, 2, 6, -1],
    [7, 3, 9, 7, 9, 4, 3, 2, 9, 5, 9, 6, 2, 6, 9, -1],
    [3, 11, 2, 7, 8, 4, 10, 6, 5, -1],
    [5, 10, 6, 4, 7, 2, 4, 2, 0, 2, 7, 11, -1],
    [0, 1, 9, 4, 7, 8, 2, 3, 11, 5, 10, 6, -1],
    [9, 2, 1, 9, 11, 2, 9, 4, 11, 7, 11, 4, 5, 10, 6, -1],
    [8, 4, 7, 3, 11, 5, 3, 5, 1, 5, 11, 6, -1],
    [5, 1, 11, 5, 11, 6, 1, 0, 11, 7, 11, 4, 0, 4, 11, -1],
    [0, 5, 9, 0, 6, 5, 0, 3, 6, 11, 6, 3, 8, 4, 7, -1],
    [6, 5, 9, 6, 9, 11, 4, 7, 9, 7, 11, 9, -1],
    [10, 4, 9, 6, 4, 10, -1],
    [4, 10, 6, 4, 9, 10, 0, 8, 3, -1],
    [10, 0, 1, 10, 6, 0, 6, 4, 0, -1],
    [8, 3, 1, 8, 1, 6, 8, 6, 4, 6, 1, 10, -1],
    [1, 4, 9, 1, 2, 4, 2, 6, 4, -1],
    [3, 0, 8, 1, 2, 9, 2, 4, 9, 2, 6, 4, -1],
    [0, 2, 4, 4, 2, 6, -1],
    [8, 3, 2, 8, 2, 4, 4, 2, 6, -1],
    [10, 4, 9, 10, 6, 4, 11, 2, 3, -1],
    [0, 8, 2, 2, 8, 11, 4, 9, 10, 4, 10, 6, -1],
    [3, 11, 2, 0, 1, 6, 0, 6, 4, 6, 1, 10, -1],
    [6, 4, 1, 6, 1, 10, 4, 8, 1, 2, 1, 11, 8, 11, 1, -1],
    [9, 6, 4, 9, 3, 6, 9, 1, 3, 11, 6, 3, -1],
    [8, 11, 1, 8, 1, 0, 11, 6, 1, 9, 1, 4, 6, 4, 1, -1],
    [3, 11, 6, 3, 6, 0, 0, 6, 4, -1],
    [6, 4, 8, 11, 6, 8, -1],
    [7, 10, 6, 7, 8, 10, 8, 9, 10, -1],
    [0, 7, 3, 0, 10, 7, 0, 9, 10, 6, 7, 10, -1],
    [10, 6, 7, 1, 10, 7, 1, 7, 8, 1, 8, 0, -1],
    [10, 6, 7, 10, 7, 1, 1, 7, 3, -1],
    [1, 2, 6, 1, 6, 8, 1, 8, 9, 8, 6, 7, -1],
    [2, 6, 9, 2, 9, 1, 6, 7, 9, 0, 9, 3, 7, 3, 9, -1],
    [7, 8, 0, 7, 0, 6, 6, 0, 2, -1],
    [7, 3, 2, 6, 7, 2, -1],
    [2, 3, 11, 10, 6, 8, 10, 8, 9, 8, 6, 7, -1],
    [2, 0, 7, 2, 7, 11, 0, 9, 7, 6, 7, 10, 9, 10, 7, -1],
    [1, 8, 0, 1, 7, 8, 1, 10, 7, 6, 7, 10, 2, 3, 11, -1],
    [11, 2, 1, 11, 1, 7, 10, 6, 1, 6, 7, 1, -1],
    [8, 9, 6, 8, 6, 7, 9, 1, 6, 11, 6, 3, 1, 3, 6, -1],
    [0, 9, 1, 11, 6, 7, -1],
    [7, 8, 0, 7, 0, 6, 3, 11, 0, 11, 6, 0, -1],
    [7, 11, 6, -1],
    [7, 6, 11, -1],
    [3, 0, 8, 11, 7, 6, -1],
    [0, 1, 9, 11, 7, 6, -1],
    [8, 1, 9, 8, 3, 1, 11, 7, 6, -1],
    [10, 1, 2, 6, 11, 7, -1],
    [1, 2, 10, 3, 0, 8, 6, 11, 7, -1],
    [2, 9, 0, 2, 10, 9, 6, 11, 7, -1],
    [6, 11, 7, 2, 10, 3, 10, 8, 3, 10, 9, 8, -1],
    [7, 2, 3, 6, 2, 7, -1],
    [7, 0, 8, 7, 6, 0, 6, 2, 0, -1],
    [2, 7, 6, 2, 3, 7, 0, 1, 9, -1],
    [1, 6, 2, 1, 8, 6, 1, 9, 8, 8, 7, 6, -1],
    [10, 7, 6, 10, 1, 7, 1, 3, 7, -1],
    [10, 7, 6, 1, 7, 10, 1, 8, 7, 1, 0, 8, -1],
    [0, 3, 7, 0, 7, 10, 0, 10, 9, 6, 10, 7, -1],
    [7, 6, 10, 7, 10, 8, 8, 10, 9, -1],
    [6, 8, 4, 11, 8, 6, -1],
    [3, 6, 11, 3, 0, 6, 0, 4, 6, -1],
    [8, 6, 11, 8, 4, 6, 9, 0, 1, -1],
    [9, 4, 6, 9, 6, 3, 9, 3, 1, 11, 3, 6, -1],
    [6, 8, 4, 6, 11, 8, 2, 10, 1, -1],
    [1, 2, 10, 3, 0, 11, 0, 6, 11, 0, 4, 6, -1],
    [4, 11, 8, 4, 6, 11, 0, 2, 9, 2, 10, 9, -1],
    [10, 9, 3, 10, 3, 2, 9, 4, 3, 11, 3, 6, 4, 6, 3, -1],
    [8, 2, 3, 8, 4, 2, 4, 6, 2, -1],
    [0, 4, 2, 4, 6, 2, -1],
    [1, 9, 0, 2, 3, 4, 2, 4, 6, 4, 3, 8, -1],
    [1, 9, 4, 1, 4, 2, 2, 4, 6, -1],
    [8, 1, 3, 8, 6, 1, 8, 4, 6, 6, 10, 1, -1],
    [10, 1, 0, 10, 0, 6, 6, 0, 4, -1],
    [4, 6, 3, 4, 3, 8, 6, 10, 3, 0, 3, 9, 10, 9, 3, -1],
    [10, 9, 4, 6, 10, 4, -1],
    [4, 9, 5, 7, 6, 11, -1],
    [0, 8, 3, 4, 9, 5, 11, 7, 6, -1],
    [5, 0, 1, 5, 4, 0, 7, 6, 11, -1],
    [11, 7, 6, 8, 3, 4, 3, 5, 4, 3, 1, 5, -1],
    [9, 5, 4, 10, 1, 2, 7, 6, 11, -1],
    [6, 11, 7, 1, 2, 10, 0, 8, 3, 4, 9, 5, -1],
    [7, 6, 11, 5, 4, 10, 4, 2, 10, 4, 0, 2, -1],
    [3, 4, 8, 3, 5, 4, 3, 2, 5, 10, 5, 2, 11, 7, 6, -1],
    [7, 2, 3, 7, 6, 2, 5, 4, 9, -1],
    [9, 5, 4, 0, 8, 6, 0, 6, 2, 6, 8, 7, -1],
    [3, 6, 2, 3, 7, 6, 1, 5, 0, 5, 4, 0, -1],
    [6, 2, 8, 6, 8, 7, 2, 1, 8, 4, 8, 5, 1, 5, 8, -1],
    [9, 5, 4, 10, 1, 6, 1, 7, 6, 1, 3, 7, -1],
    [1, 6, 10, 1, 7, 6, 1, 0, 7, 8, 7, 0, 9, 5, 4, -1],
    [4, 0, 10, 4, 10, 5, 0, 3, 10, 6, 10, 7, 3, 7, 10, -1],
    [7, 6, 10, 7, 10, 8, 5, 4, 10, 4, 8, 10, -1],
    [6, 9, 5, 6, 11, 9, 11, 8, 9, -1],
    [3, 6, 11, 0, 6, 3, 0, 5, 6, 0, 9, 5, -1],
    [0, 11, 8, 0, 5, 11, 0, 1, 5, 5, 6, 11, -1],
    [6, 11, 3, 6, 3, 5, 5, 3, 1, -1],
    [1, 2, 10, 9, 5, 11, 9, 11, 8, 11, 5, 6, -1],
    [0, 11, 3, 0, 6, 11, 0, 9, 6, 5, 6, 9, 1, 2, 10, -1],
    [11, 8, 5, 11, 5, 6, 8, 0, 5, 10, 5, 2, 0, 2, 5, -1],
    [6, 11, 3, 6, 3, 5, 2, 10, 3, 10, 5, 3, -1],
    [5, 8, 9, 5, 2, 8, 5, 6, 2, 3, 8, 2, -1],
    [9, 5, 6, 9, 6, 0, 0, 6, 2, -1],
    [1, 5, 8, 1, 8, 0, 5, 6, 8, 3, 8, 2, 6, 2, 8, -1],
    [1, 5, 6, 2, 1, 6, -1],
    [1, 3, 6, 1, 6, 10, 3, 8, 6, 5, 6, 9, 8, 9, 6, -1],
    [10, 1, 0, 10, 0, 6, 9, 5, 0, 5, 6, 0, -1],
    [0, 3, 8, 5, 6, 10, -1],
    [10, 5, 6, -1],
    [11, 5, 10, 7, 5, 11, -1],
    [11, 5, 10, 11, 7, 5, 8, 3, 0, -1],
    [5, 11, 7, 5, 10, 11, 1, 9, 0, -1],
    [10, 7, 5, 10, 11, 7, 9, 8, 1, 8, 3, 1, -1],
    [11, 1, 2, 11, 7, 1, 7, 5, 1, -1],
    [0, 8, 3, 1, 2, 7, 1, 7, 5, 7, 2, 11, -1],
    [9, 7, 5, 9, 2, 7, 9, 0, 2, 2, 11, 7, -1],
    [7, 5, 2, 7, 2, 11, 5, 9, 2, 3, 2, 8, 9, 8, 2, -1],
    [2, 5, 10, 2, 3, 5, 3, 7, 5, -1],
    [8, 2, 0, 8, 5, 2, 8, 7, 5, 10, 2, 5, -1],
    [9, 0, 1, 5, 10, 3, 5, 3, 7, 3, 10, 2, -1],
    [9, 8, 2, 9, 2, 1, 8, 7, 2, 10, 2, 5, 7, 5, 2, -1],
    [1, 3, 5, 3, 7, 5, -1],
    [0, 8, 7, 0, 7, 1, 1, 7, 5, -1],
    [9, 0, 3, 9, 3, 5, 5, 3, 7, -1],
    [9, 8, 7, 5, 9, 7, -1],
    [5, 8, 4, 5, 10, 8, 10, 11, 8, -1],
    [5, 0, 4, 5, 11, 0, 5, 10, 11, 11, 3, 0, -1],
    [0, 1, 9, 8, 4, 10, 8, 10, 11, 10, 4, 5, -1],
    [10, 11, 4, 10, 4, 5, 11, 3, 4, 9, 4, 1, 3, 1, 4, -1],
    [2, 5, 1, 2, 8, 5, 2, 11, 8, 4, 5, 8, -1],
    [0, 4, 11, 0, 11, 3, 4, 5, 11, 2, 11, 1, 5, 1, 11, -1],
    [0, 2, 5, 0, 5, 9, 2, 11, 5, 4, 5, 8, 11, 8, 5, -1],
    [9, 4, 5, 2, 11, 3, -1],
    [2, 5, 10, 3, 5, 2, 3, 4, 5, 3, 8, 4, -1],
    [5, 10, 2, 5, 2, 4, 4, 2, 0, -1],
    [3, 10, 2, 3, 5, 10, 3, 8, 5, 4, 5, 8, 0, 1, 9, -1],
    [5, 10, 2, 5, 2, 4, 1, 9, 2, 9, 4, 2, -1],
    [8, 4, 5, 8, 5, 3, 3, 5, 1, -1],
    [0, 4, 5, 1, 0, 5, -1],
    [8, 4, 5, 8, 5, 3, 9, 0, 5, 0, 3, 5, -1],
    [9, 4, 5, -1],
    [4, 11, 7, 4, 9, 11, 9, 10, 11, -1],
    [0, 8, 3, 4, 9, 7, 9, 11, 7, 9, 10, 11, -1],
    [1, 10, 11, 1, 11, 4, 1, 4, 0, 7, 4, 11, -1],
    [3, 1, 4, 3, 4, 8, 1, 10, 4, 7, 4, 11, 10, 11, 4, -1],
    [4, 11, 7, 9, 11, 4, 9, 2, 11, 9, 1, 2, -1],
    [9, 7, 4, 9, 11, 7, 9, 1, 11, 2, 11, 1, 0, 8, 3, -1],
    [11, 7, 4, 11, 4, 2, 2, 4, 0, -1],
    [11, 7, 4, 11, 4, 2, 8, 3, 4, 3, 2, 4, -1],
    [2, 9, 10, 2, 7, 9, 2, 3, 7, 7, 4, 9, -1],
    [9, 10, 7, 9, 7, 4, 10, 2, 7, 8, 7, 0, 2, 0, 7, -1],
    [3, 7, 10, 3, 10, 2, 7, 4, 10, 1, 10, 0, 4, 0, 10, -1],
    [1, 10, 2, 8, 7, 4, -1],
    [4, 9, 1, 4, 1, 7, 7, 1, 3, -1],
    [4, 9, 1, 4, 1, 7, 0, 8, 1, 8, 7, 1, -1],
    [4, 0, 3, 7, 4, 3, -1],
    [4, 8, 7, -1],
    [9, 10, 8, 10, 11, 8, -1],
    [3, 0, 9, 3, 9, 11, 11, 9, 10, -1],
    [0, 1, 10, 0, 10, 8, 8, 10, 11, -1],
    [3, 1, 10, 11, 3, 10, -1],
    [1, 2, 11, 1, 11, 9, 9, 11, 8, -1],
    [3, 0, 9, 3, 9, 11, 1, 2, 9, 2, 11, 9, -1],
    [0, 2, 11, 8, 0, 11, -1],
    [3, 2, 11, -1],
    [2, 3, 8, 2, 8, 10, 10, 8, 9, -1],
    [9, 10, 2, 0, 9, 2, -1],
    [2, 3, 8, 2, 8, 10, 0, 1, 8, 1, 10, 8, -1],
    [1, 10, 2, -1],
    [1, 3, 8, 9, 1, 8, -1],
    [0, 9, 1, -1],
    [0, 3, 8, -1],
    [-1]
];
