import { placement } from '../../../Framework/Components/Dropdown';
import { snapshot } from './FilterRow';

// Delegated from the document, so the row also works after the page replaces its results.
const opened = new WeakMap<HTMLElement, string>();
const phone = window.matchMedia('(max-width: 640px)');

function form(element: Element): HTMLFormElement | null {
    return element.closest<HTMLFormElement>('form[data-discover-filters]');
}

function choices(popup: HTMLElement): string {
    return snapshot(popup.querySelectorAll<HTMLInputElement>('input'));
}

// A select of the row applies at once.
document.addEventListener('change', event => {
    const target = event.target as Element;
    if (target instanceof HTMLSelectElement && form(target)) form(target)!.requestSubmit();
});

// A popup opens under its button (on a phone as a sheet the stylesheet places) and applies its choices when it closes.
document.addEventListener('toggle', event => {
    const popup = event.target as HTMLElement;
    if (!(popup instanceof HTMLElement) || !popup.classList.contains('discover-filter__popup')) return;
    const owner = form(popup);
    if ((event as ToggleEvent).newState === 'open') {
        opened.set(popup, choices(popup));
        const button = popup.parentElement?.querySelector<HTMLElement>('.discover-filter__button');
        if (button && !phone.matches) {
            const place = placement(button.getBoundingClientRect(), window.innerHeight, window.innerWidth, popup.scrollHeight);
            Object.assign(popup.style, { top: `${place.top}px`, left: `${place.left}px`, maxHeight: `${place.maxHeight}px`, minWidth: `${place.width}px` });
        } else {
            popup.removeAttribute('style');
        }

        button?.setAttribute('aria-expanded', 'true');
        return;
    }

    popup.parentElement?.querySelector('.discover-filter__button')?.setAttribute('aria-expanded', 'false');
    if (owner && opened.get(popup) !== choices(popup)) owner.requestSubmit();
}, true);
