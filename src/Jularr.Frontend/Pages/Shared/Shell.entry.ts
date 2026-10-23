import { applyMenuFilter } from "../../Framework/Components/ListFilter";

// The shell's own interactions, installed once for the document: the phone's menu drawer, the slash key for the search and the find field of the administration menu.
// The shell itself is swapped with every page, so the handlers are delegated.
const phone = window.matchMedia("(max-width: 640px)");

function setMenu(open: boolean): void {
    document.querySelector(".app-layout")?.classList.toggle("app-layout--menu-open", open);
    document.querySelectorAll("[data-sidebar-toggle]").forEach(button => button.setAttribute("aria-expanded", String(open)));
}

function typing(target: EventTarget | null): boolean {
    return target instanceof HTMLElement && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName));
}

document.addEventListener("click", event => {
    if (!(event.target instanceof Element)) return;
    if (event.target.closest("[data-sidebar-toggle]")) {
        setMenu(!document.querySelector(".app-layout")?.classList.contains("app-layout--menu-open"));
    } else if (event.target.closest("[data-sidebar-overlay]")) {
        setMenu(false);
    }
});

document.addEventListener("keydown", event => {
    if (event.key === "Escape") setMenu(false);
    if (event.key !== "/" || event.ctrlKey || event.metaKey || event.altKey || typing(event.target)) return;
    const search = document.querySelector<HTMLInputElement>(".app-search .search-box__input");
    if (search) {
        event.preventDefault();
        search.focus();
    }
});

// The find field narrows the entries as one types (the group names count too); Enter opens the first one left, Escape clears the field.
function menuFind(target: EventTarget | null): HTMLInputElement | null {
    return target instanceof HTMLInputElement && target.closest("[data-menu-find]") ? target : null;
}

document.addEventListener("input", event => {
    const field = menuFind(event.target);
    if (field) applyMenuFilter(field.closest(".menu") ?? document, field.value);
});

document.addEventListener("submit", event => {
    if (event.target instanceof HTMLFormElement && event.target.closest("[data-menu-find]")) {
        event.preventDefault();
        event.target.closest(".menu")?.querySelector<HTMLElement>(".menu-entry:not([hidden])")?.click();
    }
});

document.addEventListener("keydown", event => {
    const field = menuFind(event.target);
    if (event.key === "Escape" && field?.value) {
        field.value = "";
        applyMenuFilter(field.closest(".menu") ?? document, "");
    }
});

phone.addEventListener("change", () => { if (!phone.matches) setMenu(false); });
