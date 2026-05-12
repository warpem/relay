/**
 * MRC File Parser for JavaScript
 *
 * Parses MRC (Medical Research Council) format files commonly used in cryo-EM.
 * Following the C# implementation in WarpLib/Headers/MRC.cs
 */

/**
 * MRC data type enumeration
 */
export const MRCDataType = {
    Byte: 0,
    Short: 1,
    Float: 2,
    ShortComplex: 3,
    FloatComplex: 4,
    UnsignedShort: 6,
    Half: 12,
    RGB: 16
};

/**
 * Get bytes per element for a given MRC data type
 * @param {number} mode - The MRC data type mode
 * @returns {number} Bytes per element
 */
function getBytesPerElement(mode) {
    switch (mode) {
        case MRCDataType.Byte:
            return 1;
        case MRCDataType.Short:
        case MRCDataType.UnsignedShort:
        case MRCDataType.Half:
            return 2;
        case MRCDataType.Float:
            return 4;
        case MRCDataType.ShortComplex:
            return 4; // 2 shorts
        case MRCDataType.FloatComplex:
            return 8; // 2 floats
        case MRCDataType.RGB:
            return 3;
        default:
            throw new Error(`Unknown MRC data type: ${mode}`);
    }
}

/**
 * MRC Header class containing all header fields
 */
export class MRCHeader {
    constructor() {
        // Dimensions
        this.dimensions = { x: 0, y: 0, z: 0 };
        this.mode = MRCDataType.Float;
        this.startSubImage = { x: 0, y: 0, z: 0 };
        this.gridDimensions = { x: 1, y: 1, z: 1 };
        this.pixelSize = { x: 1, y: 1, z: 1 };
        this.angles = { x: 90, y: 90, z: 90 };
        this.mapOrder = { x: 1, y: 2, z: 3 };

        // Statistics
        this.minValue = 0;
        this.maxValue = 0;
        this.meanValue = 0;
        this.stdDevValue = 0;
        this.spaceGroup = 0;

        // Extended header info
        this.extendedBytes = 0;
        this.creatorId = 0;
        this.extId = '';
        this.nInt = 0;
        this.nReal = 0;

        // Lens/detector info
        this.idType = 0;
        this.lens = 0;
        this.nd1 = 0;
        this.nd2 = 0;
        this.vd1 = 0;
        this.vd2 = 0;

        // Tilt info
        this.tiltOriginal = { x: 0, y: 0, z: 0 };
        this.tiltCurrent = { x: 0, y: 0, z: 0 };
        this.origin = { x: 0, y: 0, z: 0 };

        // Map identifiers
        this.cmap = 'MAP ';
        this.stamp = new Uint8Array([68, 65, 0, 0]);

        // Labels
        this.numLabels = 0;
        this.labels = [];

        // Extended header data
        this.extended = null;

        // IMOD/SerialEM specific
        this.imodHasTilt = false;
        this.imodHasMontage = false;
        this.imodHasStagePos = false;
        this.imodHasMagnification = false;
        this.imodHasIntensity = false;
        this.imodHasExposure = false;

        this.imodRotation = 0;
        this.imodTilt = null;
        this.imodMagnification = null;
        this.imodIntensity = null;
        this.imodExposure = null;

        // Header size (including extended header)
        this.headerSize = 1024;
    }

    /**
     * Get the total number of voxels
     * @returns {number}
     */
    get voxelCount() {
        return this.dimensions.x * this.dimensions.y * this.dimensions.z;
    }

    /**
     * Get the number of voxels per slice
     * @returns {number}
     */
    get sliceVoxelCount() {
        return this.dimensions.x * this.dimensions.y;
    }

    /**
     * Get bytes per data element based on mode
     * @returns {number}
     */
    get bytesPerElement() {
        return getBytesPerElement(this.mode);
    }

    /**
     * Get the byte size of a single slice
     * @returns {number}
     */
    get sliceByteSize() {
        return this.sliceVoxelCount * this.bytesPerElement;
    }

    /**
     * Get the byte offset of a slice's data relative to the start of the file
     * @param {number} sliceIndex - The 0-indexed slice
     * @returns {number}
     */
    sliceByteOffset(sliceIndex) {
        return this.headerSize + sliceIndex * this.sliceByteSize;
    }

