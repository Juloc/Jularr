import { markTruncated } from "../../../Framework/Components/Truncated";
import { onPage } from "../../../Modules/Navigation/PageSwap";
import { dependantNames, switchesOf, viewOf } from "./Modules";

// Shows each card as the switches allow: the tags that name what is missing or who needs it, and whether its switch can move.
function applyRules(): void {
    const modules = switchesOf(document);
    for (const card of Array.from(document.querySelectorAll<HTMLElement>("[data-module]"))) {
        const view = viewOf(modules, card.dataset.module ?? "");
        const input = card.querySelector<HTMLInputElement>("input[type=checkbox]");
        card.classList.toggle("setting-card--on", input?.checked === true);
        card.classList.toggle("setting-card--blocked", view.blocked);
        input?.setAttribute("aria-disabled", String(view.blocked));
        card.querySelectorAll<HTMLElement>("[data-needs]").forEach(tag => tag.classList.toggle("tag--warning", view.missing.includes(tag.dataset.needs ?? "")));

        // "Needed by ...": the names of the modules that need this one and are on, cut where there is no room, whole in the tip.
        const needed = card.querySelector<HTMLElement>("[data-needed-names]");
        if (needed) {
            const text = dependantNames(JSON.parse(needed.dataset.neededNames ?? "{}") as Record<string, string>, view.dependants);
            needed.querySelectorAll<HTMLElement>(".truncated__text, .truncated__tip").forEach(part => { part.textContent = text; });
            needed.hidden = view.dependants.length === 0;
        }
    }

    markTruncated(document);
}

// A switch that cannot move now (what it needs is off, or others need it) stays where it is; its card shows why.
document.addEventListener("click", event => {
    const input = event.target instanceof HTMLInputElement ? event.target : null;
    const card = input?.closest<HTMLElement>("[data-module]");
    if (!input || !card || input.type !== "checkbox") return;

    // The browser has already turned the box over; the rules read the state before the click.
    const wasOn = !input.checked;
    input.checked = wasOn;
    if (viewOf(switchesOf(document), card.dataset.module ?? "").blocked) {
        event.preventDefault();
        card.classList.remove("setting-card--refused");
        void card.offsetWidth;
        card.classList.add("setting-card--refused");
        return;
    }

    input.checked = !wasOn;
    applyRules();
});

// A discarded or reloaded form shows its switches as saved.
document.addEventListener("reset", () => queueMicrotask(applyRules));

onPage(import.meta.url, applyRules);
