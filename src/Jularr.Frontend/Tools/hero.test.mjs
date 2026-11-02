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
    for (const stored of [null, 'not json', '5', '{"perType":9,"withContinue":"yes","auto":1}', '{"perType":0}']) {
        assert.deepEqual({ ...carousel(stored).exports.readHeroPreferences('key') }, { perType: 3, withContinue: true, auto: true, paused: false }, String(stored));
    }
});

test('valid stored preferences are kept and a write announces the change', () => {
    const { exports, written } = carousel('{"perType":5,"max":8,"withContinue":false,"auto":false,"paused":true}');
    const preferences = exports.readHeroPreferences('key');
    assert.deepEqual({ ...preferences }, { perType: 5, withContinue: false, auto: false, paused: true });
    exports.writeHeroPreferences('key', preferences);
    assert.equal(written.length, 2);
    assert.equal(written[1], 'key');
});

test('each type shows its first places, one place after the other across the types', () => {
    const { heroOrder } = carousel(null).exports;
    // Films: continued, two featured; series: one featured, one added lately.
    const slides = [
        { group: 'movies', isContinue: true }, { group: 'series', isContinue: false },
        { group: 'movies', isContinue: false }, { group: 'series', isContinue: false },
        { group: 'movies', isContinue: false }
    ];
    const preferences = { perType: 2, withContinue: true, auto: true, paused: false };
    assert.deepEqual([...heroOrder(slides, preferences)], [0, 1, 2, 3]);

    // Without continued titles the films' next title takes the place, and the films keep their turn before the series.
    assert.deepEqual([...heroOrder(slides, { ...preferences, withContinue: false })], [2, 1, 4, 3]);

    // A type with fewer titles simply shows fewer; nothing chosen leaves the list empty.
    assert.deepEqual([...heroOrder(slides, { ...preferences, perType: 5 })], [0, 1, 2, 3, 4]);
    assert.deepEqual([...heroOrder([{ group: 'movies', isContinue: true }], { ...preferences, withContinue: false })], []);
});
