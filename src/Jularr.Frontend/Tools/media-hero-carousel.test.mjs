import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/MediaHeroCarousel.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS }
}).outputText;

function mount(count = 3, reduced = false) {
    const timerCallbacks = new Map();
    const documentEvents = {};
    const rootEvents = {};
    let timerId = 0;
    let intersection;
    const motion = { matches: reduced, addEventListener() {} };
    const document = {
        hidden: false, addEventListener: (name, action) => { documentEvents[name] = action; },
        removeEventListener: name => { delete documentEvents[name]; },
        querySelectorAll: () => [carousel]
    };

    class Element {
        constructor(dataset = {}) {
            this.dataset = dataset;
            this.attrs = {};
            this.classList = { classes: new Set(), toggle: (name, enable) => {
                if (enable) this.classList.classes.add(name);
                else this.classList.classes.delete(name);
            } };
            this.isConnected = true;
        }
        closest(selector) {
            if (selector === '[data-hero-direction]' && this.dataset.heroDirection) return this;
            if (selector === '[data-hero-slide]' && this.dataset.heroSlide !== undefined) return this;
            return null;
        }
        setAttribute(name, value) { this.attrs[name] = value; }
        removeAttribute(name) { delete this.attrs[name]; }
    }

    const slides = Array.from({ length: count }, () => new Element());
    const segments = Array.from({ length: count }, (_, i) => new Element({ heroSlide: String(i) }));
    const carousel = new Element();
    carousel.querySelectorAll = selector => selector === '[data-hero-slide-item]' ? slides
        : selector === '[data-hero-slide]' ? segments : [];
    carousel.addEventListener = (name, action) => { rootEvents[name] = action; };
    carousel.contains = target => target === carousel;

    class IntersectionObserver {
        constructor(callback) { intersection = callback; }
        observe() {}
    }

    const exports = {};
    runInNewContext(script, {
        exports, document, window: { IntersectionObserver }, IntersectionObserver, Element,
        matchMedia: () => motion,
        setInterval: callback => { timerCallbacks.set(++timerId, callback); return timerId; },
        clearInterval: id => timerCallbacks.delete(id),
        WeakSet, Set
    });

    exports.initializeHeroCarousels();
    const active = () => slides.findIndex(slide => slide.classList.classes.has('is-active'));
    return {
        active, slides, segments, carousel, motion, document,
        get timerCount() { return timerCallbacks.size; },
        tick() { for (const fn of [...timerCallbacks.values()]) fn(); },
        arrow(direction) { rootEvents.click({ target: new Element({ heroDirection: direction }) }); },
        choose(index) { rootEvents.click({ target: segments[index] }); },
        enter() { rootEvents.mouseenter(); }, leave() { rootEvents.mouseleave(); },
        focus() { rootEvents.focusin(); },
        blur() { rootEvents.focusout({ relatedTarget: null }); },
        intersect(visible) { intersection?.([{ isIntersecting: visible }]); },
        visibility(hidden) { document.hidden = hidden; documentEvents.visibilitychange(); },
        swipe(from, to) {
            rootEvents.touchstart({ changedTouches: [{ clientX: from }] });
            rootEvents.touchend({ changedTouches: [{ clientX: to }] });
        },
        initializeAgain() { exports.initializeHeroCarousels(); }
    };
}

test('carousel has at most one timer and manual controls select exactly one interactive slide', () => {
    const ui = mount();
    assert.equal(ui.timerCount, 1);
    ui.tick();
    assert.equal(ui.active(), 1);
    ui.arrow('next');
    assert.equal(ui.active(), 2);
    ui.arrow('next');
    assert.equal(ui.active(), 0);
    ui.arrow('previous');
    assert.equal(ui.active(), 2);
    ui.choose(1);
    assert.equal(ui.active(), 1);
    assert.equal(ui.slides[1].attrs['aria-hidden'], 'false');
    assert.equal(ui.slides[1].attrs.inert, undefined);
    assert.equal(ui.slides[0].attrs['aria-hidden'], 'true');
    assert.equal(ui.slides[0].attrs.inert, '');
    assert.equal(ui.segments[1].attrs['aria-current'], 'true');
    assert.equal(ui.timerCount, 1);
    ui.initializeAgain();
    assert.equal(ui.timerCount, 1);
});

test('swipe navigates; hover, focus, background and offscreen pause autoplay', () => {
    const ui = mount();
    ui.swipe(200, 100);
    assert.equal(ui.active(), 1);
    ui.swipe(100, 200);
    assert.equal(ui.active(), 0);
    ui.enter();
    assert.equal(ui.timerCount, 0);
    ui.leave();
    assert.equal(ui.timerCount, 1);
    ui.focus();
    assert.equal(ui.timerCount, 0);
    ui.blur();
    assert.equal(ui.timerCount, 1);
    ui.visibility(true);
    assert.equal(ui.timerCount, 0);
    ui.visibility(false);
    assert.equal(ui.timerCount, 1);
    ui.intersect(false);
    assert.equal(ui.timerCount, 0);
    ui.intersect(true);
    assert.equal(ui.timerCount, 1);
});

test('reduced-motion and a single real item never create autoplay', () => {
    assert.equal(mount(3, true).timerCount, 0);
    assert.equal(mount(1).timerCount, 0);
});
