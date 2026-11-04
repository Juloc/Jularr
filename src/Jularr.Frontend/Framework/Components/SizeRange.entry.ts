import { onPage } from "../../Modules/Navigation/PageSwap";
import { updateSizeRange } from "./SizeRange";

document.addEventListener('input', event => {
    if (!(event.target instanceof HTMLInputElement) || !event.target.matches('[data-size-range] input[data-size-thumb]')) return;
    const range = event.target.closest<HTMLElement>('[data-size-range]');
    if (range) updateSizeRange(range, event.target);
});

onPage(import.meta.url, () => {
    document.querySelectorAll<HTMLElement>('[data-size-range]').forEach(range => updateSizeRange(range));
}, { partial: true });
