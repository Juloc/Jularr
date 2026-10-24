import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const load = path => readFileSync(new URL('../' + path, import.meta.url), 'utf8');
const source = load('Modules/Pwa/Pwa.ts');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const pwa = {};
runInNewContext(script, { exports: pwa });

test('the worker is registered at the site root with the whole site as scope and never from the cache', async () => {
    const calls = [];
    const registered = await pwa.registerWorker({ register: async (address, options) => { calls.push([address, options]); } }, true);
    assert.equal(registered, true);
    assert.equal(JSON.stringify(calls), JSON.stringify([['/service-worker.js', { scope: '/', updateViaCache: 'none' }]]));
});

test('without service workers or a secure context nothing is registered, and a refusal never throws', async () => {
    let calls = 0;
    const container = { register: async () => { calls++; } };
    assert.equal(await pwa.registerWorker(undefined, true), false);
    assert.equal(await pwa.registerWorker(container, false), false);
    assert.equal(calls, 0);
    assert.equal(await pwa.registerWorker({ register: async () => { throw new Error('refused'); } }, true), false);
});

test('the entry registers after the page has loaded and has no inline script', () => {
    const entry = load('Modules/Pwa/pwa.entry.ts');
    assert.match(entry, /window\.addEventListener\("load", start, \{ once: true \}\)/);
    assert.match(entry, /registerWorker\(navigator\.serviceWorker, window\.isSecureContext\)/);
    const layout = load('Pages/Shared/_Layout.cshtml');
    assert.match(layout, /Assets\.Scripts\("Modules\/Pwa\/pwa\.entry\.ts"\)/);
    assert.match(layout, /<link rel="manifest" href="\/manifest\.webmanifest" \/>/);
    for (const meta of ['theme-color', 'mobile-web-app-capable', 'apple-mobile-web-app-capable', 'apple-mobile-web-app-title', 'apple-mobile-web-app-status-bar-style']) {
        assert.match(layout, new RegExp('<meta name="' + meta + '"'));
    }
});

test('the worker caches nothing and never answers a request itself', () => {
    const worker = load('Modules/Pwa/ServiceWorker.js');
    assert.doesNotMatch(worker, /respondWith|caches|importScripts|\bfetch\(/);
    const listeners = {};
    const waited = [];
    let skipped = 0;
    let claimed = 0;
    const self = {
        addEventListener: (name, listener) => { listeners[name] = listener; },
        skipWaiting: () => { skipped++; },
        clients: { claim: () => { claimed++; return Promise.resolve(); } }
    };
    runInNewContext(worker, { self });
    assert.deepEqual(Object.keys(listeners).sort(), ['activate', 'fetch', 'install']);
    listeners.install();
    listeners.activate({ waitUntil: promise => waited.push(promise) });
    const event = { request: { url: '/live/events' }, respondWith() { throw new Error('answered'); } };
    assert.equal(listeners.fetch(event), undefined);
    assert.equal(skipped, 1);
    assert.equal(claimed, 1);
    assert.equal(waited.length, 1);
});


test('standalone safe areas cover login, tablet shell, mobile drawer, modal and reader', () => {
    const login = load('Pages/Account/Login/Login.entry.scss');
    const shell = load('Pages/Shared/Shell.scss');
    const modal = load('Framework/Components/Modal.scss');
    const reader = load('Modules/Reader/reader.scss');
    for (const style of [login, shell, modal, reader]) {
        assert.match(style, /env\(safe-area-inset-top\)/);
        assert.match(style, /env\(safe-area-inset-bottom\)/);
    }
    assert.match(shell, /max\(13px, env\(safe-area-inset-right\)\)/);
    assert.match(reader, /max\(18px, env\(safe-area-inset-left\)\)/);
    assert.match(login, /\.login-card \.ui-field__control > input \{ font-size: 16px/);
});
