// Baseline TIFF images with one sample per pixel, as Warp writes masks and other single images: 8- to 64-bit integer
// or IEEE float samples, in strips or tiles, uncompressed or LZW, Deflate or PackBits compressed (horizontal predictor
// allowed). Rows come back reversed, as Warp reads TIFFs: Warp writes them bottom row first (tagged as such) to keep
// MRC's orientation, so reversed rows line up with MRC planes, and with particle coordinates on them.

/** True when the bytes start with a classic or big TIFF signature. */
export function isTiff(bytes) {
    if (bytes.length < 4) return false;
    const little = bytes[0] === 0x49 && bytes[1] === 0x49, big = bytes[0] === 0x4d && bytes[1] === 0x4d;
    if (!little && !big) return false;
    const magic = little ? bytes[2] | bytes[3] << 8 : bytes[2] << 8 | bytes[3];
    return magic === 42 || magic === 43;
}

const TypeSizes = { 1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8, 16: 8, 17: 8, 18: 8 };

function readValue(view, type, offset, little) {
    switch (type) {
        case 1: case 2: case 7: return view.getUint8(offset);
        case 6: return view.getInt8(offset);
        case 3: return view.getUint16(offset, little);
        case 8: return view.getInt16(offset, little);
        case 4: case 13: return view.getUint32(offset, little);
        case 9: return view.getInt32(offset, little);
        case 5: return view.getUint32(offset, little) / (view.getUint32(offset + 4, little) || 1);
        case 10: return view.getInt32(offset, little) / (view.getInt32(offset + 4, little) || 1);
        case 11: return view.getFloat32(offset, little);
        case 12: return view.getFloat64(offset, little);
        case 16: case 18: return Number(view.getBigUint64(offset, little));
        case 17: return Number(view.getBigInt64(offset, little));
        default: throw new Error(`Unsupported TIFF field type ${type}`);
    }
}

/** The tags of every image (IFD) in the file, in order: number arrays keyed by tag. */
function readDirectories(view) {
    const little = view.getUint8(0) === 0x49;
    const bigTiff = view.getUint16(2, little) === 43;
    const pointer = offset => bigTiff ? Number(view.getBigUint64(offset, little)) : view.getUint32(offset, little);
    const directories = [];
    let next = pointer(bigTiff ? 8 : 4);
    const seen = new Set();
    while (next && next < view.byteLength && !seen.has(next)) {
        seen.add(next);
        const count = bigTiff ? Number(view.getBigUint64(next, little)) : view.getUint16(next, little);
        const entrySize = bigTiff ? 20 : 12, inline = bigTiff ? 8 : 4;
        let entry = next + (bigTiff ? 8 : 2);
        const tags = new Map();
        for (let i = 0; i < count; i++, entry += entrySize) {
            const tag = view.getUint16(entry, little), type = view.getUint16(entry + 2, little);
            const n = bigTiff ? Number(view.getBigUint64(entry + 4, little)) : view.getUint32(entry + 4, little);
            const size = TypeSizes[type];
            if (!size) continue;
            const valueOffset = n * size <= inline ? entry + (bigTiff ? 12 : 8) : pointer(entry + (bigTiff ? 12 : 8));
            const values = new Array(n);
            for (let k = 0; k < n; k++) values[k] = readValue(view, type, valueOffset + k * size, little);
            tags.set(tag, values);
        }
        directories.push(tags);
        next = pointer(entry);
    }
    return { directories, little };
}

/** TIFF LZW: MSB-first codes from 9 to 12 bits, widened one code early. */
export function decodeLzw(input, expectedLength) {
    const output = new Uint8Array(expectedLength);
    const table = [];
    for (let i = 0; i < 256; i++) table.push(Uint8Array.of(i));
    table.push(null, null); // 256 clear, 257 end of information
    let width = 9, bitBuffer = 0, bitCount = 0, position = 0, written = 0, previous = null;
    const reset = () => { table.length = 258; width = 9; previous = null; };
    while (written < expectedLength) {
        while (bitCount < width) {
            if (position >= input.length) return output;
            bitBuffer = (bitBuffer << 8 | input[position++]) >>> 0; bitCount += 8;
        }
        const code = bitBuffer >>> (bitCount - width) & ((1 << width) - 1);
        bitCount -= width;
        if (code === 257) break;
        if (code === 256) { reset(); continue; }
        let entry;
        if (code < table.length && table[code]) entry = table[code];
        else if (previous) { entry = new Uint8Array(previous.length + 1); entry.set(previous); entry[previous.length] = previous[0]; }
        else throw new Error('Corrupt LZW data');
        output.set(entry.subarray(0, Math.min(entry.length, expectedLength - written)), written);
        written += entry.length;
        if (previous) {
            const added = new Uint8Array(previous.length + 1);
            added.set(previous); added[previous.length] = entry[0];
            table.push(added);
        }
        previous = entry;
        if (table.length + 1 >= 1 << width && width < 12) width++;
    }
    return output;
}

export function decodePackBits(input, expectedLength) {
    const output = new Uint8Array(expectedLength);
    let i = 0, written = 0;
    while (i < input.length && written < expectedLength) {
        const header = input[i] > 127 ? input[i] - 256 : input[i]; i++;
        if (header >= 0) { const n = header + 1; output.set(input.subarray(i, i + n).subarray(0, expectedLength - written), written); written += n; i += n; }
        else if (header > -128) { const n = 1 - header; output.fill(input[i], written, Math.min(expectedLength, written + n)); written += n; i++; }
    }
    return output;
}

