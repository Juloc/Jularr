import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import ts from './transpile.mjs';

// The page script registers its setup with the in-place navigation; outside a module it names itself.
const source = readFileSync(new URL('../Pages/User/Discover/Index.entry.ts', import.meta.url), 'utf8').replace('import.meta.url', "'discover'");
const script = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText;
const settle = () => new Promise(resolve => setImmediate(resolve));

// Small event/DOM doubles, not a browser.
function discover(landing = false) {
    const formEvents = {};
    const documentEvents = {};
    const input = { value: '' };
    const form = { action: 'https://jularr.test/', addEventListener: (name, action) => { formEvents[name] = action; } };
    const requests = [];
    const pages = new Map();
    let active;
    let observed;
    let observerAction;
    let address;

    function region(ids, next = null, unavailable = false, recommendations = false) {
        const cards = ids.map(id => ({ querySelector: () => ({ getAttribute: () => '/Details/' + id }) }));
        const grid = { querySelectorAll: () => cards.map(card => card.querySelector()), append: card => cards.push(card) };
        const href = next ? 'https://jularr.test/?q=Matrix&p=' + next : null;
        const paging = { dataset: {}, before() {}, next: next ? { href, dataset: { retryLabel: 'Try again' }, getAttribute: () => href } : null,
            querySelector: () => paging.next?.end ? null : paging.next, replaceChildren: (...nodes) => { paging.next = nodes[0] ?? null; } };
        const end = next ? null : { end: true };
        const feedback = { textContent: '' };
        const shelves = recommendations ? recommendationRegion('https://jularr.test/?handler=Recommendations') : null;
        return {
            cards, feedback, shelves, attributes: {}, dataset: { loadError: 'Try again', providerUnavailable: String(unavailable) },
            querySelector: selector => selector === '.media-grid' ? grid : selector === '.discover-paging' ? paging
                : selector === '.discover-feedback' ? feedback : selector === '[data-discover-next]' ? (paging.next?.end ? null : paging.next) : selector === '.discover-end' ? end
                : selector === '#discover-recommendations' ? shelves : selector === '#discover-recommendations .discover-paging' ? shelves?.paging ?? null : null,
            querySelectorAll: () => cards,
            setAttribute(name, value) { this.attributes[name] = value; },
            removeAttribute(name) { delete this.attributes[name]; },
            replaceWith(incoming) { active = incoming; }
        };
    }

    // The recommendation shelves: what arrived, and the link of the next part in its row.
    function recommendationRegion(href) {
        const shelves = { nodes: [], paging: null,
            querySelector: selector => selector === '[data-discover-recommendations]' ? shelves.paging?.link ?? null
                : selector === ':scope > [role=status]' ? shelves.nodes.find(node => node.status) ?? null : null };
        shelves.paging = row(href);
        function row(next) {
            const nav = { dataset: { automatic: '' }, before() {},
                replaceWith(...nodes) {
                    shelves.paging = null;
                    for (const node of nodes) {
                        if (node.next) shelves.paging = row(node.next);
                        else shelves.nodes.push(node);
                    }
                } };
            nav.link = { href: next, textContent: 'Load more', dataset: { retryLabel: 'Try again' }, closest: () => nav };
            return nav;
        }
        return shelves;
    }

    // A part as the server sends it: shelves, a notice, the next part's link.
    function fragment(nodes) {
        const part = { children: nodes, querySelector: selector => selector === ':scope > [role=status]' ? part.children.find(node => node.status) ?? null : null };
        for (const node of nodes) node.remove = () => { part.children = part.children.filter(other => other !== node); };
        return part;
    }

    active = region([1, 2], landing ? null : 2, false, landing);
    class Element {}
    class Observer {
        constructor(action) { observerAction = action; }
        disconnect() { observed = undefined; }
        observe(next) { observed = next; }
    }

    runInNewContext(script, {
        exports: {},
        require: name => {
            if (name === '../../../Framework/Components/MediaCard') return { initializeCards() {} };
            if (name === '../../../Framework/Components/HeroCarousel') return { initializeHero() {} };
            if (name === '../../../Modules/Navigation/PageSwap') return { onPage: (_, setup) => setup(), inserted() {} };
            assert.equal(name, '../../../Framework/Components/MediaShelf');
            return { initializeShelves() {} };
        },
        document: { querySelector: selector => selector.includes('search-box') ? form : active, addEventListener: (name, action) => { documentEvents[name] = action; },
            createElement: () => ({ className: '', children: [], setAttribute() {}, append(node) { this.children.push(node); }, remove() { this.removed = true; } }) },
        getComputedStyle: () => ({ gridTemplateColumns: '150px 150px 150px' }),
        window: { IntersectionObserver: Observer }, IntersectionObserver: Observer,
        AbortController, URL, Set, Error, Element, location: { href: 'https://jularr.test/' },
        FormData: class { forEach(action) { action(input.value, 'q'); action('movies', 'type'); } },
        DOMParser: class { parseFromString(key) { return { querySelector: selector => {
            const value = pages.get(key);
            return value?.shelves ? selector === '#discover-recommendations' ? value.shelves : null : value;
        } }; } },
        history: { replaceState: (_, __, url) => { address = url.href; } },
        fetch: (url, options) => new Promise((resolve, reject) => requests.push({ url, options, resolve, reject })),
        MouseEvent: class {}
    });

    return {
        requests, region, fragment, Element,
        get active() { return active; }, get address() { return address; }, get observing() { return !!observed; },
        search(value) { input.value = value; formEvents.submit({ preventDefault() {} }); },
        scroll(target) { documentEvents.scroll({ target }); },
        click(link) { documentEvents.click({ target: { closest: selector => selector === '[data-discover-recommendations]' ? link : null }, button: 0, preventDefault() {} }); },
        intersect() { if (observed) observerAction([{ isIntersecting: true }]); },
        async answer(index, incoming) {
            const key = String(index);
            pages.set(key, incoming);
            requests[index].resolve({ ok: true, redirected: false, text: async () => key });
            await settle();
        }
    };
}

