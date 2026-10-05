(() => {
    "use strict";

    // Filters, Sort and the card menus are <details>: they open and close natively. This only closes
    // them when the visitor clicks elsewhere, presses Escape or opens another one, and closes the
    // Filters/Sort sheet from its backdrop and close button. Every view is a plain link or GET form,
    // so without script the page still works.
    const openMenus = () => document.querySelectorAll("details[data-lib-pop][open], details[data-lib-menu][open]");

    document.addEventListener("click", event => {
        const target = event.target;
        if (!(target instanceof Element)) {
            return;
        }

        const closer = target.closest("[data-lib-close]");
        if (closer) {
            const pop = closer.closest("details");
            if (pop) {
                pop.open = false;
                pop.querySelector("summary")?.focus();
            }
            return;
        }

        for (const menu of openMenus()) {
            if (!menu.contains(target)) {
                menu.open = false;
            }
        }
    });

    document.addEventListener("keydown", event => {
        if (event.key !== "Escape") {
            return;
        }

        for (const menu of openMenus()) {
            menu.open = false;
            menu.querySelector("summary")?.focus();
        }
    });

    document.addEventListener("toggle", event => {
        const opened = event.target;
        if (!(opened instanceof HTMLDetailsElement) || !opened.open) {
            return;
        }

        for (const menu of openMenus()) {
            if (menu !== opened && !menu.contains(opened)) {
                menu.open = false;
            }
        }
    }, true);

    // Loading state: the current results dim while the next view is requested.
    const results = document.querySelector("[data-lib-results]");
    const setBusy = busy => results?.setAttribute("aria-busy", busy ? "true" : "false");

    document.addEventListener("click", event => {
        const link = event.target instanceof Element ? event.target.closest("a[data-lib-nav]") : null;
        if (link && !event.defaultPrevented && event.button === 0
            && !(event.metaKey || event.ctrlKey || event.shiftKey || event.altKey)) {
            setBusy(true);
        }
    });

    document.addEventListener("submit", event => {
        if (event.target instanceof HTMLFormElement && event.target.hasAttribute("data-lib-form")) {
            setBusy(true);
        }
    });

    // Coming back through the history restores the page as it was.
    window.addEventListener("pageshow", () => setBusy(false));

    // Offline notice: shown while the browser has no connection.
    const offline = document.querySelector("[data-lib-offline]");
    const syncOffline = () => {
        if (offline) {
            offline.hidden = navigator.onLine;
        }
    };
    window.addEventListener("online", syncOffline);
    window.addEventListener("offline", syncOffline);
    syncOffline();
})();
