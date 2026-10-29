import { rangeBetween, update } from "./ActionDock";
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

// Shift-click chooses a range from the box clicked before; a clear button (data-dock-clear) unchooses every row.
let last: HTMLInputElement | null = null;

document.addEventListener('click', event => {
    const target = event.target;
    if (target instanceof Element && target.closest('[data-dock-clear]')) {
        boxes().forEach(box => { box.checked = false; });
        last = null;
        refresh();
        return;
    }

    if (!(target instanceof HTMLInputElement) || !target.hasAttribute('data-dock-select')) return;
    if (event instanceof MouseEvent && event.shiftKey && last && last !== target && last.isConnected) {
        const items = boxes();
        rangeBetween(items.indexOf(last), items.indexOf(target)).forEach(index => { items[index].checked = target.checked; });
        refresh();
    }

    last = target;
});

onPage(import.meta.url, refresh);
