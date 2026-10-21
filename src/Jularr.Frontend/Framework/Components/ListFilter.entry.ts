import { applyFilter } from "./ListFilter";
import { onPage } from "../../Modules/Navigation/PageSwap";

// A field with data-list-filter="<selector of the list>" narrows that list as the person types; without script its form still filters on the server.
function filter(input: HTMLInputElement): void {
    const list = document.querySelector(input.dataset.listFilter ?? '');
    if (!list) return;
    const shown = applyFilter(list, input.value);
    const count = document.querySelector<HTMLElement>('[data-list-count]');
    if (count?.dataset.format) count.textContent = count.dataset.format.replace('{0}', String(shown));
}

document.addEventListener('input', event => {
    if (event.target instanceof HTMLInputElement && event.target.dataset.listFilter) filter(event.target);
});

// Enter keeps the person where they are.
document.addEventListener('submit', event => {
    if (event.target instanceof HTMLFormElement && event.target.querySelector('[data-list-filter]')) event.preventDefault();
});

document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && event.target instanceof HTMLInputElement && event.target.dataset.listFilter && event.target.value) {
        event.target.value = '';
        filter(event.target);
    }
});

onPage(import.meta.url, () => document.querySelectorAll<HTMLInputElement>('[data-list-filter]').forEach(filter));
