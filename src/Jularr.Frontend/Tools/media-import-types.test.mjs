import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Pages/Admin/MediaImport/MediaImport.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const { isShown, typesOf } = (() => {
    const exports = {};
    runInNewContext(script, { exports, URLSearchParams });
    return exports;
})();

test('a bar link names the types it chooses, none for all of them', () => {
    assert.deepEqual([...typesOf('http://localhost/Admin/Import#type=manga,music')], ['manga', 'music']);
    assert.deepEqual([...typesOf('http://localhost/Admin/Import#type=')], []);
    assert.deepEqual([...typesOf('http://localhost/Admin/Import')], []);
});

test('a row shows when no type is chosen, its type is chosen or it has no known type', () => {
    assert.equal(isShown([], 'movies'), true);
    assert.equal(isShown(['music'], 'music'), true);
    assert.equal(isShown(['music'], 'movies'), false);
    assert.equal(isShown(['music'], undefined), true);
});
