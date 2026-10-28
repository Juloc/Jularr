import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Reader/ComicPages.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports, require: () => ({}) });

test('shared Reader image adapter keeps covers and wide pages alone', () => {
    const spread = (page, two = true, wide = []) => [...exports.comicSpread(page, 5, two, new Set(wide))];
    assert.deepEqual(spread(0), [0]);
    assert.deepEqual(spread(1), [1, 2]);
    assert.deepEqual(spread(3), [3, 4]);
    assert.deepEqual(spread(4), [4]);
    assert.deepEqual(spread(1, false), [1]);
    assert.deepEqual(spread(1, true, [1]), [1]);
    assert.deepEqual(spread(1, true, [2]), [1]);
});

test('scroll Reader completes only at the loaded final image bottom, including within-page scrolling and leaving', () => {
    const saves = [];
    const events = {};
    const stage = {
        children: [], clientWidth: 500, clientHeight: 600, scrollTop: 0, offsetTop: 0,
        get scrollHeight() { return this.children.length * 1000; },
        getBoundingClientRect: () => ({ top: 0, bottom: 600 }),
        addEventListener: (name, handler) => { events[name] = handler; },
        replaceChildren(...children) { this.children = children; },
        querySelectorAll() { return this.children; },
        contains(image) { return this.children.includes(image); }
    };
    class TestImage {
        dataset = {};
        complete = true;
        naturalWidth = 500;
        naturalHeight = 1000;
        get offsetTop() { return Number(this.dataset.index) * 1000; }
        getBoundingClientRect() { return { top: this.offsetTop - stage.scrollTop, bottom: this.offsetTop + 1000 - stage.scrollTop }; }
        addEventListener() {}
    }
    const seek = { value: '0', get valueAsNumber() { return Number(this.value); }, addEventListener(name, handler) { events['seek-' + name] = handler; } };
    const reader = {
        dataset: { count: '3', page: '0', pages: '/pages/', progressUrl: '/progress', antiforgery: 'test' },
        style: { setProperty() {} },
        querySelector: selector => selector === '[data-role=stage]' ? stage : selector.includes('input[type=range]') ? seek : { addEventListener() {} },
        querySelectorAll: () => [],
        addEventListener() {}
    };
    const settings = { mode: 'scroll', layout: 'single', direction: 'ltr', animation: 'off' };
    const adapter = {};
    runInNewContext(script, {
        exports: adapter,
        require: name => name.includes('Progress')
            ? { progressReporter: () => (value, leaving) => saves.push({ ...value, leaving }) }
            : name.includes('Range') ? { updateRangeValue() {} }
            : name.includes('bookmarks') ? { initializeBookmarks: () => ({ refresh() {} }) } : { initializeReaderSettings: () => settings },
        Image: TestImage, URL,
        location: { href: 'https://reader.test/read' }, history: { replaceState() {} },
        matchMedia: () => ({ matches: false, addEventListener() {} }),
        requestAnimationFrame: callback => callback(),
        ResizeObserver: class { observe() {} },
        window: { addEventListener: (name, handler) => { events[name] = handler; } }
    });
    adapter.initializeComicPages(reader);
    seek.value = '3';
    events['seek-change']();
    assert.equal(saves.at(-1).locator, 'p2');
    assert.equal(saves.at(-1).isCompleted, false, 'seeking to the final image does not read its bottom');
    seek.value = '1';
    events['seek-change']();
    assert.equal(saves.at(-1).locator, 'p0');
    const scroll = top => { stage.scrollTop = top; events.scroll(); return saves.at(-1); };
    let saved = scroll(2000);
    assert.equal(Number(seek.value), 3);
    assert.equal(saved.locator, 'p2');
    assert.equal(saved.isCompleted, false, 'seeing the last page is not completion');
    assert.ok(saved.progression < 1);
    events.pagehide();
    assert.equal(saves.at(-1).isCompleted, false, 'leaving midway must not complete the book');
    assert.equal(scroll(2398).isCompleted, false, 'unread content below the viewport still counts');
    const finalImage = stage.children.at(-1);
    finalImage.complete = false;
    assert.equal(scroll(2400).isCompleted, false, 'a pending image is not read');
    finalImage.complete = true;
    finalImage.naturalHeight = 0;
    assert.equal(scroll(2400).isCompleted, false, 'a broken image is not read');
    finalImage.naturalHeight = 1000;
    saved = scroll(2400);
    assert.equal(saved.locator, 'p2');
    assert.equal(saved.isCompleted, true, 'reaching the bottom without changing page must save completion');
    assert.equal(saved.progression, 1);
    events.pagehide();
    assert.equal(saves.at(-1).isCompleted, true);
    assert.equal(saves.at(-1).leaving, true);
    assert.equal(scroll(2100).isCompleted, false);
    settings.mode = 'paged';
    events.pagehide();
    assert.equal(saves.at(-1).isCompleted, true, 'paged completion is unchanged');
});
