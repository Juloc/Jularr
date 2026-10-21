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

export function initializeCards(): void {
    if (initialized) return;
    initialized = true;

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
