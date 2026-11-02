import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Framework/Components/Truncated.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports });

const window = { width: 1280, height: 800 };
const tip = { width: 320, height: 52 };

test('the tip sits under the text, level with its left edge', () => {
    const spot = exports.tipPosition({ left: 300, top: 400, right: 500, bottom: 420 }, tip, window);
    assert.deepEqual({ ...spot }, { left: 300, top: 424 });
});

test('the tip stays inside the window on the right', () => {
    const spot = exports.tipPosition({ left: 1200, top: 100, right: 1270, bottom: 120 }, tip, window);
    assert.equal(spot.left, 1280 - 320 - 8);
    assert.equal(spot.top, 124);
});

test('the tip goes above the text when there is no room below', () => {
    const spot = exports.tipPosition({ left: 100, top: 760, right: 300, bottom: 780 }, tip, window);
    assert.equal(spot.top, 760 - 4 - 52);
});

test('the tip never leaves the window on the left or the top', () => {
    const spot = exports.tipPosition({ left: -40, top: 2, right: 100, bottom: 790 }, tip, window);
    assert.equal(spot.left, 8);
    assert.equal(spot.top, 8);
});
