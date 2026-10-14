import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/MediaCard.entry.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText;

function preview(viewportWidth = 1280, slotLeft = 100) {
    const pending = new Map();
    const events = {};
    const frames = [];
    let sequence = 0;
    class Element {}
    const document = { hidden: false, activeElement: null,
        createElement: () => { const frame = {}; frames.push(frame); return frame; },
        addEventListener: (name, action) => { events[name] = action; } };
    runInNewContext(script, {
        document, Element, Node: Element, matchMedia: () => ({ matches: true }),
        window: { innerWidth: viewportWidth, addEventListener: (name, action) => { events[name] = action; } },
        setTimeout: (action, delay) => { assert.equal(delay, 2000); pending.set(++sequence, action); return sequence; },
        clearTimeout: id => pending.delete(id)
    });
    return { document, events, frames, pending,
        card() {
            const target = { children: [], replaceChildren(...children) { this.children = children; } };
            const card = Object.assign(new Element(), {
                isConnected: true, dataset: { trailerId: 'k4xGqY5IDBE', trailerLabel: 'Play trailer' }, target,
                parentElement: { getBoundingClientRect: () => ({ left: slotLeft, top: 80, width: 260 }) },
                style: { values: {}, setProperty(name, value) { this.values[name] = value; }, removeProperty(name) { delete this.values[name]; } },
                querySelector: () => target, closest() { return this; }, contains(element) { return element === this; }
            });
            return card;
        },
        enter(card, pointerType = 'mouse') { events.pointerover({ target: card, pointerType }); },
        leave(card, relatedTarget = null) { events.pointerout({ target: card, relatedTarget }); },
        advance() { for (const [id, action] of [...pending]) { if (pending.has(id)) { pending.delete(id); action(); } } }
    };
}

test('fragment cards require sustained mouse hover, keep one trailer and release on leaving', () => {
    const view = preview();
    const first = view.card();
    view.enter(first);
    assert.equal(view.frames.length, 0);
    view.leave(first);
    view.advance();
    assert.equal(view.frames.length, 0);

    view.enter(first);
    view.advance();
    assert.match(view.frames[0].src, /^https:\/\/www\.youtube\.com\/embed\/k4xGqY5IDBE\?autoplay=1&mute=1&playsinline=1&controls=0$/);
    assert.equal(first.dataset.expanded, 'true');
    assert.equal(first.style.values['--preview-width'], '420px');
    assert.equal(first.style.values['--preview-left'], '24px');
    view.leave(first, first);
    assert.equal(first.target.children.length, 1);

    // Added after initial script load.
    const second = view.card();
    view.enter(second);
    view.advance();
    assert.equal(first.target.children.length, 0);
    assert.equal(second.target.children.length, 1);
    view.leave(second);
    assert.equal(second.target.children.length, 0);
    assert.equal(second.dataset.expanded, undefined);
    assert.equal(Object.keys(second.style.values).length, 0);
});

test('touch, missing or invalid trailers and removed fragments never start playback', () => {
    const view = preview();
    const card = view.card();
    view.enter(card, 'touch');
    assert.equal(view.pending.size, 0);
    delete card.dataset.trailerId;
    view.enter(card);
    assert.equal(view.pending.size, 0);
    card.dataset.trailerId = 'https://untrusted.example/video';
    view.enter(card);
    view.advance();
    assert.equal(view.frames.length, 0);
    card.dataset.trailerId = 'k4xGqY5IDBE';
    view.enter(card);
    card.isConnected = false;
    view.advance();
    assert.equal(view.frames.length, 0);
});

test('expanded previews fit both viewport edges and clean up on movement or navigation', () => {
    for (const [viewportWidth, slotLeft] of [[390, 65], [1280, 24], [1280, 960]]) {
        const view = preview(viewportWidth, slotLeft);
        const card = view.card();
        for (const event of ['scroll', 'resize', 'pagehide', 'visibilitychange']) {
            view.enter(card);
            view.advance();
            const width = parseFloat(card.style.values['--preview-width']);
            const left = parseFloat(card.style.values['--preview-left']);
            assert.ok(left >= 24 && left + width <= viewportWidth - 24);
            assert.ok(width > 260);
            assert.equal(card.style.values['--preview-top'], '80px');
            view.document.hidden = true;
            view.events[event]();
            assert.equal(card.target.children.length, 0);
            assert.equal(card.dataset.expanded, undefined);
        }
    }
});
