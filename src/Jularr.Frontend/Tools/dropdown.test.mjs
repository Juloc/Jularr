import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/Dropdown.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
class HTMLOptGroupElement { constructor(disabled) { this.disabled = disabled; } }
const exports = {};
runInNewContext(script, { exports, Array, Math, HTMLOptGroupElement });

const options = [
    { value: '', label: 'Any', disabled: false },
    { value: 'a', label: 'Apple', disabled: false },
    { value: 'b', label: 'Banana', disabled: true },
    { value: 'c', label: 'Cherry', disabled: false },
    { value: 'd', label: 'Avocado', disabled: false }
];

test('the options of a select become plain values, a disabled group disables its options', () => {
    const group = new HTMLOptGroupElement(true);
    const select = { options: [
        { value: '1', textContent: '  One ', disabled: false, parentElement: null },
        { value: '2', textContent: 'Two', disabled: false, parentElement: group },
        { value: '3', textContent: 'Three', disabled: true, parentElement: null }] };
    assert.deepEqual(exports.optionsOf(select).map(({ value, label, disabled }) => [value, label, disabled]),
        [['1', 'One', false], ['2', 'Two', true], ['3', 'Three', true]]);
});

test('arrow keys skip disabled options and stop at the ends', () => {
    assert.equal(exports.step(options, 1, 1), 3);
    assert.equal(exports.step(options, 3, -1), 1);
    assert.equal(exports.step(options, 4, 1), 4);
    assert.equal(exports.step(options, 0, -1), 0);
});

test('typing a letter jumps to the next option that starts with it, typing more narrows the same word', () => {
    assert.equal(exports.match(options, 'a', 0), 1);
    assert.equal(exports.match(options, 'a', 1), 4);
    assert.equal(exports.match(options, 'av', 4), 4);
    assert.equal(exports.match(options, 'b', 0), -1);
    assert.equal(exports.match(options, 'x', 0), -1);
});

test('the list opens below the button and above it when there is no room below', () => {
    const below = exports.placement({ top: 100, bottom: 140, left: 50, width: 240 }, 800, 1200, 200);
    assert.equal(below.above, false);
    assert.equal(below.top, 146);
    assert.equal(below.width, 240);
    const above = exports.placement({ top: 700, bottom: 740, left: 50, width: 240 }, 800, 1200, 300);
    assert.equal(above.above, true);
    assert.equal(above.top, 700 - 6 - 300);
});

test('the list never leaves the screen sideways and is at least as wide as a readable list', () => {
    const narrow = exports.placement({ top: 100, bottom: 140, left: 1150, width: 40 }, 800, 1200, 100);
    assert.equal(narrow.width, 160);
    assert.equal(narrow.left, 1200 - 160 - 10);
    const wide = exports.placement({ top: 100, bottom: 140, left: -30, width: 3000 }, 800, 1200, 100);
    assert.equal(wide.width, 1180);
    assert.equal(wide.left, 10);
});
