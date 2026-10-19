import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';

// The page script registers its setup with the in-place navigation; outside a module it names itself.
const source = readFileSync(new URL('../Pages/User/Discover/Index.entry.ts', import.meta.url), 'utf8').replace('import.meta.url', "'discover'");
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const settle = () => new Promise(resolve => setImmediate(resolve));

// Small event/DOM doubles, not a browser.
function discover(landing = false) {
    const inputEvents = {};
    const formEvents = {};
    const documentEvents = {};
    const input = { value: '', addEventListener: (name, action) => { inputEvents[name] = action; } };
    const form = { action: 'https://jularr.test/', querySelector: () => input, addEventListener: (name, action) => { formEvents[name] = action; } };
    const requests = [];
    const pages = new Map();
    const timers = new Map();
    let timerId = 0;
    let active;
    let observed;
    let observerAction;
    let address;

    function region(ids, next = null, unavailable = false, recommendations = false) {
        const cards = ids.map(id => ({ querySelector: () => ({ getAttribute: () => '/Details/' + id }) }));
        const grid = { querySelectorAll: () => cards.map(card => card.querySelector()), append: card => cards.push(card) };
        const paging = { next: next ? { href: 'https://jularr.test/?q=Matrix&p=' + next } : null, replaceChildren: (...nodes) => { paging.next = nodes[0] ?? null; } };
        const feedback = { textContent: '' };
        const slots = {};
        return {
            cards, feedback, slots, attributes: {}, dataset: { loadError: 'Try again', providerUnavailable: String(unavailable) },
            querySelector: selector => selector === '.media-grid' ? grid : selector === '.discover-paging' ? paging
                : selector === '.discover-feedback' ? feedback : selector === '[data-discover-next]' ? paging.next
                : selector === '[data-discover-recommendations]' ? recommendations ? { href: 'https://jularr.test/?handler=Recommendations' } : null
                : selector === '#discover-featured' ? { replaceWith: value => slots.featured = value }
                : selector === '#discover-recommendations' ? { replaceWith: value => slots.shelves = value } : null,
            querySelectorAll: () => cards,
            setAttribute(name, value) { this.attributes[name] = value; },
            removeAttribute(name) { delete this.attributes[name]; },
            replaceWith(incoming) { active = incoming; }
        };
    }

    active = region([1, 2], landing ? null : 2, false, landing);
    class Observer {
        constructor(action) { observerAction = action; }
        disconnect() { observed = undefined; }
        observe(next) { observed = next; }
    }

    runInNewContext(script, {
        exports: {},
        require: name => {
            if (name === '../../../Framework/Components/MediaCard') return { initializeCards() {} };
            if (name === '../../../Modules/Navigation/PageSwap') return { onPage: (_, setup) => setup(), inserted() {} };
            assert.equal(name, '../../../Framework/Components/MediaShelf');
            return { initializeShelves() {} };
        },
        document: { querySelector: selector => selector.includes('search-box') ? form : active, addEventListener: (name, action) => { documentEvents[name] = action; } },
        window: { IntersectionObserver: Observer }, IntersectionObserver: Observer,
        AbortController, URL, Set, Error,
        FormData: class { forEach(action) { action(input.value, 'q'); action('movies', 'type'); } },
        DOMParser: class { parseFromString(key) { return { querySelector: selector => {
            const value = pages.get(key);
            return value?.featured ? selector === '#discover-featured' ? value.featured : value.shelves : value;
        } }; } },
        history: { replaceState: (_, __, url) => { address = url.href; } },
        fetch: (url, options) => new Promise((resolve, reject) => requests.push({ url, options, resolve, reject })),
        setTimeout: action => { timers.set(++timerId, action); return timerId; },
        clearTimeout: id => timers.delete(id),
        MouseEvent: class {}
    });

    return {
        requests, region,
        get active() { return active; }, get address() { return address; }, get observing() { return !!observed; },
        type(value) { input.value = value; inputEvents.input(); },
        flush() { const pending = [...timers.values()]; timers.clear(); pending.forEach(action => action()); },
        intersect() { if (observed) observerAction([{ isIntersecting: true }]); },
        async answer(index, incoming) {
            const key = String(index);
            pages.set(key, incoming);
            requests[index].resolve({ ok: true, redirected: false, text: async () => key });
            await settle();
        }
    };
}

test('live search debounces, cancels and discards obsolete responses without replacing the shell', async () => {
    const ui = discover();
    ui.type('Ma');
    ui.type('Matrix');
    assert.equal(ui.requests.length, 0);
    ui.flush();
    assert.equal(ui.requests.length, 1);
    assert.equal(ui.requests[0].url.searchParams.get('q'), 'Matrix');
    assert.equal(ui.requests[0].url.searchParams.get('type'), 'movies');
    assert.equal(ui.requests[0].url.searchParams.get('handler'), 'Results');

    ui.type('Arrival');
    assert.equal(ui.requests[0].options.signal.aborted, true);
    ui.flush();
    const current = ui.region([3]);
    await ui.answer(1, current);
    await ui.answer(0, ui.region([4]));
    assert.equal(ui.active, current);
    assert.match(ui.address, /q=Arrival/);
    assert.equal(ui.active.attributes['aria-busy'], undefined);
});

test('viewport paging appends unique cards, retains existing identities and stops on an empty page', async () => {
    const ui = discover();
    const first = ui.active.cards[0];
    ui.intersect();
    ui.intersect();
    assert.equal(ui.requests.length, 1);
    await ui.answer(0, ui.region([2, 3], 3));
    assert.equal(ui.active.cards.length, 3);
    assert.equal(ui.active.cards[0], first);
    assert.equal(ui.observing, true);
    assert.equal(ui.address, undefined);

    ui.intersect();
    await ui.answer(1, ui.region([]));
    assert.equal(ui.active.cards.length, 3);
    assert.equal(ui.observing, false);
});

test('failed later pages retain results and do not hammer the provider with automatic retries', async () => {
    const ui = discover();
    const initial = ui.active;
    ui.intersect();
    await ui.answer(0, ui.region([], null, true));
    assert.equal(ui.active, initial);
    assert.equal(ui.active.cards.length, 2);
    assert.equal(ui.active.feedback.textContent, 'Try again');
    assert.equal(ui.observing, false);
    ui.intersect();
    assert.equal(ui.requests.length, 1);
});

test('a repeated provider page cannot create an automatic loading loop', async () => {
    const ui = discover();
    ui.intersect();
    await ui.answer(0, ui.region([1, 2], 3));
    assert.equal(ui.active.cards.length, 2);
    assert.equal(ui.observing, false);
    assert.equal(ui.active.querySelector('[data-discover-next]').href, 'https://jularr.test/?q=Matrix&p=3');
});

test('recommendation fragments preserve local content and obsolete responses are discarded', async () => {
    const ui = discover(true);
    const local = ui.active;
    assert.equal(ui.requests.length, 1);
    assert.equal(ui.requests[0].options.headers['X-Jularr-Fragment'], 'recommendations');
    const providers = { featured: {}, shelves: {} };
    await ui.answer(0, providers);
    assert.equal(ui.active, local);
    assert.equal(local.cards.length, 2);
    assert.equal(local.slots.featured, providers.featured);

    const obsolete = discover(true);
    obsolete.type('Matrix');
    assert.equal(obsolete.requests[0].options.signal.aborted, true);
    await obsolete.answer(0, providers);
    assert.equal(obsolete.active.slots.featured, undefined);
});
