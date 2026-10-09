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

function filterHarness(initialValues = []) {
    class Element extends Target {
        children = [];
        attributes = new Map();
        style = {};
        open = false;
        classList = { add() {}, toggle() {} };
        append(...children) { this.children.push(...children); }
        replaceChildren(...children) { this.children = children; }
        setAttribute(name, value) { this.attributes.set(name, value); }
        getAttribute(name) { return this.attributes.get(name); }
        hasAttribute(name) { return this.attributes.has(name); }
        matches() { return this.open; }
        showPopover() { this.open = true; }
        hidePopover() { this.open = false; }
        focus() {}
        getBoundingClientRect() { return { left: 20, top: 20, bottom: 60, width: 140 }; }
        offsetWidth = 180;
        offsetHeight = 200;
    }
    const document = new Element();
    const form = new Element();
    form.dataset = { cancelLabel: 'Cancel', applyLabel: 'Apply' };
    form.submissions = 0;
    form.requestSubmit = () => form.submissions++;
    const applyAll = new Element();
    applyAll.hidden = true;
    const control = new Element();
    control.closest = selector => selector === 'form' ? form : null;
    const select = new Element();
    select.multiple = true;
    select.name = 'type';
    select.form = form;
    select.options = ['', 'movie', 'tv'].map(value => ({ value, textContent: value || 'Media type', selected: initialValues.includes(value) }));
    Object.defineProperty(select, 'selectedOptions', { get: () => select.options.filter(option => option.selected) });
    select.closest = () => control;
    form.querySelectorAll = selector => selector === 'select[multiple]' || selector === 'select' ? [select] : [];
    form.querySelector = selector => selector === '[data-admreq-filter-apply]' ? applyAll : null;
    document.querySelector = selector => selector === '[data-admreq-filters]' ? form : null;
    document.querySelectorAll = selector => selector === 'select[data-admreq-custom-select]' ? [select] : [];
    document.createElement = () => new Element();
    document.documentElement = { clientWidth: 1440, clientHeight: 900 };
    const window = new Element();
    vm.runInNewContext(source, { document, window, Event: class { constructor(type) { this.type = type; } } });
    const panel = control.children[1];
    const options = panel.children[0].children;
    const [cancel, apply] = panel.children[1].children;
    return { form, select, panel, options, cancel, apply, applyAll, trigger: control.children[0] };
}

test('Multiple filters keep the popup open until Apply and submit all selected values', () => {
    const { form, select, panel, options, apply, trigger, applyAll } = filterHarness();
    trigger.dispatchEvent({ type: 'click' });
    options[1].dispatchEvent({ type: 'click' });
    options[2].dispatchEvent({ type: 'click' });
    assert.equal(panel.open, true);
    assert.equal(form.submissions, 0);
    assert.equal(applyAll.hidden, false);
    assert.deepEqual(select.selectedOptions.map(option => option.value), ['movie', 'tv']);
    assert.equal(options[1].children[0].className, 'admreq-dropdown-checkbox');
    assert.equal(options[1].getAttribute('aria-selected'), 'true');
    apply.dispatchEvent({ type: 'click' });
    assert.equal(form.submissions, 1);
});

test('Cancel restores the applied filter selection without submitting', () => {
    const { form, select, panel, options, cancel, trigger, applyAll } = filterHarness(['movie']);
    trigger.dispatchEvent({ type: 'click' });
    options[1].dispatchEvent({ type: 'click' });
    options[2].dispatchEvent({ type: 'click' });
    cancel.dispatchEvent({ type: 'click' });
    assert.deepEqual(select.selectedOptions.map(option => option.value), ['movie']);
    assert.equal(form.submissions, 0);
    assert.equal(panel.open, false);
    assert.equal(applyAll.hidden, true);
    assert.equal(options[1].getAttribute('aria-selected'), 'true');
    assert.equal(options[2].getAttribute('aria-selected'), 'false');
});
