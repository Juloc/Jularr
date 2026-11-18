import { onPage, refresh } from '../../../Modules/Navigation/PageSwap';

// A catalog title opened for the first time showed its catalog row at once: its details come from its catalog now, and the page is then shown again in place.
// A title asks once while its details arrive, so one its catalog does not describe does not ask again and again; when the catalog cannot answer, the notice says so.
const asked = new Set<string>();

onPage(import.meta.url, () => {
    const notice = document.querySelector<HTMLElement>('[data-details-pending]');
    const address = notice?.dataset.detailsPending;
    if (!notice || !address || asked.has(address)) return;
    asked.add(address);
    const unavailable = () => {
        asked.delete(address);
        if (notice.isConnected) notice.hidden = false;
    };
    fetch(address, { credentials: 'same-origin' })
        .then((response): Promise<{ available?: boolean }> | { available?: boolean } => response.ok ? response.json() : {})
        .then(result => {
            if (!result.available) return unavailable();
            if (notice.isConnected) return refresh();
        })
        .catch(unavailable);
});
