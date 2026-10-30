import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Framework/Components/ActionDock.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports, Array });

test('the bulk zone shows with the count while rows are chosen and gives way to the actions otherwise', () => {
    const attributes = new Set();
    const bulk = { hidden: true };
    const label = { dataset: { format: '{0} selected' }, textContent: '' };
    const dock = {
        toggleAttribute: (name, on) => (on ? attributes.add(name) : attributes.delete(name)),
        querySelector: selector => (selector === '.action-dock__bulk' ? bulk : selector === '[data-dock-count]' ? label : null)
    };

    exports.update(dock, [{ checked: true }, { checked: false }, { checked: true }]);
    assert.ok(attributes.has('data-selecting'));
    assert.equal(bulk.hidden, false);
    assert.equal(label.textContent, '2 selected');

    exports.update(dock, [{ checked: false }]);
    assert.ok(!attributes.has('data-selecting'));
    assert.equal(bulk.hidden, true);
});

test('the select-all box follows the rows: all, some or none chosen', () => {
    assert.deepEqual({ ...exports.allState([{ checked: true }, { checked: true }]) }, { checked: true, indeterminate: false });
    assert.deepEqual({ ...exports.allState([{ checked: true }, { checked: false }]) }, { checked: false, indeterminate: true });
    assert.deepEqual({ ...exports.allState([{ checked: false }]) }, { checked: false, indeterminate: false });
    assert.deepEqual({ ...exports.allState([]) }, { checked: false, indeterminate: false });

    const all = { checked: false, indeterminate: false };
    exports.update(null, [{ checked: true }, { checked: false }], all);
    assert.deepEqual([all.checked, all.indeterminate], [false, true]);
});

test('shift and a click choose every row between the two boxes, in either direction', () => {
    assert.deepEqual([...exports.rangeBetween(1, 4)], [1, 2, 3, 4]);
    assert.deepEqual([...exports.rangeBetween(4, 1)], [1, 2, 3, 4]);
    assert.deepEqual([...exports.rangeBetween(2, 2)], [2]);
    assert.deepEqual([...exports.rangeBetween(-1, 2)], []);
});
