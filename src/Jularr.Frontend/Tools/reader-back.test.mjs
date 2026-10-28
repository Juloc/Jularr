import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Reader/BackTarget.ts', import.meta.url), 'utf8');
const exports = {};
runInNewContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText, { exports, URL });
const origin = 'http://jularr.local';

test('With the navigation history known, Back goes to the previous page of this app and nowhere else', () => {
    assert.equal(exports.leavesByHistory({ previous: 'http://jularr.local/Library', length: 3, referrer: '', origin }), true);
    assert.equal(exports.leavesByHistory({ previous: 'https://example.com/', length: 3, referrer: '', origin }), false);
    assert.equal(exports.leavesByHistory({ previous: null, length: 1, referrer: '', origin }), false);
});

test('Without it, a book opened from a page of this app or from nothing known leaves one step, an outside referrer or a single entry does not', () => {
    assert.equal(exports.leavesByHistory({ previous: undefined, length: 4, referrer: 'http://jularr.local/Details/9', origin }), true);
    assert.equal(exports.leavesByHistory({ previous: undefined, length: 4, referrer: '', origin }), true);
    assert.equal(exports.leavesByHistory({ previous: undefined, length: 4, referrer: 'https://example.com/', origin }), false);
    assert.equal(exports.leavesByHistory({ previous: undefined, length: 1, referrer: '', origin }), false);
});