    /**
     * Get the TypedArray constructor for this data type
     * @returns {Function}
     */
    get typedArrayConstructor() {
        switch (this.mode) {
            case MRCDataType.Byte:
                return Uint8Array;
            case MRCDataType.Short:
                return Int16Array;
            case MRCDataType.UnsignedShort:
                return Uint16Array;
            case MRCDataType.Float:
            case MRCDataType.Half: // Will be converted to float32
                return Float32Array;
            case MRCDataType.ShortComplex:
                return Int16Array;
            case MRCDataType.FloatComplex:
                return Float32Array;
            case MRCDataType.RGB:
                return Uint8Array;
            default:
                throw new Error(`Unknown MRC data type: ${this.mode}`);
        }
    }
}

/**
 * Binary reader helper class
 */
class BinaryReader {
    /**
     * @param {ArrayBuffer} buffer - The buffer to read from
     * @param {boolean} littleEndian - Whether to use little-endian byte order
     */
    constructor(buffer, littleEndian = true) {
        this.buffer = buffer;
        this.view = new DataView(buffer);
        this.offset = 0;
        this.littleEndian = littleEndian;
    }

    readInt16() {
        const value = this.view.getInt16(this.offset, this.littleEndian);
        this.offset += 2;
        return value;
    }

    readUint16() {
        const value = this.view.getUint16(this.offset, this.littleEndian);
        this.offset += 2;
        return value;
    }

    readInt32() {
        const value = this.view.getInt32(this.offset, this.littleEndian);
        this.offset += 4;
        return value;
    }

    readFloat32() {
        const value = this.view.getFloat32(this.offset, this.littleEndian);
        this.offset += 4;
        return value;
    }

    readBytes(count) {
        const bytes = new Uint8Array(this.buffer, this.offset, count);
        this.offset += count;
        return bytes;
    }

    readChars(count) {
        const bytes = this.readBytes(count);
        return String.fromCharCode(...bytes);
    }

    readInt3() {
        return {
            x: this.readInt32(),
            y: this.readInt32(),
            z: this.readInt32()
        };
    }

    readFloat3() {
        return {
            x: this.readFloat32(),
            y: this.readFloat32(),
            z: this.readFloat32()
        };
    }

    seek(position) {
        this.offset = position;
    }

    get position() {
        return this.offset;
    }
}

/**
 * Convert Float16 (half precision) to Float32
 * @param {number} half - The 16-bit half precision value
 * @returns {number} The 32-bit float value
 */
function float16ToFloat32(half) {
    const sign = (half >> 15) & 0x1;
    const exponent = (half >> 10) & 0x1f;
    const mantissa = half & 0x3ff;

    if (exponent === 0) {
        if (mantissa === 0) {
            return sign ? -0 : 0;
        }
        // Denormalized number
        const e = -14;
        const m = mantissa / 1024;
        return (sign ? -1 : 1) * m * Math.pow(2, e);
    } else if (exponent === 31) {
        if (mantissa === 0) {
            return sign ? -Infinity : Infinity;
        }
        return NaN;
    }

    const e = exponent - 15;
    const m = 1 + mantissa / 1024;
    return (sign ? -1 : 1) * m * Math.pow(2, e);
}

/**
 * Parse an MRC header from a buffer
 * @param {ArrayBuffer} buffer - The buffer containing MRC data
 * @returns {MRCHeader} The parsed header
 */
