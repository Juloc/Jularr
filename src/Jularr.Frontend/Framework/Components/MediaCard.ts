import { attachCardPreview } from './CardPreview';
import { visit } from '../../Modules/Navigation/PageSwap';

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

// Which tag a phase of the request shows; a phase that ended otherwise leaves the tag as it is.
const tagOfPhase: Record<string, string> = {
    open: 'requested', approved: 'requested', searching: 'searching', downloading: 'downloading', importing: 'downloading', completed: 'available'
};

function followPhase(button: HTMLElement): void {
    const state = tagOfPhase[button.dataset.phase ?? ''];
    const surface = button.closest<HTMLElement>('.media-card__surface');
    const art = surface?.querySelector<HTMLElement>('.media-card__art');
    if (!state || !surface || !art) return;
    const current = surface.querySelector<HTMLElement>('.media-availability-badge');
    if (current?.classList.contains(`media-availability-badge--${state}`)) return;
    const tag = document.querySelector<HTMLTemplateElement>('template[data-availability-badges]')?.content
        .querySelector<HTMLElement>(`.media-availability-badge--${state}`)?.cloneNode(true) as HTMLElement | undefined;
    if (!tag) return;
    tag.setAttribute('href', art.getAttribute('href') ?? '#');
    if (current) current.replaceWith(tag);
    else art.after(tag);
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

    // The tag top left follows the live phase of the card's request, from the page's templates.
    new MutationObserver(records => {
        for (const record of records) {
            if (record.target instanceof HTMLElement && record.target.matches('.card-request')) followPhase(record.target);
        }
    }).observe(document.body, { attributes: true, attributeFilter: ['data-phase'], subtree: true });

    // Whether a mouse moves the pointer: the card actions then wait for its hover, also where the browser reports a touch screen.
    document.addEventListener('pointermove', event => {
        const pointer = event.pointerType === 'mouse' ? 'mouse' : 'touch';
        if (document.documentElement.dataset.pointer !== pointer) document.documentElement.dataset.pointer = pointer;
    }, { passive: true });

    // The round request icon requests at once; the live phase of the request then takes over the icon, a refusal says so.
    document.addEventListener('submit', event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-quick-request')) return;
        event.preventDefault();
        const button = form.querySelector<HTMLButtonElement>('button');
        if (!button || button.getAttribute('aria-busy') === 'true') return;

        // Once requested, the icon leads to the title's page, which shows the progress.
        if (button.dataset.phase && form.dataset.details) {
            void visit(form.dataset.details);
            return;
        }

        button.setAttribute('aria-busy', 'true');
        button.dataset.phase = 'open';
        fetch(form.action, { method: 'POST', body: new FormData(form), credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
            .then(response => { if (!response.ok) throw new Error(String(response.status)); })
            .finally(() => button.removeAttribute('aria-busy'))
            .catch(() => {
                delete button.dataset.phase;
                document.dispatchEvent(new CustomEvent('jularr:hint', { detail: { text: form.dataset.failed ?? '', failed: true } }));
            });
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
