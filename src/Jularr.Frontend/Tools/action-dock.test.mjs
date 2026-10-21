import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

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
