import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Framework/Components/CardMenu.ts', import.meta.url), 'utf8');
const exports = {};
runInNewContext(ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText, { exports, Element: class {}, document: {}, window: {} });
const { leavesRow } = exports;
const panelPosition = (...args) => ({ ...exports.panelPosition(...args) });
const area = { left: 250, right: 1440, bottom: 900 };
const panel = { width: 220, height: 143 };

test('the panel opens under the button with its right edge on the button', () => {
    assert.deepEqual(panelPosition({ left: 700, top: 100, right: 740, bottom: 140 }, panel, area), { left: 520, top: 144 });
});

test('the panel stays right of the sidebar, inside the right edge, and above a button near the bottom', () => {
    assert.equal(panelPosition({ left: 290, top: 100, right: 330, bottom: 140 }, panel, area).left, 258);
    assert.equal(panelPosition({ left: 1400, top: 100, right: 1430, bottom: 140 }, panel, area).left, 1210);
    assert.deepEqual(panelPosition({ left: 700, top: 800, right: 740, bottom: 840 }, panel, area), { left: 520, top: 653 });
});

test('only removing and marking take the card out of its row', () => {
    assert.deepEqual(['remove', 'watched', 'unwatched', 'favorite', undefined].map(kind => leavesRow(kind)), [true, true, true, false, false]);
});
