import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Framework/Components/CardPreview.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;

const plain = value => JSON.parse(JSON.stringify(value));

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
            '.media-card[data-preview-src]': 'card', '.media-card__surface': 'surface', '[data-trailer-action]': 'trailerButton', '.media-card__preview': 'panel', '[data-autoplay-previews]': 'shelf'
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
    matches(selector) { return selector === ':hover' ? !!this.hovered : selector === ':popover-open' ? !!this.popoverOpen : selector === ':focus-visible' ? !!this.focusVisible : false; }
    querySelector(selector) {
        if (selector === '[data-card-trailer]') return this.trailerHolder ?? null;
        if (selector === 'iframe') return this.trailerHolder?.children[0] ?? null;
        if (selector === '.media-card__preview-extra') return this.extra ?? null;
        if (selector === '.media-card__surface') return this.surface ?? null;
        if (selector === '.media-card__actions' || selector === '.media-card__bookmark') return null;
        if (selector === '[data-trailer-time]') return this.time ?? null;
        if (selector.startsWith('[data-trailer-action=')) return this.buttons?.[selector.slice(21, -1)] ?? null;
        return null;
    }
    append(child) { child.parent = this; this.children.push(child); this.appended = child; }
    replaceChildren(...children) { this.children = children; }
    remove() { this.removed = true; this.isConnected = false; }
    showPopover() { this.popoverOpen = true; }
    get style() { return this.styles ??= { values: {}, setProperty(name, value) { this.values[name] = value; } }; }
    hidePopover() { this.popoverOpen = false; }
}


// A tiny stand-in for the parts of the DOM the card preview touches, with a clock the test advances.
function world({ canHover = true, autoplay = 'true', answer = { ok: true, status: 200, html: '<div class="media-card__preview" data-preview-mode="trailer" data-trailer-id="Way9Dexny3w"></div>' } } = {}) {
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
    const shelf = new Element('shelf');
    shelf.attributes['data-autoplay-previews'] = autoplay;
    const card = new Element('card', shelf);
    card.dataset.previewSrc = '/MediaPreview?work=2001';
    card.surface = surface;
    surface.parent = card;
    card.hovered = true;

    const panelFor = () => {
        const panel = new Element('panel');
        panel.trailerHolder = new Element('holder', panel);
        panel.time = { textContent: '' };
        panel.buttons = {};
        panel.extra = new Element('extra', panel);
        panel.dataset.trailerId = 'Way9Dexny3w';
        panel.dataset.trailerLabel = 'Trailer';
        return panel;
    };

    const env = {
        document: {
            hidden: false,
            activeElement: null,
            addEventListener: (name, action) => { listeners[name] = action; },
            createElement: kind => { assert.equal(kind, 'iframe'); const frame = new Element('iframe'); frame.listeners = {}; frame.addEventListener = (name, action) => { frame.listeners[name] = action; }; frame.contentWindow = { postMessage: (message) => posted.push(message) }; frames.push(frame); return frame; }
        },
        window: { addEventListener: (name, action) => { listeners['window:' + name] = action; } },
        fetch: async (address, init) => { fetches.push(address); assert.ok(init.signal); return { ok: answer.ok, status: answer.status, text: async () => answer.html }; },
        setTimeout: (action, milliseconds) => { const timer = { action, at: now + milliseconds, live: true }; timers.push(timer); return timer; },
        clearTimeout: timer => { if (timer) timer.live = false; },
        canHover: () => canHover,
        canAutoplay: () => true,
        parse: () => panelFor(),
        measure: () => ({ card: { left: 100, top: 80, width: 144, height: 260 }, row: { left: 0, right: 600 }, viewport: { width: 1000, height: 800 } }),
        viewport: () => ({ width: 400, height: 800 }),
        candidates: () => [{ card, ratio: candidate.ratio, centerX: 200, centerY: 400 }],
        origin: 'http://localhost'
    };
    const candidate = { ratio: 1 };
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
    return { listeners, card, art, button, surface, shelf, frames, fetches, posted, advance, event, stop, env, candidate };
}

