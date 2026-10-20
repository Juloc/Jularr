import { closeMenus, escape } from "./Requests";
import { onPage } from "../../../Modules/Navigation/PageSwap";

document.addEventListener("click", event => closeMenus(document, (event.target as Element | null)?.closest(".requests-menu")));
document.addEventListener("keydown", event => {
    if (event.key === "Escape" && document.querySelector(".requests-bar") && escape(document)) event.preventDefault();
});

// An opened panel takes the focus; its close link is the first thing in it.
onPage(import.meta.url, () => document.querySelector<HTMLElement>(".request-drawer__head a")?.focus({ preventScroll: true }));
