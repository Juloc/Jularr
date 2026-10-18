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
        events, registrations, initialize: exports.initializeCards, document, card, poster, link,
        click(target) { events.get('click')({ target }); },
        escape(target = poster) { events.get('keydown')({ key: 'Escape', target }); }
    };
}

test('repeated entry initialization registers one delegated click and keyboard handler', () => {
    const view = mounted();
    view.initialize();
    view.initialize();
    assert.deepEqual(view.registrations, ['click', 'keydown']);
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

test('card script never starts a trailer or expands a poster', () => {
    assert.doesNotMatch(source, /youtube\.com\/embed|data-hover-trailer|data-expanded|pointerover|setTimeout|createElement/);
});
