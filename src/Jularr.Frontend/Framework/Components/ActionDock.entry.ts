import { update } from "./ActionDock";
import { onPage } from "../../Modules/Navigation/PageSwap";

const boxes = (): HTMLInputElement[] => Array.from(document.querySelectorAll<HTMLInputElement>('input[data-dock-select]'));

function refresh(): void {
    const items = boxes();
    update(document.querySelector<HTMLElement>('.action-dock'), items, document.querySelector<HTMLInputElement>('input[data-dock-select-all]'));
    items.forEach(box => box.closest('.list-row')?.classList.toggle('list-row--selected', box.checked));
}

document.addEventListener('change', event => {
    const target = event.target;
    if (!(target instanceof HTMLInputElement)) return;
    if (target.hasAttribute('data-dock-select-all')) boxes().forEach(box => { box.checked = target.checked; });
    else if (!target.hasAttribute('data-dock-select')) return;
    refresh();
});

onPage(import.meta.url, refresh);
