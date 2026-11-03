import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Framework/Components/LiveSearch.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports, URL });

const base = 'https://arr.example/Admin/Works?q=old';

test('a form address carries its values and leaves the empty ones out', () => {
    const values = new Map([['q', ' naruto '], ['status', '2'], ['type', 'movies,series'], ['library', '']]);
    assert.equal(exports.formAddress('/Admin/Works', values, base), '/Admin/Works?q=naruto&status=2&type=movies,series');
    assert.equal(exports.formAddress('/Library', new Map([['q', '  ']]), base), '/Library');
});

test('a link takes the words typed last, and loses its query when they are gone', () => {
    assert.deepEqual({ ...exports.withQuery('/Admin/Works?status=2', 'yoga', base) }, { href: '/Admin/Works?status=2&q=yoga', changed: true });
    assert.deepEqual({ ...exports.withQuery('/Admin/Works?q=old&sort=2', '', base) }, { href: '/Admin/Works?sort=2', changed: true });
    assert.deepEqual({ ...exports.withQuery('/?type=movies,anime&q=naruto', 'naruto ', base) }, { href: '/?type=movies,anime&q=naruto', changed: false });
    assert.equal(exports.withQuery('/?type=movies,anime', 'one piece', base).href, '/?type=movies,anime&q=one+piece');
});
