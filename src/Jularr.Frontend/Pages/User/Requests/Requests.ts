// Closes the open menu of the request bar, or the request panel, on Escape; a click outside also closes the menu.
export function closeMenus(root: ParentNode, except?: Element | null): void {
    for (const menu of Array.from(root.querySelectorAll<HTMLDetailsElement>(".requests-menu[open]"))) {
        if (menu !== except) menu.open = false;
    }
}

// What Escape does: the open menu first, otherwise the panel through its close link.
export function escape(root: ParentNode): boolean {
    if (root.querySelector(".requests-menu[open]")) {
        closeMenus(root);
        return true;
    }

    const close = root.querySelector<HTMLElement>(".request-drawer__head a");
    if (!close) return false;
    close.click();
    return true;
}
