import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

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

const entry = text => ({ dataset: { menuText: text }, hidden: false });
const group = (text, entries) => ({ dataset: { menuText: text }, hidden: false, querySelectorAll: () => entries });

test('the menu keeps an entry by its name or pages and a group with no entry left goes', () => {
    const jobs = entry('Jobs Jobs Transcoding');
    const media = entry('Media Works Matches');
    const operations = group('Operations', [jobs]);
    const content = group('Content', [media]);
    const menu = { querySelectorAll: () => [operations, content] };

    assert.equal(exports.applyMenuFilter(menu, 'transcod'), 1);
    assert.deepEqual([jobs.hidden, media.hidden, operations.hidden, content.hidden], [false, true, false, true]);

    // A group's own name shows all of its entries.
    assert.equal(exports.applyMenuFilter(menu, 'content'), 1);
    assert.deepEqual([jobs.hidden, media.hidden, operations.hidden, content.hidden], [true, false, true, false]);

    assert.equal(exports.applyMenuFilter(menu, 'nothing'), 0);
    assert.deepEqual([operations.hidden, content.hidden], [true, true]);

    assert.equal(exports.applyMenuFilter(menu, ''), 2);
    assert.deepEqual([jobs.hidden, media.hidden, operations.hidden, content.hidden], [false, false, false, false]);
});
