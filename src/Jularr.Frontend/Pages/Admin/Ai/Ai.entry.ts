import { onPage } from "../../../Modules/Navigation/PageSwap";

// A login that waits for the person: the page asks for the card again every three seconds while it is shown and reloads once the state changed, so the cards
// that belong to a connection (usage, settings) come and go with it.
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
                if (next?.id === "codex-card" && next.getAttribute("data-codex-phase") !== current.getAttribute("data-codex-phase")) {
                    polling = false;
                    location.reload();
                    return;
                }
            }
        } catch {
            // The next round tries again.
        }
    }

    window.setTimeout(() => { void poll(); }, Interval);
}

// Requests to Codex that run: the activity card is asked for again every three seconds while one runs, and swapped in place.
let watching = false;

async function watchActivity(): Promise<void> {
    const current = document.querySelector<HTMLElement>("#codex-activity[data-codex-busy]");
    if (!current?.isConnected) {
        watching = false;
        return;
    }

    if (!document.hidden) {
        try {
            const response = await fetch(location.pathname + "?handler=Activity", { credentials: "same-origin", cache: "no-store" });
            if (response.ok) {
                const template = document.createElement("template");
                template.innerHTML = (await response.text()).trim();
                const next = template.content.firstElementChild;
                if (next?.id === "codex-activity") current.replaceWith(next);
            }
        } catch {
            // The next round tries again.
        }
    }

    window.setTimeout(() => { void watchActivity(); }, Interval);
}

// An action swaps the card in place, so the setup also runs after a region swap; one loop of each kind at a time.
onPage(import.meta.url, () => {
    if (!polling && document.querySelector("#codex-card[data-codex-pending]")) {
        polling = true;
        window.setTimeout(() => { void poll(); }, Interval);
    }

    if (!watching && document.querySelector("#codex-activity[data-codex-busy]")) {
        watching = true;
        window.setTimeout(() => { void watchActivity(); }, Interval);
    }
}, { partial: true });
