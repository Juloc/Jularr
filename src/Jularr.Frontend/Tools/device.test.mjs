import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Player/Device.ts', import.meta.url), 'utf8');
const exports = {};
runInNewContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText, { exports });

test('A touch screen with a short side under 600 px is a phone in either orientation, a larger one a tablet, anything without touch has a pointer', () => {
    assert.equal(exports.deviceKind(true, 390, 844), 'phone');
    assert.equal(exports.deviceKind(true, 844, 390), 'phone');
    assert.equal(exports.deviceKind(true, 820, 1180), 'tablet');
    assert.equal(exports.deviceKind(true, 1180, 820), 'tablet');
    assert.equal(exports.deviceKind(false, 390, 844), 'pointer');
    assert.equal(exports.deviceKind(false, 1920, 1080), 'pointer');
});

test('libass is the device\'s own choice; without one it is on for a pointer device and off for a touch device', () => {
    assert.equal(exports.libassWanted('on', 'phone'), true);
    assert.equal(exports.libassWanted('off', 'pointer'), false);
    assert.equal(exports.libassWanted(null, 'pointer'), true);
    assert.equal(exports.libassWanted(null, 'phone'), false);
    assert.equal(exports.libassWanted(null, 'tablet'), false);
});
