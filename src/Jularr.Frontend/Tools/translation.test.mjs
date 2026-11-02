import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Reader/Translation/translation.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
const lifetime = { require: () => ({ readerLifetime: () => new AbortController().signal }) };
runInNewContext(script, { exports, ...lifetime });

test('a book names its language, else its text tells it', () => {
    assert.equal(exports.languageOf('de-DE', ''), 'de');
    assert.equal(exports.languageOf('fr', ''), 'fr');
    assert.equal(exports.languageOf('in', ''), 'id');
    assert.equal(exports.languageOf('', 'ルイ・クリードの記憶では、ある瞬間がいつも魔法のような性質を持っていた。'), 'ja');
    assert.equal(exports.languageOf(undefined, 'Er sagte, dass die Tür nicht zu war und das Haus sich mit der Nacht füllte, und der Hund schlief.'), 'de');
    assert.equal(exports.languageOf(undefined, 'Dia bilang itu tidak dari rumah yang di sana dan dengan anjing yang tidur untuk malam ini.'), 'id');
    assert.equal(exports.languageOf(undefined, 'He said that the door was open and the house was full of the night, and it was quiet.'), 'en');
    assert.equal(exports.languageOf(undefined, "Il dit que la porte n'est pas fermée et que la maison est dans le noir, une nuit pour les chiens."), 'fr');
    assert.equal(exports.languageOf(undefined, '12 34'), undefined);
});

test('English is a step of its own; other pairs go through it', () => {
    const steps = (source, target) => JSON.stringify(exports.steps(source, target));
    assert.equal(steps('en', 'ja'), '[["en","ja"]]');
    assert.equal(steps('id', 'en'), '[["id","en"]]');
    assert.equal(steps('de', 'ja'), '[["de","en"],["en","ja"]]');
});

test('restoring a section leaves nested block identities intact', async () => {
    let replacedParent = 0;
    const leaf = { innerHTML: 'Original text', innerText: 'Original text', querySelector: () => null };
    const parent = {
        get innerHTML() { return '<p data-b="1">Original text</p>'; },
        set innerHTML(_) { replacedParent++; },
        querySelector: () => leaf
    };
    const content = { querySelectorAll: () => [parent, leaf], innerText: 'Original text' };
    const reader = { dataset: { language: 'en' }, querySelector: () => null };
    const { choose } = exports.translator(reader, content, () => { throw new Error('No worker is needed'); });
    await choose('');
    assert.equal(replacedParent, 0, 'Restoring a parent would detach the cached nested block.');
    assert.equal(leaf.innerHTML, 'Original text');
});

test('a failed browser batch restores original blocks before local fallback', async () => {
    const blocks = Array.from({ length: 25 }, (_, index) => {
        let markup = 'original-' + index;
        return {
            get innerHTML() { return markup; },
            set innerHTML(next) { markup = next; },
            get innerText() { return markup; },
            set textContent(next) { markup = next; },
            querySelector: () => null
        };
    });
    let restoredBeforeFallback = false;
    const fetchStub = async (url, options) => {
        if (url === '/route/en/de') return {
            ok: true, json: async () => [
                { step: 0, fileType: 'engine', url: '/engine', precision: null },
                { step: 1, fileType: 'model', url: '/model', precision: null }
            ]
        };
        if (url.startsWith('/local/de/translategemma%3A4b?')) {
            restoredBeforeFallback = blocks[0].innerText === 'original-0';
            return { ok: true, json: async () => ({ blocks: '[]' }) };
        }
        if (url.startsWith('/local/de/translategemma%3A4b/blocks/') && options?.method === 'POST') {
            return { ok: true, json: async () => ({ text: 'local translation' }) };
        }
        throw new Error('Unexpected fetch: ' + url);
    };
    const localExports = {};
    runInNewContext(script, { exports: localExports, fetch: fetchStub, ...lifetime });
    const fakeWorker = {
        onmessage: null,
        terminate() {},
        postMessage(message) {
            if (message.type === 'load') queueMicrotask(() => this.onmessage({ data: { type: 'ready' } }));
            else if (message.type === 'translate') queueMicrotask(() => this.onmessage({
                data: message.id === 0
                    ? { type: 'translated', id: message.id, texts: Array(24).fill('browser partial') }
                    : { type: 'error', id: message.id, message: 'worker failure' }
            }));
        }
    };
    const reader = {
        dataset: {
            language: 'en', translationEngine: 'bergamot', localModel: 'translategemma:4b',
            localTranslationUrl: '/local/', routeUrl: '/route/',
            translatingLabel: 'Translating'
        },
        querySelector: () => null
    };
    const content = { querySelectorAll: () => blocks, innerText: 'English original text' };
    const { choose } = localExports.translator(reader, content, () => fakeWorker);
    await choose('de');
    assert.equal(restoredBeforeFallback, true);
    assert.equal(reader.dataset.translationState, 'ready');
    assert.equal(blocks[0].innerText, 'local translation');
});

