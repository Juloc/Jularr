import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

function load(name) {
    const source = readFileSync(new URL(`../Modules/Reader/${name}.ts`, import.meta.url), 'utf8');
    const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const exports = {};
    runInNewContext(script, { exports, URL });
    return exports;
}

const view = load('PageView');
const switchView = load('ViewSwitch');
const smartFit = load('SmartPageFit');

test('a page fits the whole viewport or its width, and zoom multiplies either', () => {
    const viewport = { width: 800, height: 600 }, page = { width: 400, height: 800 };
    assert.deepEqual({ ...view.pageSize(viewport, page, 'page', 1) }, { width: 300, height: 600 });
    assert.deepEqual({ ...view.pageSize(viewport, page, 'width', 1) }, { width: 800, height: 1600 });
    assert.deepEqual({ ...view.pageSize(viewport, page, 'page', 2) }, { width: 600, height: 1200 });
    assert.deepEqual({ ...view.pageSize({ width: 0, height: 600 }, page, 'page', 1) }, { width: 0, height: 0 });
});

test('zoom stays between the whole page and five times, by steps, wheel and pinch', () => {
    assert.equal(view.clampZoom(0.2), 1);
    assert.equal(view.clampZoom(99), 5);
    assert.equal(view.clampZoom(Number.NaN), 1);
    assert.equal(view.stepZoom(1, 1), 1.25);
    assert.equal(view.stepZoom(1, -1), 1);
    assert.equal(view.stepZoom(5, 1), 5);
    assert.ok(view.wheelZoom(1, -100, false) > 1);
    assert.ok(view.wheelZoom(2, 100, false) < 2);
    assert.ok(view.wheelZoom(1, -10, true) > view.wheelZoom(1, -10, false), 'a pinch zooms faster than a notch');
    assert.equal(view.pinchZoom(1, 100, 200), 2);
    assert.equal(view.pinchZoom(2, 200, 100), 1);
    assert.equal(view.pinchZoom(2, 0, 100), 2);
});

test('the bitmap is sharp on the device but bounded', () => {
    const page = { width: 600, height: 800 };
    assert.equal(view.renderScale(600, page, 1, 16_000_000), 1);
    assert.equal(view.renderScale(600, page, 2, 16_000_000), 2);
    assert.equal(view.renderScale(600, page, 9, 16_000_000), 3, 'the pixel ratio counts up to three');
    const scale = view.renderScale(6000, page, 2, 4_000_000);
    assert.ok(page.width * scale * page.height * scale <= 4_000_001);
    assert.equal(view.renderScale(0, page, 2, 1_000_000), 1);
});

test('turns stop at the first and last page and clamp what is asked for', () => {
    assert.equal(view.turnTarget(1, 10, -1), 1);
    assert.equal(view.turnTarget(10, 10, 1), 10);
    assert.equal(view.turnTarget(5, 10, 3), 8);
    assert.equal(view.turnTarget(5, 10, -99), 1);
    assert.equal(view.clampPage(0, 10), 1);
    assert.equal(view.clampPage(12.9, 10), 10);
    assert.equal(view.clampPage(Number.NaN, 10), 1);
    assert.equal(view.clampPage(3, 0), 1);
});

test('a horizontal drag turns, a tap or a scroll does not, right to left turns the other way', () => {
    assert.equal(view.swipeDelta(-80, 5, false), 1);
    assert.equal(view.swipeDelta(80, 5, false), -1);
    assert.equal(view.swipeDelta(-80, 5, true), -1);
    assert.equal(view.swipeDelta(-30, 0, false), 0);
    assert.equal(view.swipeDelta(-80, 90, false), 0);
});

test('the position of a PDF is p and its page; other formats are not read as one', () => {
    assert.equal(view.locatorPage('p12'), 12);
    assert.equal(view.locatorPage('p0'), null);
    assert.equal(view.locatorPage('s3/b17'), null);
    assert.equal(view.locatorPage(null), null);
    assert.equal(view.pageLocator(7), 'p7');
    assert.equal(view.progression(50, 200), 0.25);
    assert.equal(view.progression(200, 200), 1);
    assert.equal(view.progression(1, 0), 0);
});

test('a note finds its page by the first words of the page', () => {
    assert.equal(view.pageAnchor(['  A  ', 'short', 'page']), 'A short page');
    const long = view.pageAnchor(Array.from({ length: 80 }, (_, index) => `word${index}`));
    assert.ok(long.length <= 120 && long.startsWith('word0 word1') && !long.endsWith(' '));
    assert.equal(view.pageAnchor([]), '');
});

