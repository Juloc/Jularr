import { onPage } from '../../../Modules/Navigation/PageSwap';

const boxSelector = 'input[name="workIds"][form="works-bulk"]';

function boxes(): HTMLInputElement[] {
    return Array.from(document.querySelectorAll<HTMLInputElement>(boxSelector));
}

// The mass editor follows the boxes: it shows how many titles are chosen and is only there while a title is.
function initializeSelection(): void {
    const editor = document.querySelector<HTMLElement>('[data-works-bar]');
    const all = document.querySelector<HTMLInputElement>('[data-works-select-all]');
    if (!editor || editor.dataset.worksReady) return;
    editor.dataset.worksReady = 'true';
    const count = editor.querySelector<HTMLElement>('[data-works-count]');
    const format = count?.dataset.format ?? '{0}';

    const update = (): void => {
        const items = boxes();
        const chosen = items.filter(box => box.checked);
        editor.dataset.active = chosen.length > 0 ? 'true' : 'false';
        if (count) count.textContent = format.replace('{0}', String(chosen.length));
        items.forEach(box => box.closest('tr')?.classList.toggle('works-row--selected', box.checked));
        if (all) {
            all.checked = items.length > 0 && chosen.length === items.length;
            all.indeterminate = chosen.length > 0 && chosen.length < items.length;
        }
    };

    document.querySelector('.works-list')?.addEventListener('change', event => {
        const target = event.target as HTMLInputElement;
        if (target === all) boxes().forEach(box => { box.checked = all!.checked; });
        if (target === all || target.matches(boxSelector)) update();
    });
    update();
}

// The titles of a delete are written into its dialog when it opens.
function initializeDelete(): void {
    const dialog = document.getElementById('works-delete') as HTMLDialogElement | null;
    const holder = dialog?.querySelector<HTMLElement>('[data-works-ids]');
    document.querySelectorAll<HTMLElement>('[data-works-delete]').forEach(button => {
        if (button.dataset.worksReady || !dialog || !holder) return;
        button.dataset.worksReady = 'true';
        button.addEventListener('click', () => {
            const ids = boxes().filter(box => box.checked).map(box => box.value);
            if (ids.length === 0) return;
            holder.replaceChildren(...ids.map(id => Object.assign(document.createElement('input'), { type: 'hidden', name: 'workIds', value: id })));
            dialog.showModal();
        });
    });
}

// A filter applies as soon as it is chosen; the button stays for everything without script.
function initializeFilters(): void {
    document.querySelectorAll<HTMLFormElement>('form[data-works-filters]').forEach(form => {
        if (form.dataset.worksReady) return;
        form.dataset.worksReady = 'true';
        form.addEventListener('change', event => {
            if ((event.target as HTMLElement).matches('select')) form.requestSubmit();
        });

        // A filter that lets everything through stays out of the address.
        form.addEventListener('submit', () => {
            const empty = Array.from(form.querySelectorAll<HTMLSelectElement>('select')).filter(select => select.value === '' && !select.disabled);
            empty.forEach(select => { select.disabled = true; });
            setTimeout(() => empty.forEach(select => { select.disabled = false; }), 0);
        });
    });
}

onPage(import.meta.url, () => {
    initializeSelection();
    initializeDelete();
    initializeFilters();
});
