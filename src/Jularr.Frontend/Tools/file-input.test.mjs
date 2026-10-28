import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const load = path => {
    const source = readFileSync(new URL(path, import.meta.url), 'utf8');
    const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
    const exports = {};
    runInNewContext(script, { exports });
    return exports;
};
const { acceptEntries, fileProblem, formatSize, firstFile, attach } = load('../Framework/Components/FileInput.ts');
const { listStatus, summarize, countText } = load('../Pages/Admin/MediaImport/MediaImport.ts');

const rule = { accept: '.json', maximumBytes: 1000 };
const messages = { empty: 'empty', size: 'large', type: 'wrong' };

// The native field and the places that show the state, as the page would give them.
function page(initial = null) {
    const log = [];
    const state = { files: initial ? [initial] : [], shown: null, size: '', error: null, dragging: false, notified: 0, opened: 0 };
    let control;
    const parts = {
        native: {
            get files() { return state.files; },
            clear() { state.files = []; },
            open() { state.opened++; },
            assign(file) { state.files = [file]; },
            // The page's own change event reaches the controller's listener too, as in a browser.
            notify() { state.notified++; control.changed(); }
        },
        show(file, size) { state.shown = file; state.size = size; log.push(['show', file?.name ?? null]); },
        error(message) { state.error = message; log.push(['error', message]); },
        dragging(active) { state.dragging = active; }
    };
    control = attach(parts, rule, messages, 'en');
    return { state, control, log };
}

test('accepted entries are read in lower case, extensions and types alike', () => {
    assert.deepEqual([...acceptEntries('.JSON, .cbz,image/*')], ['.json', '.cbz', 'image/*']);
    assert.deepEqual([...acceptEntries('')], []);
});

test('a file is refused when it is empty, too large or of another kind', () => {
    assert.equal(fileProblem({ name: 'list.json', size: 10 }, rule), null);
    assert.equal(fileProblem({ name: 'LIST.JSON', size: 1000 }, rule), null, 'the limit itself fits and case does not matter');
    assert.equal(fileProblem({ name: 'list.json', size: 0 }, rule), 'empty');
    assert.equal(fileProblem({ name: 'list.json', size: 1001 }, rule), 'size');
    assert.equal(fileProblem({ name: 'list.txt', size: 10 }, rule), 'type');
    assert.equal(fileProblem({ name: 'cover.png', size: 10, type: 'image/png' }, { accept: 'image/*', maximumBytes: 0 }), null);
    assert.equal(fileProblem({ name: 'anything', size: 10 }, { accept: '', maximumBytes: 0 }), null, 'no rule, no problem');
});

test('sizes are written in decimal units like the server does', () => {
    assert.equal(formatSize(0, 'en'), '0 B');
    assert.equal(formatSize(999, 'en'), '999 B');
    assert.equal(formatSize(1000, 'en'), '1.0 KB');
    assert.equal(formatSize(1_048_576, 'en'), '1.0 MB');
    assert.equal(formatSize(2_000_000_000, 'en'), '2.0 GB');
    assert.equal(formatSize(1500, 'de'), '1,5 KB');
});

test('a dropped file goes into the field once, is shown with its size and is announced', () => {
    const { state, control } = page();
    control.dragging(true);
    assert.equal(state.dragging, true);
    control.drop([{ name: 'list.json', size: 1500 - 600 }, { name: 'second.json', size: 5 }]);
    assert.equal(state.dragging, false);
    assert.equal(state.files.length, 1);
    assert.equal(state.shown.name, 'list.json', 'only the first of several files is taken');
    assert.equal(state.size, '900 B');
    assert.equal(state.error, null);
    assert.equal(state.notified, 1, 'announced once, and its own listener did not show it again');
});

test('a dropped file that does not fit is refused with the reason and leaves the field empty', () => {
    const { state, control } = page({ name: 'old.json', size: 10 });
    control.drop([{ name: 'big.json', size: 5000 }]);
    assert.equal(state.files.length, 0);
    assert.equal(state.shown, null);
    assert.equal(state.error, 'large');
    control.drop([{ name: 'list.txt', size: 5 }]);
    assert.equal(state.error, 'wrong');
    control.drop([{ name: 'empty.json', size: 0 }]);
    assert.equal(state.error, 'empty');
    control.drop([]);
    assert.equal(state.error, 'empty', 'a drop without a file changes nothing');
});

test('a file chosen in the system dialog is shown, and a refused one is taken out again', () => {
    const chosen = page({ name: 'list.json', size: 12_000 });
    chosen.control.changed();
    assert.equal(chosen.state.error, 'large');
    assert.equal(chosen.state.files.length, 0);

    const fits = page({ name: 'list.json', size: 12 });
    fits.control.changed();
    assert.equal(fits.state.shown.name, 'list.json');
    assert.equal(fits.state.size, '12 B');
    assert.equal(fits.state.notified, 0, 'the dialog already announced it');
});

test('removing the file empties the field, hides the file and clears the error', () => {
    const { state, control } = page();
    control.drop([{ name: 'list.json', size: 12 }]);
    control.remove();
    assert.equal(state.files.length, 0);
    assert.equal(state.shown, null);
    assert.equal(state.size, '');
    assert.equal(state.error, null);
    assert.equal(state.notified, 2);
});

test('the button opens the system dialog', () => {
    const { state, control } = page();
    control.choose();
    assert.equal(state.opened, 1);
    assert.equal(firstFile(null), null);
});

test('the import page reads a list from its text', () => {
    assert.deepEqual({ ...listStatus('') }, { state: 'empty' });
    assert.deepEqual({ ...listStatus('  \n') }, { state: 'empty' });
    assert.deepEqual({ ...listStatus('{"version":1,"items":[{"title":"A"},{"title":"B"}]}') }, { state: 'valid', count: 2 });
    assert.deepEqual({ ...listStatus('{"version":1,"items":[') }, { state: 'invalid' });
    assert.deepEqual({ ...listStatus('[{"title":"A"}]') }, { state: 'invalid' }, 'a bare array is not a list of version 1');
    assert.deepEqual({ ...listStatus('{"version":1}') }, { state: 'invalid' });
});

test('the import page counts the chosen rows', () => {
    const none = summarize([{ checked: false }, { checked: false }]);
    assert.deepEqual({ ...none }, { count: 0, total: 2, all: false });
    const all = summarize([{ checked: true }, { checked: true }]);
    assert.equal(all.all, true);
    assert.equal(summarize([]).all, false);
    assert.equal(countText('{0} of {1} selected', { count: 3, total: 6, all: false }), '3 of 6 selected');
});
