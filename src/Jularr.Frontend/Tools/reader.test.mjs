import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../Modules/Reader/reader.entry.ts', import.meta.url), 'utf8').replace('import "./reader.scss";', '');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;

function reader(stored = '{}', blocked = false) {
    const events = {}, buttons = {}, saved = [], properties = {};
    let bottom = 10000;
    const block = { dataset: { b: '0' }, getBoundingClientRect: () => ({ bottom, right: 100 }) };
    const content = {
        querySelectorAll: () => [block], querySelector: () => null, addEventListener() {},
        getBoundingClientRect: () => ({ left: 0, bottom })
    };
    const button = { dataset: { setting: 'size', value: 'larger' }, setAttribute() {}, addEventListener: (name, action) => buttons[name] = action };
    const element = {
        dataset: { section: '0', sections: '1', blocks: '1', before: '0', characters: '100', total: '100' },
        style: { setProperty: (key, value) => { properties[key] = value; if (key === '--reader-size') element.size = value; } },
        querySelector: selector => selector === '[data-role=content]' ? content : null,
        querySelectorAll: selector => selector === '[data-setting]' ? [button] : []
    };
    runInNewContext(script, {
        exports: {}, require: () => ({ progressReporter: () => value => saved.push(value), highlights() {}, findings() {} }),
        document: { querySelector: () => element, addEventListener() {} },
        window: { innerHeight: 900, addEventListener: (name, action) => events[name] = action },
        location: { hash: '', search: '' }, URLSearchParams,
        localStorage: { getItem: () => stored, setItem() { if (blocked) throw new Error('Unavailable storage'); } }
    });
    return { element, properties, saved, enlarge: () => buttons.click(), leave: () => events.pagehide(), finish: () => bottom = 800 };
}

test('reader requires the visible section end before marking a single-block book complete', () => {
    const ui = reader();
    ui.leave();
    assert.equal(ui.saved[0].isCompleted, false);
    ui.finish();
    ui.leave();
    assert.equal(ui.saved[1].isCompleted, true);
    assert.equal(ui.saved[1].progression, 1);
});

test('invalid saved preferences fall back and blocked persistence does not break controls', () => {
    const ui = reader('{"size":"large","theme":"invalid","mode":"invalid"}', true);
    assert.equal(ui.element.size, '1.1rem');
    assert.equal(ui.element.dataset.theme, 'light');
    assert.equal(ui.element.dataset.mode, 'scroll');
    ui.enlarge();
    assert.equal(ui.element.size, '1.2rem');
});

test('player planning network failure displays the localized status', async () => {
    const source = readFileSync(new URL('../Modules/Player/player.entry.ts', import.meta.url), 'utf8').replace('import "./player.scss";', '');
    const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const status = { hidden: true, textContent: '' };
    const player = {
        dataset: { startUrl: '/start', antiforgery: 'test' },
        querySelector: selector => selector === '[data-role=status]' ? status : selector === '[data-status=failed]' ? { textContent: 'Localized failure' } : {}
    };
    runInNewContext(code, {
        exports: {}, require: () => ({}), document: { querySelector: () => player, createElement: () => ({ canPlayType: () => '' }) },
        fetch: () => Promise.reject(new Error('Offline'))
    });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(status.hidden, false);
    assert.equal(status.textContent, 'Localized failure');
});

test('reader reuses the preview typography controls with validated device settings', () => {
    const ui = reader('{"font":"sans","lineHeight":"spacious","width":"narrow","preset":"custom"}');
    assert.equal(ui.properties['--reader-font-family'], 'system-ui, sans-serif');
    assert.equal(ui.properties['--reader-line-height'], '1.9');
    assert.equal(ui.properties['--reader-width'], '34rem');
    ui.enlarge();
    assert.equal(ui.element.size, '1.2rem');
});

test('invalid reader appearance preferences cannot inject CSS or change layout', () => {
    const ui = reader('{"font":"invalid","lineHeight":"99","width":"999vw","preset":"unknown"}');
    assert.match(ui.properties['--reader-font-family'], /Georgia/);
    assert.equal(ui.properties['--reader-line-height'], '1.65');
    assert.equal(ui.properties['--reader-width'], '42rem');
});
