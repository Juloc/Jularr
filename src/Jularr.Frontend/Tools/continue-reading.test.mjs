import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Framework/Components/ContinueReadingBar.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const module = { exports: {} };
runInNewContext(script, { exports: module.exports, module });
const { isHidden, wantsBar } = module.exports;

test('a dismissed bar stays hidden until the reading position changes', () => {
    assert.equal(isHidden('12:0.5', null), false);
    assert.equal(isHidden('12:0.5', '12:0.5'), true);
    assert.equal(isHidden('12:0.6', '12:0.5'), false);
    assert.equal(isHidden('13:0.5', '12:0.5'), false);
});

test('a bar without a key is never shown', () => {
    assert.equal(isHidden(undefined, null), true);
    assert.equal(isHidden('', null), true);
});

test('the Explore feed fills the screen and never gets the bar', () => {
    assert.equal(wantsBar('/Discover/Feed'), false);
    assert.equal(wantsBar('/'), true);
    assert.equal(wantsBar('/Library'), true);
});