export function parseHeader(buffer) {
    const reader = new BinaryReader(buffer);
    const header = new MRCHeader();

    // Read dimensions
    header.dimensions = reader.readInt3();
    header.mode = reader.readInt32();
    header.startSubImage = reader.readInt3();
    header.gridDimensions = reader.readInt3();

    // Read cell dimensions and calculate pixel size
    const cellDimensions = reader.readFloat3();
    header.pixelSize = {
        x: cellDimensions.x / header.dimensions.x,
        y: cellDimensions.y / header.dimensions.y,
        z: cellDimensions.z / header.dimensions.z
    };

    header.angles = reader.readFloat3();
    header.mapOrder = reader.readInt3();

    // Statistics
    header.minValue = reader.readFloat32();
    header.maxValue = reader.readFloat32();
    header.meanValue = reader.readFloat32();
    header.spaceGroup = reader.readInt32();

    // Extended header info
    header.extendedBytes = reader.readInt32();
    header.creatorId = reader.readInt16();

    // Extra data
    reader.readBytes(6); // ExtraData10
    header.extId = reader.readChars(4);
    reader.readBytes(20); // ExtraData11

    header.nInt = reader.readInt16();
    header.nReal = reader.readInt16();

    reader.readBytes(28); // ExtraData2

    // Lens/detector info
    header.idType = reader.readInt16();
    header.lens = reader.readInt16();
    header.nd1 = reader.readInt16();
    header.nd2 = reader.readInt16();
    header.vd1 = reader.readInt16();
    header.vd2 = reader.readInt16();

    // Tilt info
    header.tiltOriginal = reader.readFloat3();
    header.tiltCurrent = reader.readFloat3();
    header.origin = reader.readFloat3();

    // Map identifiers
    header.cmap = reader.readChars(4);
    header.stamp = reader.readBytes(4);

    header.stdDevValue = reader.readFloat32();

    // Labels
    header.numLabels = reader.readInt32();
    header.labels = [];
    for (let i = 0; i < 10; i++) {
        const labelBytes = reader.readBytes(80);
        try {
            const label = String.fromCharCode(...labelBytes).replace(/\0+$/, '');
            header.labels.push(label);
        } catch {
            header.labels.push('');
        }
    }

    // Parse IMOD/SerialEM extended header flags
    header.imodHasTilt = (header.nReal & (1 << 0)) > 0;
    header.imodHasMontage = (header.nReal & (1 << 1)) > 0;
    header.imodHasStagePos = (header.nReal & (1 << 2)) > 0;
    header.imodHasMagnification = (header.nReal & (1 << 3)) > 0;
    header.imodHasIntensity = (header.nReal & (1 << 4)) > 0;
    header.imodHasExposure = (header.nReal & (1 << 5)) > 0;

    // Calculate bytes per section for IMOD extended header
    const bytesPerSection = (header.imodHasTilt ? 2 : 0) +
                           (header.imodHasMontage ? 6 : 0) +
                           (header.imodHasStagePos ? 4 : 0) +
                           (header.imodHasMagnification ? 2 : 0) +
                           (header.imodHasIntensity ? 2 : 0) +
                           (header.imodHasExposure ? 4 : 0);

    // Read extended header
    if (bytesPerSection * header.dimensions.z > header.extendedBytes) {
        // Not from SerialEM, just store raw extended header
        if (header.extendedBytes > 0) {
            header.extended = reader.readBytes(header.extendedBytes);
        }
    } else {
        // SerialEM extended header
        if (header.imodHasTilt) {
            header.imodTilt = new Float32Array(header.dimensions.z);
        }
        if (header.imodHasMagnification) {
            header.imodMagnification = new Float32Array(header.dimensions.z);
        }
        if (header.imodHasIntensity) {
            header.imodIntensity = new Float32Array(header.dimensions.z);
        }
        if (header.imodHasExposure) {
            header.imodExposure = new Float32Array(header.dimensions.z);
        }

        for (let i = 0; i < header.dimensions.z; i++) {
            if (header.imodHasTilt) {
                header.imodTilt[i] = reader.readInt16() / 100;
            }
            if (header.imodHasMontage) {
                reader.readBytes(6);
            }
            if (header.imodHasStagePos) {
                reader.readBytes(4);
            }
            if (header.imodHasMagnification) {
                header.imodMagnification[i] = reader.readInt16() / 100;
            }
            if (header.imodHasIntensity) {
                header.imodIntensity[i] = reader.readInt16() / 2500;
            }
            if (header.imodHasExposure) {
                header.imodExposure[i] = reader.readFloat32();
            }
        }
    }

    // Seek to end of header
    reader.seek(1024 + header.extendedBytes);
    header.headerSize = reader.position;

    return header;
}

/**
 * Decode raw voxel data from an ArrayBuffer into Float32Array
 * @param {ArrayBuffer} buffer - Buffer containing raw voxel data
 * @param {number} mode - MRC data type mode
 * @param {number} voxelCount - Number of voxels to decode
 * @param {number} [byteOffset=0] - Starting byte offset within the buffer
 * @returns {Float32Array} Decoded data
 */
