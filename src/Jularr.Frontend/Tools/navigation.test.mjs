import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../Modules/Navigation/Navigation.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const module = { exports: {} };
runInNewContext(script, { exports: module.exports, module, URL });
const { isNavigable } = module.exports;

const current = { href: 'https://jularr.test/Details/1', origin: 'https://jularr.test', pathname: '/Details/1', search: '' };
const click = (extra = {}) => ({ defaultPrevented: false, button: 0, metaKey: false, ctrlKey: false, shiftKey: false, altKey: false, ...extra });
const anchor = (href, extra = {}) => ({ href: new URL(href, current.href).href, target: '', dataset: {}, hasAttribute: () => false, ...extra });

test('app pages stay in the document while playing', () => {
    assert.equal(isNavigable(anchor('/Library'), current, click()), true);
    assert.equal(isNavigable(anchor('/Details/2?p=3'), current, click()), true);
});

test('other pages, origins and modified clicks load normally', () => {
    for (const href of ['/Read/4', '/Player/5', '/Account/Logout', '/Setup/Progress', '/api/v1/auth', 'https://elsewhere.test/']) {
        assert.equal(isNavigable(anchor(href), current, click()), false, href);
    }

    assert.equal(isNavigable(anchor('/Library'), current, click({ ctrlKey: true })), false);
    assert.equal(isNavigable(anchor('/Library', { target: '_blank' }), current, click()), false);
    assert.equal(isNavigable(anchor('/Library', { dataset: { navigation: 'full' } }), current, click()), false);
    assert.equal(isNavigable(anchor('#section'), current, click()), false);
});
