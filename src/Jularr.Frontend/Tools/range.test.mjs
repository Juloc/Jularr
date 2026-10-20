import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../Framework/Components/Range.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;

test('shared range output keeps fractional values, localizes display and binds only once', async () => {
    const output = { value: '' };
    class Input {
        dataset = { rangePrecision: '5' };
        min = '0';
        max = '10';
        style = { setProperty: (name, value) => this[name] = value };
        valueAsNumber = 1.23456;
        parentElement = { querySelector: () => output };
        matches = () => true;
    }
    const input = new Input();
    const events = new Map();
    let listeners = 0;
    const root = {
        dataset: {}, querySelectorAll: () => [input],
        addEventListener(name, handler) { events.set(name, handler); listeners++; }
    };
    const exports = {};
    runInNewContext(script, { exports, HTMLInputElement: Input, document: { documentElement: { lang: 'de-DE' } }, queueMicrotask });
    exports.initializeRanges(root);
    assert.equal(output.value, '1,23456');
    assert.ok(Math.abs(parseFloat(input['--range-progress']) - 12.3456) < 0.000001);
    input.valueAsNumber = 2.75;
    events.get('input')({ target: input });
    assert.equal(output.value, '2,75');
    assert.ok(Math.abs(parseFloat(input['--range-progress']) - 27.5) < 0.000001);
    input.valueAsNumber = 1.25;
    exports.initializeRanges(root);
    assert.equal(output.value, '1,25');
    assert.equal(listeners, 2);
    events.get('reset')();
    input.valueAsNumber = 0.25;
    await new Promise(resolve => queueMicrotask(resolve));
    assert.equal(output.value, '0,25');
    assert.equal(listeners, 2);
    input.dataset.rangePrecision = '2';
    input.valueAsNumber = 100 / 3;
    exports.updateRangeValue(input);
    assert.equal(output.value, '33,33', 'display precision comes from the canonical numeric field');
    assert.equal(input['--range-progress'], '100%', 'paint is bounded to the native range');
    input.min = input.max = '5';
    exports.updateRangeValue(input);
    assert.equal(input['--range-progress'], '0%');
});