function decodeVoxelData(buffer, mode, voxelCount, byteOffset = 0) {
    const bytesPerElement = getBytesPerElement(mode);
    const result = new Float32Array(voxelCount);
    const dataView = new DataView(buffer, byteOffset, voxelCount * bytesPerElement);

    switch (mode) {
        case MRCDataType.Byte: {
            const bytes = new Uint8Array(buffer, byteOffset, voxelCount);
            for (let i = 0; i < voxelCount; i++) {
                result[i] = bytes[i];
            }
            break;
        }
        case MRCDataType.Short: {
            for (let i = 0; i < voxelCount; i++) {
                result[i] = dataView.getInt16(i * 2, true);
            }
            break;
        }
        case MRCDataType.UnsignedShort: {
            for (let i = 0; i < voxelCount; i++) {
                result[i] = dataView.getUint16(i * 2, true);
            }
            break;
        }
        case MRCDataType.Float: {
            for (let i = 0; i < voxelCount; i++) {
                result[i] = dataView.getFloat32(i * 4, true);
            }
            break;
        }
        case MRCDataType.Half: {
            for (let i = 0; i < voxelCount; i++) {
                const half = dataView.getUint16(i * 2, true);
                result[i] = float16ToFloat32(half);
            }
            break;
        }
        case MRCDataType.FloatComplex: {
            for (let i = 0; i < voxelCount; i++) {
                const real = dataView.getFloat32(i * 8, true);
                const imag = dataView.getFloat32(i * 8 + 4, true);
                result[i] = Math.sqrt(real * real + imag * imag);
            }
            break;
        }
        case MRCDataType.ShortComplex: {
            for (let i = 0; i < voxelCount; i++) {
                const real = dataView.getInt16(i * 4, true);
                const imag = dataView.getInt16(i * 4 + 2, true);
                result[i] = Math.sqrt(real * real + imag * imag);
            }
            break;
        }
        case MRCDataType.RGB: {
            const bytes = new Uint8Array(buffer, byteOffset, voxelCount * 3);
            for (let i = 0; i < voxelCount; i++) {
                result[i] = 0.299 * bytes[i * 3] + 0.587 * bytes[i * 3 + 1] + 0.114 * bytes[i * 3 + 2];
            }
            break;
        }
        default:
            throw new Error(`Unsupported MRC data type: ${mode}`);
    }

    return result;
}

/**
 * Read data from an MRC file buffer
 * @param {ArrayBuffer} buffer - The full MRC file buffer
 * @param {MRCHeader} header - The parsed header
 * @param {Object} options - Read options
 * @param {number} [options.sliceStart] - First slice to read (0-indexed)
 * @param {number} [options.sliceEnd] - Last slice to read (exclusive)
 * @returns {Float32Array} The data as Float32Array
 */
export function readData(buffer, header, options = {}) {
    const sliceStart = options.sliceStart ?? 0;
    const sliceEnd = options.sliceEnd ?? header.dimensions.z;
    const sliceCount = sliceEnd - sliceStart;
    const totalVoxels = header.sliceVoxelCount * sliceCount;
    const dataOffset = header.sliceByteOffset(sliceStart);

    return decodeVoxelData(buffer, header.mode, totalVoxels, dataOffset);
}

/**
 * Read a single slice from an MRC file
 * @param {ArrayBuffer} buffer - The full MRC file buffer
 * @param {MRCHeader} header - The parsed header
 * @param {number} sliceIndex - The slice index to read
 * @returns {Float32Array} The slice data
 */
export function readSlice(buffer, header, sliceIndex) {
    if (sliceIndex < 0 || sliceIndex >= header.dimensions.z) {
        throw new Error(`Slice index ${sliceIndex} out of range [0, ${header.dimensions.z})`);
    }
    return readData(buffer, header, { sliceStart: sliceIndex, sliceEnd: sliceIndex + 1 });
}

/**
 * Parse a complete MRC file
 * @param {ArrayBuffer} buffer - The MRC file buffer
 * @returns {{ header: MRCHeader, data: Float32Array }}
 */
export function parseMRC(buffer) {
    const header = parseHeader(buffer);
    const data = readData(buffer, header);
    return { header, data };
}

/**
 * Fetch and parse an MRC file from a URL
 * @param {string} url - The URL to fetch from
 * @param {Object} options - Fetch options
 * @param {AbortSignal} [options.signal] - Abort signal for cancellation
 * @param {function} [options.onProgress] - Progress callback (received, total)
 * @returns {Promise<{ header: MRCHeader, data: Float32Array }>}
 */
export async function fetchMRC(url, options = {}) {
    const response = await fetch(url, { signal: options.signal });

    if (!response.ok) {
        throw new Error(`Failed to fetch MRC file: ${response.status} ${response.statusText}`);
    }

    const contentLength = response.headers.get('Content-Length');
    const total = contentLength ? parseInt(contentLength, 10) : 0;

    if (options.onProgress && total > 0) {
        const reader = response.body.getReader();
        const chunks = [];
        let received = 0;

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            chunks.push(value);
            received += value.length;
            options.onProgress(received, total);
        }

        const buffer = new ArrayBuffer(received);
        const view = new Uint8Array(buffer);
        let offset = 0;
        for (const chunk of chunks) {
            view.set(chunk, offset);
            offset += chunk.length;
        }

        return parseMRC(buffer);
    }

    const buffer = await response.arrayBuffer();
    return parseMRC(buffer);
}

