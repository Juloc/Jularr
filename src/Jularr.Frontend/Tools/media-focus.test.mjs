import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../Modules/Playback/MediaFocus.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source.slice(0, source.indexOf('// The two shared instances')), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports, Math });

function lockEnvironment(state) {
    let requests = 0;
    return {
        requests: () => requests,
        request: async () => {
            if (state.denied) throw new Error('denied');
            requests++;
            const listeners = [];
            const lock = { released: false, release: async () => { lock.released = true; }, addEventListener: (type, listener) => listeners.push(listener) };
            state.last = lock;
            return lock;
        },
        visible: () => state.visible,
        allowed: () => state.allowed
    };
}

test('The screen lock is held while an owner asks, the page is shown and the profile allows it', async () => {
    const state = { visible: true, allowed: true };
    const environment = lockEnvironment(state);
    const awake = exports.createAwake(environment);
    awake.hold('player');
    await awake.refresh();
    assert.equal(awake.holding(), true);
    assert.equal(environment.requests(), 1);
    awake.hold('reader');
    await awake.refresh();
    assert.equal(environment.requests(), 1);
    awake.drop('player');
    await awake.refresh();
    assert.equal(awake.holding(), true);
    awake.drop('reader');
    await awake.refresh();
    assert.equal(awake.holding(), false);
    assert.equal(state.last.released, true);
});

test('A hidden page or a profile that turned it off holds nothing, and the lock returns when the page is shown again', async () => {
    const state = { visible: true, allowed: false };
    const awake = exports.createAwake(lockEnvironment(state));
    awake.hold('reader');
    await awake.refresh();
    assert.equal(awake.holding(), false);
    state.allowed = true;
    await awake.refresh();
    assert.equal(awake.holding(), true);
    state.visible = false;
    await awake.refresh();
    assert.equal(awake.holding(), false);
    state.visible = true;
    await awake.refresh();
    assert.equal(awake.holding(), true);
});

test('A browser that refuses the lock does not break the owner', async () => {
    const state = { visible: true, allowed: true, denied: true };
    const awake = exports.createAwake(lockEnvironment(state));
    awake.hold('player');
    await awake.refresh();
    assert.equal(awake.holding(), false);
});

test('A medium that starts takes the focus and the one that had it pauses, in this page and in another tab', () => {
    const paused = [];
    const focus = exports.createFocus(null, 'tab');
    focus.claim('player', () => paused.push('player'));
    focus.claim('player', () => paused.push('again'));
    assert.deepEqual(paused, []);
    focus.claim('read-aloud', () => paused.push('read-aloud'));
    assert.deepEqual(paused, ['again']);
    focus.release('player');
    assert.equal(focus.owner(), 'read-aloud');
    focus.release('read-aloud');
    assert.equal(focus.owner(), null);

    const channel = { postMessage() {}, onmessage: null };
    const tab = exports.createFocus(channel, 'mine');
    tab.claim('player', () => paused.push('mine'));
    channel.onmessage({ data: 'mine' });
    assert.equal(tab.owner(), 'player');
    channel.onmessage({ data: 'other' });
    assert.equal(tab.owner(), null);
    assert.equal(paused.at(-1), 'mine');
});
