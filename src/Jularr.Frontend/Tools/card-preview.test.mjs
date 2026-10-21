import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/CardPreview.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;

function load() {
    const exports = {};
    runInNewContext(script, { exports, encodeURIComponent, JSON, AbortController, Element, Node });
    return exports;
}

class Node {}
class Element extends Node {
    constructor(kind, parent = null) { super(); this.kind = kind; this.parent = parent; this.dataset = {}; this.attributes = {}; this.isConnected = true; this.children = []; }
    closest(selector) {
        const kinds = {
            '.media-card[data-preview-src]': 'card', '.media-card__surface': 'surface', '[data-trailer-action]': 'trailerButton', '.media-card__preview': 'panel'
        };
        const controls = 'button, a.button, form, input, .media-card__actions, .media-card__bookmark, .media-availability-badge, .media-card__preview';
        if (selector === controls) return ['button', 'actions', 'panel'].includes(this.kind) ? this : null;
        let element = this;
        while (element) {
            if (kinds[selector] === element.kind) return element;
            if (selector === '.media-card__surface' && ['art', 'surface', 'button', 'actions'].includes(element.kind) && element.parent?.kind === 'surface') return element.parent;
            element = element.parent;
        }
        return null;
    }
    contains(node) { for (let element = node; element; element = element.parent) if (element === this) return true; return false; }
    setAttribute(name, value) { this.attributes[name] = value; }
    getAttribute(name) { return this.attributes[name] ?? null; }
    matches(selector) { return selector === ':hover' ? !!this.hovered : selector === ':popover-open' ? !!this.popoverOpen : false; }
    querySelector(selector) {
        if (selector === '[data-card-trailer]') return this.trailerHolder ?? null;
        if (selector === 'iframe') return this.trailerHolder?.children[0] ?? null;
        if (selector === '.media-card__preview-extra') return this.extra ?? null;
        if (selector === '.media-card__surface') return this.surface ?? null;
        if (selector === '.media-card__actions' || selector === '.media-card__bookmark') return null;
        return null;
    }
    append(child) { child.parent = this; this.children.push(child); this.appended = child; }
    replaceChildren(...children) { this.children = children; }
    remove() { this.removed = true; this.isConnected = false; }
    showPopover() { this.popoverOpen = true; }
    hidePopover() { this.popoverOpen = false; }
}


// A tiny stand-in for the parts of the DOM the card preview touches, with a clock the test advances.
function world({ canHover = true, answer = { ok: true, status: 200, html: '<div class="media-card__preview" data-preview-mode="trailer" data-trailer-id="Way9Dexny3w"></div>' } } = {}) {
    const listeners = {};
    const timers = [];
    let now = 0;
    const frames = [];
    const fetches = [];
    const posted = [];

    const surface = new Element('surface');
    const art = new Element('art', surface);
    const button = new Element('button', surface);
    surface.getAttribute = () => 'Dune';
    const card = new Element('card');
    card.dataset.previewSrc = '/MediaPreview?work=2001';
    card.surface = surface;
    surface.parent = card;
    card.hovered = true;

    const panelFor = () => {
        const panel = new Element('panel');
        panel.trailerHolder = new Element('holder', panel);
        panel.extra = new Element('extra', panel);
        panel.dataset.trailerId = 'Way9Dexny3w';
        panel.dataset.trailerLabel = 'Trailer';
        return panel;
    };

    const env = {
        document: {
            hidden: false,
            addEventListener: (name, action) => { listeners[name] = action; },
            createElement: kind => { assert.equal(kind, 'iframe'); const frame = new Element('iframe'); frame.listeners = {}; frame.addEventListener = (name, action) => { frame.listeners[name] = action; }; frame.contentWindow = { postMessage: (message) => posted.push(message) }; frames.push(frame); return frame; }
        },
        window: { addEventListener: (name, action) => { listeners['window:' + name] = action; } },
        fetch: async (address, init) => { fetches.push(address); assert.ok(init.signal); return { ok: answer.ok, status: answer.status, text: async () => answer.html }; },
        setTimeout: (action, milliseconds) => { const timer = { action, at: now + milliseconds, live: true }; timers.push(timer); return timer; },
        clearTimeout: timer => { if (timer) timer.live = false; },
        canHover: () => canHover,
        parse: () => panelFor(),
        origin: 'http://localhost'
    };
    const stop = load().attachCardPreview(env);

    const event = (target, extra = {}) => ({ target, pointerType: 'mouse', relatedTarget: null, ...extra });
    // Runs every timer that is due, in order, and lets the pending answers settle.
    const advance = async milliseconds => {
        now += milliseconds;
        for (let guard = 0; guard < 20; guard++) {
            const due = timers.filter(timer => timer.live && timer.at <= now);
            if (due.length === 0) break;
            due.forEach(timer => { timer.live = false; timer.action(); });
            await new Promise(resolve => setImmediate(resolve));
        }
        await new Promise(resolve => setImmediate(resolve));
    };
    return { listeners, card, art, button, surface, frames, fetches, posted, advance, event, stop, env };
}

