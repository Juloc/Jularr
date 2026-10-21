import { onPage } from "../../../Modules/Navigation/PageSwap";
import { applySelection } from "./Wanted";

const boxes = (): HTMLInputElement[] => Array.from(document.querySelectorAll<HTMLInputElement>('input[name="wantedItemIds"]'));

// The bar follows the boxes; the select-all box chooses every row of the page.
document.addEventListener("change", event => {
    const target = event.target as HTMLElement | null;
    const form = document.querySelector<HTMLFormElement>("#wanted-bulk");
    if (!form || !target) return;
    if (target.matches("[data-wanted-all]")) boxes().forEach(box => { box.checked = (target as HTMLInputElement).checked; });
    else if (!target.matches('input[name="wantedItemIds"]')) return;
    applySelection(form, boxes());
});

onPage(import.meta.url, () => {
    const form = document.querySelector<HTMLFormElement>("#wanted-bulk");
    if (form) applySelection(form, boxes());
});