async function inflate(input) {
    const stream = new Blob([input]).stream().pipeThrough(new DecompressionStream('deflate'));
    return new Uint8Array(await new Response(stream).arrayBuffer());
}

async function decompress(bytes, compression, expectedLength) {
    switch (compression) {
        case 1: return bytes;
        case 5: return decodeLzw(bytes, expectedLength);
        case 8: case 32946: return inflate(bytes);
        case 32773: return decodePackBits(bytes, expectedLength);
        default: throw new Error(`Unsupported TIFF compression ${compression}`);
    }
}

function sampleReader(view, bits, format, little) {
    if (format === 3) {
        if (bits === 32) return offset => view.getFloat32(offset, little);
        if (bits === 64) return offset => view.getFloat64(offset, little);
    } else if (format === 2) {
        if (bits === 8) return offset => view.getInt8(offset);
        if (bits === 16) return offset => view.getInt16(offset, little);
        if (bits === 32) return offset => view.getInt32(offset, little);
    } else {
        if (bits === 8) return offset => view.getUint8(offset);
        if (bits === 16) return offset => view.getUint16(offset, little);
        if (bits === 32) return offset => view.getUint32(offset, little);
    }
    throw new Error(`Unsupported TIFF samples (${bits}-bit, format ${format})`);
}

/** Undoes horizontal differencing (predictor 2) on integer samples, row by row, in place. */
function undoPredictor(bytes, width, rows, bits, little) {
    const bytesPerSample = bits / 8, view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const get = bits === 8 ? o => view.getUint8(o) : bits === 16 ? o => view.getUint16(o, little) : o => view.getUint32(o, little);
    const set = bits === 8 ? (o, v) => view.setUint8(o, v) : bits === 16 ? (o, v) => view.setUint16(o, v, little) : (o, v) => view.setUint32(o, v, little);
    const mask = bits === 32 ? 0xffffffff : (1 << bits) - 1;
    for (let y = 0; y < rows; y++)
        for (let x = 1; x < width; x++) {
            const o = (y * width + x) * bytesPerSample;
            if (o + bytesPerSample > bytes.byteLength) return;
            set(o, (get(o) + get(o - bytesPerSample)) & mask >>> 0);
        }
}

function reverseRows(data, width, height) {
    const reversed = new Float32Array(data.length);
    for (let y = 0; y < height; y++) reversed.set(data.subarray(y * width, (y + 1) * width), (height - 1 - y) * width);
    return reversed;
}

/** One image (page) of a TIFF as float samples, rows reversed as Warp reads them. */
export async function parseTiff(buffer, page = 0) {
    const view = new DataView(buffer);
    if (!isTiff(new Uint8Array(buffer, 0, Math.min(4, buffer.byteLength)))) throw new Error('Not a TIFF file');
    const { directories, little } = readDirectories(view);
    const tags = directories[page];
    if (!tags) throw new Error(`TIFF has no image ${page}`);
    const first = (tag, fallback) => tags.get(tag)?.[0] ?? fallback;
    const width = first(256), height = first(257);
    if (!(width > 0 && height > 0)) throw new Error('Invalid TIFF dimensions');
    if (first(277, 1) !== 1) throw new Error('Only single-sample TIFF images are supported');
    const bits = first(258, 1), format = first(339, 1), compression = first(259, 1), predictor = first(317, 1);
    if (predictor === 3) throw new Error('The floating-point TIFF predictor is not supported');
    const bytesPerSample = bits / 8;
    const tiled = tags.has(322);
    const blockWidth = tiled ? first(322) : width, blockHeight = tiled ? first(323) : Math.min(height, first(278, height));
    const offsets = tags.get(tiled ? 324 : 273) ?? [], counts = tags.get(tiled ? 325 : 279) ?? [];
    const across = Math.ceil(width / blockWidth);
    const data = new Float32Array(width * height);
    for (let b = 0; b < offsets.length; b++) {
        const expected = blockWidth * blockHeight * bytesPerSample;
        let block = await decompress(new Uint8Array(buffer, offsets[b], counts[b] ?? expected), compression, expected);
        if (predictor === 2) { block = block.slice(); undoPredictor(block, blockWidth, blockHeight, bits, little); }
        const blockView = new DataView(block.buffer, block.byteOffset, block.byteLength);
        const read = sampleReader(blockView, bits, format, little);
        const x0 = tiled ? (b % across) * blockWidth : 0, y0 = tiled ? Math.floor(b / across) * blockHeight : b * blockHeight;
        for (let y = 0; y < blockHeight && y0 + y < height; y++)
            for (let x = 0; x < blockWidth && x0 + x < width; x++) {
                const o = (y * blockWidth + x) * bytesPerSample;
                if (o + bytesPerSample <= block.byteLength) data[(y0 + y) * width + x0 + x] = read(o);
            }
    }
    // White-is-zero images are inverted so dark stays dark.
    if (first(262, 1) === 0 && format !== 3) { const max = 2 ** bits - 1; for (let i = 0; i < data.length; i++) data[i] = max - data[i]; }
    return { width, height, pixelSize: 1, data: reverseRows(data, width, height) };
}
