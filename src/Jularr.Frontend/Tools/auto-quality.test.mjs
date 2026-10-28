import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Player/AutoQuality.ts', import.meta.url), 'utf8');
const exports = {};
runInNewContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText, { exports });

test('Two stalls within a minute are trouble, an older one is forgotten', () => {
    let stalls = exports.recordStall([], 1_000);
    assert.equal(exports.troubled(stalls), false);
    stalls = exports.recordStall(stalls, 30_000);
    assert.equal(exports.troubled(stalls), true);
    stalls = exports.recordStall(exports.recordStall([], 1_000), 90_000);
    assert.equal(exports.troubled(stalls), false);
    assert.equal(stalls.length, 1);
});

test('Automatic steps down to High, or to the saver on a slow measured line; High to the saver; Original and the saver stay', () => {
    assert.equal(exports.nextQuality(exports.Automatic, 0), exports.High);
    assert.equal(exports.nextQuality(exports.Automatic, 9_000_000), exports.High);
    assert.equal(exports.nextQuality(exports.Automatic, 1_800_000), exports.Saver);
    assert.equal(exports.nextQuality(exports.High, 1_000_000), exports.Saver);
    assert.equal(exports.nextQuality(exports.Saver, 500_000), null);
    assert.equal(exports.nextQuality(2, 500_000), null);
});
