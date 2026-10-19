import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../Modules/Navigation/Navigation.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const module = { exports: {} };
runInNewContext(script, { exports: module.exports, module, URL });
const { staysInApp } = module.exports;

const current = { href: 'https://jularr.test/Details/1', origin: 'https://jularr.test', pathname: '/Details/1', search: '' };
const element = (attributes = {}, dataset = {}) => ({
    target: attributes.target ?? '', dataset,
    hasAttribute: name => name in attributes
});

test('app pages and their forms swap in place', () => {
    assert.equal(staysInApp(element(), '/Library', current), true);
    assert.equal(staysInApp(element(), '/Details/2?p=3', current), true);
    assert.equal(staysInApp(element(), '/Admin/Instance?handler=Save', current), true);
    assert.equal(staysInApp(element(), '', current), true, 'a form without action posts to the current page');
});

test('other shells, files, origins and new windows load normally', () => {
    for (const href of ['/Read/4', '/Player/5', '/Game/6', '/Account/Logout', '/Setup/Progress', '/api/v1/auth', 'https://elsewhere.test/']) {
        assert.equal(staysInApp(element(), href, current), false, href);
    }

    assert.equal(staysInApp(element({ target: '_blank' }), '/Library', current), false);
    assert.equal(staysInApp(element({ download: '' }), '/Library', current), false);
    assert.equal(staysInApp(element({}, { navigation: 'full' }), '/Library', current), false);
});