test('Enter loads the results in place, cancels and discards obsolete responses without replacing the shell', async () => {
    const ui = discover();
    ui.search('Matrix');
    assert.equal(ui.requests.length, 1);
    assert.equal(ui.requests[0].url.searchParams.get('q'), 'Matrix');
    assert.equal(ui.requests[0].url.searchParams.get('type'), 'movies');
    assert.equal(ui.requests[0].url.searchParams.get('handler'), 'Results');

    ui.search('Arrival');
    assert.equal(ui.requests[0].options.signal.aborted, true);
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
    const paging = initial.querySelector('.discover-paging');
    assert.equal(paging.dataset.automatic, '');
    ui.intersect();
    await ui.answer(0, ui.region([], null, true));
    assert.equal(ui.active, initial);
    assert.equal(ui.active.cards.length, 2);
    assert.equal(ui.active.feedback.textContent, 'Try again');
    assert.equal(paging.dataset.automatic, undefined);
    assert.equal(paging.next.textContent, 'Try again');
    assert.equal(ui.observing, false);
    ui.intersect();
    assert.equal(ui.requests.length, 1);
});

test('a repeated provider page cannot create an automatic loading loop', async () => {
    const ui = discover();
    for (const page of [3, 4, 5]) {
        assert.equal(ui.observing, true);
        ui.intersect();
        await ui.answer(page - 3, ui.region([1, 2], page));
    }
    assert.equal(ui.active.cards.length, 2);
    assert.equal(ui.observing, false);
    assert.equal(ui.active.querySelector('[data-discover-next]').href, 'https://jularr.test/?q=Matrix&p=5');
});

test('a page the filter empties keeps loading, and the last page says the ranking ends', async () => {
    const ui = discover();
    ui.intersect();
    await ui.answer(0, ui.region([], 3));
    assert.equal(ui.observing, true);
    ui.intersect();
    await ui.answer(1, ui.region([5]));
    assert.equal(ui.active.cards.length, 3);
    assert.equal(ui.observing, false);
    assert.deepEqual(ui.active.querySelector('.discover-paging').next, { end: true });
});

