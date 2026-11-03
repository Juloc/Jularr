import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Pages/User/Discover/FilterRow.ts', import.meta.url), 'utf8');
const exports = {};
runInNewContext(ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText, { exports });
const { snapshot } = exports;

const box = (value, checked) => ({ name: 'genre', value, type: 'checkbox', checked });

test('a popup applies only when its choice changed', () => {
    const before = snapshot([box('Action', true), box('Drama', false)]);
    assert.equal(before, 'genre=Action');
    assert.equal(snapshot([box('Action', true), box('Drama', false)]), before);
    assert.notEqual(snapshot([box('Action', true), box('Drama', true)]), before);
    assert.notEqual(snapshot([box('Action', false), box('Drama', false)]), before);
});

test('empty fields and nameless controls do not count', () => {
    assert.equal(snapshot([{ name: 'yearFrom', value: '', type: 'number', checked: false }, { name: '', value: 'x', type: 'text', checked: false }]), '');
    assert.equal(snapshot([{ name: 'yearFrom', value: '1990', type: 'number', checked: false }]), 'yearFrom=1990');
});
