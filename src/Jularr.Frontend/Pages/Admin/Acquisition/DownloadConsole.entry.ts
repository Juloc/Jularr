// Refresh only the provider snapshot; never replace configuration inputs or an in-progress action.
const intervalMs = 5_000;
let requestActive = false;
let lastLocation = window.location.href;

window.setInterval(async () => {
    const current = document.querySelector<HTMLElement>('.download-console[data-auto-refresh="true"]');
    if (!current || document.hidden || requestActive || window.location.href !== lastLocation
        || current.querySelector('form:focus-within, a:focus-visible, .download-console__confirm')) return;

    requestActive = true;
    try {
        const response = await fetch(window.location.href, { credentials: 'same-origin', cache: 'no-store' });
        if (!response.ok) return;
        const parsed = new DOMParser().parseFromString(await response.text(), 'text/html');
        const next = parsed.querySelector<HTMLElement>('.download-console[data-auto-refresh="true"]');
        if (next && current.isConnected && window.location.href === lastLocation) current.replaceWith(document.importNode(next, true));
    } catch {
        // Network outages keep the last known snapshot until a later refresh.
    } finally {
        requestActive = false;
        lastLocation = window.location.href;
    }
}, intervalMs);
