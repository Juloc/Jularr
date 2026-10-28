import { initializeInfiniteList } from "../../Framework/Components/InfiniteList";
import { readerLifetime } from "./Lifetime";

export function initializeNotes(reader: HTMLElement): void {
    const root = reader.querySelector<HTMLElement>('[data-infinite-list=".reader__notes"]');
    const panel = root?.closest<HTMLElement>('[data-panel="notes"]');
    if (!root || !panel) return;
    const dispose = initializeInfiniteList(root, {
        scrollRoot: panel,
        requestAddress: href => {
            const address = new URL(href);
            address.searchParams.set("handler", "Notes");
            address.searchParams.set("s", reader.dataset.section ?? "0");
            return address.href;
        }
    });
    const signal = readerLifetime();
    if (dispose) {
        window.addEventListener("pagehide", event => { if (!event.persisted) dispose(); }, { signal });
        signal.addEventListener("abort", dispose);
    }
}
