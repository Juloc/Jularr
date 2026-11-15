import { isVideoKey, listeningMessage, trailerAddress, trailerCommand } from '../../../Framework/Components/CardPreview';
import { inserted, onPage } from '../../../Modules/Navigation/PageSwap';

// The Explore feed: the parts of titles load as the reader nears the end, the title in view plays its trailer muted (only one at a time, only where the profile
// allows previews), and the watchlist heart saves in place without leaving the scroll position.

const settleDelay = 450;
const handshakeInterval = 500;
let cleanup: (() => void) | undefined;

onPage(import.meta.url, setup);

// The feed fills the screen under the header: its top edge, measured once the page is laid out, leaves the rest to it.
function fit(feed: HTMLElement): void {
    feed.style.removeProperty('--feed-top');
    feed.style.setProperty('--feed-top', Math.round(feed.getBoundingClientRect().top + window.scrollY) + 'px');
}

function setup(): void {
    cleanup?.();
    cleanup = undefined;
    const feed = document.querySelector<HTMLElement>('#discover-feed');
    if (!feed) return;

    const autoplay = feed.dataset.autoplay === 'true' && !matchMedia('(prefers-reduced-motion: reduce)').matches;
    const controller = new AbortController();
    const { signal } = controller;
    let loading = false;
    let active: HTMLElement | undefined;
    let settling: number | undefined;
    let listening: number | undefined;

    fit(feed);
    window.addEventListener('resize', () => fit(feed), { signal });

    // --- trailer of the title in view ---------------------------------------------------------------------------------------------------------------------------
    function stop(): void {
        window.clearTimeout(settling);
        window.clearInterval(listening);
        if (!active) return;
        active.querySelector('[data-feed-trailer] iframe')?.remove();
        active.querySelector<HTMLElement>('[data-feed-sound]')?.setAttribute('hidden', 'hidden');
        delete active.dataset.playing;
        active = undefined;
    }

    function play(item: HTMLElement): void {
        const key = item.dataset.trailerId;
        const holder = item.querySelector<HTMLElement>('[data-feed-trailer]');
        if (!holder || !isVideoKey(key) || item.dataset.playing) return;
        stop();
        const frame = document.createElement('iframe');
        frame.title = item.querySelector('.feed__title')?.textContent ?? '';
        frame.src = trailerAddress(key, location.origin);
        frame.allow = 'autoplay; encrypted-media';
        frame.referrerPolicy = 'strict-origin-when-cross-origin';
        frame.tabIndex = -1;
        frame.addEventListener('load', () => {
            let attempts = 0;
            window.clearInterval(listening);
            listening = window.setInterval(() => {
                frame.contentWindow?.postMessage(listeningMessage(), '*');
                if (++attempts > 6) window.clearInterval(listening);
            }, handshakeInterval);
        });
        holder.append(frame);
        item.dataset.playing = 'true';
        item.dataset.muted = 'true';
        const sound = item.querySelector<HTMLElement>('[data-feed-sound]');
        sound?.removeAttribute('hidden');
        sound?.setAttribute('aria-label', sound.dataset.labelMuted ?? '');
        active = item;
    }

    // The title that is more than half in view is the one that plays, after it has settled; the others are stopped.
    const watcher = new IntersectionObserver(entries => {
        for (const entry of entries) {
            const item = entry.target as HTMLElement;
            if (entry.isIntersecting && entry.intersectionRatio >= 0.6) {
                if (autoplay && item.dataset.trailerId && item !== active) {
                    window.clearTimeout(settling);
                    settling = window.setTimeout(() => play(item), settleDelay);
                }
            } else if (item === active) {
                stop();
            } else if (!entry.isIntersecting) {
                window.clearTimeout(settling);
            }
        }
    }, { root: feed, threshold: [0, 0.6] });

    // The next part is read when one of the last two titles is in view.
    const edge = new IntersectionObserver(entries => {
        if (entries.some(entry => entry.isIntersecting)) void more();
    }, { root: feed, threshold: 0.3 });

    function watch(items: Iterable<Element>): void {
        const list = Array.from(items).filter(item => item.matches('[data-feed-item]'));
        list.forEach(item => watcher.observe(item));
        edge.disconnect();
        const all = Array.from(feed!.querySelectorAll('[data-feed-item]'));
        all.slice(-2).forEach(item => edge.observe(item));
    }

    async function more(): Promise<void> {
        const link = feed!.querySelector<HTMLAnchorElement>('[data-feed-next]');
        if (!link || loading) return;
        const nav = link.closest<HTMLElement>('.feed__more');
        loading = true;
        nav?.removeAttribute('data-failed');
        try {
            const response = await fetch(link.href, { signal, headers: { Accept: 'text/html', 'X-Jularr-Fragment': 'feed' } });
            if (!response.ok || response.redirected) throw new Error('Unavailable feed');
            const page = new DOMParser().parseFromString(await response.text(), 'text/html');
            const incoming = page.querySelector('#discover-feed-part');
            if (!incoming || !feed!.isConnected) throw new Error('Missing feed part');
            const added = Array.from(incoming.children);
            feed!.querySelector('.feed__item--skeleton')?.remove();
            nav?.replaceWith(...added);
            added.forEach(inserted);
            watch(added);
            // The empty first part leaves its own note; nothing more is asked for.
        } catch {
            if (signal.aborted) return;
            if (nav) {
                nav.dataset.failed = '';
                if (link.dataset.retryLabel) link.textContent = link.dataset.retryLabel;
            }
        } finally {
            loading = false;
        }
    }

    // --- the buttons --------------------------------------------------------------------------------------------------------------------------------------------
    feed.addEventListener('click', event => {
        const target = event.target as Element | null;
        const sound = target?.closest<HTMLElement>('[data-feed-sound]');
        const item = target?.closest<HTMLElement>('[data-feed-item]');
        if (sound && item) {
            const frame = item.querySelector<HTMLIFrameElement>('[data-feed-trailer] iframe');
            const muted = item.dataset.muted !== 'true';
            item.dataset.muted = String(muted);
            frame?.contentWindow?.postMessage(trailerCommand(muted ? 'mute' : 'unMute'), '*');
            sound.setAttribute('aria-label', (muted ? sound.dataset.labelMuted : sound.dataset.labelSound) ?? '');
            return;
        }

        // Where trailers do not start by themselves a tap on the picture starts it.
        if (item && !item.dataset.playing && item.dataset.trailerId && target?.closest('.feed__media')) play(item);
    }, { signal });

    // The heart saves in place: the same form, posted in the background, so the feed keeps its place.
    feed.addEventListener('submit', event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.closest('[data-feed-item]')) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        const button = form.querySelector<HTMLButtonElement>('button');
        const hidden = form.querySelector<HTMLInputElement>('input[name$="IsWatchlisted"], input[name="isWatchlisted"]');
        if (!button || !hidden) return;
        const body = new URLSearchParams(new FormData(form) as unknown as Record<string, string>);
        void fetch(form.action, { method: 'POST', body, signal, credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch', 'Content-Type': 'application/x-www-form-urlencoded' } }).then(response => {
            if (!response.ok) return;
            const saved = hidden.value.toLowerCase() === 'true';
            button.setAttribute('aria-pressed', String(saved));
            const label = (saved ? feed.dataset.labelRemove : feed.dataset.labelAdd) ?? '';
            button.setAttribute('aria-label', label);
            button.setAttribute('title', label);
            hidden.value = String(!saved);
        });
    }, { signal, capture: true });

    document.addEventListener('visibilitychange', () => { if (document.hidden) stop(); }, { signal });

    // The first part, at once.
    void more();
    cleanup = () => {
        stop();
        watcher.disconnect();
        edge.disconnect();
        controller.abort();
    };
}