test('the choice of the profile always wins; automatic follows the recommendation, else the original pages', () => {
    assert.equal(switchView.choiceOf('smart'), 'smart');
    assert.equal(switchView.choiceOf('nonsense'), 'auto');
    assert.equal(switchView.choiceOf(undefined), 'auto');
    assert.equal(switchView.effectiveView('auto', 'smart'), 'smart');
    assert.equal(switchView.effectiveView('auto', 'original'), 'original');
    assert.equal(switchView.effectiveView('auto', null), 'original');
    assert.equal(switchView.effectiveView('original', 'smart'), 'original');
    assert.equal(switchView.effectiveView('smart', 'original'), 'smart');
});

test('a better view is offered quietly: only to automatic, only from the original pages, once ready, once dismissed never again', () => {
    assert.equal(switchView.offersSmartBook('auto', 'original', 'smart', false), true);
    assert.equal(switchView.offersSmartBook('original', 'original', 'smart', false), false, 'a manual choice is respected');
    assert.equal(switchView.offersSmartBook('smart', 'original', 'smart', false), false);
    assert.equal(switchView.offersSmartBook('auto', 'smart', 'smart', false), false, 'already in it');
    assert.equal(switchView.offersSmartBook('auto', 'original', 'original', false), false);
    assert.equal(switchView.offersSmartBook('auto', 'original', null, false), false);
    assert.equal(switchView.offersSmartBook('auto', 'original', 'smart', true), false);
});

test('the state is looked at soon, then ever less often, never more rarely than a minute', () => {
    const waits = [0, 1, 2, 3, 4, 5, 50].map(attempt => switchView.nextLook(attempt));
    assert.deepEqual(waits, [5000, 10000, 20000, 40000, 60000, 60000, 60000]);
});

test('the other view opens at the same page, without a section or block of the old view', () => {
    assert.equal(switchView.viewAddress('/Read/7?s=3&b=17', 'original', 12), '/Read/7?view=original&pg=12');
    assert.equal(switchView.viewAddress('/Read/7?view=original&pg=2', 'smart', 40), '/Read/7?view=smart&pg=40');
    assert.equal(switchView.viewAddress('/Read/7?pg=2', 'smart', null), '/Read/7?view=smart');
});

test('Smart Pages are a fourth choice, stored as view 3, and open at the same page', () => {
    assert.equal(switchView.choiceOf('smartpages'), 'smartpages');
    assert.deepEqual(['auto', 'smart', 'original', 'smartpages'].map(switchView.viewTypeOf), [null, 1, 2, 3]);
    assert.equal(switchView.viewAddress('/Read/7?view=original&pg=2', 'smartpages', 9), '/Read/7?view=smartpages&pg=9');
});

test('a Smart Page sets its text at the measured size, in the boxes of the page, spreading a single line like the print', () => {
    assert.equal(smartFit.startSize(0.036, 1000), 50);
    assert.equal(smartFit.startSize(Number.NaN, 1000), 12);
    assert.deepEqual({ ...smartFit.blockBox('0.1 0.2 0.5 0.05') }, { left: 0.1, top: 0.2, width: 0.5, height: 0.05 });
    assert.equal(smartFit.blockBox('0.1 0.2 0.5'), undefined);
    assert.equal(smartFit.blockBox('0.1 0.2 1.5 0.1'), undefined);
    assert.equal(smartFit.blockBox('0.1 0.2 0 0.1'), undefined);
    assert.equal(smartFit.letterSpacing(40, 10, 20, false), 4);
    assert.equal(smartFit.letterSpacing(400, 10, 20, false), 8, 'at most 0.4 em');
    assert.equal(smartFit.letterSpacing(400, 10, 20, true), 1, 'a translation only a little');
    assert.equal(smartFit.letterSpacing(-5, 10, 20, false), 0);
    assert.equal(smartFit.spreads('ALWAYS'), true);
    assert.equal(smartFit.spreads('THE OPPOSITE'), true);
    assert.equal(smartFit.spreads('And Jacob.'), false);
    assert.equal(smartFit.spreads('ONE TWO THREE'), false);
    assert.equal(smartFit.spreads('12'), false);
    assert.deepEqual([...smartFit.splitText('Das sind die riskantesten Dreiecke der Welt', [30, 10, 20])], ['Das sind die riskantesten', 'Dreiecke', 'der Welt']);
    assert.deepEqual([...smartFit.splitText('Kurz', [10, 10])], ['Kurz', '']);
});
