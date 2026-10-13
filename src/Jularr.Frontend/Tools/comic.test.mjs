import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../Modules/Reader/comic.entry.ts', import.meta.url), 'utf8').replace('import "./comic.scss";', '');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;

// A comic of five pages in a minimal DOM.
function comic(settings, direction = 'rtl') {
    const saved = [], keys = {}, controls = {};
    const stage = { dataset: {}, shown: [], replaceChildren: (...images) => stage.shown = images.map(image => image.page), addEventListener() {}, getBoundingClientRect: () => ({ left: 0 }), clientWidth: 100 };
    const position = { textContent: '' };
    const control = name => ({ setAttribute() {}, addEventListener: (_, action) => controls[name] = action });
    const element = {
        dataset: { count: '5', page: '0', pages: '/pages?path=', direction, progressUrl: '/progress', antiforgery: 'token', pageLabel: 'Page' },
        querySelector: selector => selector === '[data-role=stage]' ? stage : selector === '[data-role=position]' ? position
            : selector.startsWith('[data-action=') ? control(selector.slice(13, -1)) : selector.startsWith('[data-role=') && selector !== '[data-role=save-error]' ? { hidden: false, addEventListener() {} } : null
    };
    class Image {
        addEventListener() {}
        set src(value) { this.page = Number(decodeURIComponent(value).slice(-5)); }
    }

    runInNewContext(script, {
        exports: {}, require: () => ({ progressReporter: () => value => saved.push(value) }), Image, encodeURIComponent, String, Number, Math,
        document: { querySelector: () => element, addEventListener: (name, action) => keys[name] = action },
        window: { addEventListener() {}, matchMedia: () => ({ matches: false }) },
        history: { replaceState() {} }, URL, location: { href: 'https://jularr.test/Read/1?p=0' },
        localStorage: { getItem: () => JSON.stringify(settings), setItem() {} }
    });
    const press = key => keys.keydown({ key, target: null, preventDefault() {} });
    return { stage, position, saved, press, keys };
}

test('two-page mode keeps the cover alone and pairs the following pages', () => {
    const ui = comic({ spread: true });
    assert.deepEqual(ui.stage.shown, [0]);
    ui.press('ArrowLeft');
    assert.deepEqual(ui.stage.shown, [1, 2]);
    assert.equal(ui.position.textContent, '2–3 / 5');
    ui.press('ArrowLeft');
    assert.deepEqual(ui.stage.shown, [3, 4]);
    assert.deepEqual({ ...ui.saved.at(-1) }, { locator: 'p3', progression: 1, isCompleted: true });
    ui.press('ArrowRight');
    assert.deepEqual(ui.stage.shown, [1, 2]);
});

test('left-to-right comics turn forward with the right arrow', () => {
    const ui = comic({ spread: false }, 'ltr');
    ui.press('ArrowRight');
    assert.deepEqual(ui.stage.shown, [1]);
    assert.deepEqual({ ...ui.saved.at(-1) }, { locator: 'p1', progression: 0.4, isCompleted: false });
});

test('modified keys are left to the browser', () => {
    const ui = comic({ spread: true });
    ui.press('ArrowLeft');
    assert.deepEqual(ui.stage.shown, [1, 2]);
    ui.keys.keydown({ key: 'ArrowLeft', altKey: true, target: null, preventDefault() { throw new Error('History navigation was blocked'); } });
    assert.deepEqual(ui.stage.shown, [1, 2]);
});
