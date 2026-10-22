import { enhanceDropdowns, installDropdowns } from "./Dropdown";
import { onPage } from "../../Modules/Navigation/PageSwap";

installDropdowns();

// Every select of the app turns into the styled dropdown, now and wherever a script or a swapped page inserts one later.
let scheduled = false;
new MutationObserver(() => {
    if (scheduled) return;
    scheduled = true;
    queueMicrotask(() => { scheduled = false; enhanceDropdowns(); });
}).observe(document.documentElement, { childList: true, subtree: true });

onPage(import.meta.url, () => enhanceDropdowns());