test('the preview opens only after the mouse rested on the cover, not before', async () => {
    const w = world();
    const cover = w.art;
    w.listeners.pointerover(w.event(cover));
    await w.advance(500);
    assert.equal(w.fetches.length, 0, 'nothing is requested before the delay of 550 ms');
    await w.advance(100);
    assert.deepEqual(w.fetches, ['/MediaPreview?work=2001']);
    assert.equal(w.card.appended?.popoverOpen, true);
    assert.equal(w.card.appended.attributes.popover, 'manual');
    assert.equal(w.card.dataset.previewOpen, 'true', 'the card under the panel is marked hidden');
    assert.equal(w.frames.length, 0, 'the embed follows the panel after a moment');
    await w.advance(220);
    assert.equal(w.frames.length, 1);
    assert.equal(w.frames[0].src, 'https://www.youtube-nocookie.com/embed/Way9Dexny3w?autoplay=1&mute=1&playsinline=1&controls=0&modestbranding=1&rel=0&enablejsapi=1&origin=http%3A%2F%2Flocalhost');

    // The panel is as high as the card and 1.84 times as wide, over the card.
    const values = w.card.appended.style.values;
    assert.deepEqual([values['--preview-left'], values['--preview-top'], values['--preview-width'], values['--preview-height']], ['100px', '80px', '265px', '260px']);
    w.listeners.keydown({ key: 'Escape' });
    assert.equal(w.card.dataset.previewOpen, undefined, 'the card shows again');
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
    await w.advance(300);
    w.listeners.pointerout(w.event(w.art));
    await w.advance(3000);
    assert.equal(w.fetches.length, 0);

    w.listeners.pointerover(w.event(w.art));
    await w.advance(700);
    const panel = w.card.appended;
    assert.equal(panel.popoverOpen, true);
    w.listeners.pointerout(w.event(w.art));
    await w.advance(100);
    assert.equal(panel.removed, undefined, 'the panel stays through the settle delay of 120 ms');
    await w.advance(40);
    assert.equal(panel.removed, true, 'the panel and its embed leave with the pointer');
});

test('scrolling, Escape, a hidden page and resizing close the preview', async () => {
    for (const close of [w => w.listeners.scroll({ target: null }), w => w.listeners.keydown({ key: 'Escape' }), w => { w.env.document.hidden = true; w.listeners.visibilitychange(); }, w => w.listeners['window:resize'](), w => w.listeners['window:pagehide']()]) {
        const w = world();
            w.listeners.pointerover(w.event(w.art));
        await w.advance(600);
    await w.advance(300);
        const panel = w.card.appended;
        assert.equal(panel.popoverOpen, true);
        close(w);
        assert.equal(panel.removed, true);
    }
});

test('the trailer controls send play, pause, mute and unmute to the embed and keep their state', async () => {
    const w = world();
    w.listeners.pointerover(w.event(w.art));
    await w.advance(600);
    await w.advance(300);
    const panel = w.card.appended;
    assert.equal(panel.dataset.playing, 'true');
    assert.equal(panel.dataset.muted, 'true');
    w.frames[0].listeners.load();
    assert.equal(panel.dataset.trailerReady, 'true');

    const toggle = Object.assign(new Element('trailerButton', panel), { dataset: { trailerAction: 'toggle', labelOn: 'Pause trailer', labelOff: 'Play trailer' } });
    const mute = Object.assign(new Element('trailerButton', panel), { dataset: { trailerAction: 'mute', labelOn: 'Unmute trailer', labelOff: 'Mute trailer' } });
    panel.buttons = { toggle, mute };
    w.listeners.click({ target: toggle });
    w.listeners.click({ target: mute });
    w.listeners.click({ target: toggle });
    w.listeners.click({ target: mute });
    assert.deepEqual(w.posted.map(message => JSON.parse(message)).filter(message => message.event === 'command').map(message => message.func), ['pauseVideo', 'unMute', 'playVideo', 'mute']);
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
    await plain.advance(600);
    await plain.advance(300);
    assert.equal(plain.frames.length, 0, 'no trailer, no embed');

    const slow = world();
    slow.listeners.pointerover(slow.event(slow.art));
    await slow.advance(600);
    await slow.advance(300);
    const panel = slow.card.appended;
    assert.equal(slow.frames.length, 1);
    await slow.advance(7000);
    assert.equal(panel.dataset.previewMode, 'description');
    assert.equal(panel.trailerHolder.children.length, 0, 'the embed is dropped');
});

