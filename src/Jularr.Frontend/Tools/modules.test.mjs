import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

function load(path) {
    const source = readFileSync(new URL(path, import.meta.url), 'utf8');
    const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
    const exports = {};
    runInNewContext(script, { exports, URLSearchParams });
    return exports;
}

const { viewOf, switchesOf, dependantNames, withRequired } = load('../Pages/Admin/Modules/Modules.ts');
const { insertToken, previewAddress } = load('../Pages/Admin/Naming/Naming.ts');

const modules = on => [
    { key: 'Library', requires: [], on: on.includes('Library') },
    { key: 'Playback', requires: ['Library'], on: on.includes('Playback') },
    { key: 'Reading', requires: ['Library'], on: on.includes('Reading') },
    { key: 'Trickplay', requires: ['Playback'], on: on.includes('Trickplay') }
];

test('a module cannot be switched on while a module it needs is off, and names it', () => {
    const view = viewOf(modules(['Reading']), 'Playback');
    assert.deepEqual([...view.missing], ['Library']);
    assert.equal(view.blocked, true);
});

test('a module that others need cannot be switched off and names those that are on', () => {
    const view = viewOf(modules(['Library', 'Playback', 'Reading']), 'Library');
    assert.deepEqual([...view.dependants], ['Playback', 'Reading']);
    assert.equal(view.blocked, true);
});

test('a module nobody needs, or whose dependants are off, moves freely', () => {
    assert.equal(viewOf(modules(['Library', 'Playback']), 'Trickplay').blocked, false);
    assert.equal(viewOf(modules(['Library', 'Playback']), 'Reading').blocked, false);
    assert.equal(viewOf(modules(['Library']), 'Library').blocked, false);
    assert.equal(viewOf(modules([]), 'Library').blocked, false);
});

test('switching the needed module on first and its dependant after is allowed, in that order only', () => {
    assert.equal(viewOf(modules([]), 'Playback').blocked, true);
    assert.equal(viewOf(modules(['Library']), 'Playback').blocked, false);
});

test('the switches are read from the cards', () => {
    const card = (key, requires, checked) => ({ dataset: { module: key, requires }, querySelector: () => ({ checked }) });
    const root = { querySelectorAll: () => [card('Library', '', true), card('Playback', 'Library', false)] };
    assert.equal(JSON.stringify(switchesOf(root)), JSON.stringify([
        { key: 'Library', requires: [], on: true }, { key: 'Playback', requires: ['Library'], on: false }]));
    assert.equal(dependantNames({ Playback: 'Playback', Reading: 'Reader' }, ['Playback', 'Reading', 'Other']), 'Playback, Reader, Other');
});

test('a placeholder replaces the selection and moves the caret behind it', () => {
    assert.deepEqual({ ...insertToken('{Title} - ', 10, 10, '{Quality}') }, { value: '{Title} - {Quality}', caret: 19 });
    assert.deepEqual({ ...insertToken('abc', 1, 2, '{X}') }, { value: 'a{X}c', caret: 4 });
    assert.deepEqual({ ...insertToken('abc', 99, 99, '{X}') }, { value: 'abc{X}', caret: 6 });
});

test('the preview asks for the type, the folder when there is one, and the file name', () => {
    assert.equal(previewAddress('Series', '', '{Title}'), '/api/v1/admin/library/naming/preview?mediaTypeId=Series&file=%7BTitle%7D');
    assert.equal(previewAddress('Movie', '{Title}', '{Title}'), '/api/v1/admin/library/naming/preview?mediaTypeId=Movie&file=%7BTitle%7D&folder=%7BTitle%7D');
});

test('switching a module on takes what it needs, and what that needs, with it', () => {
    assert.deepEqual([...withRequired(modules([]), 'Trickplay')], ['Trickplay', 'Playback', 'Library']);
    assert.deepEqual([...withRequired(modules([]), 'Library')], ['Library']);
});
