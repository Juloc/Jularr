import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../Modules/Navigation/Action.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const module = { exports: {} };
runInNewContext(script, { exports: module.exports, module, URL });
const { regionIdOf, selectorOf, isSamePage, hintText, hintMilliseconds, matchForm } = module.exports;

const node = (id, dataset, parentElement = null) => ({ id, dataset, parentElement });

test('an action updates the nearest region around its form', () => {
    const page = node('app-content', {});
    const outer = node('work-page', { region: '' }, page);
    const section = node('season', {}, outer);
    const inner = node('row-1', { region: '' }, section);
    const form = node('', {}, inner);
    assert.equal(regionIdOf(form), 'row-1');
    assert.equal(regionIdOf(node('', {}, section)), 'work-page');
    assert.equal(regionIdOf(node('', {}, page)), null, 'a form outside every region updates the page content');
    assert.equal(regionIdOf(node('', { region: '' })), null, 'a region needs an id');
    assert.equal(regionIdOf(null), null);
});

test('the selector of an id escapes what css would read differently', () => {
    assert.equal(selectorOf('work-page'), '#work-page');
    assert.equal(selectorOf('work.page:1'), '#work\\.page\\:1');
});

test('an answer is the same page when it comes from the same path, however the query differs', () => {
    const current = { pathname: '/Admin/Works/5', origin: 'https://jularr.test' };
    assert.equal(isSamePage('https://jularr.test/Admin/Works/5?Tab=History', current), true);
    assert.equal(isSamePage('/admin/works/5', current), true);
    assert.equal(isSamePage('/Admin/Works', current), false, 'a redirect to the list is a navigation');
    assert.equal(isSamePage('https://elsewhere.test/Admin/Works/5', current), false);
    assert.equal(isSamePage('http://[', current), false);
});

test('the hint is the text of the trigger, trimmed, or nothing', () => {
    assert.equal(hintText({ text: ' Überwachung an ' }), 'Überwachung an');
    assert.equal(hintText('Gespeichert'), 'Gespeichert');
    assert.equal(hintText({ value: 'Saved' }), 'Saved');
    assert.equal(hintText({ text: '   ' }), null);
    assert.equal(hintText({}), null);
    assert.equal(hintText(null), null);
    assert.equal(hintText(42), null);
});

test('a hint stays about two seconds, longer texts a little longer, never more than seven', () => {
    assert.equal(hintMilliseconds('Gespeichert'), 2200);
    assert.ok(hintMilliseconds('x'.repeat(100)) > 2200);
    assert.equal(hintMilliseconds('x'.repeat(5000)), 7000);
});

const fingerprint = (action, values) => ({ action, values: new Map(Object.entries(values)) });

test('the form that was acted on is found again by its address and the row it belongs to', () => {
    const before = fingerprint('/Admin/Works/5?handler=Unit&workUnitId=7', { isMonitored: 'False', workUnitId: '7' });
    const after = [
        fingerprint('/Admin/Works/5?handler=Unit&workUnitId=8', { isMonitored: 'False', workUnitId: '8' }),
        fingerprint('/Admin/Works/5?handler=Unit&workUnitId=7', { isMonitored: 'True', workUnitId: '7' })
    ];
    assert.equal(matchForm(before, after), 1, 'the switch flipped its value but stays the same form');

    const files = [fingerprint('/Admin/Works/5?handler=DeleteFiles', { storedFileIds: '1' }), fingerprint('/Admin/Works/5?handler=DeleteFiles', { storedFileIds: '2' })];
    assert.equal(matchForm(fingerprint('/Admin/Works/5?handler=DeleteFiles', { storedFileIds: '2' }), files), 1);
    assert.equal(matchForm(fingerprint('/Admin/Works/5?handler=Gone', {}), files), -1, 'a form that is gone is not replaced by another');
});
