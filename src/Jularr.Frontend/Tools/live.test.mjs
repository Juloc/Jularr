import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Live/Live.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;

// A marked element with the few DOM members the module uses.
function element(attributes, children = []) {
    const names = new Map(Object.entries(attributes));
    const dataset = {};
    const sync = () => {
        for (const key of Object.keys(dataset)) delete dataset[key];
        for (const [name, value] of names) {
            if (name.startsWith('data-')) dataset[name.slice(5).replace(/-(\w)/g, (_, letter) => letter.toUpperCase())] = value;
        }
    };
    sync();
    const node = {
        dataset, children, textContent: '', hidden: names.has('hidden'),
        hasAttribute: name => names.has(name),
        getAttribute: name => names.get(name) ?? null,
        setAttribute: (name, value) => { names.set(name, String(value)); sync(); },
        removeAttribute: name => { names.delete(name); sync(); },
        querySelectorAll: () => children
    };
    return node;
}

function page(...marked) {
    const all = marked.flatMap(item => [item, ...item.children]);
    return { querySelectorAll: selector => selector === '[data-live]' ? marked : all.filter(item => item.hasAttribute('data-live-text') || item.hasAttribute('data-live-value')) };
}

function load(extra = {}) {
    const exports = {};
    runInNewContext(script, { exports, ...extra });
    return exports;
}

test('stream entities patch text, bar, phase and step of marked elements only', () => {
    const { applyLive } = load();
    const phase = element({ 'data-live-text': 'phase' });
    const facts = element({ 'data-live-text': 'facts' });
    const bar = element({ 'data-live-value': 'percent' });
    const block = element({ 'data-live': 'request:1', 'data-phase': 'searching' }, [phase, facts, bar]);
    const other = element({ 'data-live': 'request:2', 'data-phase': 'approved' }, [element({ 'data-live-text': 'phase' })]);
    other.children[0].textContent = 'Requested';

    applyLive(page(block, other), { 'request:1': { phase: 'downloading', step: 3, percent: 43, text: { phase: 'Downloading 43 %' } } });

    assert.equal(block.getAttribute('data-phase'), 'downloading');
    assert.equal(block.getAttribute('data-step'), '3');
    assert.equal(phase.textContent, 'Downloading 43 %');
    assert.equal(phase.hidden, false);
    assert.equal(facts.hidden, true, 'A text the entity lacks is hidden.');
    assert.equal(bar.getAttribute('value'), '43');
    assert.equal(bar.hidden, false);
    assert.equal(other.getAttribute('data-phase'), 'approved', 'Unknown entities stay as the page made them.');
    assert.equal(other.children[0].textContent, 'Requested');
});

test('without a percent the bar goes away, and an optional block shows only while the stream knows it', () => {
    const { applyLive } = load();
    const bar = element({ 'data-live-value': 'percent', value: '40' });
    const block = element({ 'data-live': 'wanted:5', 'data-live-optional': '', hidden: 'hidden' }, [bar]);
    const own = page(block);

    applyLive(own, { 'wanted:5': { phase: 'importing', percent: null } });
    assert.equal(block.hidden, false);
    assert.equal(bar.hidden, true);
    assert.equal(bar.hasAttribute('value'), false);

    applyLive(own, {});
    assert.equal(block.hidden, true);
});

test('a speed line takes its points from the entity and keeps them while the stream has none', () => {
    const { applyLive } = load();
    const line = element({ 'data-live-points': 'line', points: '0,10 360,10' });
    const block = element({ 'data-live': 'downloads:all' }, [line]);
    applyLive(page(block), { 'downloads:all': { phase: 'downloads', text: { line: '0,63 360,1' } } });
    assert.equal(line.getAttribute('points'), '0,63 360,1');
    applyLive(page(block), { 'downloads:all': { phase: 'downloads', text: {} } });
    assert.equal(line.getAttribute('points'), '0,63 360,1');
});

test('the element that carries data-live-text itself is patched', () => {
    const { applyLive } = load();
    const button = element({ 'data-live': 'request-work:9', 'data-live-text': 'phase' });
    button.textContent = 'Requested';

    applyLive(page(button), { 'request-work:9': { phase: 'downloading', text: { phase: 'Downloading 7 %' } } });

    assert.equal(button.textContent, 'Downloading 7 %');
});

// A fake document and EventSource show when the stream is open.
function stream(initiallyLive = true) {
    const listeners = {};
    const sources = [];
    const timers = [];
    let live = initiallyLive;
    const block = element({ 'data-live': 'request:1' }, [element({ 'data-live-text': 'phase' })]);
    const document = {
        hidden: false,
        querySelector: () => live ? block : null,
        querySelectorAll: selector => page(block).querySelectorAll(selector),
        addEventListener: (name, action) => { listeners[name] = action; }
    };
    class EventSource {
        static CLOSED = 2;
        constructor(address) { this.address = address; this.readyState = 1; this.handlers = {}; this.closed = false; sources.push(this); }
        addEventListener(name, action) { this.handlers[name] = action; }
        close() { this.closed = true; this.readyState = 2; }
    }
    const exports = load({ document, EventSource, addEventListener: () => {}, queueMicrotask: action => action(),
        setTimeout: (action, delay) => { timers.push({ action, delay }); return timers.length; }, clearTimeout: () => {} });
    return { exports, document, sources, timers, block, setLive: value => { live = value; }, listeners };
}

test('the stream opens for a live page, applies its snapshot and closes on a page without live values or a hidden tab', () => {
    const view = stream();
    view.exports.syncLive();
    assert.equal(view.sources.length, 1);
    assert.equal(view.sources[0].address, '/live/events');
    view.exports.syncLive();
    assert.equal(view.sources.length, 1, 'One stream serves the page.');

    view.sources[0].handlers.snapshot({ data: JSON.stringify({ entities: { 'request:1': { text: { phase: 'Searching' } } } }) });
    assert.equal(view.block.children[0].textContent, 'Searching');

    view.document.hidden = true;
    view.exports.syncLive();
    assert.equal(view.sources[0].closed, true);

    view.document.hidden = false;
    view.exports.syncLive();
    assert.equal(view.sources.length, 2, 'A visible tab opens a fresh stream, which starts with a snapshot.');

    view.setLive(false);
    view.exports.syncLive();
    assert.equal(view.sources[1].closed, true);
});

test('a refused stream is opened again later, a little slower each time', () => {
    const view = stream();
    view.exports.syncLive();
    view.sources[0].readyState = 2;
    view.sources[0].handlers.error();
    assert.equal(view.timers.length, 1);
    assert.equal(view.timers[0].delay, 1000);

    view.timers[0].action();
    assert.equal(view.sources.length, 2);
    view.sources[1].readyState = 2;
    view.sources[1].handlers.error();
    assert.equal(view.timers[1].delay, 2000);
});
