import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const parser = await readFile(new URL('../../Refund/wwwroot/js/mrc-parser.js', import.meta.url), 'utf8');
const parserUrl = `data:text/javascript;base64,${Buffer.from(parser).toString('base64')}`;
const source = (await readFile(new URL('../../Refund/wwwroot/js/micrograph-worker.js', import.meta.url), 'utf8'))
    .replace("'./mrc-parser.js'", JSON.stringify(parserUrl));
const worker = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('STAR coordinates preserve absolute/normalized conventions and empty loops', () => {
    const prefix = 'data_optics\nloop_\n_rlnOpticsGroup #1\n1\ndata_particles\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n';
    assert.deepEqual(worker.parseParticles(prefix+'0.5 0.25\n', 9, 5), [{x:4,y:3}]);
    assert.deepEqual(worker.parseParticles(prefix+'2 3 # an inline comment\n', 9, 5), [{x:2,y:1}]);
    assert.deepEqual(worker.parseParticles(prefix, 9, 5), []);
});
test('contrast and masks flip Y together and handle constant data', () => {
    const values = new Float32Array([0,0,1,1]);
    const gray = worker.pixels(values, 2, 2);
    assert.ok(gray[0] > gray[8]);
    const mask = worker.pixels(values, 2, 2, true);
    assert.equal(mask[3],51); assert.equal(mask[11],0);
    assert.deepEqual([...worker.pixels(new Float32Array([2]),1,1)], [0,0,0,255]);
});
test('mipmaps bin 2x2 blocks and terminate for narrow odd images', () => {
    const binned = worker.bin2(new Float32Array([1,2,3,4,5,6,7,8]),4,2);
    assert.deepEqual([...binned.data], [3.5,5.5]);
    const narrow = worker.bin2(new Float32Array(513),1,513);
    assert.equal(narrow.width,1);assert.equal(narrow.height,256);
});
