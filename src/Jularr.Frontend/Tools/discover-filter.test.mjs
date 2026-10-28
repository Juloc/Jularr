import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Pages/User/Discover/Feeds.ts', import.meta.url), 'utf8');
const exports = {};
runInNewContext(ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText, { exports });
const { shownFeeds, showsFeedGroup } = exports;

const everyType = ['movies', 'series', 'anime', 'books', 'music', 'games'];
// All and Continue belong to every type; the rankings to the types that have them.
const feeds = [undefined, ['movies', 'series', 'anime'], ['movies'], ['books', 'anime']];

test('no chosen type means every type, so every ranking stays', () => {
    assert.deepEqual(shownFeeds(feeds, [], everyType), [true, true, true, true]);
});

test('a ranking leaves the filter when none of the chosen types has it', () => {
    assert.deepEqual(shownFeeds(feeds, ['books'], everyType), [true, false, false, true]);
    assert.deepEqual(shownFeeds(feeds, ['series'], everyType), [true, true, false, false]);
    assert.deepEqual(shownFeeds(feeds, ['movies', 'books'], everyType), [true, true, true, true]);
});

test('a type without rankings leaves only all, and the group goes', () => {
    const music = shownFeeds(feeds, ['music'], everyType);
    assert.deepEqual(music, [true, false, false, false]);
    assert.equal(showsFeedGroup(music), false);
    assert.equal(showsFeedGroup(shownFeeds(feeds, ['movies'], everyType)), true);
});

test('Continue keeps the group even for a type without rankings', () => {
    const withContinue = [undefined, ['movies'], undefined];
    assert.equal(showsFeedGroup(shownFeeds(withContinue, ['games'], everyType)), true);
});
