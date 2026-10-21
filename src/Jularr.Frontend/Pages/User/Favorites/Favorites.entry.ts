import { onPage } from '../../../Modules/Navigation/PageSwap';

// Device-local layout, selecting titles and the state of the collections popover. Everything is delegated from the document,
// so it also reaches pages swapped in later; the selection lives in the buttons of the page itself.
const layoutKey = 'favorites-layout';
const pickerId = 'favorites-picker';

document.documentElement.classList.add('favorites-ready');

function applyLayout(layout: string | null): void {
    if (layout === 'list') document.documentElement.dataset.favoritesLayout = 'list';
    else delete document.documentElement.dataset.favoritesLayout;
}

try {
    applyLayout(localStorage.getItem(layoutKey));
} catch {
    // Without storage the titles stay in a grid.
}

function selected(): HTMLElement[] {
    return Array.from(document.querySelectorAll<HTMLElement>('.favorites [data-favorites-select][aria-pressed="true"]'));
}

// The count and the actions that need a selection.
function updateSelection(): void {
    const bar = document.querySelector<HTMLElement>('[data-favorites-bulk]');
    if (!bar) return;
    const count = selected().length;
    const label = bar.querySelector<HTMLElement>('[data-favorites-count]');
    if (label) label.textContent = (label.dataset.label ?? '').replace('{0}', String(count));
    bar.querySelectorAll<HTMLButtonElement>('[data-favorites-picker], [data-favorites-bulk-form] button').forEach(button => { button.disabled = count === 0; });
}

function setSelecting(on: boolean): void {
    const root = document.querySelector<HTMLElement>('.favorites');
    if (!root) return;
    root.classList.toggle('favorites--bulk', on);
    root.querySelector('[data-favorites-bulk-toggle]')?.setAttribute('aria-pressed', String(on));
    const bar = root.querySelector<HTMLElement>('[data-favorites-bulk]');
    if (bar) bar.hidden = !on;
    if (!on) root.querySelectorAll('[data-favorites-select]').forEach(button => button.setAttribute('aria-pressed', 'false'));
    updateSelection();
}

// The popover holds the collections of one title, or of every selected one; the server decides what changes from what is checked.
function preparePicker(target: string): void {
    const picker = document.getElementById(pickerId);
    if (!picker) return;
    const bulk = target === 'bulk';
    const ids = bulk ? selected().map(button => button.dataset.favoritesSelect ?? '') : [target];
    const entries = ids.map(id => document.querySelector<HTMLElement>(`[data-favorites-entry="${CSS.escape(id)}"]`));

    const works = picker.querySelector<HTMLElement>('[data-favorites-works]');
    works?.replaceChildren(...ids.map(id => {
        const input = document.createElement('input');
        input.type = 'hidden';
        input.name = 'workIds';
        input.value = id;
        return input;
    }));

    // A collection is checked when it holds every one of them.
    picker.querySelectorAll<HTMLInputElement>('input[name="collectionIds"]').forEach(box => {
        box.checked = entries.length > 0 && entries.every(entry => (entry?.dataset.collections ?? '').split(',').includes(box.value));
    });

    const subject = picker.querySelector<HTMLElement>('[data-favorites-subject]');
    if (subject) subject.textContent = bulk ? (document.querySelector('[data-favorites-count]')?.textContent ?? '') : (entries[0]?.dataset.title ?? '');
    picker.querySelector<HTMLElement>('[data-favorites-remove]')?.toggleAttribute('hidden', bulk);
    const name = picker.querySelector<HTMLInputElement>('input[name="newName"]');
    if (name) name.value = '';
}

document.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;

    const picker = event.target.closest<HTMLElement>('[data-favorites-picker]');
    if (picker) {
        preparePicker(picker.dataset.favoritesPicker ?? '');
        return;
    }

    if (event.target.closest('[data-favorites-layout]')) {
        const list = document.documentElement.dataset.favoritesLayout !== 'list';
        applyLayout(list ? 'list' : null);
        try {
            localStorage.setItem(layoutKey, list ? 'list' : 'grid');
        } catch {
            // The layout then applies to this visit only.
        }

        return;
    }

    const toggle = event.target.closest<HTMLElement>('[data-favorites-bulk-toggle]');
    if (toggle) {
        setSelecting(toggle.getAttribute('aria-pressed') !== 'true');
        return;
    }

    const select = event.target.closest<HTMLElement>('[data-favorites-select]');
    if (select) {
        select.setAttribute('aria-pressed', String(select.getAttribute('aria-pressed') !== 'true'));
        updateSelection();
        return;
    }

    if (event.target.closest('[data-favorites-select-shown]')) {
        document.querySelectorAll('.favorites [data-favorites-select]').forEach(button => button.setAttribute('aria-pressed', 'true'));
        updateSelection();
        return;
    }

    if (event.target.closest('[data-favorites-clear]')) {
        document.querySelectorAll('.favorites [data-favorites-select]').forEach(button => button.setAttribute('aria-pressed', 'false'));
        updateSelection();
    }
});

// The selected titles travel with the form that acts on them.
document.addEventListener('submit', event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || !form.matches('[data-favorites-bulk-form]')) return;
    const chosen = selected();
    if (chosen.length === 0) {
        event.preventDefault();
        return;
    }

    form.querySelectorAll('input[name="workIds"]').forEach(input => input.remove());
    for (const button of chosen) {
        const input = document.createElement('input');
        input.type = 'hidden';
        input.name = 'workIds';
        input.value = button.dataset.favoritesSelect ?? '';
        form.append(input);
    }
});

// The chosen collection stays in view along the strip.
function showCurrentCollection(): void {
    document.querySelector('.favorites-collection__card--current')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
}

onPage(import.meta.url, () => {
    updateSelection();
    showCurrentCollection();
});
