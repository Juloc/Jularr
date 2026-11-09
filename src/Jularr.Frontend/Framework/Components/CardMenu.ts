// The three dots of a continue card: the panel opens at the button without being cut off by the row it sits in, the arrow keys walk its entries, and an entry is sent
// in the background (the form of the title's page, answered without a redirect). A row that no longer fits drops its card; the favorites entry flips its text.

export interface Box { left: number; top: number; right: number; bottom: number }

// Under the button, its right edge on the button's, kept inside the area the page's content has (not under the sidebar); above the button where there is no room below.
export function panelPosition(button: Box, panel: { width: number; height: number }, area: { left: number; right: number; bottom: number }, gap = 4, margin = 8): { left: number; top: number } {
    const left = Math.max(area.left + margin, Math.min(button.right - panel.width, area.right - panel.width - margin));
    const below = button.bottom + gap;
    const top = below + panel.height > area.bottom - margin ? Math.max(margin, button.top - gap - panel.height) : below;
    return { left, top };
}

// The kinds of entry that take the card out of its row.
export const leavesRow = (kind: string | undefined): boolean => kind === 'remove' || kind === 'watched' || kind === 'unwatched';

const menuOf = (target: EventTarget | null): HTMLDetailsElement | null => target instanceof Element ? target.closest<HTMLDetailsElement>('.media-card__menu details') : null;

function place(menu: HTMLDetailsElement): void {
    const panel = menu.querySelector<HTMLElement>('.action-menu__panel');
    const button = menu.querySelector<HTMLElement>('summary');
    if (!panel || !button) return;
    const rect = button.getBoundingClientRect();
    // The row of cards starts where the page's content does, right of a sidebar that would lie over the panel.
    const row = menu.closest<HTMLElement>('.media-shelf__items, .media-grid')?.getBoundingClientRect();
    const area = { left: Math.max(0, row?.left ?? 0), right: document.documentElement.clientWidth, bottom: window.innerHeight };
    const spot = panelPosition(rect, { width: panel.offsetWidth, height: panel.offsetHeight }, area);
    panel.style.left = `${spot.left}px`;
    panel.style.top = `${spot.top}px`;
}

function closeAll(except?: HTMLDetailsElement | null): void {
    document.querySelectorAll<HTMLDetailsElement>('.media-card__menu details[open]').forEach(menu => { if (menu !== except) menu.open = false; });
}

// The card leaves its row; a row without cards leaves with it, and focus moves to a neighbour.
function removeCard(card: HTMLElement): void {
    const shelf = card.closest<HTMLElement>('.media-shelf');
    const items = card.parentElement;
    const next = (card.nextElementSibling ?? card.previousElementSibling) as HTMLElement | null;
    card.remove();
    if (shelf && !shelf.querySelector('.media-card')) shelf.remove();
    else items?.dispatchEvent(new Event('scroll'));
    next?.querySelector<HTMLElement>('.media-card__link')?.focus();
}

function flipFavorite(form: HTMLFormElement): void {
    const now = form.dataset.state !== 'true';
    form.dataset.state = now ? 'true' : 'false';
    const value = form.querySelector<HTMLInputElement>('input[name=isWatchlisted]');
    if (value) value.value = now ? 'false' : 'true';
    const label = form.querySelector<HTMLElement>('button span');
    if (label) label.textContent = (now ? form.dataset.remove : form.dataset.add) ?? label.textContent;
}

export function attachCardMenus(): void {
    // The panel is fixed to the screen: a row that scrolls sideways would cut an absolute one.
    document.addEventListener('toggle', event => {
        const menu = menuOf(event.target);
        if (!menu) return;
        if (menu.open) { closeAll(menu); place(menu); }
    }, true);
    // The panel follows its button while the page scrolls or the screen is resized, and closes when the button leaves the screen.
    const follow = (): void => document.querySelectorAll<HTMLDetailsElement>('.media-card__menu details[open]').forEach(menu => {
        const button = menu.querySelector<HTMLElement>('summary')?.getBoundingClientRect();
        if (!button || button.bottom < 0 || button.top > window.innerHeight || button.right < 0 || button.left > document.documentElement.clientWidth) menu.open = false;
        else place(menu);
    });
    window.addEventListener('scroll', follow, { capture: true, passive: true });
    window.addEventListener('resize', follow, { passive: true });
    document.addEventListener('click', event => { const inside = menuOf(event.target); closeAll(inside); });

    document.addEventListener('keydown', event => {
        const menu = menuOf(event.target) ?? document.querySelector<HTMLDetailsElement>('.media-card__menu details[open]');
        if (!menu) return;
        // The shared menu listener may have closed it already; the focus still returns to the button.
        if (event.key === 'Escape') {
            menu.open = false;
            menu.querySelector<HTMLElement>('summary')?.focus();
            return;
        }

        // The arrow keys walk the entries, from the button into the panel too.
        if ((event.key === 'ArrowDown' || event.key === 'ArrowUp') && menu.open) {
            const entries = Array.from(menu.querySelectorAll<HTMLElement>('.action-menu__item'));
            const at = entries.indexOf(document.activeElement as HTMLElement);
            const to = event.key === 'ArrowDown' ? (at + 1) % entries.length : (at <= 0 ? entries.length - 1 : at - 1);
            entries[to]?.focus();
            event.preventDefault();
        }
    });

    document.addEventListener('submit', event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.dataset.cardMenu) return;
        event.preventDefault();
        if (form.getAttribute('aria-busy') === 'true') return;
        const kind = form.dataset.cardMenu;
        const card = form.closest<HTMLElement>('.media-card');
        form.setAttribute('aria-busy', 'true');
        // A hidden field named action shadows form.action.
        fetch(form.getAttribute('action') ?? '', { method: 'POST', body: new FormData(form), credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
            .then(response => {
                if (!response.ok) throw new Error(String(response.status));
                const menu = menuOf(form);
                if (menu) { menu.open = false; menu.querySelector<HTMLElement>('summary')?.focus(); }
                if (leavesRow(kind) && card) removeCard(card);
                else if (kind === 'favorite') flipFavorite(form);
            })
            .catch(() => document.dispatchEvent(new CustomEvent('jularr:hint', { detail: { text: form.dataset.failed ?? '', failed: true } })))
            .finally(() => form.removeAttribute('aria-busy'));
    });
}
