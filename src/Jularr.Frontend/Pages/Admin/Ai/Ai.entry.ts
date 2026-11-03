import { onPage } from "../../../Modules/Navigation/PageSwap";

// A login that waits for the person: the page asks for the card again every three seconds while it is shown and swaps it once the state changed.
const Interval = 3000;
let polling = false;

async function poll(): Promise<void> {
    const current = document.querySelector<HTMLElement>("#codex-card[data-codex-pending]");
    if (!current?.isConnected) {
        polling = false;
        return;
    }

    if (!document.hidden) {
        try {
            const response = await fetch(location.pathname + "?handler=Card", { credentials: "same-origin", cache: "no-store" });
            if (response.ok) {
                const template = document.createElement("template");
                template.innerHTML = (await response.text()).trim();
                const next = template.content.firstElementChild;
                if (next?.id === "codex-card" && next.getAttribute("data-codex-phase") !== current.getAttribute("data-codex-phase")) current.replaceWith(next);
            }
        } catch {
            // The next round tries again.
        }
    }

    window.setTimeout(() => { void poll(); }, Interval);
}

// An action swaps the card in place, so the setup also runs after a region swap; one loop at a time.
onPage(import.meta.url, () => {
    if (polling || !document.querySelector("#codex-card[data-codex-pending]")) return;
    polling = true;
    window.setTimeout(() => { void poll(); }, Interval);
}, { partial: true });
