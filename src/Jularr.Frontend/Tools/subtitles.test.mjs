import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Player/Subtitles.ts', import.meta.url), 'utf8');
const exports = {};
runInNewContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText, { exports });

const options = [
    { key: 's4', language: 'de', forced: false },
    { key: 's5', language: 'en', forced: false },
    { key: 's6', language: 'de', forced: true },
    { key: 's7', language: 'en', forced: true }
];

test('The second subtitle is the profile language, a full track before a forced one', () => {
    assert.equal(exports.pickSecondSubtitle(options, 'de', 's5', 'en').key, 's4');
    assert.equal(exports.pickSecondSubtitle([options[2], options[0]], 'de', 's5', 'en').key, 's4');
    assert.equal(exports.pickSecondSubtitle([options[2]], 'de', 's5', 'en').key, 's6');
});

test('There is no second subtitle without a language, in the first one\'s language, or when only the first track has it', () => {
    assert.equal(exports.pickSecondSubtitle(options, undefined, 's5', 'en'), undefined);
    assert.equal(exports.pickSecondSubtitle(options, '', 's5', 'en'), undefined);
    assert.equal(exports.pickSecondSubtitle(options, 'en', 's5', 'en'), undefined);
    assert.equal(exports.pickSecondSubtitle(options, 'de', 's4', 'de'), undefined);
    assert.equal(exports.pickSecondSubtitle([options[1]], 'en', 's5', 'de'), undefined);
    assert.equal(exports.SecondSubtitleLine, -4);
});
