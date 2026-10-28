import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import ts from './transpile.mjs';

const source = readFileSync(new URL('../Modules/Navigation/Navigation.ts', import.meta.url), 'utf8');
const script = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
const module = { exports: {} };
runInNewContext(script, { exports: module.exports, module, URL });
const { hasNewBuild, redirectedToSignIn, signInForExpiredPage, staysInApp } = module.exports;

const current = { href: 'https://jularr.test/Details/1', origin: 'https://jularr.test', pathname: '/Details/1', search: '', hash: '' };
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


test('htmx responses from a different frontend build require a full navigation', () => {
    const html = build => '<!DOCTYPE html><html lang="de" data-skin="clean" data-build="' + build + '"><body><div id="app-content"></div></body></html>';
    assert.equal(hasNewBuild('aaaa1111', html('aaaa1111')), false);
    assert.equal(hasNewBuild('aaaa1111', html('bbbb2222')), true);
    assert.equal(hasNewBuild('aaaa1111', '<div>partial response without document version</div>'), false);
    assert.equal(hasNewBuild(undefined, html('bbbb2222')), false);
    assert.equal(hasNewBuild('aaaa1111', undefined), false);
});


test('expired in-place navigation returns to the requested local GET after sign-in', () => {
    assert.equal(signInForExpiredPage('GET', '/Details/17?season=3#episodes', current),
        '/Account/Login?ReturnUrl=%2FDetails%2F17%3Fseason%3D3%23episodes');
    assert.equal(signInForExpiredPage('GET', 'https://other.test/x', current),
        '/Account/Login?ReturnUrl=%2FDetails%2F1');
});

test('expired POST never repeats a mutation on return from login', () => {
    assert.equal(signInForExpiredPage('POST', '/Details/17?handler=Delete', current),
        '/Account/Login?ReturnUrl=%2FDetails%2F1');
    assert.equal(signInForExpiredPage('GET', '/Account/Login', current),
        '/Account/Login?ReturnUrl=%2FDetails%2F1');
});

test('a followed authentication redirect must not render the login document in the app shell', () => {
    assert.equal(redirectedToSignIn('https://jularr.test/Account/Login?ReturnUrl=%2FDetails%2F17', '/Details/17', current), true);
    assert.equal(redirectedToSignIn('/Account/Login', '/Details/17', current), true);
    assert.equal(redirectedToSignIn('https://evil.test/Account/Login', '/Details/17', current), false);
    assert.equal(redirectedToSignIn('/Account/Login', '/Account/Login', current), false);
    assert.equal(redirectedToSignIn('/Details/17', '/Details/17', current), false);
    assert.equal(redirectedToSignIn(undefined, '/Details/17', current), false);
});
