import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Jularr.Web/wwwroot/js/admin-requests.js', import.meta.url), 'utf8');

class Target {
    listeners = new Map();
    checked = false;
    indeterminate = false;
    classList = { toggle() {} };
    dataset = {};
    addEventListener(name, callback) {
        this.listeners.set(name, [...(this.listeners.get(name) || []), callback]);
    }
    dispatchEvent(event) {
        event.target ||= this;
        for (const callback of this.listeners.get(event.type) || []) callback(event);
    }
    setAttribute() {}
    querySelector() { return null; }
    querySelectorAll() { return []; }
}

function harness() {
    const document = new Target();
    const table = new Target();
    const all = new Target();
    const inputs = Array.from({ length: 5 }, () => {
        const input = new Target();
        const row = new Target();
        row.querySelector = () => input;
        input.closest = () => row;
        input.row = row;
        return input;
    });
    table.querySelectorAll = () => inputs;
    table.querySelector = selector => selector === '[data-admreq-select-all]' ? all : null;
    const pageSize = new Target();
    pageSize.value = '50';
    pageSize.checkValidity = () => Number(pageSize.value) >= 1 && Number(pageSize.value) <= 500;
    const pageForm = new Target();
    pageForm.submissions = 0;
    pageForm.requestSubmit = () => pageForm.submissions++;
    pageForm.querySelector = selector => selector === '[data-admreq-page-size-input]' ? pageSize : null;
    document.querySelector = selector => selector === '[data-admin-requests]' ? table : selector === '[data-admreq-page-size]' ? pageForm : null;
    document.body = new Target();
    const window = new Target();
    vm.runInNewContext(source, { document, window, Event: class { constructor(type) { this.type = type; } } });
    return { table, all, inputs, pageSize, pageForm };
}

test('Shift row selection spans a range and keeps select-all tri-state consistent', () => {
    const { table, all, inputs } = harness();
    inputs[0].checked = true;
    inputs[0].dispatchEvent({ type: 'change' });
    assert.equal(all.indeterminate, true);
    const target = { closest: selector => selector === '[data-admreq-row]' ? inputs[3].row : null };
    table.dispatchEvent({ type: 'click', target, shiftKey: true });
    assert.deepEqual(inputs.map(input => input.checked), [true, true, true, true, false]);
    assert.equal(all.checked, false);
    assert.equal(all.indeterminate, true);
    all.checked = true;
    all.dispatchEvent({ type: 'change' });
    assert.equal(inputs.filter(input => input.checked).length, 5);
    assert.equal(all.indeterminate, false);
    table.dispatchEvent({ type: 'click', target, ctrlKey: true });
    assert.equal(inputs[3].checked, false);
    assert.equal(all.indeterminate, true);
});

test('Free numeric page size submits on Enter and rejects out-of-range values', () => {
    const { pageSize, pageForm } = harness();
    pageSize.value = '2';
    pageSize.dispatchEvent({ type: 'keydown', key: 'Enter', preventDefault() {} });
    assert.equal(pageForm.submissions, 1);
    pageSize.value = '501';
    pageSize.dispatchEvent({ type: 'keydown', key: 'Enter', preventDefault() {} });
    assert.equal(pageForm.submissions, 1);
    pageSize.value = '200';
    pageSize.dispatchEvent({ type: 'change' });
    assert.equal(pageForm.submissions, 2);
});