/**
 * Fetch only the header of an MRC file (minimal data transfer)
 * @param {string} url - The URL to fetch from
 * @param {Object} options - Fetch options
 * @param {AbortSignal} [options.signal] - Abort signal for cancellation
 * @returns {Promise<MRCHeader>}
 */
export async function fetchMRCHeader(url, options = {}) {
    // Fetch first 1024 bytes for basic header, then extended if needed
    const response = await fetch(url, {
        signal: options.signal,
        headers: { 'Range': 'bytes=0-1023' }
    });

    if (!response.ok && response.status !== 206) {
        throw new Error(`Failed to fetch MRC header: ${response.status} ${response.statusText}`);
    }

    const buffer = await response.arrayBuffer();
    const header = parseHeader(buffer);

    // If there's an extended header, fetch it too
    if (header.extendedBytes > 0) {
        const extResponse = await fetch(url, {
            signal: options.signal,
            headers: { 'Range': `bytes=0-${1023 + header.extendedBytes}` }
        });

        if (extResponse.ok || extResponse.status === 206) {
            const fullBuffer = await extResponse.arrayBuffer();
            return parseHeader(fullBuffer);
        }
    }

    return header;
}

/**
 * Fetch a single slice from a remote MRC file using Range requests
 * @param {string} url - The URL to fetch from
 * @param {MRCHeader} header - Previously fetched header
 * @param {number} sliceIndex - The slice to fetch (0-indexed)
 * @param {Object} [options] - Fetch options
 * @param {AbortSignal} [options.signal] - Abort signal for cancellation
 * @returns {Promise<Float32Array>} The decoded slice data
 */
export async function fetchSlice(url, header, sliceIndex, options = {}) {
    if (sliceIndex < 0 || sliceIndex >= header.dimensions.z) {
        throw new Error(`Slice index ${sliceIndex} out of range [0, ${header.dimensions.z})`);
    }

    const rangeStart = header.sliceByteOffset(sliceIndex);
    const rangeEnd = rangeStart + header.sliceByteSize - 1;

    const response = await fetch(url, {
        signal: options.signal,
        headers: { 'Range': `bytes=${rangeStart}-${rangeEnd}` }
    });

    if (!response.ok && response.status !== 206) {
        throw new Error(`Failed to fetch slice ${sliceIndex}: ${response.status} ${response.statusText}`);
    }

    const buffer = await response.arrayBuffer();
    return decodeVoxelData(buffer, header.mode, header.sliceVoxelCount);
}

/**
 * Fetch a range of slices from a remote MRC file using a single Range request
 * @param {string} url - The URL to fetch from
 * @param {MRCHeader} header - Previously fetched header
 * @param {number} sliceStart - First slice to fetch (0-indexed, inclusive)
 * @param {number} sliceEnd - Last slice to fetch (exclusive)
 * @param {Object} [options] - Fetch options
 * @param {AbortSignal} [options.signal] - Abort signal for cancellation
 * @returns {Promise<Float32Array>} The decoded data for all requested slices
 */
export async function fetchSlices(url, header, sliceStart, sliceEnd, options = {}) {
    if (sliceStart < 0 || sliceEnd > header.dimensions.z || sliceStart >= sliceEnd) {
        throw new Error(`Slice range [${sliceStart}, ${sliceEnd}) out of bounds [0, ${header.dimensions.z})`);
    }

    const rangeStart = header.sliceByteOffset(sliceStart);
    const rangeEnd = header.sliceByteOffset(sliceEnd) - 1;

    const response = await fetch(url, {
        signal: options.signal,
        headers: { 'Range': `bytes=${rangeStart}-${rangeEnd}` }
    });

    if (!response.ok && response.status !== 206) {
        throw new Error(`Failed to fetch slices [${sliceStart}, ${sliceEnd}): ${response.status} ${response.statusText}`);
    }

    const sliceCount = sliceEnd - sliceStart;
    const totalVoxels = header.sliceVoxelCount * sliceCount;
    const buffer = await response.arrayBuffer();
    return decodeVoxelData(buffer, header.mode, totalVoxels);
}