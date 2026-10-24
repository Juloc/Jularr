import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const compile = name => ts.transpileModule(readFileSync(new URL(`../Modules/Reader/${name}.ts`, import.meta.url), 'utf8'),
    { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;

test('one column measurement counts the same scrolled LTR and RTL content without a padded blank page', () => {
    const exports = {};
    let right = 2040, left = -1040;
    runInNewContext(compile('PaperMeasure'), { exports, document: { createRange: () => ({ selectNodeContents() {}, detach() {}, getClientRects: () => [{ right, left }] }) } });
    const content = { clientWidth: 1000, scrollLeft: 0, getBoundingClientRect: () => ({ left: 0, right: 1000 }) };
    assert.equal(exports.paperPageCount(content, 2, 40, false), 4);
    content.scrollLeft = 1040; right = 1000;
    assert.equal(exports.paperPageCount(content, 2, 40, false), 4);
    content.scrollLeft = 0;
    assert.equal(exports.paperPageCount(content, 2, 40, true), 4);
    content.scrollLeft = -1040; left = 0;
    assert.equal(exports.paperPageCount(content, 2, 40, true), 4);
    content.scrollLeft = 0; right = 1520;
    assert.equal(exports.paperPageCount(content, 2, 40, false), 3);
});

function harness(fetchChapter, cached = new Map(), section = 1) {
    let currentImages = [], font = '20px serif';
    const events = new Map(), windowEvents = new Map(), documentEvents = new Map();
    const tasks = new Map();
    let clock = 0, identity = 0, measuring = '', maximumRulers = 0, attached = 0, parsed = 0;
    const ruler = { style: {}, classList: { add() {} }, removeAttribute() {}, setAttribute() {},
        querySelectorAll: () => [],
        replaceChildren(fragment) { measuring = fragment?.html ?? ''; if (fragment) { maximumRulers = Math.max(maximumRulers, 1); attached++; } },
        remove() { attached--; }
    };
    const status = { hidden: true, dataset: { readyLabel: 'Book pages: {0}', calculatingLabel: 'calculating', pausedLabel: 'paused', unavailableLabel: 'unavailable' } };
    const content = { cloneNode: () => ruler, querySelectorAll: () => currentImages };
    const reader = {
        dataset: { section: String(section), sections: '3', sectionsUrl: '/sections/', documentRevision: 'source-a:completed', mode: 'paged', illustrations: 'true' },
        querySelector: selector => selector.includes('book-pages') ? status : null,
        append() { attached++; },
        addEventListener(name, callback) { events.set(name, callback); }
    };
    const document = {
        hidden: false, documentElement: { lang: 'en' }, fonts: { ready: Promise.resolve() },
        addEventListener(name, callback) { documentEvents.set(name, callback); },
        createElement(tag) {
            if (tag === 'div') return { setAttribute() {}, append() {}, remove() { attached--; } };
            parsed++;
            return { content: { html: '', querySelectorAll: () => [] }, set innerHTML(value) { this.content.html = value; } };
        }
    };
    const schedule = (callback, delay = 0) => { const id = ++identity; tasks.set(id, { at: clock + delay, callback }); return id; };
    const window = {
        setTimeout: schedule, cancelIdleCallback: id => tasks.delete(id),
        requestIdleCallback: callback => schedule(() => callback({ timeRemaining: () => 50 })),
        addEventListener(name, callback) { windowEvents.set(name, callback); }
    };
    const exports = {};
    runInNewContext(compile('BookPages'), { exports, window, document, AbortController, TextDecoder,
        performance: { now: () => clock }, clearTimeout: id => tasks.delete(id), fetch: fetchChapter,
        getComputedStyle: () => ({ font, letterSpacing: 'normal', getPropertyValue: () => '68ch' }),
        require: name => name === './PaperMeasure' ? { paperPageCount: () => Number(measuring) } : {
            readBookPageCounts: async key => cached.get(key)?.slice(),
            writeBookPageCounts: async (key, counts) => { cached.set(key, counts.slice()); }
        }
    });
    const counter = exports.initializeBookPages(reader, content);
    const layout = { width: 1000, height: 600, columns: 2, gap: 40, pages: 3, rtl: false };
    async function advance(milliseconds = 500) {
        await new Promise(resolve => setImmediate(resolve));
        clock += milliseconds;
        for (let tick = 0; tick < 30; tick++) {
            const due = [...tasks].filter(([, task]) => task.at <= clock);
            due.forEach(([id, task]) => { tasks.delete(id); task.callback(); });
            await new Promise(resolve => setImmediate(resolve));
            if (![...tasks.values()].some(task => task.at <= clock)) break;
        }
    }
    return { reader, counter, layout, events, windowEvents, documentEvents, document, ruler, status, advance,
        setImages: images => { currentImages = images; },
        setFont: value => { font = value; },
        stats: () => ({ maximumRulers, attached, parsed, tasks: tasks.size, measuring }) };
}

test('background counts pause, discard stale work, finish sequentially and do no idle work after completion', async () => {
    let release;
    const requests = [];
    const h = harness((url, options) => {
        requests.push({ url, signal: options.signal });
        if (requests.length === 1) return new Promise(resolve => { release = resolve; });
        return Promise.resolve(new Response(JSON.stringify({ sectionIndex: Number(url.at(-1)), html: '5' })));
    });
    h.counter.update(h.layout);
    assert.equal(h.reader.dataset.bookPageCount, undefined);
    await h.advance();
    assert.equal(requests.length, 1);
    h.events.get('pointerdown')({ pointerId: 7 });
    assert.equal(requests[0].signal.aborted, false, 'ordinary page turns must not cancel the in-flight request');
    assert.equal(h.reader.dataset.bookPageState, 'paused');
    h.counter.update({ ...h.layout, width: 400, pages: 5 });
    assert.equal(requests[0].signal.aborted, true, 'a real layout change still invalidates the old measurement');
    release(new Response(JSON.stringify({ sectionIndex: 0, html: '999' })));
    await h.advance(1000);
    assert.equal(requests.length, 1, 'holding a pointer must prevent more work');
    assert.equal(h.reader.dataset.bookPageCount, undefined);
    h.document.hidden = true;
    h.windowEvents.get('pointerup')({ pointerId: 7 });
    h.documentEvents.get('visibilitychange')();
    await h.advance(1000);
    assert.equal(requests.length, 1, 'hidden pages must not fetch chapters');
    h.document.hidden = false;
    h.documentEvents.get('visibilitychange')();
    await h.advance();
    await h.advance();
    assert.deepEqual(requests.map(request => request.url), ['/sections/0', '/sections/0', '/sections/2']);
    assert.equal(h.reader.dataset.bookPageState, 'ready');
    assert.equal(h.reader.dataset.bookPageCount, '15', 'the stale count must not enter the new layout');
    assert.equal(h.reader.dataset.bookPageBefore, '5');
    h.counter.position(3);
    assert.equal(h.reader.dataset.bookPage, '8');
    assert.equal(h.stats().measuring, '');
    assert.equal(h.stats().tasks, 0);
    h.counter.update({ ...h.layout, width: 400, pages: 5 });
    await h.advance(5000);
    assert.equal(requests.length, 3, 'unchanged layout must reuse completed numeric counts');
});

test('failed and oversized chapters leave no fabricated count or retry loop and release the ruler', async () => {
    for (const response of [() => new Response('', { status: 403 }), () => new Response('x'.repeat(2 * 1024 * 1024 + 1))]) {
        let requests = 0;
        const h = harness(async () => { requests++; return response(); });
        h.counter.update(h.layout);
        await h.advance();
        await h.advance();
        assert.equal(h.reader.dataset.bookPageState, 'unavailable');
        assert.equal(h.reader.dataset.bookPageCount, undefined);
        assert.equal(h.stats().parsed, 0, 'an oversized response must not be parsed into DOM');
        h.events.get('wheel')();
        await h.advance(5000);
        assert.equal(requests, 1);
        assert.equal(h.stats().measuring, '');
        h.windowEvents.get('pagehide')({ persisted: false });
        assert.equal(h.reader.dataset.bookPageCount, undefined);
    }
});

test('page suspension cancels a pending job and resumes only after the restored Reader supplies its layout', async () => {
    let release, requests = 0, signal;
    const h = harness((_, options) => {
        requests++; signal = options.signal;
        if (requests === 1) return new Promise(resolve => { release = resolve; });
        return Promise.resolve(new Response(JSON.stringify({ sectionIndex: requests === 2 ? 0 : 2, html: '4' })));
    });
    h.counter.update(h.layout);
    await h.advance();
    h.windowEvents.get('pagehide')({ persisted: true });
    assert.equal(signal.aborted, true);
    release(new Response(JSON.stringify({ sectionIndex: 0, html: '999' })));
    await h.advance(5000);
    assert.equal(requests, 1);
    h.windowEvents.get('pageshow')({ persisted: true });
    h.counter.update(h.layout);
    await h.advance();
    await h.advance();
    assert.equal(h.reader.dataset.bookPageCount, '11');
    h.counter.invalidate();
    assert.equal(h.reader.dataset.bookPageCount, undefined, 'loaded font changes must discard completed counts too');
});

test('an unavailable translated view and scroll mode never publish original-text page counts', async () => {
    let requests = 0;
    const h = harness(async () => { requests++; return new Response(''); });
    h.reader.dataset.translated = 'true';
    h.counter.update(h.layout);
    await h.advance();
    assert.equal(h.reader.dataset.bookPageState, 'unavailable');
    assert.equal(requests, 0);
    h.reader.dataset.mode = 'scroll';
    h.counter.update(h.layout);
    assert.equal(h.reader.dataset.bookPageState, 'off');
    assert.equal(h.status.hidden, true);
});

test('choosing the original language with a regional tag does not disable measurement', async () => {
    const h = harness(async url => new Response(JSON.stringify({ sectionIndex: Number(url.at(-1)), html: '4' })));
    h.reader.dataset.language = 'en-US';
    h.reader.dataset.readingLanguage = 'en';
    h.counter.update(h.layout);
    await h.advance();
    await h.advance();
    assert.equal(h.reader.dataset.bookPageCount, '11');
});

test('completed book pages survive chapter navigation and reload without a second background fetch', async () => {
    const cache = new Map();
    const requests = [];
    const fetchChapter = async url => {
        requests.push(url);
        return new Response(JSON.stringify({ sectionIndex: Number(url.at(-1)), html: '4' }));
    };
    const first = harness(fetchChapter, cache);
    first.counter.update(first.layout);
    await first.advance();
    await first.advance();
    assert.equal(first.reader.dataset.bookPageCount, '11');
    assert.equal(cache.size, 1, 'one completed layout is stored, not partial estimates');
    assert.deepEqual(requests, ['/sections/0', '/sections/2']);
    const second = harness(fetchChapter, cache, 2);
    second.counter.update({ ...second.layout, pages: 4 });
    await second.advance();
    assert.equal(second.reader.dataset.bookPageState, 'ready');
    assert.equal(second.reader.dataset.bookPageCount, '11');
    assert.equal(second.reader.dataset.bookPageBefore, '7');
    second.counter.position(4);
    assert.equal(second.reader.dataset.bookPage, '11');
    assert.deepEqual(requests, ['/sections/0', '/sections/2'], 'no chapter is fetched again for an unchanged book layout');
    second.counter.update({ ...second.layout, width: 400, pages: 4 });
    await second.advance();
    await second.advance();
    assert.equal(requests.length, 4, 'a changed page width invalidates the stored layout');
});

test('repeated turns allow the in-flight chapter to finish without repeated cancellation', async () => {
    const requests = [];
    let finish;
    const h = harness((url, options) => {
        requests.push({ url, signal: options.signal });
        if (requests.length === 1) return new Promise(resolve => { finish = resolve; });
        return Promise.resolve(new Response(JSON.stringify({ sectionIndex: Number(url.at(-1)), html: '4' })));
    });
    h.counter.update(h.layout);
    await h.advance();
    for (let index = 0; index < 100; index++) h.events.get('keydown')();
    assert.equal(requests[0].signal.aborted, false);
    finish(new Response(JSON.stringify({ sectionIndex: 0, html: '4' })));
    await h.advance();
    await h.advance();
    assert.equal(h.reader.dataset.bookPageCount, '11');
    assert.deepEqual(requests.map(request => request.url), ['/sections/0', '/sections/2']);
});

test('late illustrations defer cache lookup but retry it after the current chapter finishes loading', async () => {
    const cache = new Map();
    const requests = [];
    const fetchChapter = async url => {
        requests.push(url);
        return new Response(JSON.stringify({ sectionIndex: Number(url.at(-1)), html: '4' }));
    };
    const first = harness(fetchChapter, cache);
    first.counter.update(first.layout);
    await first.advance();
    await first.advance();
    assert.equal(first.reader.dataset.bookPageCount, '11');

    const next = harness(fetchChapter, cache);
    next.setImages([{ complete: false, naturalWidth: 0 }]);
    next.counter.update(next.layout);
    await next.advance();
    assert.equal(next.reader.dataset.bookPageState, 'paused');
    assert.equal(requests.length, 2, 'loading an illustration must not cause redundant chapter reads');
    next.setImages([{ complete: true, naturalWidth: 250 }]);
    next.counter.update(next.layout);
    await next.advance();
    assert.equal(next.reader.dataset.bookPageState, 'ready');
    assert.equal(next.reader.dataset.bookPageCount, '11');
    assert.equal(requests.length, 2, 'the completed cache is restored once the illustration settles');
});

// Browser pagination is deliberately not portable across display widths or font sizes.
// A source-derived revision must also keep an updated document from reusing old chapter counts.
test('cache is separated by book revision, viewport dimensions and typography', async () => {
    const cached = new Map(), requested = [];
    const fetchChapter = async url => {
        requested.push(url);
        return new Response(JSON.stringify({ sectionIndex: Number(url.at(-1)), html: '4' }));
    };
    const first = harness(fetchChapter, cached);
    first.counter.update(first.layout);
    await first.advance(); await first.advance();
    assert.equal(first.reader.dataset.bookPageState, 'ready');
    assert.equal(requested.length, 2);

    const same = harness(fetchChapter, cached);
    same.counter.update(same.layout);
    await same.advance();
    assert.equal(same.reader.dataset.bookPageState, 'ready');
    assert.equal(requested.length, 2, 'same book, source and screen should reuse cache');

    const resized = harness(fetchChapter, cached);
    resized.counter.update({ ...resized.layout, width: 420, height: 700, pages: 3 });
    await resized.advance(); await resized.advance();
    assert.equal(requested.length, 4, 'another device screen needs its own pagination');

    const differentFont = harness(fetchChapter, cached);
    differentFont.setFont('26px sans-serif');
    differentFont.counter.update(differentFont.layout);
    await differentFont.advance(); await differentFont.advance();
    assert.equal(requested.length, 6, 'profile font and size change must invalidate layout counts');

    const revised = harness(fetchChapter, cached);
    revised.reader.dataset.documentRevision = 'source-b:completed';
    revised.counter.update(revised.layout);
    await revised.advance(); await revised.advance();
    assert.equal(requested.length, 8, 'a newer reading derivation cannot reuse a previous source revision');

    const unknown = harness(fetchChapter, cached);
    delete unknown.reader.dataset.documentRevision;
    unknown.counter.update(unknown.layout);
    await unknown.advance(); await unknown.advance();
    assert.equal(requested.length, 10, 'absence of a trusted source revision disables persistent cache reuse');
});
