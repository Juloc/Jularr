import { inserted, onPage } from "../../Modules/Navigation/PageSwap";

// Loads every deferred part of the page (DeferredSection) at once, each in place of its skeleton. A failed part keeps its room
// and offers to try again; a part that arrives replaces the skeleton without moving the page.
function load(section: HTMLElement): void {
    const address = section.dataset.deferred;
    if (!address || section.dataset.deferredLoading) return;
    section.dataset.deferredLoading = "true";
    fetch(address, { credentials: "same-origin", headers: { "X-Requested-With": "fetch" } })
        .then(response => {
            if (!response.ok) throw new Error(String(response.status));
            return response.text();
        })
        .then(html => {
            const template = document.createElement("template");
            template.innerHTML = html.trim();
            const parts = Array.from(template.content.children);
            section.replaceWith(...parts);
            parts.forEach(part => inserted(part));
        })
        .catch(() => fail(section));
}

function fail(section: HTMLElement): void {
    delete section.dataset.deferredLoading;
    section.removeAttribute("aria-busy");
    const message = document.createElement("p");
    message.className = "deferred-section__failed";
    message.textContent = section.dataset.failed ?? "";
    const retry = document.createElement("button");
    retry.type = "button";
    retry.className = "button button--soft";
    retry.textContent = section.dataset.retry ?? "";
    retry.addEventListener("click", () => {
        section.replaceChildren(skeleton());
        section.setAttribute("aria-busy", "true");
        load(section);
    }, { once: true });
    section.replaceChildren(message, retry);
}

function skeleton(): HTMLElement {
    const block = document.createElement("span");
    block.className = "skeleton-block deferred-section__skeleton";
    block.setAttribute("aria-hidden", "true");
    return block;
}

onPage(import.meta.url, () => {
    document.querySelectorAll<HTMLElement>("[data-deferred]").forEach(load);
}, { partial: true });
