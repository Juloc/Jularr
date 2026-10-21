import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Pages/Admin/ManualImport/ManualImport.ts', import.meta.url), 'utf8');
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
