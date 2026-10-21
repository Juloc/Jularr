import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/MediaCard.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;

function mounted(desktop = false) {
    const events = new Map();
    const registrations = [];
    const exports = {};
    class Element {}

    const document = {
        activeElement: null,
        addEventListener(name, action) {
            registrations.push(name);
            events.set(name, action);
        }
    };
    runInNewContext(script, {
        document,
        exports,
        Element,
        HTMLElement: Element,
        Node: Element,
        matchMedia: () => ({ matches: desktop })
    });
    exports.initializeCards();

    const card = Object.assign(new Element(), {
        dataset: {},
        contains(element) { return element === this || element === poster || element === link; },
        hasAttribute(name) { return name === 'data-open' && this.dataset.open !== undefined; }
    });
    const poster = Object.assign(new Element(), {
        closest(selector) { return selector === '[data-media-card]' ? card : null; },
        blurred: false,
        blur() { this.blurred = true; }
    });
    const link = Object.assign(new Element(), {
        closest(selector) {
            return selector === '[data-media-card]' ? card
                : selector === 'a, button, input, summary' ? this : null;
        }
    });
    poster.closest = selector => selector === '[data-media-card]' ? card : null;
    document.activeElement = poster;

    return {
        events, registrations, initialize: exports.initializeCards, document, card, poster, link, Element,
        click(target) { events.get('click')({ target }); },
        escape(target = poster) { events.get('keydown')({ key: 'Escape', target }); }
    };
}

test('repeated entry initialization registers one delegated click and keyboard handler', () => {
    const view = mounted();
    view.initialize();
    view.initialize();
    assert.deepEqual(view.registrations, ['click', 'keydown', 'mouseover', 'focusin', 'mouseout', 'focusout', 'scroll']);
    assert.equal(view.events.has('pointerover'), false);
});

test('touch taps in a noninteractive card area toggle the existing status reveal', () => {
    const view = mounted();
    view.click(view.poster);
    assert.equal(view.card.dataset.open, 'true');
    view.click(view.poster);
    assert.equal(view.card.dataset.open, undefined);
    view.click(view.poster);
    view.escape();
    assert.equal(view.card.dataset.open, undefined);
    assert.equal(view.poster.blurred, true);
});

test('native links and buttons preserve their navigation without toggling the status', () => {
    const view = mounted();
    view.click(view.link);
    assert.equal(view.card.dataset.open, undefined);
    view.click(view.poster);
    view.click(view.link);
    assert.equal(view.card.dataset.open, 'true');
});

test('fine-pointer desktop clicks never toggle the touch status', () => {
    const view = mounted(true);
    view.click(view.poster);
    assert.equal(view.card.dataset.open, undefined);
    view.escape();
    assert.equal(view.poster.blurred, true);
});

function withTitle(view, clipped) {
    const tip = { hidden: true, textContent: '' };
    const holder = Object.assign(new view.Element(), {
        dataset: { full: 'The complete title of the work' },
        scrollWidth: clipped ? 180 : 100,
        clientWidth: 100,
        querySelectorAll() { return []; },
        contains(node) { return node === this; },
        closest(selector) { return selector.includes('media-card__title') ? this : selector === '.media-card' ? { querySelector: () => tip } : null; }
    });
    return { holder, tip };
}

test('a clipped title shows its complete text over the cover and hides on leave', () => {
    const view = mounted();
    const { holder, tip } = withTitle(view, true);
    view.events.get('mouseover')({ target: holder });
    assert.equal(tip.hidden, false);
    assert.equal(tip.textContent, 'The complete title of the work');
    view.events.get('mouseout')({ target: holder, relatedTarget: null });
    assert.equal(tip.hidden, true);
    view.events.get('focusin')({ target: holder });
    assert.equal(tip.hidden, false);
    view.escape(holder);
    assert.equal(tip.hidden, true);
});

test('a title that fits shows no tip', () => {
    const view = mounted();
    const { holder, tip } = withTitle(view, false);
    view.events.get('mouseover')({ target: holder });
    assert.equal(tip.hidden, true);
});

test('card script never starts a trailer or expands a poster', () => {
    assert.doesNotMatch(source, /youtube\.com\/embed|data-hover-trailer|data-expanded|pointerover|setTimeout|createElement/);
});