test('a title the server has no preview for opens nothing', async () => {
    const w = world({ answer: { ok: true, status: 204, html: '' } });
    w.listeners.pointerover(w.event(w.art));
    await w.advance(600);
    await w.advance(300);
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

test('the geometry: 1.84 times the card, shifted to stay inside the row and the viewport, never moving the card', () => {
    const { previewWidth, panelShift, previewBox } = load();
    assert.equal(previewWidth(144), 265);
    assert.equal(previewWidth(176), 324);
    // The mockup's own numbers: a card 159 wide opens 292 wide.
    assert.equal(previewWidth(159), 293);
    assert.equal(panelShift(100, { left: 0, right: 600 }, 265), 0, 'room enough: no shift');
    assert.equal(panelShift(2, { left: 0, right: 600 }, 265), 2, 'a card at the left edge keeps four pixels of margin');
    assert.equal(panelShift(500, { left: 0, right: 600 }, 265), -(265 + 8 - 100), 'a card at the right edge moves the panel left');
    assert.deepEqual(plain(previewBox({ left: 100, top: 80, width: 144, height: 260 }, { left: 0, right: 600 }, { width: 1000, height: 800 })), { left: 100, top: 80, width: 265, height: 260 });
    assert.deepEqual(plain(previewBox({ left: 500, top: 80, width: 144, height: 260 }, { left: 0, right: 600 }, { width: 1000, height: 800 })), { left: 327, top: 80, width: 265, height: 260 });
    // The viewport limits it even where the row reaches further; the height follows the card.
    assert.deepEqual(plain(previewBox({ left: 300, top: 700, width: 144, height: 260 }, null, { width: 400, height: 800 })), { left: 127, top: 536, width: 265, height: 260 });
});

test('the time of the trailer and what the provider reports', () => {
    const { formatTime, readTrailerInfo, listeningMessage } = load();
    assert.equal(formatTime(12.9), '0:12');
    assert.equal(formatTime(144), '2:24');
    assert.equal(formatTime(3744), '1:02:24');
    assert.deepEqual(plain(readTrailerInfo(JSON.stringify({ event: 'infoDelivery', info: { currentTime: 12.5, duration: 144, playerState: 1 } }))), { current: 12.5, duration: 144, state: 1 });
    assert.deepEqual(plain(readTrailerInfo({ info: { playerState: 2 } })), { state: 2 });
    assert.equal(readTrailerInfo('not json'), undefined);
    assert.equal(readTrailerInfo(JSON.stringify({ event: 'onReady' })), undefined);
    assert.deepEqual(JSON.parse(listeningMessage()).channel, 'widget');
});

test('the progress line follows the provider, and without a time it falls back to the running line', async () => {
    const live = world();
    live.listeners.pointerover(live.event(live.art));
    await live.advance(600);
    await live.advance(300);
    const panel = live.card.appended;
    const frame = live.frames[0];
    frame.listeners.load();
    assert.equal(JSON.parse(live.posted.at(-1)).event, 'listening', 'the listener is registered with the player');
    const from = { origin: 'https://www.youtube-nocookie.com', source: frame.contentWindow };
    live.listeners['window:message']({ ...from, data: JSON.stringify({ event: 'infoDelivery', info: { currentTime: 12, duration: 144, playerState: 1 } }) });
    assert.equal(panel.dataset.progress, 'live');
    assert.equal(panel.style.values['--trailer-progress'], '8.33%');
    assert.equal(panel.time.textContent, '0:12 / 2:24');
    // A message of another origin or window is ignored.
    live.listeners['window:message']({ origin: 'https://evil.example', source: frame.contentWindow, data: JSON.stringify({ info: { currentTime: 100, duration: 144 } }) });
    live.listeners['window:message']({ origin: from.origin, source: {}, data: JSON.stringify({ info: { currentTime: 100, duration: 144 } }) });
    assert.equal(panel.time.textContent, '0:12 / 2:24');
    // The player says it was paused: the control follows.
    panel.buttons = { toggle: Object.assign(new Element('trailerButton', panel), { dataset: { labelOn: 'Pause trailer', labelOff: 'Play trailer' } }) };
    live.listeners['window:message']({ ...from, data: JSON.stringify({ info: { playerState: 2 } }) });
    assert.equal(panel.dataset.playing, 'false');
    assert.equal(panel.buttons.toggle.attributes['aria-label'], 'Play trailer');

    const silent = world();
    silent.listeners.pointerover(silent.event(silent.art));
    await silent.advance(600);
    await silent.advance(300);
    silent.frames[0].listeners.load();
    await silent.advance(4100);
    assert.equal(silent.card.appended.dataset.progress, 'fake');
});

test('the keyboard focus opens it after the same rest, and leaving the card closes it', async () => {
    const w = world();
    w.env.document.activeElement = w.surface;
    w.surface.focusVisible = true;
    w.listeners.focusin({ target: w.surface });
    await w.advance(500);
    assert.equal(w.fetches.length, 0);
    await w.advance(100);
    assert.equal(w.card.appended?.popoverOpen, true);
    w.listeners.focusout({ target: w.surface, relatedTarget: null });
    await w.advance(130);
    assert.equal(w.card.appended.removed, true);

    // A focus by the mouse (not visible) starts nothing.
    const mouse = world();
    mouse.listeners.focusin({ target: mouse.surface });
    await mouse.advance(2000);
    assert.equal(mouse.fetches.length, 0);
});

test('a card that lingers on a touch device starts after the dwell, one at a time, and only where the profile allows it', async () => {
    const { pickCandidate, createDwell, dwellDelay } = load();
    assert.equal(dwellDelay, 2000);
    const viewport = { width: 400, height: 800 };
    assert.equal(pickCandidate([{ id: 'a', ratio: 0.5, centerX: 200, centerY: 400 }], viewport), undefined, 'half in view is not lingering');
    assert.equal(pickCandidate([{ id: 'a', ratio: 1, centerX: 200, centerY: 50 }], viewport), undefined, 'the edge of the screen is not the middle');
    assert.equal(pickCandidate([{ id: 'far', ratio: 1, centerX: 20, centerY: 300 }, { id: 'near', ratio: 0.9, centerX: 190, centerY: 410 }], viewport), 'near', 'the nearest to the middle');

    // The dwell fires once for a candidate that stays and starts again for another.
    const fired = [];
    let now = 0;
    const timers = [];
    const dwell = createDwell(2000, (action, ms) => { const timer = { action, at: now + ms, live: true }; timers.push(timer); return timer; }, timer => { if (timer) timer.live = false; }, id => fired.push(id));
    const run = ms => { now += ms; timers.filter(timer => timer.live && timer.at <= now).forEach(timer => { timer.live = false; timer.action(); }); };
    dwell.update('a'); run(1500); dwell.update('a'); run(400);
    assert.deepEqual(fired, []);
    run(200);
    assert.deepEqual(fired, ['a']);
    dwell.update('a'); run(5000);
    assert.deepEqual(fired, ['a'], 'the same card does not start twice');
    dwell.update('b'); run(1000); dwell.update(undefined); run(5000);
    assert.deepEqual(fired, ['a'], 'a card that leaves before the end starts nothing');
    dwell.update('b'); run(2000);
    assert.deepEqual(fired, ['a', 'b']);

    // In the preview: nothing before 2 s, then the panel with the muted trailer.
    const touch = world({ canHover: false });
    touch.stop.evaluate();
    await touch.advance(1900);
    assert.equal(touch.fetches.length, 0, 'no data before the dwell is over');
    await touch.advance(200);
    assert.deepEqual(touch.fetches, ['/MediaPreview?work=2001']);
    assert.equal(touch.card.appended.popoverOpen, true);
    await touch.advance(300);
    assert.match(touch.frames[0].src, /mute=1/);

    // Scrolling away ends it; a card that is only half in view does not start.
    touch.listeners.scroll({ target: null });
    assert.equal(touch.card.appended.removed, true);
    const half = world({ canHover: false });
    half.candidate.ratio = 0.5;
    half.stop.evaluate();
    await half.advance(5000);
    assert.equal(half.fetches.length, 0);

    // The profile's switch: off means no automatic preview at all.
    const off = world({ canHover: false, autoplay: 'false' });
    off.stop.evaluate();
    await off.advance(5000);
    assert.equal(off.fetches.length, 0);

    // A tap elsewhere ends an open one and it does not come back by itself.
    const tap = world({ canHover: false });
    tap.stop.evaluate();
    await tap.advance(2100);
    assert.equal(tap.card.appended.popoverOpen, true);
    tap.listeners.click({ target: new Element('page') });
    assert.equal(tap.card.appended.removed, true);
    tap.stop.evaluate();
    await tap.advance(5000);
    assert.equal(tap.fetches.length, 1);
});