test('the preview opens only after the mouse rested on the cover, not before', async () => {
    const w = world();
    const cover = w.art;
    w.listeners.pointerover(w.event(cover));
    await w.advance(1700);
    assert.equal(w.fetches.length, 0, 'nothing is requested before the delay');
    await w.advance(200);
    assert.deepEqual(w.fetches, ['/MediaPreview?work=2001']);
    assert.equal(w.card.appended?.popoverOpen, true);
    assert.equal(w.card.appended.attributes.popover, 'manual');
    assert.equal(w.frames.length, 1);
    assert.match(w.frames[0].src, /^https:\/\/www\.youtube-nocookie\.com\/embed\/Way9Dexny3w\?autoplay=1&mute=1&controls=0/);
});

test('a control of the card, a touch pointer or a device without hover start nothing', async () => {
    const w = world();
    w.listeners.pointerover(w.event(w.button));
    await w.advance(5000);
    w.listeners.pointerover(w.event(w.art, { pointerType: 'touch' }));
    await w.advance(5000);
    assert.equal(w.fetches.length, 0);

    const noHover = world({ canHover: false });
    noHover.listeners.pointerover(noHover.event(noHover.art));
    await noHover.advance(5000);
    assert.equal(noHover.fetches.length, 0);
});

test('leaving the cover before the delay cancels it, and leaving later closes the panel', async () => {
    const w = world();
    w.listeners.pointerover(w.event(w.art));
    await w.advance(1000);
    w.listeners.pointerout(w.event(w.art));
    await w.advance(3000);
    assert.equal(w.fetches.length, 0);

    w.listeners.pointerover(w.event(w.art));
    await w.advance(2000);
    const panel = w.card.appended;
    assert.equal(panel.popoverOpen, true);
    w.listeners.pointerout(w.event(w.art));
    await w.advance(200);
    assert.equal(panel.removed, true, 'the panel and its embed leave with the pointer');
});

test('scrolling, Escape, a hidden page and resizing close the preview', async () => {
    for (const close of [w => w.listeners.scroll(), w => w.listeners.keydown({ key: 'Escape' }), w => { w.env.document.hidden = true; w.listeners.visibilitychange(); }, w => w.listeners['window:resize'](), w => w.listeners['window:pagehide']()]) {
        const w = world();
            w.listeners.pointerover(w.event(w.art));
        await w.advance(2000);
        const panel = w.card.appended;
        assert.equal(panel.popoverOpen, true);
        close(w);
        assert.equal(panel.removed, true);
    }
});

test('the trailer controls send play, pause, mute and unmute to the embed and keep their state', async () => {
    const w = world();
    w.listeners.pointerover(w.event(w.art));
    await w.advance(2000);
    const panel = w.card.appended;
    assert.equal(panel.dataset.playing, 'true');
    assert.equal(panel.dataset.muted, 'true');
    w.frames[0].listeners.load();
    assert.equal(panel.dataset.trailerReady, 'true');

    const toggle = Object.assign(new Element('trailerButton', panel), { dataset: { trailerAction: 'toggle', labelOn: 'Pause trailer', labelOff: 'Play trailer' } });
    const mute = Object.assign(new Element('trailerButton', panel), { dataset: { trailerAction: 'mute', labelOn: 'Unmute trailer', labelOff: 'Mute trailer' } });
    w.listeners.click({ target: toggle });
    w.listeners.click({ target: mute });
    w.listeners.click({ target: toggle });
    w.listeners.click({ target: mute });
    assert.deepEqual(w.posted.map(message => JSON.parse(message).func), ['pauseVideo', 'unMute', 'playVideo', 'mute']);
    assert.equal(panel.dataset.playing, 'true');
    assert.equal(panel.dataset.muted, 'true');
    assert.equal(toggle.attributes['aria-label'], 'Pause trailer');
    assert.equal(panel.removed, undefined, 'the controls keep the preview open');
});

test('a title without a trailer shows its description and a slow embed falls back to it', async () => {
    const plain = world({ answer: { ok: true, status: 200, html: '<div class="media-card__preview" data-preview-mode="description"></div>' } });
    const { parse } = plain.env;
    plain.env.parse = () => { const panel = parse(); panel.dataset.trailerId = undefined; delete panel.dataset.trailerId; return panel; };
    plain.listeners.pointerover(plain.event(plain.art));
    await plain.advance(2000);
    assert.equal(plain.frames.length, 0, 'no trailer, no embed');

    const slow = world();
    slow.listeners.pointerover(slow.event(slow.art));
    await slow.advance(2000);
    const panel = slow.card.appended;
    assert.equal(slow.frames.length, 1);
    await slow.advance(7000);
    assert.equal(panel.dataset.previewMode, 'description');
    assert.equal(panel.trailerHolder.children.length, 0, 'the embed is dropped');
});

test('a title the server has no preview for opens nothing', async () => {
    const w = world({ answer: { ok: true, status: 204, html: '' } });
    w.listeners.pointerover(w.event(w.art));
    await w.advance(2500);
    assert.equal(w.fetches.length, 1);
    assert.equal(w.card.appended, undefined);
});

test('only well-formed video keys are embedded', () => {
    const { isVideoKey, trailerAddress, trailerCommand } = load();
    assert.equal(isVideoKey('Way9Dexny3w'), true);
    assert.equal(isVideoKey('short'), false);
    assert.equal(isVideoKey('has space!!'), false);
    assert.equal(isVideoKey(undefined), false);
    assert.match(trailerAddress('Way9Dexny3w', 'http://localhost:5281'), /enablejsapi=1&origin=http%3A%2F%2Flocalhost%3A5281$/);
    assert.deepEqual(JSON.parse(trailerCommand('mute')), { event: 'command', func: 'mute', args: [] });
});
