import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Pages/User/Requests/Requests.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const { closeMenus, escape } = (() => {
    const exports = {};
    runInNewContext(script, { exports });
    return exports;
})();

function root({ menus = [], close } = {}) {
    return {
        querySelectorAll: () => menus,
        querySelector: selector => selector === '.requests-menu[open]' ? menus.find(menu => menu.open) ?? null : close ?? null
    };
}

test('a click outside closes every open menu but the one it was in', () => {
    const first = { open: true };
    const second = { open: true };
    closeMenus(root({ menus: [first, second] }), second);
    assert.equal(first.open, false);
    assert.equal(second.open, true);
});

test('escape closes the open menu first and the panel when no menu is open', () => {
    const menu = { open: true };
    let closed = 0;
    const close = { click: () => closed++ };
    const view = root({ menus: [menu], close });
    assert.equal(escape(view), true);
    assert.equal(menu.open, false);
    assert.equal(closed, 0);
    assert.equal(escape(view), true);
    assert.equal(closed, 1);
    assert.equal(escape(root()), false);
});
