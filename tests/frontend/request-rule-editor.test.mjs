import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Jularr.Web/wwwroot/js/request-rule-editor.js', import.meta.url), 'utf8');

class Target {
    listeners = new Map();
    dataset = {};
    value = '';
    checked = false;
    disabled = false;
    classList = { add() {}, remove() {}, toggle() {} };
    addEventListener(name, listener) { this.listeners.set(name, [...(this.listeners.get(name) || []), listener]); }
    dispatchEvent(event) {
        event.target ||= this;
        for (const listener of this.listeners.get(event.type) || []) listener(event);
    }
    querySelector() { return null; }
    querySelectorAll() { return []; }
    focus() {}
    setAttribute() {}
}

function harness(isUser = true) {
    const profiles = [
        { id: '1', name: 'Standard', description: '', values: { limit: 10, periodDays: 30, approval: 'Manual', kinds: ['book', 'movie'] } },
        { id: '2', name: 'Trusted', description: '', values: { limit: 20, periodDays: 7, approval: 'Automatic', kinds: ['book'] } }
    ];
    const form = new Target();
    const fields = Object.fromEntries(['Id', 'RuleId', 'Name', 'Description', 'Revision'].map(name => [name, new Target()]));
    fields.Id.value = '1';
    fields.Name.value = 'Standard';
    fields.Revision.value = '4';
    form.elements = { namedItem: name => fields[name.replace('Editor.', '')] };
    const values = { limit: new Target(), periodDays: new Target(), approval: new Target(), kinds: ['book', 'movie'].map(kind => Object.assign(new Target(), { value: kind, checked: true })) };
    values.limit.value = '10';
    values.periodDays.value = '30';
    values.approval.value = 'Manual';
    const overrides = new Target();
    const unlimited = new Target();
    const fieldset = new Target();
    const cancel = new Target();
    const back = new Target();
    const state = new Target();
    const indicators = Object.keys(values).map(key => Object.assign(new Target(), { dataset: { rreInheritance: key } }));
    const controls = { '[data-rre-overrides]': overrides, '[data-rre-unlimited]': unlimited, '[data-rre-limit]': values.limit,
        '[data-rre-period]': values.periodDays, '[data-rre-approval]': values.approval, '[data-rre-fields]': fieldset, '[data-rre-name]': fields.Name };
    form.querySelector = selector => controls[selector] || null;
    form.querySelectorAll = selector => selector === '[data-rre-kind]' ? values.kinds : selector === '[data-rre-inheritance]' ? indicators : [];
    const select = new Target();
    select.dataset.rreSelect = '2';
    const add = new Target();
    const workspace = new Target();
    workspace.querySelectorAll = selector => selector === '[data-rre-select]' ? [select] : [];
    workspace.querySelector = selector => selector === '[data-rre-new]' ? add : null;
    const editor = new Target();
    editor.dataset = { user: String(isUser), defaultId: '1', revision: '4', based: 'Based on {name}', custom: 'Custom', inherited: 'Inherited', unsaved: 'Discard?' };
    editor.closest = () => workspace;
    const editorNodes = { '[data-rre-form]': form, '[data-rre-state]': state, '[data-rre-cancel]': cancel, '[data-rre-back]': back,
        '[data-rre-profiles]': { textContent: JSON.stringify(profiles) }, '[data-rre-custom-fields]': { textContent: JSON.stringify({ limit: false, periodDays: false, approval: false, kinds: false }) },
        '[data-rre-new-values]': { textContent: JSON.stringify(profiles[0].values) },
        '[data-rre-initial]': isUser ? { textContent: JSON.stringify({ ruleId: '', ...profiles[0].values }) } : null };
    editor.querySelector = selector => editorNodes[selector] || null;
    const document = new Target();
    document.querySelector = () => editor;
    const window = new Target();
    window.discard = false;
    window.confirm = () => window.discard;
    vm.runInNewContext(source, { document, window, Event: class { constructor(type) { this.type = type; } } });
    const input = target => form.dispatchEvent({ type: 'input', target });
    return { form, fields, values, overrides, unlimited, fieldset, cancel, select, add, indicators, window, input };
}

test('Override enables editing, marks only changed fields and Cancel restores persisted inheritance', () => {
    const h = harness();
    assert.equal(h.fieldset.disabled, true);
    h.overrides.checked = true;
    h.overrides.dispatchEvent({ type: 'change' });
    assert.equal(h.fieldset.disabled, false);
    h.values.limit.value = '5';
    h.input(h.values.limit);
    assert.equal(h.indicators[0].textContent, 'Custom');
    assert.equal(h.indicators[1].textContent, 'Inherited');
    h.cancel.dispatchEvent({ type: 'click' });
    assert.equal(h.values.limit.value, '10');
    assert.equal(h.overrides.checked, false);
    assert.equal(h.fieldset.disabled, true);
});

test('Changing assignment retains intended sparse values and updates all inherited fields', () => {
    const h = harness();
    h.overrides.checked = true;
    h.overrides.dispatchEvent({ type: 'change' });
    h.values.limit.value = '5';
    h.input(h.values.limit);
    h.fields.RuleId.value = '2';
    h.fields.RuleId.dispatchEvent({ type: 'change' });
    assert.equal(h.values.limit.value, '5');
    assert.equal(h.values.periodDays.value, '7');
    assert.equal(h.values.approval.value, 'Automatic');
    assert.equal(h.values.kinds[1].checked, false);
    h.overrides.checked = false;
    h.overrides.dispatchEvent({ type: 'change' });
    assert.equal(h.values.limit.value, '20');
    assert.equal(h.fieldset.disabled, true);
});

test('Unlimited and media overrides survive profile changes without overriding the period', () => {
    const h = harness();
    h.overrides.checked = true;
    h.overrides.dispatchEvent({ type: 'change' });
    h.unlimited.checked = true;
    h.input(h.unlimited);
    h.values.kinds[0].checked = false;
    h.input(h.values.kinds[0]);
    h.fields.RuleId.value = '2';
    h.fields.RuleId.dispatchEvent({ type: 'change' });
    assert.equal(h.unlimited.checked, true);
    assert.equal(h.values.limit.disabled, true);
    assert.deepEqual(h.values.kinds.map(input => input.checked), [false, true]);
    assert.equal(h.values.periodDays.value, '7');
});

test('Rule switching protects unsaved edits and Add opens the same editor without navigation', () => {
    const h = harness(false);
    h.fields.Name.value = 'Unsaved';
    h.input(h.fields.Name);
    h.select.dispatchEvent({ type: 'click', preventDefault() {} });
    assert.equal(h.fields.Id.value, '1');
    h.window.discard = true;
    h.select.dispatchEvent({ type: 'click', preventDefault() {} });
    assert.equal(h.fields.Id.value, '2');
    assert.equal(h.fields.Name.value, 'Trusted');
    h.add.dispatchEvent({ type: 'click' });
    assert.equal(h.fields.Id.value, '');
    assert.equal(h.fields.Name.value, '');
    assert.equal(h.values.limit.value, '10');
});
