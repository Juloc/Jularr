import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Pages/Admin/Work/Work.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports });
const { normalizeColumns, compareValues, moveItem, listTemplates } = exports;

test('the grid of a list follows the shown columns: the medium width drops the first level, the narrow one both', () => {
    const grid = listTemplates([{ width: 'a', hide: 0 }, { width: 'b', hide: 1 }, { width: 'c', hide: 2 }, { width: 'd', hide: 0 }]);
    assert.equal(grid.wide, 'a b c d');
    assert.equal(grid.medium, 'a c d');
    assert.equal(grid.narrow, 'a d');
    assert.equal(listTemplates([]).wide, '');
});

test('a first visit shows the default columns in their order', () => {
    const state = normalizeColumns(null, ['title', 'size', 'path'], ['path']);
    assert.deepEqual([...state.order], ['title', 'size', 'path']);
    assert.deepEqual([...state.hidden], ['path']);
});

test('a stored choice keeps its order and hidden columns, drops unknown ones and appends new ones', () => {
    const state = normalizeColumns({ order: ['size', 'gone', 'title'], hidden: ['title', 'gone'] }, ['title', 'size', 'quality'], ['quality']);
    assert.deepEqual([...state.order], ['size', 'title', 'quality']);
    assert.deepEqual([...state.hidden], ['title']);
});

test('damaged storage falls back to the defaults', () => {
    const state = normalizeColumns('nonsense', ['title', 'size'], ['size']);
    assert.deepEqual([...state.order], ['title', 'size']);
    assert.deepEqual([...state.hidden], ['size']);
});

test('numbers sort as numbers and text in natural order', () => {
    assert.ok(compareValues('9', '10') < 0);
    assert.ok(compareValues('2020-03-01', '2020-12-01') < 0);
    assert.ok(compareValues('Episode 2', 'Episode 10') < 0);
    assert.equal(compareValues('a', 'A'), 0);
});

test('moving an item keeps the others in order and ignores impossible moves', () => {
    assert.deepEqual([...moveItem(['a', 'b', 'c'], 0, 2)], ['b', 'c', 'a']);
    assert.deepEqual([...moveItem(['a', 'b', 'c'], 2, 0)], ['c', 'a', 'b']);
    assert.deepEqual([...moveItem(['a', 'b', 'c'], 1, 5)], ['a', 'b', 'c']);
});
