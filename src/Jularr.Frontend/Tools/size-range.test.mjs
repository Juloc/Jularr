import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Framework/Components/SizeRange.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports, Intl, Math, Number });

test('a size thumb stays between its neighbours', () => {
    assert.deepEqual([...exports.clampThumb([20, 10, 400], 1)], [20, 20, 400]);
    assert.deepEqual([...exports.clampThumb([500, 100, 400], 0)], [100, 100, 400]);
    assert.deepEqual([...exports.clampThumb([10, 50, 5], 2)], [10, 50, 50]);
    assert.deepEqual([...exports.clampThumb([10, 400], 1)], [10, 400]);
});

test('the top of the track reads as no limit, other sizes with one decimal', () => {
    assert.equal(exports.sizeText(1000, 1000, 'Unlimited', 'en'), 'Unlimited');
    assert.equal(exports.sizeText(17.25, 1000, 'Unlimited', 'en'), '17.3');
    assert.equal(exports.sizeText(0, 1000, 'Unlimited', 'de'), '0');
});

test('two thumbs that meet at the end of the track leave the lower one on top', () => {
    assert.deepEqual([...exports.thumbLayers([0, 1000, 1000], 1000)], [1, 2, 1]);
    assert.deepEqual([...exports.thumbLayers([0, 0, 1000], 1000)], [1, 2, 1]);
});