test('a ranking shelf adds its next page near its end and shows where it ends', async () => {
    const ui = discover();
    const links = [{ getAttribute: () => '/Details/1' }];
    const added = [];
    const waiting = [];
    const items = { scrollWidth: 2000, clientWidth: 500, scrollLeft: 0, querySelectorAll: () => links,
        append: node => (node.className?.includes('discover-skeletons') ? waiting : added).push(node) };
    const shelf = { dataset: { shelfMore: '/?type=movies&view=Trending&p=2' }, querySelectorAll: () => [] };
    const target = { closest: selector => selector === '.media-shelf__items' ? items : null };
    items.closest = () => shelf.dataset.shelfMore ? shelf : null;
    Object.setPrototypeOf(target, ui.Element.prototype);

    ui.scroll(target);
    assert.equal(ui.requests.length, 0);
    items.scrollLeft = 1100;
    ui.scroll(target);
    ui.scroll(target);
    assert.equal(ui.requests.length, 1);
    assert.equal(ui.requests[0].url.searchParams.get('handler'), 'Results');
    assert.equal(ui.requests[0].url.searchParams.get('p'), '2');
    assert.equal(waiting.length, 1);
    assert.equal(waiting[0].children.length, 3);
    assert.equal(waiting[0].removed, undefined);
    await ui.answer(0, ui.region([1, 2], 3));
    assert.equal(waiting[0].removed, true);
    assert.equal(added.length, 1);
    assert.equal(shelf.dataset.shelfMore, 'https://jularr.test/?q=Matrix&p=3');

    ui.scroll(target);
    await ui.answer(1, ui.region([]));
    assert.equal(shelf.dataset.shelfMore, undefined);
    assert.deepEqual(added.at(-1), { end: true });
    ui.scroll(target);
    assert.equal(ui.requests.length, 2);

    // A failing provider is asked again at the next scroll, three times at most.
    shelf.dataset.shelfMore = '/?type=movies&view=Trending&p=4';
    shelf.dataset.shelfEmpty = '0';
    for (const index of [2, 3, 4]) {
        assert.equal(shelf.dataset.shelfMore, '/?type=movies&view=Trending&p=4');
        ui.scroll(target);
        ui.requests[index].resolve({ ok: false, redirected: false });
        await settle();
    }
    assert.equal(shelf.dataset.shelfMore, undefined);
    ui.scroll(target);
    assert.equal(ui.requests.length, 5);
});

test('recommendation shelves arrive a part at a time near their link, keep local content and say a failure once', async () => {
    const ui = discover(true);
    const local = ui.active;
    const shelves = local.shelves;
    assert.equal(ui.requests.length, 1);
    assert.equal(ui.requests[0].options.headers['X-Jularr-Fragment'], 'recommendations');
    await ui.answer(0, { shelves: ui.fragment([{ shelf: 'movies' }, { status: true }, { next: 'https://jularr.test/?p=2&handler=Recommendations' }]) });
    assert.equal(ui.active, local);
    assert.equal(local.cards.length, 2);
    assert.deepEqual(shelves.nodes.map(node => node.shelf ?? 'notice'), ['movies', 'notice']);

    // The next part waits until the page nears its link; a notice that a provider did not answer is said once.
    assert.equal(ui.requests.length, 1);
    ui.intersect();
    assert.equal(ui.requests[1].url, 'https://jularr.test/?p=2&handler=Recommendations');
    await ui.answer(1, { shelves: ui.fragment([{ status: true }, { shelf: 'anime' }, { next: 'https://jularr.test/?p=3&handler=Recommendations' }]) });
    assert.deepEqual(shelves.nodes.map(node => node.shelf ?? 'notice'), ['movies', 'notice', 'anime']);

    // A part that fails leaves its link as the retry, without asking again by itself.
    ui.intersect();
    ui.requests[2].reject(new Error('offline'));
    await settle();
    assert.equal(shelves.paging.link.textContent, 'Try again');
    assert.equal(shelves.paging.dataset.automatic, undefined);
    assert.equal(local.feedback.textContent, 'Try again');
    assert.equal(ui.observing, false);
    ui.click(shelves.paging.link);
    assert.equal(ui.requests[3].url, 'https://jularr.test/?p=3&handler=Recommendations');
    await ui.answer(3, { shelves: ui.fragment([{ shelf: 'manga' }]) });
    assert.deepEqual(shelves.nodes.map(node => node.shelf ?? 'notice'), ['movies', 'notice', 'anime', 'manga']);
    assert.equal(shelves.paging, null);
    assert.equal(local.feedback.textContent, '');

    const obsolete = discover(true);
    obsolete.search('Matrix');
    assert.equal(obsolete.requests[0].options.signal.aborted, true);
    await obsolete.answer(0, { shelves: obsolete.fragment([{ shelf: 'movies' }]) });
    assert.deepEqual(obsolete.active.shelves.nodes, []);
});

test('Hero never arrives in an asynchronous recommendations fragment', () => {
    const source = readFileSync(new URL('../Pages/User/Discover/Index.cshtml.cs', import.meta.url), 'utf8');
    assert.match(source, /await LoadHeroAsync\(await keptTask\)/);
    assert.doesNotMatch(source, /discover-featured/);
    // The carousel is part of the page itself and keeps its place without highlights.
    assert.match(source, /page\.Content\.Add\(new HeroCarousel\(assets, _hero, HeroPreferences, "discover\.hero\.empty"\)\);/);
    const fragment = source.slice(source.indexOf('private void DefineRecommendations'), source.indexOf('public async Task<IActionResult> OnGetAsync'));
    assert.doesNotMatch(fragment, /HeroCarousel/);
});
