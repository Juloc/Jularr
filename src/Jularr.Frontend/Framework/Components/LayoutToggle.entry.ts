import "./LayoutToggle.scss";

// The device's layout of personal lists; delegated from the document, so it also reaches pages swapped in later.
const layoutKey = 'list-layout';

document.documentElement.classList.add('layout-ready');

function applyLayout(layout: string | null): void {
    if (layout === 'list') document.documentElement.dataset.listLayout = 'list';
    else delete document.documentElement.dataset.listLayout;
}

try {
    applyLayout(localStorage.getItem(layoutKey));
} catch {
    // Without storage the titles stay in a grid.
}

document.addEventListener('click', event => {
    if (!(event.target instanceof Element) || !event.target.closest('[data-layout-toggle]')) return;
    const list = document.documentElement.dataset.listLayout !== 'list';
    applyLayout(list ? 'list' : null);
    try {
        localStorage.setItem(layoutKey, list ? 'list' : 'grid');
    } catch {
        // The layout then applies to this visit only.
    }
});
