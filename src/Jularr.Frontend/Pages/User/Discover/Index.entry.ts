import { initializeShelves } from '../../../Framework/Components/MediaShelf';
import { initializeCards } from '../../../Framework/Components/MediaCard';
import { inserted, onPage } from '../../../Modules/Navigation/PageSwap';

// The header search of this page loads results here instead of swapping the page; set up once per visit.
let searching: HTMLFormElement | undefined;
let more: ((next: HTMLAnchorElement) => void) | undefined;

document.addEventListener('htmx:config:request', event => {
    if ((event as CustomEvent).detail.ctx.sourceElement === searching && searching?.isConnected) event.preventDefault();
});

document.addEventListener('click', event => {
    const next = (event.target as Element | null)?.closest<HTMLAnchorElement>('[data-discover-next]');
    if (!next || !more || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    more(next);
});

onPage(import.meta.url, setup);

function setup(): void {
    initializeShelves();
    initializeCards();

    const searchForm = document.querySelector<HTMLFormElement>('.app-header .search-box');
    const searchInput = searchForm?.querySelector<HTMLInputElement>('input[type="search"]');
    let results = document.querySelector<HTMLElement>('#discover-results');
    searching = undefined;
    more = undefined;

    if (searchForm && searchInput && results) {
        searching = searchForm;
        let request: AbortController | undefined;
        let generation = 0;
        let timer: ReturnType<typeof setTimeout> | undefined;
        let loading = false;
        let observer: IntersectionObserver | undefined;
        let recommendationRequest: AbortController | undefined;

        // Provider shelves never block local HTML.
        async function recommendations(): Promise<void> {
            const link = results!.querySelector<HTMLAnchorElement>('[data-discover-recommendations]');
            if (!link) return;
            recommendationRequest?.abort();
            recommendationRequest = new AbortController();
            const signal = recommendationRequest.signal;
            const current = generation;
            try {
                const response = await fetch(link.href, { signal, headers: { Accept: 'text/html', 'X-Jularr-Fragment': 'recommendations' } });
                if (!response.ok || response.redirected) throw new Error('Unavailable recommendations');
                const page = new DOMParser().parseFromString(await response.text(), 'text/html');
                const featured = page.querySelector('#discover-featured');
                const shelves = page.querySelector('#discover-recommendations');
                if (!featured || !shelves) throw new Error('Missing recommendations');
                if (current !== generation || signal.aborted) return;
                results!.querySelector('#discover-featured')?.replaceWith(featured);
                results!.querySelector('#discover-recommendations')?.replaceWith(shelves);
                inserted(featured);
                inserted(shelves);
                initializeShelves(results!);
            } catch {
                if (signal.aborted || current !== generation) return;
                const feedback = results!.querySelector<HTMLElement>('.discover-feedback');
                if (feedback) feedback.textContent = results!.dataset.loadError ?? '';
            }
        }

        // Only HTML from this route.
        async function load(address: URL, append: boolean): Promise<void> {
            if (append && loading) return;
            request?.abort();
            recommendationRequest?.abort();
            request = new AbortController();
            const current = ++generation;
            const signal = request.signal;
            loading = true;
            results!.setAttribute('aria-busy', 'true');
            observer?.disconnect();

            try {
                const endpoint = new URL(address);
                endpoint.searchParams.set('handler', 'Results');
                const response = await fetch(endpoint, { signal, headers: { Accept: 'text/html' } });
                if (!response.ok || response.redirected) throw new Error('Unavailable results');

                const page = new DOMParser().parseFromString(await response.text(), 'text/html');
                const incoming = page.querySelector<HTMLElement>('#discover-results');
                if (!incoming || incoming.dataset.providerUnavailable === 'true') throw new Error('Unavailable results');
                if (current !== generation) return;
                let appended = 0;

                // Append without moving existing cards.
                if (append) {
                    const grid = results!.querySelector('.media-grid');
                    if (!grid) return;
                    const known = new Set(Array.from(grid.querySelectorAll<HTMLAnchorElement>('.media-card__link'), link => link.getAttribute('href')));
                    for (const card of Array.from(incoming.querySelectorAll('.media-grid .media-card'))) {
                        const identity = card.querySelector('a')?.getAttribute('href');
                        if (!identity || known.has(identity)) continue;
                        known.add(identity);
                        grid.append(card);
                        ++appended;
                    }

                    const paging = results!.querySelector('.discover-paging');
                    const next = incoming.querySelector<HTMLAnchorElement>('[data-discover-next]');
                    // Automatic pages retain a manual retry.
                    paging?.replaceChildren(...(next ? [next] : []));
                    inserted(grid);
                } else {
                    results!.replaceWith(incoming);
                    results = incoming;
                    inserted(incoming);
                    // The search address replaces this history entry and stays reachable by Back.
                    history.replaceState({ htmx: true }, '', address);
                }

                initializeShelves(results!);

                // Repeated pages require deliberate navigation.
                if (!append || appended > 0) observeNext();
                if (!append) void recommendations();
            } catch (error) {
                if (signal.aborted || current !== generation) return;
                const feedback = results!.querySelector<HTMLElement>('.discover-feedback');
                if (feedback) feedback.textContent = results!.dataset.loadError ?? '';
                // No automatic retry loop.
            } finally {
                if (current === generation) {
                    loading = false;
                    results!.removeAttribute('aria-busy');
                }
            }
        }

        // Fill tall viewports progressively.
        function observeNext(): void {
            observer?.disconnect();
            const next = results!.querySelector<HTMLAnchorElement>('[data-discover-next]');
            if (!next || !('IntersectionObserver' in window)) return;
            observer = new IntersectionObserver(entries => {
                if (entries.some(entry => entry.isIntersecting) && !loading) void load(new URL(next.href), true);
            }, { rootMargin: '600px' });
            observer.observe(next);
        }

        function searchAddress(): URL {
            const address = new URL(searchForm!.action);
            new FormData(searchForm!).forEach((value, name) => {
                if (typeof value === 'string' && value.trim()) address.searchParams.set(name, value.trim());
            });
            return address;
        }

        // Cancel at the next keystroke.
        searchInput.addEventListener('input', () => {
            request?.abort();
            recommendationRequest?.abort();
            recommendationRequest?.abort();
            ++generation;
            loading = false;
            observer?.disconnect();
            clearTimeout(timer);
            timer = setTimeout(() => void load(searchAddress(), false), 300);
        });

        searchForm.addEventListener('submit', event => {
            event.preventDefault();
            clearTimeout(timer);
            void load(searchAddress(), false);
        });

        more = next => void load(new URL(next.href), true);

        observeNext();
        void recommendations();
    }
}
