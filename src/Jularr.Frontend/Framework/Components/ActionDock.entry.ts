import { update } from "./ActionDock";
import { onPage } from "../../Modules/Navigation/PageSwap";

function refresh(): void {
    const dock = document.querySelector<HTMLElement>('.action-dock');
    if (dock) update(dock, document.querySelectorAll<HTMLInputElement>('input[data-dock-select]'));
}

document.addEventListener('change', event => {
    if (event.target instanceof HTMLInputElement && event.target.hasAttribute('data-dock-select')) refresh();
});

onPage(import.meta.url, refresh);
