import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Pages/Admin/Downloads/Downloads.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports });

test('a row moved up stands before the row above it, moved down before the one after its lower neighbour', () => {
    assert.equal(exports.beforeAfterMove(2, 5, -1), 1);
    assert.equal(exports.beforeAfterMove(1, 5, 1), 3);
    assert.equal(exports.beforeAfterMove(3, 5, 1), 5, 'the second to last row moved down goes to the end');
});

test('a row at its end does not move further', () => {
    assert.equal(exports.beforeAfterMove(0, 5, -1), null);
    assert.equal(exports.beforeAfterMove(4, 5, 1), null);
    assert.equal(exports.beforeAfterMove(-1, 5, 1), null);
});
