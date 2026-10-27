import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../Modules/Reader/Settings.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const exports = {};
runInNewContext(script, { exports, require: () => ({}) });

test('Reader validates optional device settings without trusting saved shapes or colours', () => {
    const invalid = exports.readerSettings({size:'large', spacing:Infinity, mode:'bad', paging:'bad', layout:'bad', colour:'url(https://bad.test)'});
    assert.equal(invalid.size, 20);
    assert.equal(invalid.spacing, 165);
    assert.equal(invalid.mode, 'paged');
    assert.equal(invalid.paging, 'book');
    assert.equal(invalid.layout, 'auto');
    assert.equal(invalid.colour, '#f7f3ec');
    assert.equal(exports.readerSettings(null, true).direction, 'rtl');
    assert.equal(exports.readerSettings({size:100, spacing:20, colour:'#123ABC'}).size, 40);
    assert.equal(exports.readerSettings({spacing:20}).spacing, 120);
    assert.equal(exports.readerSettings({colour:'#123ABC'}).colour, '#123ABC');
    // Page margins are a share of the page width: defaults keep the text off the paper's edge, saved values stay within 0-20 %.
    const margins = exports.readerSettings({});
    assert.deepEqual([margins.marginTop, margins.marginBottom, margins.marginInner, margins.marginOuter], [6, 6, 8, 7]);
    const limited = exports.readerSettings({marginTop:-5, marginBottom:99, marginInner:'wide', marginOuter:12});
    assert.deepEqual([limited.marginTop, limited.marginBottom, limited.marginInner, limited.marginOuter], [0, 20, 8, 12]);
    assert.deepEqual([margins.binding, exports.readerSettings({binding:'hard'}).binding, exports.readerSettings({binding:'cloth'}).binding], ['soft', 'hard', 'soft']);
    // Flat pages or the book; a device that had turned the page animation off keeps reading flat.
    assert.deepEqual([exports.readerSettings({paging:'flat'}).paging, exports.readerSettings({animation:'off'}).paging, exports.readerSettings({animation:'on'}).paging], ['flat', 'flat', 'book']);
    assert.equal(exports.readerSettings({size:1.1, theme:'dark'}).size, 18);
    assert.equal(exports.readerSettings({theme:'dark'}).colour, '#141414');
});

test('Reader typography keeps existing defaults and validates new local preferences', () => {
    const defaults = exports.readerSettings({ size: 20, spacing: 165 });
    assert.equal(defaults.font, 'serif');
    assert.equal(defaults.width, 68);
    assert.equal(defaults.indent, false);
    assert.equal(defaults.hyphenation, true);
    assert.equal(defaults.illustrations, true);
    const changed = exports.readerSettings({ font: 'sans', width: 25, indent: true, hyphenation: false, illustrations: false });
    assert.equal(changed.font, 'sans');
    assert.equal(changed.width, 40);
    assert.equal(changed.indent, true);
    assert.equal(changed.hyphenation, false);
    assert.equal(changed.illustrations, false);
    const invalid = exports.readerSettings({ font: 'url(bad)', width: Infinity, indent: 'true', hyphenation: 'false' });
    assert.equal(invalid.font, 'serif');
    assert.equal(invalid.width, 68);
    assert.equal(invalid.indent, false);
    assert.equal(invalid.hyphenation, true);
    assert.equal(exports.readerSettings({ width: 500 }).width, 90);
});

test('player planning network failure displays the localized status', async () => {
    const source = readFileSync(new URL('../Modules/Player/player.entry.ts', import.meta.url), 'utf8').replace('import "./player.scss";', '');
    const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const skip = {};
    runInNewContext(ts.transpileModule(readFileSync(new URL('../Modules/Player/Skip.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText, { exports: skip });
    const status = { hidden: true, textContent: '' };
    let requests = 0;
    const player = {
        dataset: { startUrl: '/start', antiforgery: 'test' },
        classList: { toggle() {}, contains: () => false },
        querySelectorAll: () => [],
        querySelector: selector => selector === '[data-role=status]' ? status : selector === '[data-status=failed]' ? { textContent: 'Localized failure' }
            : selector === '#player-sidebar' ? null : { dataset: {}, addEventListener() {}, setAttribute() {} }
    };
    runInNewContext(code, {
        exports: {}, require: name => name === './Skip' ? skip : name.endsWith('/Range') ? { updateRangeValue() {} } : {},
        document: { querySelector: () => player, createElement: () => ({ canPlayType: () => '' }), addEventListener() {} },
        matchMedia: () => ({ matches: false, addEventListener() {} }),
        // The player starts a deferred loading indicator before requesting its plan.
        // Provide the browser timer APIs so this fixture reaches the actual fetch.
        window: { setTimeout: () => 1 },
        clearTimeout() {},
        fetch: () => { requests++; return Promise.reject(new Error('Offline')); }
    });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(requests, 1, 'the actual planning request must fail, not an incomplete UI fixture');
    assert.equal(status.hidden, false);
    assert.equal(status.textContent, 'Localized failure');
});
