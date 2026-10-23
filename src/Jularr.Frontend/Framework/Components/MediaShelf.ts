const initialized = new Set<HTMLElement>();

// One observer for visible shelf widths.
const resize = new ResizeObserver(entries => {
    for (const entry of entries) update(entry.target as HTMLElement);
});

function update(items: HTMLElement): void {
    if (!items.isConnected) return;
    const shelf = items.closest('.media-shelf')!;
    const position = Math.abs(items.scrollLeft);
    const maximum = items.scrollWidth - items.clientWidth;
    shelf.querySelectorAll<HTMLButtonElement>('[data-shelf-direction]').forEach(button => {
        const hidden = maximum <= 1 || (button.dataset.shelfDirection === 'previous' ? position <= 1 : position >= maximum - 1);
        const focused = document.activeElement === button;
        button.hidden = hidden;
        // Preserve focus at either edge.
        if (hidden && focused) items.focus({ preventScroll: true });
    });
}

export function initializeShelves(root: ParentNode = document): void {
    // Release removed fragment shelves.
    for (const items of initialized) {
        if (!items.isConnected) {
            resize.unobserve(items);
            initialized.delete(items);
        }
    }

    root.querySelectorAll<HTMLElement>('.media-shelf__items').forEach(items => {
        if (!initialized.has(items)) {
            initialized.add(items);
            items.addEventListener('scroll', () => update(items), { passive: true });
            resize.observe(items);
        }
        update(items);
    });
}

// Delegation includes later HTML fragments.
document.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;
    const button = event.target.closest<HTMLButtonElement>('[data-shelf-direction]');
    const items = button?.closest('.media-shelf')?.querySelector<HTMLElement>('.media-shelf__items');
    if (!button || !items || button.hidden) return;
    const direction = getComputedStyle(items).direction === 'rtl' ? -1 : 1;
    const step = button.dataset.shelfDirection === 'previous' ? -1 : 1;
    items.scrollBy({ left: direction * step * items.clientWidth * .85, behavior: 'smooth' });
});
