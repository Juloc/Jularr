import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Pages/Admin/Wanted/Wanted.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const { summarize, countText, applySelection } = (() => {
    const exports = {};
    runInNewContext(script, { exports });
    return exports;
})();

function bar() {
    const all = { checked: false, indeterminate: false };
    const count = { textContent: '', hidden: true, dataset: { template: '{0} selected' } };
    const priorities = [{ disabled: false }, { disabled: false }];
    const label = { textContent: 'Start search' };
    const search = { dataset: { labelAll: 'Search all due', labelSelected: 'Search selected' }, querySelector: () => label };
    const parts = { '[data-wanted-all]': all, '[data-wanted-count]': count, '[data-wanted-search]': search };
    return { all, count, priorities, label, form: { querySelector: selector => parts[selector] ?? null, querySelectorAll: () => priorities } };
}

test('the summary tells how many rows are chosen and whether it is all or some', () => {
    assert.deepEqual({ ...summarize([]) }, { count: 0, all: false, some: false });
    assert.deepEqual({ ...summarize([{ checked: true }, { checked: false }]) }, { count: 1, all: false, some: true });
    assert.deepEqual({ ...summarize([{ checked: true }, { checked: true }]) }, { count: 2, all: true, some: false });
    assert.equal(countText('{0} selected', 3), '3 selected');
});

test('priorities are offered only for chosen rows and the search names what it will search', () => {
    const view = bar();
    applySelection(view.form, [{ checked: false }, { checked: false }]);
    assert.equal(view.count.hidden, true);
    assert.deepEqual(view.priorities.map(button => button.disabled), [true, true]);
    assert.equal(view.label.textContent, 'Search all due');

    applySelection(view.form, [{ checked: true }, { checked: false }]);
    assert.equal(view.count.hidden, false);
    assert.equal(view.count.textContent, '1 selected');
    assert.equal(view.all.indeterminate, true);
    assert.deepEqual(view.priorities.map(button => button.disabled), [false, false]);
    assert.equal(view.label.textContent, 'Search selected');

    applySelection(view.form, [{ checked: true }, { checked: true }]);
    assert.equal(view.all.checked, true);
    assert.equal(view.all.indeterminate, false);
});
