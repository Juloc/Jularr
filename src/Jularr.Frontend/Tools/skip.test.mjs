import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Player/Skip.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports });

const intro = { kind: 'Intro', start: 30, end: 105 };
const credits = { kind: 'Credits', start: 1300, end: 1440 };

test('Markers are read from the page and unknown or impossible ones are dropped', () => {
    const markers = exports.parseMarkers(JSON.stringify([intro, { kind: 'Chapter', start: 1, end: 2 }, { kind: 'Intro', start: 10, end: 5 }, { kind: 'Intro', start: -1, end: 5 }, { kind: 'Credits', start: 'x', end: 5 }, null]));
    assert.deepEqual([...markers.map(marker => marker.kind)], ['Intro']);
    assert.equal(exports.parseMarkers('not json').length, 0);
    assert.equal(exports.parseMarkers(undefined).length, 0);
    assert.equal(exports.parseMarkers('{"kind":"Intro"}').length, 0);
});

test('Modes fall back to the button and intro choices cover recaps, outro choices credits and previews', () => {
    assert.equal(exports.parseMode('Automatic'), 'Automatic');
    assert.equal(exports.parseMode('Off'), 'Off');
    assert.equal(exports.parseMode('other'), 'Button');
    assert.equal(exports.parseMode(undefined), 'Button');
    assert.equal(exports.modeFor('Intro', 'Off', 'Automatic'), 'Off');
    assert.equal(exports.modeFor('Recap', 'Off', 'Automatic'), 'Off');
    assert.equal(exports.modeFor('Credits', 'Off', 'Automatic'), 'Automatic');
    assert.equal(exports.modeFor('Preview', 'Off', 'Automatic'), 'Automatic');
});

test('A stretch is active inside it, not before, not at its very end, and not when the profile turned it off', () => {
    const markers = [intro, credits];
    assert.equal(exports.activeMarker(markers, 29.9, 'Button', 'Button'), null);
    assert.equal(exports.activeMarker(markers, 30, 'Button', 'Button'), intro);
    assert.equal(exports.activeMarker(markers, 104, 'Button', 'Button'), intro);
    assert.equal(exports.activeMarker(markers, 104.6, 'Button', 'Button'), null);
    assert.equal(exports.activeMarker(markers, 60, 'Off', 'Button'), null);
    assert.equal(exports.activeMarker(markers, 1350, 'Off', 'Automatic'), credits);
    assert.equal(exports.activeMarker(markers, 1350, 'Button', 'Off'), null);
});

test('Automatic skipping happens once and only where playback arrives, not where a viewer seeks to', () => {
    const handled = new Set();
    assert.equal(exports.shouldAutoSkip(intro, 30.4, handled), true);
    assert.equal(exports.shouldAutoSkip(intro, 32.9, handled), true);
    assert.equal(exports.shouldAutoSkip(intro, 60, handled), false);
    handled.add(exports.markerKey(intro));
    assert.equal(exports.shouldAutoSkip(intro, 30.4, handled), false);
});

test('Skipping jumps to the end of the stretch, and credits to the end give way to the next episode only when it follows by itself', () => {
    assert.deepEqual({ ...exports.skipPlan(intro, 1440, true, true) }, { type: 'seek', to: 105 });
    assert.deepEqual({ ...exports.skipPlan(credits, 1440, true, true) }, { type: 'next' });
    assert.deepEqual({ ...exports.skipPlan(credits, 1440, true, false) }, { type: 'seek', to: 1440 });
    assert.deepEqual({ ...exports.skipPlan(credits, 1440, false, true) }, { type: 'seek', to: 1440 });

    // Credits followed by more picture are skipped to their end, whatever the autoplay choice.
    assert.deepEqual({ ...exports.skipPlan({ kind: 'Credits', start: 1300, end: 1400 }, 1440, true, true) }, { type: 'seek', to: 1400 });

    // An intro that ends the video does not start the next episode.
    assert.deepEqual({ ...exports.skipPlan({ kind: 'Intro', start: 10, end: 1440 }, 1440, true, true) }, { type: 'seek', to: 1440 });
    assert.deepEqual({ ...exports.skipPlan(intro, Number.NaN, true, true) }, { type: 'seek', to: 105 });
});

test('The chapter playing is the last one that has begun', () => {
    const chapters = [{ start: 0 }, { start: 90 }, { start: 600 }];
    assert.equal(exports.chapterIndexAt(chapters, 0), 0);
    assert.equal(exports.chapterIndexAt(chapters, 89.9), 0);
    assert.equal(exports.chapterIndexAt(chapters, 90), 1);
    assert.equal(exports.chapterIndexAt(chapters, 5000), 2);
    assert.equal(exports.chapterIndexAt([{ start: 10 }], 5), -1);
    assert.equal(exports.chapterIndexAt([], 5), -1);
});
