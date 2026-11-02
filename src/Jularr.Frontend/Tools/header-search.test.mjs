import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Pages/Shared/HeaderSearch.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const settle = () => new Promise(resolve => setImmediate(resolve));

// Small event/DOM doubles, not a browser.
function header() {
    class Element {}
    class HTMLInputElement extends Element {}
    const events = {};
    const requests = [];
    const pages = new Map();
    const timers = new Map();
    let timerId = 0;

    const list = Object.assign(new Element(), { hidden: true, children: [], replaceChildren(...nodes) { this.children = nodes; }, querySelectorAll: () => [] });
    const search = Object.assign(new Element(), {
        popup: null,
        querySelector: selector => selector === '.app-search__popup' ? search.popup : selector === '.search-box__input' ? input : null,
        append(element) { search.popup = element; },
        contains: node => node === input || node === search.popup,
        closest: selector => selector === '.app-search' ? search : null
    });
    const input = Object.assign(new HTMLInputElement(), {
        value: '', form: { action: 'https://jularr.test/' }, isConnected: true, focused: false,
        setAttribute() {}, matches: () => true, closest: selector => selector === '.app-search' ? search : null,
        focus() { this.focused = true; }, blur() { this.focused = false; }
    });

    const context = {
        exports: {},
        require: name => {
            assert.equal(name, '../../Modules/Navigation/PageSwap');
            return { inserted() {} };
        },
        document: { addEventListener: (name, action) => { events[name] = action; }, createElement: () => list, activeElement: input },
        Element, HTMLInputElement, AbortController, URL, Array, Node: class {},
        FormData: class { forEach(action) { action(input.value, 'q'); } },
        DOMParser: class { parseFromString(key) { return { querySelector: () => pages.get(key) }; } },
        fetch: (url, options) => new Promise(resolve => requests.push({ url, options, resolve })),
        setTimeout: action => { timers.set(++timerId, action); return timerId; },
        clearTimeout: id => timers.delete(id)
    };
    runInNewContext(script, context);

    return {
        requests, list, input, fit: context.exports.fit, Element,
        type(value) { input.value = value; events.input({ target: input }); },
        key(name) { events.keydown({ key: name, target: input, preventDefault() {} }); },
        flush() { const pending = [...timers.values()]; timers.clear(); pending.forEach(action => action()); },
        async answer(index, results) {
            pages.set(String(index), results);
            requests[index].resolve({ ok: true, redirected: false, text: async () => String(index) });
            await settle();
        }
    };
}

test('suggestions wait for a pause, need two letters and show only the latest answer', async () => {
    const ui = header();
    ui.type('M');
    ui.flush();
    assert.equal(ui.requests.length, 0, 'one letter suggests nothing');

    ui.type('Ma');
    ui.type('Matr');
    assert.equal(ui.requests.length, 0);
    ui.flush();
    assert.equal(ui.requests.length, 1);
    assert.equal(ui.requests[0].url.searchParams.get('handler'), 'Suggestions');
    assert.equal(ui.requests[0].url.searchParams.get('q'), 'Matr');

    ui.type('Matrix');
    assert.equal(ui.requests[0].options.signal.aborted, true);
    ui.flush();
    const latest = { latest: true };
    await ui.answer(0, { obsolete: true });
    assert.equal(ui.list.hidden, true);
    await ui.answer(1, latest);
    assert.equal(ui.list.hidden, false);
    assert.deepEqual(ui.list.children, [latest]);
});

test('Escape closes the suggestions first, then leaves the field', async () => {
    const ui = header();
    ui.type('Matrix');
    ui.flush();
    await ui.answer(0, {});
    ui.input.focus();

    ui.key('Escape');
    assert.equal(ui.list.hidden, true);
    assert.deepEqual(ui.list.children, []);
    assert.equal(ui.input.focused, true);

    ui.key('Escape');
    assert.equal(ui.input.focused, false);
});

test('every group shows as many titles as fit, from six down to two', () => {
    const ui = header();
    const groups = [0, 1, 2].map(() => ({ hits: Array.from({ length: 6 }, () => ({ hidden: false })) }));
    const popup = (room) => ({
        clientHeight: room,
        get scrollHeight() { return groups.flatMap(group => group.hits).filter(hit => !hit.hidden).length * 50; },
        querySelectorAll: () => groups.map(group => ({ querySelectorAll: () => group.hits }))
    });
    const shown = () => groups.map(group => group.hits.filter(hit => !hit.hidden).length);

    ui.fit(popup(1000));
    assert.deepEqual(shown(), [6, 6, 6]);
    ui.fit(popup(450));
    assert.deepEqual(shown(), [3, 3, 3]);
    ui.fit(popup(100));
    assert.deepEqual(shown(), [2, 2, 2]);
});
