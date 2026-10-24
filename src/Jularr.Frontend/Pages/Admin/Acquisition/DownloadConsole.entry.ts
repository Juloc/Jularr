import { inserted, onPage } from "../../../Modules/Navigation/PageSwap";

// SABnzbd's external queue is not part of Jularr's operation SSE. Request only this
// server-rendered fragment; reuse the app's page lifecycle and htmx initialization.
const intervalMs = 5_000;
let interval: ReturnType<typeof setInterval> | undefined;
let pending = false;

onPage(import.meta.url, () => {
    if (interval !== undefined) clearInterval(interval);
    interval = undefined;
    if (!document.querySelector('.download-console[data-auto-refresh="true"]')) return;

    interval = setInterval(async () => {
        const current = document.querySelector<HTMLElement>('.download-console[data-auto-refresh="true"]');
        if (!current || pending || document.hidden || !current.isConnected
            || current.querySelector('form:focus-within, a:focus-visible, dialog[data-open="true"]')) return;

        pending = true;
        const address = new URL(window.location.href);
        address.searchParams.set("handler", "Console");
        const locationBefore = window.location.href;
        try {
            const response = await fetch(address, { credentials: "same-origin", cache: "no-store" });
            if (!response.ok) return;
            const template = document.createElement("template");
            template.innerHTML = await response.text();
            const next = template.content.querySelector<HTMLElement>('.download-console[data-auto-refresh="true"]');
            if (next && current.isConnected && locationBefore === window.location.href) {
                current.replaceWith(next);
                inserted(next);
            }
        } catch {
            // The visible snapshot remains available while SABnzbd is unreachable.
        } finally {
            pending = false;
        }
    }, intervalMs);
});
