import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Pages/Admin/Queue/ManualImport.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports, Array, Event: class { constructor(type) { this.type = type; } } });

test('one switch sets every file and announces only the boxes that changed', () => {
    const heard = [];
    const box = checked => ({ checked, dispatchEvent: event => heard.push(event.type) });
    const boxes = [box(true), box(false), box(false)];

    assert.equal(exports.chooseAll(boxes, true), 2);
    assert.ok(boxes.every(item => item.checked));
    assert.deepEqual(heard, ['change', 'change']);

    assert.equal(exports.chooseAll(boxes, false), 3);
    assert.ok(boxes.every(item => !item.checked));
    assert.equal(exports.chooseAll(boxes, false), 0);
});

test('a disabled box keeps its state when every file is chosen', () => {
    const boxes = [{ checked: false, disabled: true, dispatchEvent: () => true }, { checked: false, dispatchEvent: () => true }];
    assert.equal(exports.chooseAll(boxes, true), 1);
    assert.deepEqual(boxes.map(box => box.checked), [false, true]);
});

test('the run counts what went in, what failed and what was left out', () => {
    assert.deepEqual({ ...exports.tally(['imported', 'failed', 'imported'], 5) }, { imported: 2, failed: 1, left: 2 });
    assert.equal(exports.fill('Importing {0} of {1}', 3, 12), 'Importing 3 of 12');
});
