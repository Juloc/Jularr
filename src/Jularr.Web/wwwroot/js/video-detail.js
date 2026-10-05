// Movie and Series detail pages: the shared Request dialog (discover-request.js) is the only request surface, so this
// only closes the overflow menu a Request was started from and, after a request was created, reloads the page when
// the dialog closes so the hero and the episodes show the state the server persisted.
(() => {
    "use strict";

    const host = document.querySelector("[data-request-host]");
    const dialog = host?.querySelector("[data-dc-rq]");
    if (!host || !dialog) return;

    let created = false;
    host.addEventListener("dc:request-created", () => { created = true; });
    host.addEventListener("click", event => {
        const trigger = event.target instanceof Element ? event.target.closest("[data-dc-card-request]") : null;
        trigger?.closest("details[open]")?.removeAttribute("open");
    });
    dialog.addEventListener("close", () => {
        if (created) window.location.reload();
    });
})();
