import { attachCardPreview } from './CardPreview';

let initialized = false;
let shownTip: HTMLElement | undefined;

const clippable = '.media-card__title, .media-card__info';

function isClipped(holder: HTMLElement): boolean {
    return [holder, ...Array.from(holder.querySelectorAll<HTMLElement>('*'))].some(element => element.scrollWidth > element.clientWidth + 1);
}

function hideTip(): void {
    if (shownTip) shownTip.hidden = true;
    shownTip = undefined;
}

// A clipped title or line of facts shows its complete text over the cover while it is hovered, focused or tapped.
function showTip(holder: HTMLElement): void {
    const tip = holder.closest('.media-card')?.querySelector<HTMLElement>('.media-card__tip');
    const full = holder.dataset.full;
    if (!tip || !full || !isClipped(holder)) return;
    hideTip();
    tip.textContent = full;
    tip.hidden = false;
    shownTip = tip;
}

function holderOf(target: EventTarget | null): HTMLElement | null {
    return target instanceof Element ? target.closest<HTMLElement>(clippable) : null;
}

// The row a card sits in: the preview stays inside it.
const rowOf = (card: HTMLElement): HTMLElement | null => card.closest<HTMLElement>('.media-shelf__items, .media-grid');
const viewport = (): { width: number; height: number } => ({ width: document.documentElement.clientWidth, height: window.innerHeight });

// The cards near the viewport, as the observer reports them, with how much of each is in view (inside its row, too).
const nearCards = new Set<HTMLElement>();
let observer: IntersectionObserver | undefined;
let preview: ReturnType<typeof attachCardPreview> | undefined;

function inView(card: HTMLElement): { ratio: number; centerX: number; centerY: number } {
    const rect = card.getBoundingClientRect();
    const screen = viewport();
    const row = rowOf(card)?.getBoundingClientRect();
    const left = Math.max(rect.left, 0, row?.left ?? 0);
    const right = Math.min(rect.right, screen.width, row?.right ?? screen.width);
    const top = Math.max(rect.top, 0);
    const bottom = Math.min(rect.bottom, screen.height);
    const area = Math.max(0, right - left) * Math.max(0, bottom - top);
    return { ratio: rect.width * rect.height > 0 ? area / (rect.width * rect.height) : 0, centerX: rect.left + rect.width / 2, centerY: rect.top + rect.height / 2 };
}

// Every card with a preview is watched; the preview decides what lingers.
function observeCards(): void {
    if (!observer) return;
    for (const card of Array.from(nearCards)) if (!card.isConnected) { nearCards.delete(card); observer.unobserve(card); }
    document.querySelectorAll<HTMLElement>('.media-card[data-preview-src]').forEach(card => observer?.observe(card));
}

export function initializeCards(): void {
    observeCards();
    if (initialized) return;
    initialized = true;

    // The delayed hover over a cover (or a card that lingers on a touch device): description or trailer, on every page with cards.
    const hovers = (): boolean => matchMedia('(hover: hover) and (pointer: fine)').matches;
    preview = attachCardPreview({
        document, window, origin: window.location.origin,
        fetch: (address, init) => fetch(address, { ...init, credentials: 'same-origin' }),
        setTimeout: (action, milliseconds) => window.setTimeout(action, milliseconds),
        clearTimeout: handle => window.clearTimeout(handle as number),
        canHover: hovers,
        canAutoplay: () => navigator.onLine && !(navigator as Navigator & { connection?: { saveData?: boolean } }).connection?.saveData,
        parse: html => new DOMParser().parseFromString(html, 'text/html').body.firstElementChild as HTMLElement | null,
        measure: card => {
            const rect = card.getBoundingClientRect();
            const row = rowOf(card)?.getBoundingClientRect();
            return { card: { left: rect.left, top: rect.top, width: rect.width, height: rect.height }, row: row ? { left: row.left, right: row.right } : null, viewport: viewport() };
        },
        viewport,
        candidates: () => Array.from(nearCards).filter(card => card.isConnected).map(card => ({ card, ...inView(card) }))
    });

    // A touch device starts nothing by hover: the observer tells which cards are near, the preview picks the one that lingers.
    observer = new IntersectionObserver(entries => {
        for (const entry of entries) {
            if (entry.isIntersecting) nearCards.add(entry.target as HTMLElement);
            else nearCards.delete(entry.target as HTMLElement);
        }

        preview?.evaluate();
    }, { threshold: [0, 0.4, 0.8, 1] });
    observeCards();
    let watching: number | undefined;
    new MutationObserver(() => {
        window.clearTimeout(watching);
        watching = window.setTimeout(observeCards, 150);
    }).observe(document.body, { childList: true, subtree: true });

    // Native links and forms remain clickable. Tapping a noninteractive poster
    // area toggles the existing status capsule without navigation.
    document.addEventListener('click', event => {
        if (!(event.target instanceof Element)) return;
        const card = event.target.closest<HTMLElement>('[data-media-card]');
        if (!card) return;
        if (!event.target.closest('a, button, input, summary') && !matchMedia('(hover: hover) and (pointer: fine)').matches) {
            if (card.hasAttribute('data-open')) delete card.dataset.open;
            else card.dataset.open = 'true';
        }
    });

    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || !(event.target instanceof Element)) return;
        hideTip();
        const card = event.target.closest<HTMLElement>('[data-media-card]');
        if (!card) return;
        delete card.dataset.open;
        if (document.activeElement instanceof HTMLElement && card.contains(document.activeElement)) document.activeElement.blur();
    });

    // Complete text of clipped lines.
    const reveal = (event: Event): void => {
        const holder = holderOf(event.target);
        if (holder) showTip(holder);
        else hideTip();
    };
    document.addEventListener('mouseover', reveal);
    document.addEventListener('focusin', reveal);
    document.addEventListener('mouseout', event => {
        const holder = holderOf(event.target);
        if (holder && !(event.relatedTarget instanceof Node && holder.contains(event.relatedTarget))) hideTip();
    });
    document.addEventListener('focusout', hideTip);
    document.addEventListener('scroll', hideTip, { capture: true, passive: true });
}
