import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Framework/Components/HeroCarousel.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;

// Only the stored preferences are exercised; the DOM part needs a browser.
function carousel(stored) {
    const exports = {};
    const written = [];
    runInNewContext(script, {
        exports, document: { addEventListener() {}, dispatchEvent: event => written.push(event.detail) }, CustomEvent: class { constructor(type, init) { this.detail = init.detail; } },
        localStorage: { getItem: () => stored, setItem: (key, value) => written.push(value) }, Map
    });
    return { exports, written };
}

test('preferences fall back to the defaults for anything stored wrongly', () => {
    for (const stored of [null, 'not json', '5', '{"perType":9,"max":-1,"withContinue":"yes","auto":1}']) {
        assert.deepEqual({ ...carousel(stored).exports.readHeroPreferences('key') }, { perType: 2, max: 19, withContinue: true, auto: true, paused: false }, String(stored));
    }
});

test('valid stored preferences are kept and a write announces the change', () => {
    const { exports, written } = carousel('{"perType":3,"max":0,"withContinue":false,"auto":false,"paused":true}');
    const preferences = exports.readHeroPreferences('key');
    assert.deepEqual({ ...preferences }, { perType: 3, max: 0, withContinue: false, auto: false, paused: true });
    exports.writeHeroPreferences('key', preferences);
    assert.equal(written.length, 2);
    assert.equal(written[1], 'key');
});
