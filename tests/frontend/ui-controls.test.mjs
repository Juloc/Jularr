import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = name => readFileSync(new URL(`../../src/Jularr.Web/wwwroot/js/${name}.js`, import.meta.url), 'utf8');

test('Popover flips above a low trigger and stays inside the narrow viewport', () => {
    const window = {};
    const document = { documentElement: { clientWidth: 320, clientHeight: 640 } };
    vm.runInNewContext(source('ui-popover'), { window, document });
    const panel = { style: {}, offsetWidth: 240, offsetHeight: 200 };
    const trigger = { getBoundingClientRect: () => ({ left: 270, top: 590, bottom: 628, width: 110 }) };
    window.JularrPopover.place(panel, trigger);
    assert.equal(panel.style.left, '72px');
    assert.equal(panel.style.top, '384px');
    assert.equal(panel.style.maxWidth, '304px');
    assert.equal(panel.style.minWidth, '240px', 'The menu must retain its natural minimum width instead of shrinking to the trigger.');
    document.documentElement.clientHeight = 900;
    window.JularrPopover.place(panel, trigger);
    assert.equal(panel.style.top, '634px', 'A resized viewport must allow below placement again.');
});

class Target extends EventTarget {
    checked = false;
    classList = { toggle() {} };
    setAttribute() {}
    querySelectorAll() { return []; }
    querySelector() { return null; }
}

test('Table selection reinitialization and disposal never duplicate callbacks', () => {
    const window = {};
    vm.runInNewContext(source('ui-table'), { window, AbortController, Event });
    const table = new Target();
    const all = new Target();
    const input = new Target();
    const row = new Target();
    input.closest = () => row;
    table.querySelectorAll = () => [input];
    table.querySelector = selector => selector === '[data-ui-select-all]' ? all : null;
    let oldCalls = 0;
    let currentCalls = 0;
    window.JularrTableSelection.init(table, () => oldCalls++);
    const current = window.JularrTableSelection.init(table, () => currentCalls++);
    input.checked = true;
    input.dispatchEvent(new Event('change'));
    assert.equal(oldCalls, 1, 'Only the initial synchronization belongs to the disposed instance.');
    assert.equal(currentCalls, 2);
    assert.equal(all.checked, true);
    current.clear();
    assert.equal(input.checked, false);
    assert.equal(all.checked, false);
    window.JularrTableSelection.dispose(table);
    input.dispatchEvent(new Event('change'));
    assert.equal(currentCalls, 3);
});
