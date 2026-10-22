import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

function load(name) {
    const source = readFileSync(new URL(`../Modules/Reader/${name}.ts`, import.meta.url), 'utf8');
    const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const exports = {};
    runInNewContext(script, { exports, require: () => ({}), Number, Math });
    return exports;
}

const fit = load('OverflowFit');
const progress = load('ProgressMath');
const follow = load('SpeechFollow');
const settings = load('Settings');

test('tools leave the bar by the room that is left, not by a breakpoint', () => {
    // Five tools of 36 px with 2 px gaps need 188 px; More takes 36 px and a gap once something has to leave.
    const widths = [36, 36, 36, 36, 36];
    assert.equal(fit.visibleCount(widths, 188, 2, 36, false), 5);
    assert.equal(fit.visibleCount(widths, 187, 2, 36, false), 3);
    assert.equal(fit.visibleCount(widths, 100, 2, 36, false), 1);
    assert.equal(fit.visibleCount(widths, 30, 2, 36, false), 0);
    // A menu that is shown anyway reserves its place even when everything would fit.
    assert.equal(fit.visibleCount(widths, 188, 2, 36, true), 4);
    assert.equal(fit.visibleCount(widths, 400, 2, 36, true), 5);
    assert.equal(fit.visibleCount([], 100, 2, 36, true), 0);
    // Unequal widths are summed as they are.
    assert.equal(fit.visibleCount([120, 36, 36], 200, 4, 36, false), 3);
    assert.equal(fit.visibleCount([120, 36, 36], 199, 4, 36, false), 1);
});

test('a place in the book finds its chapter and the place in it', () => {
    const offsets = [0, 1000, 3000, 6000];
    assert.equal(progress.sectionAt(offsets, 10000, 0), 0);
    assert.equal(progress.sectionAt(offsets, 10000, .0999), 0);
    assert.equal(progress.sectionAt(offsets, 10000, .1), 1);
    assert.equal(progress.sectionAt(offsets, 10000, .45), 2);
    assert.equal(progress.sectionAt(offsets, 10000, 1), 3);
    assert.equal(progress.sectionAt(offsets, 10000, 7), 3);
    assert.equal(progress.sectionAt([], 10000, .5), 0);
    assert.equal(progress.fractionInSection(offsets, 10000, 2, .45).toFixed(4), '0.5000');
    assert.equal(progress.fractionInSection(offsets, 10000, 3, 1), 1);
    assert.equal(progress.fractionInSection(offsets, 10000, 0, 0), 0);
    assert.equal(progress.bookFraction(3000, 3000, 10000, .5).toFixed(4), '0.4500');
    assert.equal(progress.bookFraction(0, 0, 0, .5), 0);
});

test('the slider and a seek agree on a page, in one and in two columns', () => {
    for (const columns of [1, 2]) for (const pages of [1, 2, 7, 10, 33]) {
        for (let page = 0; page < pages; page += columns) {
            const share = progress.pageFraction(page, pages, false);
            assert.equal(progress.pageAt(share, pages, columns), page, `${pages} pages, ${columns} columns, page ${page}`);
        }
    }
    assert.equal(progress.pageFraction(8, 10, true), 1);
    assert.equal(progress.pageAt(1, 10, 2), 8);
    assert.equal(progress.pageAt(0, 10, 2), 0);
});

test('the chapter under the slider is the last that has begun', () => {
    const chapters = [{ section: 0, title: 'One', before: 0 }, { section: 3, title: 'Two', before: .3 }, { section: 6, title: 'Three', before: .7 }];
    assert.equal(progress.chapterAt(chapters, 0).title, 'One');
    assert.equal(progress.chapterAt(chapters, .3).title, 'Two');
    assert.equal(progress.chapterAt(chapters, .69).title, 'Two');
    assert.equal(progress.chapterAt(chapters, 1).title, 'Three');
    assert.equal(progress.chapterAt([], .5), undefined);
});

test('read-aloud marks the sentence around the spoken word', () => {
    const text = 'The archive fell silent. Mira had worked there for years. She followed the light.';
    const segment = value => {
        const parts = [];
        const pattern = /[^.]+\.\s*/g;
        let match;
        while ((match = pattern.exec(value))) parts.push({ index: match.index, segment: match[0] });
        return parts;
    };
    const first = follow.sentenceAround(text, 4, segment);
    assert.deepEqual({ ...first }, { start: 0, length: 24 });
    const second = follow.sentenceAround(text, text.indexOf('worked'), segment);
    assert.equal(text.slice(second.start, second.start + second.length), 'Mira had worked there for years.');
    const third = follow.sentenceAround(text, text.length - 3, segment);
    assert.equal(text.slice(third.start, third.start + third.length), 'She followed the light.');
    // Without sentences the whole text counts.
    assert.deepEqual({ ...follow.sentenceAround('One two', 2, () => []) }, { start: 0, length: 7 });
});

test('a scrolling text follows the voice only when the line leaves the comfortable band', () => {
    const bounds = { top: 100, height: 600 };
    assert.equal(follow.followDelta(300, bounds), 0);
    assert.equal(follow.followDelta(190, bounds), 0);
    assert.equal(follow.followDelta(460, bounds), 0);
    assert.equal(follow.followDelta(600, bounds), 600 - 310);
    assert.equal(follow.followDelta(120, bounds), 120 - 310);
});

test('the sleep timer counts whole minutes up and refuses nonsense', () => {
    assert.equal(follow.minutesLeft(10 * 60_000, 0), 10);
    assert.equal(follow.minutesLeft(10 * 60_000, 1), 10);
    assert.equal(follow.minutesLeft(60_000, 59_999), 1);
    assert.equal(follow.minutesLeft(60_000, 60_000), 0);
    assert.equal(follow.minutesLeft(60_000, 90_000), 0);
    assert.equal(follow.timerMinutes('30'), 30);
    assert.equal(follow.timerMinutes('0'), 0);
    assert.equal(follow.timerMinutes('-5'), 0);
    assert.equal(follow.timerMinutes('1.5'), 0);
    assert.equal(follow.timerMinutes('999'), 0);
    assert.equal(follow.timerMinutes('x'), 0);
});

test('paragraph spacing is a bounded device setting with a default of one line', () => {
    assert.equal(settings.readerSettings({}).paragraph, 100);
    assert.equal(settings.readerSettings({ paragraph: 40 }).paragraph, 40);
    assert.equal(settings.readerSettings({ paragraph: 900 }).paragraph, 200);
    assert.equal(settings.readerSettings({ paragraph: -3 }).paragraph, 0);
    assert.equal(settings.readerSettings({ paragraph: 'wide' }).paragraph, 100);
});
