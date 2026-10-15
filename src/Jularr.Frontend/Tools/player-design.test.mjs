import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../Pages/Development/Design/PlayerDesign.entry.ts', import.meta.url), 'utf8');
const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;

function preview() {
    const events = {};
    const node = (dataset = {}) => {
        const classes = new Set();
        return {
            dataset, attributes: {}, hidden: false, textContent: 'Preview only',
            style: { setProperty() {} }, addEventListener() {},
            classList: {
                toggle(name, value = !classes.has(name)) { if (value) classes.add(name); else classes.delete(name); return value; },
                remove(name) { classes.delete(name); }, contains: name => classes.has(name)
            },
            setAttribute(name, value) { this.attributes[name] = value; },
            getAttribute(name) { return this.attributes[name]; },
            hasAttribute(name) { return name.startsWith('data-') && Object.hasOwn(dataset, name.slice(5)); }
        };
    };
    const timeline = node(), volume = node();
    const toggle = node({ action: 'toggle', labelPlay: 'Play', labelPause: 'Pause' });
    const nodes = {
        '[data-role=timeline]': timeline, '[data-role=volume]': volume, '[data-action=toggle]': toggle,
        '[data-role=time]': node(), '[data-role=duration]': node(), '[data-action=minimize]': node(), '[data-role=mode]': node()
    };
    const player = { ...node(), querySelector: selector => nodes[selector] ?? null, querySelectorAll: () => [] };
    const root = {
        ...node(), querySelector: selector => selector === '.player' ? player : node(), querySelectorAll: () => [],
        addEventListener: (name, action) => events[name] = action
    };
    runInNewContext(code, {
        document: { querySelector: () => root },
        fetch: () => assert.fail('Design samples must not call playback APIs')
    });
    const click = action => {
        const button = action === 'toggle' ? toggle : node({ action });
        events.click({ target: { closest: () => button }, preventDefault() {} });
    };
    return { root, player, timeline, toggle, click };
}

test('design transport changes only sample state with bounded -10/+30 seeking', () => {
    const ui = preview();
    assert.equal(ui.timeline.value, '754');
    ui.click('forward');
    assert.equal(ui.timeline.value, '784');
    ui.click('back');
    assert.equal(ui.timeline.value, '774');
    for (let i = 0; i < 80; i++) ui.click('back');
    assert.equal(ui.timeline.value, '0');
    ui.click('toggle');
    assert.equal(ui.toggle.attributes['aria-label'], 'Pause');
});

test('minimize and expand retain the same sample control and position', () => {
    const ui = preview();
    ui.click('minimize');
    assert.ok(ui.player.classList.contains('player--mini'));
    assert.ok(ui.root.classList.contains('player-workshop--mini'));
    ui.click('expand');
    assert.equal(ui.player.classList.contains('player--mini'), false);
    assert.equal(ui.timeline.value, '754');
});
