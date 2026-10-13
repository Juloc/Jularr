import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../Modules/Tracking/Progress.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const settle = () => new Promise(resolve => setImmediate(resolve));

function transport() {
    const requests = [], timers = new Map(), feedback = [];
    const exports = {};
    let timer = 0;
    runInNewContext(script, {
        exports, AbortController,
        fetch: (_, options) => new Promise((resolve, reject) => requests.push({ options, resolve, reject })),
        setTimeout: action => { timers.set(++timer, action); return timer; },
        clearTimeout: id => timers.delete(id)
    });
    return {
        save: exports.progressReporter('/progress', 'test', failed => feedback.push(failed)), requests, feedback,
        retry() { const actions = [...timers.values()]; timers.clear(); actions.forEach(action => action()); },
        async answer(index, status) { requests[index].resolve({ ok: status === 200, status }); await settle(); }
    };
}

test('progress confirms success and serializes the latest pending position', async () => {
    const ui = transport();
    ui.save({ positionMs: 1 });
    ui.save({ positionMs: 2 });
    ui.save({ positionMs: 3 });
    assert.equal(ui.requests.length, 1);
    await ui.answer(0, 200);
    assert.equal(ui.requests.length, 2);
    assert.equal(JSON.parse(ui.requests[1].options.body).positionMs, 3);
    await ui.answer(1, 200);
    ui.save({ positionMs: 3 });
    assert.equal(ui.requests.length, 2);
});

test('failed progress retries twice, remains unsaved and can be sent again', async () => {
    const ui = transport();
    ui.save({ positionMs: 1 });
    for (let index = 0; index < 3; index++) {
        await ui.answer(index, 503);
        ui.retry();
    }
    assert.equal(ui.requests.length, 3);
    assert.equal(ui.feedback.at(-1), true);
    ui.save({ positionMs: 1 });
    assert.equal(ui.requests.length, 4);
    await ui.answer(3, 200);
    assert.equal(ui.feedback.at(-1), false);
});

test('permission failures and final keepalive reports do not start retry loops', async () => {
    const ui = transport();
    ui.save({ positionMs: 1 });
    await ui.answer(0, 403);
    ui.retry();
    assert.equal(ui.requests.length, 1);
    ui.save({ positionMs: 2, isEnded: true }, true);
    assert.equal(ui.requests[1].options.keepalive, true);
    await ui.answer(1, 503);
    ui.retry();
    assert.equal(ui.requests.length, 2);
});
