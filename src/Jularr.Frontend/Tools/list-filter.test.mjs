import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/ListFilter.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports });

const row = text => ({ dataset: { listText: text }, hidden: false });

test('every word of the query must occur, in any order and case', () => {
    assert.ok(exports.matches('plex sync providers', 'SYNC plex'));
    assert.ok(exports.matches('plex sync providers', ''));
    assert.ok(!exports.matches('plex sync providers', 'plex emby'));
});

test('rows that do not match are hidden and the empty state shows when none is left', () => {
    const rows = [row('plex sync'), row('backups')];
    const empty = { hidden: true };
    const list = { querySelectorAll: () => rows, querySelector: () => empty };

    assert.equal(exports.applyFilter(list, 'plex'), 1);
    assert.deepEqual(rows.map(item => item.hidden), [false, true]);
    assert.equal(empty.hidden, true);

    assert.equal(exports.applyFilter(list, 'nothing'), 0);
    assert.equal(empty.hidden, false);

    assert.equal(exports.applyFilter(list, ''), 2);
    assert.deepEqual(rows.map(item => item.hidden), [false, false]);
});
