import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Reader/Translation/CodexChapter.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports });

const answer = (blocks, failed = [], remaining = 0, remainingCharacters = 0, characters = 100, seconds = 10) =>
    ({ ok: true, json: async () => ({ blocks: blocks.map(index => ({ index, text: 'T' + index })), failed, characters, seconds, remaining, remainingCharacters }) });
const refusal = code => ({ ok: false, text: async () => '{"code":"' + code + '"}' });

// A run over scripted answers; it records what it asked and what arrived.
function run(answers, options = {}) {
    const asked = [];
    const arrived = [];
    const progress = [];
    const pauses = [];
    const queue = [...answers];
    const result = exports.translateChapter({
        from: options.from ?? 0,
        post: async (from, skip) => { asked.push({ from, skip: [...skip] }); const next = queue.shift(); if (next instanceof Error) throw next; return next; },
        arrived: blocks => arrived.push(...blocks.map(block => block.index)),
        progress: state => progress.push(state),
        stopped: () => options.stopped?.(arrived.length) ?? false,
        pause: async ms => { pauses.push(ms); }
    });
    return result.then(value => ({ value, asked, arrived, progress, pauses }));
}

test('a chapter goes in several requests, each continuing after the last, until nothing remains', async () => {
    const { value, asked, arrived } = await run([answer([5, 6], [], 4, 400), answer([7, 8, 0], [], 1, 100), answer([1], [], 0, 0)], { from: 5 });
    assert.equal(value.state, 'done');
    assert.equal(value.translated, 6);
    assert.deepEqual(asked.map(item => item.from), [5, 7, 9]);
    assert.deepEqual(arrived, [5, 6, 7, 8, 0, 1]);
});

test('a failed request keeps what the others brought, and asking again leaves out nothing translated', async () => {
    const first = await run([answer([0, 1], [], 2, 200), refusal('translation_request_failed')]);
    assert.equal(first.value.state, 'error');
    assert.equal(first.value.problem, 'failed');
    assert.deepEqual(first.arrived, [0, 1]);
    // The retry is a new run from the place in view; the server leaves out what is stored.
    const again = await run([answer([2, 3], [], 0, 0)], { from: 2 });
    assert.equal(again.value.state, 'done');
});

test('a block whose answer failed the checks is left out of the later requests', async () => {
    const { value, asked } = await run([answer([0, 2], [1], 3, 300), answer([3, 4, 5], [], 0, 0)]);
    assert.deepEqual(asked[1].skip, [1]);
    assert.deepEqual([...value.failed], [1]);
    assert.equal(value.state, 'done');
});

test('the cause of a failure is named: limit, not connected, busy, anything else', async () => {
    for (const [code, problem] of [['translation_limit_reached', 'limit'], ['translation_not_connected', 'notConnected'], ['translation_local_busy', 'busy'], ['translation_request_failed', 'failed']]) {
        const { value } = await run(Array(20).fill(0).map(() => refusal(code)));
        assert.equal(value.problem, problem, code);
        assert.equal(value.state, 'error');
    }
});

test('a busy server is asked again after a pause, and the chapter goes on', async () => {
    const { value, pauses, progress } = await run([refusal('translation_local_busy'), refusal('translation_local_busy'), answer([0], [], 0, 0)]);
    assert.equal(value.state, 'done');
    assert.deepEqual(pauses, [4000, 4000]);
    assert.equal(progress.filter(state => state.busy).length, 2);
});

test('a network failure is a failed request', async () => {
    const { value } = await run([new Error('offline')]);
    assert.equal(value.state, 'error');
    assert.equal(value.problem, 'failed');
});

test('stopping ends the run between requests', async () => {
    const { value, asked } = await run([answer([0], [], 5, 500), answer([1], [], 4, 400)], { stopped: arrived => arrived >= 1 });
    assert.equal(value.state, 'stopped');
    assert.equal(asked.length, 1);
});

test('the time left comes from the last three requests and rounds to minutes', () => {
    assert.equal(exports.secondsLeft([], 500), undefined);
    assert.equal(exports.secondsLeft([{ characters: 100, seconds: 10 }], 1200), 120);
    // Only the last three count: the slow first request drops out.
    assert.equal(exports.secondsLeft([{ characters: 100, seconds: 100 }, ...Array(3).fill({ characters: 100, seconds: 10 })], 100), 10);
    assert.equal(exports.minutesLeft(20), 0);
    assert.equal(exports.minutesLeft(50), 1);
    assert.equal(exports.minutesLeft(130), 2);
});
