import { inserted, onPage, regionSwapped } from "../../Modules/Navigation/PageSwap";
import { initializeInfiniteList } from "./InfiniteList";

let active: { root: HTMLElement; dispose: () => void } | undefined;
document.addEventListener("htmx:after:swap", () => {
    if (active && !active.root.isConnected) { active.dispose(); active = undefined; }
});

onPage(import.meta.url, () => {
    active?.dispose();
    active = undefined;
    const root = document.querySelector<HTMLElement>("[data-infinite-list]");
    if (!root) return;
    const dispose = initializeInfiniteList(root, {
        historyState: { htmx: true },
        inserted: target => { inserted(target); regionSwapped(); }
    });
    if (dispose) active = { root, dispose };
});
