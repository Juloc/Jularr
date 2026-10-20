// Document-level, installed once: an open link menu closes on a tap outside it, on Escape and on choosing a link.
function closeMenus(except?: Element | null): void {
    document.querySelectorAll<HTMLDetailsElement>("details.link-menu[open]").forEach(menu => {
        if (!except || !menu.contains(except)) menu.open = false;
    });
}

document.addEventListener("click", event => {
    const target = event.target instanceof Element ? event.target : null;
    closeMenus(target?.closest("details.link-menu"));
});
document.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    const open = document.querySelector<HTMLDetailsElement>("details.link-menu[open]");
    if (open) {
        open.open = false;
        open.querySelector<HTMLElement>("summary")?.focus();
    }
});
