import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import { webcrypto } from 'node:crypto';
import ts from 'typescript';

// Exercise only the pure formatting / identifier utilities; onPage is a browser lifecycle hook.
const source = readFileSync(new URL('../Pages/User/Companion/Companion.entry.ts', import.meta.url), 'utf8')
    .replace(/onPage\(import\.meta\.url, setup\);/, '');
const javascript = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
const module = { exports: {} };
runInNewContext(javascript, { exports: module.exports, module, crypto: webcrypto });
const { formatTime, newCommandId } = module.exports;

test('companion playback time is in elapsed milliseconds', () => {
    assert.equal(formatTime(0), '0:00');
    assert.equal(formatTime(61_000), '1:01');
    assert.equal(formatTime(3_723_000), '1:02:03');
    assert.equal(formatTime(-5), '0:00');
});

test('command ids are random UUIDs for idempotent requests', () => {
    const ids = Array.from({ length: 40 }, () => newCommandId());
    assert.equal(new Set(ids).size, 40);
    for (const id of ids) assert.match(id, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
});

test('browser remote uses only authenticated V1 services and waits for TV acknowledgement', () => {
    assert.doesNotMatch(source, /\/api\/client\/v1|signalr/i);
    assert.match(source, /\/api\/v1\/playback\/companion\/claims/);
    assert.match(source, /next\.lastCommandId === pending\.id/);
    assert.match(source, /next\.lastCommandSucceeded === true/);
    assert.match(source, /history\.replaceState/);
});
