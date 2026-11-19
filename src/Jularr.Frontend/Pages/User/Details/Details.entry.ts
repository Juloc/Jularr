import { onPage, showContent } from '../../../Modules/Navigation/PageSwap';

// A catalog title opened for the first time answered at once with placeholders for what its details bring: the page with them (or with its catalog row and the
// word that its catalog cannot give them now) comes from the address in data-details-pending and takes the placeholders' place, while this page is still shown.
// A title asks once while its page is shown, so a page that holds placeholders again never asks again and again.
const asked = new Set<string>();

onPage(import.meta.url, () => {
    const marker = document.querySelector<HTMLElement>('[data-details-pending]');
    const address = marker?.dataset.detailsPending;
    if (!marker || !address || asked.has(address)) return;
    asked.add(address);
    fetch(address, { credentials: 'same-origin' })
        .then(response => response.ok ? response.text() : Promise.reject(new Error(String(response.status))))
        .then(html => {
            if (marker.isConnected) showContent(html);
        })
        .catch(() => asked.delete(address));
});
