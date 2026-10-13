import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/MediaShelf.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;

function shelf(direction = 'ltr', reduced = false) {
    const events = {};
    const observed = new Set();
    let onResize;
    const document = { activeElement: null, addEventListener: (name, action) => { events[name] = action; }, querySelectorAll: () => [items] };
    const previous = { dataset: { shelfDirection: 'previous' }, hidden: true };
    const next = { dataset: { shelfDirection: 'next' }, hidden: true };
    const controls = { querySelectorAll: () => [previous, next], querySelector: () => items };
    const scrollEvents = {};
    const movements = [];
    const items = { isConnected: true, scrollLeft: 0, scrollWidth: 1000, clientWidth: 300,
        closest: () => controls, addEventListener: (name, action) => { scrollEvents[name] = action; },
        scrollBy: movement => movements.push(movement), focus: () => { document.activeElement = items; } };
    for (const button of [previous, next]) button.closest = () => controls;
    class Element { constructor(button) { this.button = button; } closest() { return this.button; } }
    const exports = {};
    runInNewContext(script, { exports, document, Element, Set,
        getComputedStyle: () => ({ direction }), matchMedia: () => ({ matches: reduced }),
        ResizeObserver: class { constructor(action) { onResize = action; } observe(item) { observed.add(item); } unobserve(item) { observed.delete(item); } }
    });
    return { previous, next, items, document, movements, observed,
        initialize: () => exports.initializeShelves(), click: button => events.click({ target: new Element(button) }),
        scroll: () => scrollEvents.scroll(), resize: () => onResize([{ target: items }]) };
}

test('shelf arrows track overflow, both edges and keyboard focus', () => {
    const view = shelf();
    view.initialize();
    assert.equal(view.previous.hidden, true);
    assert.equal(view.next.hidden, false);
    view.items.scrollLeft = 350;
    view.scroll();
    assert.equal(view.previous.hidden, false);
    assert.equal(view.next.hidden, false);
    view.document.activeElement = view.next;
    view.items.scrollLeft = 700;
    view.scroll();
    assert.equal(view.next.hidden, true);
    assert.equal(view.document.activeElement, view.items);
    view.items.clientWidth = 1000;
    view.resize();
    assert.equal(view.previous.hidden, true);
    assert.equal(view.next.hidden, true);
});

test('shelf buttons respect RTL and reduced motion without any network work', () => {
    for (const direction of ['ltr', 'rtl']) {
        const view = shelf(direction, true);
        view.initialize();
        view.click(view.next);
        assert.equal(view.movements[0].left, direction === 'rtl' ? -255 : 255);
        assert.equal(view.movements[0].behavior, 'auto');
        view.items.scrollLeft = direction === 'rtl' ? -350 : 350;
        view.scroll();
        view.click(view.previous);
        assert.equal(view.movements[1].left, -view.movements[0].left);
    }
});

test('shelf initialization releases removed fragments and does not double-observe', () => {
    const view = shelf();
    view.initialize();
    view.initialize();
    assert.equal(view.observed.size, 1);
    view.items.isConnected = false;
    view.document.querySelectorAll = () => [];
    view.initialize();
    assert.equal(view.observed.size, 0);
    view.resize();
});
