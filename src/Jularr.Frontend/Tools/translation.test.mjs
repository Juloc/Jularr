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
    const choose = exports.translator(reader, content, () => { throw new Error('No worker is needed'); });
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
        if (url === '/translated/de') return { ok: false };
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
            localTranslationUrl: '/local/', translationUrl: '/translated/', routeUrl: '/route/',
            translatingLabel: 'Translating'
        },
        querySelector: () => null
    };
    const content = { querySelectorAll: () => blocks, innerText: 'English original text' };
    const choose = localExports.translator(reader, content, () => fakeWorker);
    await choose('de');
    assert.equal(restoredBeforeFallback, true);
    assert.equal(reader.dataset.translationState, 'ready');
    assert.equal(blocks[0].innerText, 'local translation');
});

function emergencyReader(browserTranslation) {
    const blocks = Array.from({ length: 3 }, (_, index) => {
        let markup = 'original-' + index;
        return {
            get innerHTML() { return markup; },
            set innerHTML(next) { markup = next; },
            get innerText() { return markup; },
            set textContent(next) { markup = next; },
            querySelector: () => null
        };
    });
    const calls = [];
    const fetchStub = async (url, options) => {
        calls.push(url);
        if (url === '/translated/de') return options?.method === 'PUT' ? { ok: true } : { ok: false };
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
    const fakeWorker = {
        onmessage: null,
        terminate() {},
        postMessage(message) {
            if (message.type === 'load') queueMicrotask(() => this.onmessage({ data: { type: 'ready' } }));
            else if (message.type === 'translate') queueMicrotask(() => this.onmessage({
                data: { type: 'translated', id: message.id, texts: message.texts.map(() => 'browser translation') }
            }));
        }
    };
    const dataset = {
        language: 'en', translationEngine: 'gemma', localModel: 'translategemma:4b',
        localTranslationUrl: '/local/', translationUrl: '/translated/', routeUrl: '/route/', translatingLabel: 'Translating'
    };
    if (browserTranslation) dataset.browserTranslation = 'true';
    const localExports = {};
    runInNewContext(script, { exports: localExports, fetch: fetchStub, ...lifetime });
    const content = { querySelectorAll: () => blocks, innerText: 'English original text' };
    const choose = localExports.translator({ dataset, querySelector: () => null }, content, () => fakeWorker);
    return { blocks, calls, dataset, choose };
}

test('with the Mozilla module on, a page the server cannot translate falls back to the browser', async () => {
    const { blocks, dataset, choose, calls } = emergencyReader(true);
    await choose('de');
    assert.equal(dataset.translationState, 'ready');
    assert.equal(blocks[0].innerText, 'browser translation');
    assert.ok(calls.includes('/route/en/de'), 'The browser engine is asked for only when the server failed.');
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

function slowServerReader({ route }) {
    const blocks = Array.from({ length: 5 }, (_, index) => {
        let markup = 'original-' + index;
        return {
            get innerHTML() { return markup; },
            set innerHTML(next) { markup = next; },
            get innerText() { return markup; },
            set textContent(next) { markup = next; },
            querySelector: () => null
        };
    });
    let clock = 0;
    const calls = [];
    const fetchStub = async (url, options) => {
        calls.push((options?.method ?? 'GET') + ' ' + url);
        if (url.startsWith('/local/de/translategemma%3A4b?')) return { ok: true, json: async () => ({ blocks: '[]' }) };
        if (url.startsWith('/local/de/translategemma%3A4b/blocks/')) {
            clock += 31_000;
            return { ok: true, json: async () => ({ text: 'server ' + url.split('/').pop() }) };
        }
        if (url === '/route/en/de') return route
            ? { ok: true, json: async () => [{ step: 0, fileType: 'engine', url: '/engine', precision: null }, { step: 1, fileType: 'model', url: '/model', precision: null }] }
            : { ok: false };
        throw new Error('Unexpected fetch: ' + url);
    };
    const fakeWorker = {
        onmessage: null,
        terminate() {},
        postMessage(message) {
            if (message.type === 'load') queueMicrotask(() => this.onmessage({ data: { type: 'ready' } }));
            else if (message.type === 'translate') queueMicrotask(() => this.onmessage({
                data: { type: 'translated', id: message.id, texts: message.texts.map(() => 'browser') }
            }));
        }
    };
    const dataset = {
        language: 'en', translationEngine: 'gemma', localModel: 'translategemma:4b', browserTranslation: 'true',
        localTranslationUrl: '/local/', translationUrl: '/translated/', routeUrl: '/route/', translatingLabel: 'Translating',
        browserUnsupportedLabel: 'Mozilla has no models'
    };
    const localExports = {};
    runInNewContext(script, { exports: localExports, fetch: fetchStub, Date: { now: () => clock }, ...lifetime });
    const content = { querySelectorAll: () => blocks, innerText: 'English original text' };
    const status = { hidden: true, textContent: '' };
    const reader = { dataset, querySelector: selector => selector === '[data-role=translation-status]' ? status : null };
    return { choose: localExports.translator(reader, content, () => fakeWorker), blocks, calls, dataset, status };
}

test('a server too slow for a reader hands the blocks nobody has to the browser, and nothing is stored on the server', async () => {
    const { choose, blocks, calls, dataset } = slowServerReader({ route: true });
    await choose('de');
    assert.equal(blocks[0].innerText, 'server 0');
    assert.deepEqual(blocks.slice(1).map(block => block.innerText), ['browser', 'browser', 'browser', 'browser']);
    assert.equal(calls.filter(call => call.startsWith('POST /local/')).length, 1, 'the server is asked once, then the browser takes over');
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
