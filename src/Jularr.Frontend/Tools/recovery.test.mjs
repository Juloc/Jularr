import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Player/Recovery.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports });

test('A new session waits one second, then twice as long, never more than fifteen', () => {
    assert.deepEqual([0, 1, 2, 3, 4, 5, 6].map(exports.replanDelay), [1_000, 2_000, 4_000, 8_000, 15_000, 15_000, 15_000]);
    assert.equal(exports.replanDelay(-1), 1_000);
});

test('The player plans again several times before it gives up', () => {
    assert.equal(exports.ReplanAttempts, 6);
});

test('A picture that plays on for twenty seconds after a repair is healed', () => {
    assert.equal(exports.healed(100, 119.9), false);
    assert.equal(exports.healed(100, 120), true);
});