// The browser's own store, as far as the translator uses it; `store` outlives a reader, like the real IndexedDB does.
function fakeIndexedDb(store = new Map()) {
    const reply = (result) => {
        const request = {};
        queueMicrotask(() => { request.result = result; request.onsuccess?.(); });
        return request;
    };
    const objects = {
        get: key => reply(store.get(key)),
        put: value => { store.set(value.key, value); return reply(value.key); },
        getAll: () => reply([...store.values()]),
        delete: key => { store.delete(key); return reply(undefined); }
    };
    const database = { createObjectStore() {}, transaction: () => ({ objectStore: () => objects }), close() {} };
    const indexedDB = {
        open() {
            const request = { result: database };
            queueMicrotask(() => { request.onupgradeneeded?.(); request.onsuccess?.(); });
            return request;
        }
    };
    return { store, indexedDB };
}

// A block as the translator sees it.
function block(index) {
    let markup = 'original-' + index;
    return {
        get innerHTML() { return markup; },
        set innerHTML(next) { markup = next; },
        get innerText() { return markup; },
        set textContent(next) { markup = next; },
        querySelector: () => null
    };
}

// Mozilla, as far as the translator uses it; `given` collects every text it was asked to translate.
function fakeMozilla(given = []) {
    return {
        given,
        onmessage: null,
        terminate() {},
        postMessage(message) {
            if (message.type === 'load') queueMicrotask(() => this.onmessage({ data: { type: 'ready' } }));
            else if (message.type === 'translate') {
                given.push(...message.texts);
                queueMicrotask(() => this.onmessage({ data: { type: 'translated', id: message.id, texts: message.texts.map(() => 'browser translation') } }));
            }
        }
    };
}

async function until(condition) {
    for (let round = 0; round < 200 && !condition(); round++) await new Promise(resolve => setTimeout(resolve, 0));
    assert.ok(condition(), 'the condition was never met');
}

function emergencyReader(browserTranslation, browser = fakeIndexedDb(), { count = 3, ahead } = {}) {
    const blocks = Array.from({ length: count }, (_, index) => block(index));
    const calls = [];
    const fetchStub = async (url, options) => {
        calls.push((options?.method ?? 'GET') + ' ' + url);
        if (url === '/route/en/de') return {
            ok: true, json: async () => [
                { step: 0, fileType: 'engine', url: '/engine', precision: null },
                { step: 1, fileType: 'model', url: '/model', precision: null }
            ]
        };
        if (url.startsWith('/local/de/translategemma%3A4b?')) return { ok: false };
        if (url.startsWith('/local/de/translategemma%3A4b/blocks/')) return { ok: false };
        throw new Error('Unexpected fetch: ' + url);
    };
    const fakeWorker = fakeMozilla();
    const dataset = {
        language: 'en', translationEngine: 'gemma', localModel: 'translategemma:4b',
        localTranslationUrl: '/local/', routeUrl: '/route/', translatingLabel: 'Translating'
    };
    if (browserTranslation) dataset.browserTranslation = 'true';
    const localExports = {};
    runInNewContext(script, { exports: localExports, fetch: fetchStub, indexedDB: browser.indexedDB, ...lifetime });
    const content = { querySelectorAll: () => blocks, innerText: 'English original text' };
    const { choose, moved } = localExports.translator({ dataset, querySelector: () => null }, content, () => fakeWorker, ahead && (() => ahead(blocks)));
    return { blocks, calls, dataset, choose, moved, given: fakeWorker.given, store: browser.store };
}

