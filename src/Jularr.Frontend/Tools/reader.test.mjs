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
    const invalid = exports.readerSettings({size:'large', spacing:Infinity, mode:'bad', animation:'bad', layout:'bad', colour:'url(https://bad.test)'});
    assert.equal(invalid.size, 20);
    assert.equal(invalid.spacing, 165);
    assert.equal(invalid.mode, 'paged');
    assert.equal(invalid.animation, 'system');
    assert.equal(invalid.layout, 'auto');
    assert.equal(invalid.colour, '#f7f3ec');
    assert.equal(exports.readerSettings(null, true).direction, 'rtl');
    assert.equal(exports.readerSettings({size:100, spacing:20, colour:'#123ABC', animation:'on'}).size, 40);
    assert.equal(exports.readerSettings({spacing:20}).spacing, 120);
    assert.equal(exports.readerSettings({colour:'#123ABC'}).colour, '#123ABC');
    assert.equal(exports.readerSettings({animation:'on'}).animation, 'on');
    assert.equal(exports.readerSettings({size:1.1, theme:'dark'}).size, 18);
    assert.equal(exports.readerSettings({theme:'dark'}).colour, '#141414');
});

test('player planning network failure displays the localized status', async () => {
    const source = readFileSync(new URL('../Modules/Player/player.entry.ts', import.meta.url), 'utf8').replace('import "./player.scss";', '');
    const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const status = { hidden: true, textContent: '' };
    let requests = 0;
    const player = {
        dataset: { startUrl: '/start', antiforgery: 'test' },
        classList: { toggle() {}, contains: () => false },
        querySelectorAll: () => [],
        querySelector: selector => selector === '[data-role=status]' ? status : selector === '[data-status=failed]' ? { textContent: 'Localized failure' }
            : selector === '#player-sidebar' ? null : { addEventListener() {}, setAttribute() {} }
    };
    runInNewContext(code, {
        exports: {}, require: () => ({}),
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
