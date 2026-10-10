import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Jularr.Web/wwwroot/js/admin-search.js', import.meta.url), 'utf8');

class Target {
    listeners = new Map();
    attributes = new Map();
    style = {};
    value = '';
    hidden = false;
    children = [];
    open = false;
    addEventListener(name, listener) { this.listeners.set(name, listener); }
    emit(name, event = {}) { this.listeners.get(name)?.(event); }
    setAttribute(name, value) { this.attributes.set(name, value); }
    matches() { return this.open; }
    showPopover() { this.open = true; this.emit('toggle'); }
    hidePopover() { this.open = false; this.emit('toggle'); }
    replaceChildren(...children) { this.children = children; }
    focus() { this.emit('focus'); }
    querySelector() { return null; }
    querySelectorAll() { return []; }
    getBoundingClientRect() { return { width: 600, left: 200, bottom: 50 }; }
}

function harness() {
    const form = new Target();
    const input = new Target();
    const panel = new Target();
    const results = new Target();
    const loading = new Target();
    const error = new Target();
    const all = new Target();
    const document = new Target();
    const timers = new Map();
    const requests = [];
    let timerId = 0;
    form.querySelector = () => input;
    panel.offsetWidth = 600;
    panel.querySelector = selector => ({ '[data-admin-search-results]': results, '[data-admin-search-loading]': loading, '[data-admin-search-error]': error, '[data-admin-search-all]': all })[selector];
    document.querySelector = selector => selector === '[data-admin-search]' ? form : panel;
    const window = new Target();
    window.innerWidth = 1440;
    window.innerHeight = 900;
    vm.runInNewContext(source, {
        document, window, AbortController,
        DOMParser: class { parseFromString(html) { return { body: { childNodes: [html] } }; } },
        setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, { callback, delay }); return id; },
        clearTimeout: id => timers.delete(id),
        fetch: (url, options) => new Promise((resolve, reject) => requests.push({ url, options, resolve, reject }))
    });
    const runSearch = () => {
        const [id, timer] = [...timers].find(([, value]) => value.delay === 250);
        timers.delete(id);
        return timer.callback();
    };
    const type = value => { input.value = value; input.emit('input'); return runSearch(); };
    return { input, panel, results, loading, error, all, requests, timers, type };
}

test('Admin search ignores stale responses and cancels previous requests', async () => {
    const ui = harness();
    const first = ui.type('demo');
    const second = ui.type('providers');
    assert.equal(ui.requests[0].options.signal.aborted, true);
    ui.requests[1].resolve({ ok: true, redirected: false, text: async () => 'current settings' });
    await second;
    ui.requests[0].resolve({ ok: true, redirected: false, text: async () => 'stale users' });
    await first;
    assert.deepEqual(ui.results.children, ['current settings']);
    assert.equal(ui.error.hidden, true);
    assert.equal(ui.loading.hidden, true);
    assert.equal(ui.all.href, '/Admin/Search?q=providers');
});

test('Empty query clears results and Escape does not reopen the preview on restored focus', async () => {
    const ui = harness();
    const pending = ui.type('demo');
    ui.requests[0].resolve({ ok: true, redirected: false, text: async () => 'users' });
    await pending;
    ui.panel.emit('keydown', { key: 'Escape' });
    assert.equal(ui.panel.open, false);
    assert.equal([...ui.timers.values()].some(timer => timer.delay === 250), false);
    await ui.type('');
    assert.deepEqual(ui.results.children, []);
    assert.equal(ui.panel.open, false);
});

test('Failed or redirected search shows a real error without rendering response markup', async () => {
    const ui = harness();
    const pending = ui.type('demo');
    ui.requests[0].resolve({ ok: true, redirected: true, text: async () => 'login page' });
    await pending;
    assert.equal(ui.error.hidden, false);
    assert.equal(ui.loading.hidden, true);
    assert.deepEqual(ui.results.children, []);
});
