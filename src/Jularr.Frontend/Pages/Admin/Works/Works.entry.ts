import { onPage } from '../../../Modules/Navigation/PageSwap';

const boxSelector = 'input[name="workIds"][form="works-bulk"]';

// The titles of a delete are written into its dialog when it opens: the chosen ones of the list (the dock's bulk zone opens it).
function initializeDelete(): void {
    const dialog = document.getElementById('works-delete') as HTMLDialogElement | null;
    const holder = dialog?.querySelector<HTMLElement>('[data-works-ids]');
    document.querySelectorAll<HTMLElement>('[data-works-delete]').forEach(button => {
        if (button.dataset.worksReady || !dialog || !holder) return;
        button.dataset.worksReady = 'true';
        button.addEventListener('click', () => {
            const ids = Array.from(document.querySelectorAll<HTMLInputElement>(boxSelector)).filter(box => box.checked).map(box => box.value);
            if (ids.length === 0) return;
            holder.replaceChildren(...ids.map(id => Object.assign(document.createElement('input'), { type: 'hidden', name: 'workIds', value: id })));
            dialog.showModal();
        });
    });
}

// A filter that lets everything through stays out of the address.
document.addEventListener('submit', event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const empty = Array.from(form.querySelectorAll<HTMLSelectElement>('select[data-works-filter]')).filter(select => select.value === '' && !select.disabled);
    empty.forEach(select => { select.disabled = true; });
    setTimeout(() => empty.forEach(select => { select.disabled = false; }), 0);
}, true);

onPage(import.meta.url, initializeDelete);