test('with the Mozilla module on, a page the server cannot translate falls back to the browser', async () => {
    const { blocks, dataset, choose, calls } = emergencyReader(true);
    await choose('de');
    assert.equal(dataset.translationState, 'ready');
    assert.equal(blocks[0].innerText, 'browser translation');
    assert.ok(calls.includes('GET /route/en/de'), 'The browser engine is asked for only when the server failed.');
});

test('the Mozilla emergency neither reads nor writes the server cache and keeps its result in the browser', async () => {
    const { blocks, choose, calls, store } = emergencyReader(true);
    await choose('de');
    assert.deepEqual(blocks.map(block => block.innerText), ['browser translation', 'browser translation', 'browser translation']);
    assert.equal(calls.some(call => call.includes('/translations/') || call.includes('/translated/')), false, 'no request to the server translation cache');
    assert.equal(calls.some(call => call.startsWith('PUT ')), false, 'nothing is written to the server');
    const entries = [...store.values()];
    assert.equal(entries.length, 1, 'one entry in the browser store');
    assert.match(entries[0].key, /^\/local\/de#/);
    assert.deepEqual(Array.from(entries[0].blocks), ['browser translation', 'browser translation', 'browser translation']);
});

test('a reopened book shows the browser\'s earlier emergency translation at once', async () => {
    const browser = fakeIndexedDb();
    await emergencyReader(true, browser).choose('de');
    assert.equal(browser.store.size, 1);

    const { blocks, choose, calls, dataset } = emergencyReader(true, browser);
    await choose('de');
    assert.deepEqual(blocks.map(block => block.innerText), ['browser translation', 'browser translation', 'browser translation']);
    assert.equal(calls.includes('GET /route/en/de'), false, 'nothing is translated again');
    assert.equal(calls.some(call => call.startsWith('PUT ') || call.includes('/translations/')), false);
    assert.equal(dataset.translationState, 'ready');
});

test('without the Mozilla module, a page the server cannot translate stays original and asks Mozilla for nothing', async () => {
    const { blocks, dataset, choose, calls } = emergencyReader(false);
    await choose('de');
    assert.equal(dataset.translationState, 'failed');
    assert.equal(blocks[0].innerText, 'original-0');
    assert.ok(!calls.includes('/route/en/de'));
});

test('a server that needs half a minute for a block, or twenty seconds on average, is too slow for a reader', () => {
    assert.equal(exports.serverIsSlow([]), false);
    assert.equal(exports.serverIsSlow([4_600, 4_800, 5_000]), false);
    assert.equal(exports.serverIsSlow([4_600, 31_000]), true);
    assert.equal(exports.serverIsSlow([25_000, 22_000]), false, 'two blocks are too few to average');
    assert.equal(exports.serverIsSlow([25_000, 22_000, 21_000]), true);
    assert.equal(exports.serverIsSlow([60_000, 4_000, 4_000, 4_000]), false, 'only the latest blocks count');
});

test('the browser keeps its translations a week and 300 MB, the oldest go first', () => {
    const day = 24 * 3600 * 1000;
    const mb = 1024 * 1024;
    const now = 100 * day;
    const entry = (key, ageDays, size) => ({ key, savedAt: now - ageDays * day, size });
    assert.deepEqual(exports.entriesToDrop([entry('a', 1, mb), entry('b', 6, mb)], now), []);
    assert.deepEqual(exports.entriesToDrop([entry('old', 8, mb), entry('new', 1, mb)], now), ['old']);
    // 400 MB of recent entries: the oldest leave until 300 MB are left.
    const big = [entry('c', 5, 150 * mb), entry('d', 3, 150 * mb), entry('e', 1, 100 * mb)];
    assert.deepEqual(exports.entriesToDrop(big, now), ['c']);
    assert.deepEqual(exports.entriesToDrop([entry('x', 2, 500 * mb)], now), ['x']);
});

// A server needing 31 s a block; `held` keeps its answers for the blocks after the first until `release` is called.
function slowServerReader({ route, count = 5, ahead, held = false }) {
    const blocks = Array.from({ length: count }, (_, index) => block(index));
    let clock = 0;
    let release = () => {};
    const gate = held ? new Promise(resolve => { release = resolve; }) : Promise.resolve();
    const calls = [];
    const fetchStub = async (url, options) => {
        calls.push((options?.method ?? 'GET') + ' ' + url);
        if (url.startsWith('/local/de/translategemma%3A4b?')) return { ok: true, json: async () => ({ blocks: '[]' }) };
        if (url.startsWith('/local/de/translategemma%3A4b/blocks/')) {
            if (!url.endsWith('/0')) await gate;
            clock += 31_000;
            return { ok: true, json: async () => ({ text: 'server ' + url.split('/').pop() }) };
        }
        if (url === '/route/en/de') return route
            ? { ok: true, json: async () => [{ step: 0, fileType: 'engine', url: '/engine', precision: null }, { step: 1, fileType: 'model', url: '/model', precision: null }] }
            : { ok: false };
        throw new Error('Unexpected fetch: ' + url);
    };
    const fakeWorker = fakeMozilla();
    const dataset = {
        language: 'en', translationEngine: 'gemma', localModel: 'translategemma:4b', browserTranslation: 'true',
        localTranslationUrl: '/local/', routeUrl: '/route/', translatingLabel: 'Translating',
        browserUnsupportedLabel: 'Mozilla has no models'
    };
    const localExports = {};
    runInNewContext(script, { exports: localExports, fetch: fetchStub, Date: { now: () => clock }, ...lifetime });
    const content = { querySelectorAll: () => blocks, innerText: 'English original text' };
    const status = { hidden: true, textContent: '' };
    const reader = { dataset, querySelector: selector => selector === '[data-role=translation-status]' ? status : null };
    const { choose } = localExports.translator(reader, content, () => fakeWorker, ahead && (() => ahead(blocks)));
    return { choose, blocks, calls, dataset, status, release, given: fakeWorker.given };
}

test('a server too slow for a reader goes on while Mozilla fills the page in view and the three after it, and its text replaces Mozilla\'s', async () => {
    // One block a page: the page in view holds block 0, the window reaches to block 3.
    const { choose, blocks, calls, dataset, release, given } = slowServerReader({ route: true, count: 8, held: true, ahead: all => all.slice(0, 4) });
    const done = choose('de');
    await until(() => blocks[3].innerText === 'browser translation');
    assert.deepEqual(blocks.map(block => block.innerText), ['server 0', 'browser translation', 'browser translation', 'browser translation',
        'original-4', 'original-5', 'original-6', 'original-7'], 'only the window is translated in the browser');
    assert.deepEqual(given, ['original-1', 'original-2', 'original-3'], 'a block the server translated is not given to Mozilla');
    release();
    await done;
    assert.deepEqual(blocks.map(block => block.innerText), Array.from({ length: 8 }, (_, index) => 'server ' + index), 'the server\'s text replaces Mozilla\'s');
    assert.equal(calls.filter(call => call.startsWith('POST /local/')).length, 8, 'the server still translates every block');
    assert.equal(calls.some(call => call.startsWith('PUT ')), false, 'the browser translation is not stored on the server');
    assert.equal(dataset.translationState, 'ready');
});

test('a language Mozilla has no models for is left to the server, and the reader says so', async () => {
    const { choose, blocks, calls, status } = slowServerReader({ route: false });
    await choose('de');
    assert.deepEqual(blocks.map(block => block.innerText), ['server 0', 'server 1', 'server 2', 'server 3', 'server 4']);
    assert.equal(calls.filter(call => call.startsWith('POST /local/')).length, 5);
    assert.equal(calls.filter(call => call === 'GET /route/en/de').length, 1, 'the route is asked for once');
    assert.equal(status.textContent, '', 'the progress label is gone when the server is done');
});

test('Mozilla\'s window is the page in view and the three pages after it; a scrolled text counts its screen as a page', () => {
    const blocks = [
        [[-100, -10]],      // a page before
        [[-20, 30]],        // began on the page before, runs into the page in view
        [[0, 90]],          // the page in view
        [[350, 399]],       // the third page after it
        [[400, 450]],       // the fourth page after it
        [[-90, -10], [420, 480]]
    ];
    assert.deepEqual(Array.from(exports.windowOf(blocks, 100, 100)), [1, 2, 3], 'one page in view, pages 100 wide');
    assert.deepEqual(Array.from(exports.windowOf(blocks, 200, 100)), [1, 2, 3, 4, 5], 'a spread of two pages in view and three pages after it');
    assert.deepEqual(Array.from(exports.windowOf([[[0, 700]], [[3100, 3300]], [[3200, 3400]]], 800, 800)), [0, 1], 'scrolled: the screen and three screens below it');
});

test('the window moves along with the reader\'s page turns and no block is translated twice', async () => {
    let page = 0;
    const { blocks, choose, moved, given, store, dataset } = emergencyReader(true, fakeIndexedDb(), { count: 10, ahead: all => all.slice(page, page + 4) });
    await choose('de');
    assert.equal(dataset.translationState, 'ready');
    assert.deepEqual(blocks.map(block => block.innerText.startsWith('browser')), [true, true, true, true, false, false, false, false, false, false]);

    page = 2;
    moved();
    await until(() => blocks[5].innerText === 'browser translation');
    page = 1;
    moved();
    moved();
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.deepEqual(blocks.map(block => block.innerText.startsWith('browser')), [true, true, true, true, true, true, false, false, false, false]);
    assert.deepEqual(given, ['original-0', 'original-1', 'original-2', 'original-3', 'original-4', 'original-5'], 'each block went to Mozilla once');
    const [entry] = [...store.values()];
    assert.equal(Array.from(entry.blocks).filter(Boolean).length, 6, 'the browser keeps what it translated');
});

test('Smart Pages use the same window, the page in view first, through the same browser translator', async () => {
    assert.deepEqual(Array.from(exports.pagesAhead(5, () => true)), [5, 6, 7, 8]);
    assert.deepEqual(Array.from(exports.pagesAhead(5, page => page !== 7)), [5, 6, 8], 'a page without blocks of its own is left out');

    const fetchStub = async url => {
        if (url === '/route/en/de') return { ok: true, json: async () => [{ step: 0, fileType: 'engine', url: '/engine', precision: null }, { step: 1, fileType: 'model', url: '/model', precision: null }] };
        throw new Error('Unexpected fetch: ' + url);
    };
    const localExports = {};
    runInNewContext(script, { exports: localExports, fetch: fetchStub, ...lifetime });
    const worker = fakeMozilla();
    const browser = localExports.browserTranslator({ dataset: { routeUrl: '/route/' } }, 'en', () => worker);
    const view = localExports.blockSet('/smart/100005/', 'de', ['five a', '', 'five b']);
    const next = localExports.blockSet('/smart/100006/', 'de', ['six a', 'six b']);
    next.result[0] = 'server six';
    const shown = [];
    await Promise.all([
        browser.fill('de', view, [0, 1, 2], () => true, filled => shown.push(...filled.map(index => 'five ' + index))),
        browser.fill('de', next, [0, 1], () => true, filled => shown.push(...filled.map(index => 'six ' + index)))
    ]);
    assert.deepEqual(worker.given, ['five a', 'five b', 'six b'], 'the page in view goes first; a block the server translated is not given to Mozilla');
    assert.deepEqual(shown, ['five 0', 'five 2', 'six 1']);
    assert.equal(next.result[0], 'server six');
    assert.deepEqual(Array.from(view.bridge), [true, false, true]);
    await browser.fill('de', view, [0, 1, 2], () => true, () => assert.fail('nothing is translated twice'));
    assert.deepEqual(worker.given, ['five a', 'five b', 'six b']);
});
