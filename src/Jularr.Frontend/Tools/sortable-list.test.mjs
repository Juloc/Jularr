import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

function load(file, context = {}) {
    const source = readFileSync(new URL(file, import.meta.url), 'utf8');
    const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
    const exports = {};
    runInNewContext(script, { exports, Array, Math, String, Number, ...context });
    return exports;
}

const list = load('../Framework/Components/SortableList.ts');

test('a move goes one place or to an end and stays inside the list', () => {
    assert.equal(list.targetIndex(2, 5, 'up'), 1);
    assert.equal(list.targetIndex(2, 5, 'down'), 3);
    assert.equal(list.targetIndex(2, 5, 'top'), 0);
    assert.equal(list.targetIndex(2, 5, 'bottom'), 4);
    assert.equal(list.targetIndex(0, 5, 'up'), 0);
    assert.equal(list.targetIndex(4, 5, 'down'), 4);
});

test('the menu offers only the moves that change something', () => {
    assert.deepEqual({ ...list.available(0, 3) }, { top: false, up: false, down: true, bottom: true });
    assert.deepEqual({ ...list.available(1, 3) }, { top: true, up: true, down: true, bottom: true });
    assert.deepEqual({ ...list.available(2, 3) }, { top: true, up: true, down: false, bottom: false });
    assert.deepEqual({ ...list.available(0, 1) }, { top: false, up: false, down: false, bottom: false });
});

test('a dragged row takes the place after the neighbours whose centre it has passed', () => {
    const centres = [40, 120, 200, 280];
    assert.equal(list.dropIndex(10, centres), 0);
    assert.equal(list.dropIndex(130, centres), 2);
    assert.equal(list.dropIndex(500, centres), 4);
});

test('moving an item keeps every other item in order and does not change the original', () => {
    const original = ['a', 'b', 'c', 'd'];
    assert.deepEqual([...list.moveItem(original, 0, 2)], ['b', 'c', 'a', 'd']);
    assert.deepEqual([...list.moveItem(original, 3, 0)], ['d', 'a', 'b', 'c']);
    assert.deepEqual(original, ['a', 'b', 'c', 'd']);
});

test('the page scrolls while a row is held near an edge, faster the closer it is', () => {
    assert.equal(list.scrollSpeed(400, 800), 0);
    assert.ok(list.scrollSpeed(10, 800) < list.scrollSpeed(60, 800));
    assert.ok(list.scrollSpeed(10, 800) < 0);
    assert.ok(list.scrollSpeed(790, 800) > 0);
    assert.equal(list.scrollSpeed(-50, 800), -18);
});

test('the position announcement fills its numbers', () => {
    assert.equal(list.fill('Position {0} of {1}', 2, 5), 'Position 2 of 5');
});

const stepper = load('../Framework/Components/NumberStepper.ts');

test('a number steps up and down until its bounds', () => {
    assert.equal(stepper.canStep(5, 1, '0', '10'), true);
    assert.equal(stepper.canStep(10, 1, '0', '10'), false);
    assert.equal(stepper.canStep(0, -1, '0', '10'), false);
    assert.equal(stepper.canStep(0, -1, '', ''), true);
    assert.equal(stepper.canStep(Number.NaN, 1, '0', '10'), true);
});
