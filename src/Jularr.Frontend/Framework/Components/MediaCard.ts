let initialized = false;

export function initializeCards(): void {
    if (initialized) return;
    initialized = true;

    // Native links and forms remain clickable. Tapping a noninteractive poster
    // area toggles the existing status capsule without navigation.
    document.addEventListener('click', event => {
        if (!(event.target instanceof Element)) return;
        const card = event.target.closest<HTMLElement>('[data-media-card]');
        if (!card) return;
        if (!event.target.closest('a, button, input, summary') && !matchMedia('(hover: hover) and (pointer: fine)').matches) {
            if (card.hasAttribute('data-open')) delete card.dataset.open;
            else card.dataset.open = 'true';
        }
    });

    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || !(event.target instanceof Element)) return;
        const card = event.target.closest<HTMLElement>('[data-media-card]');
        if (!card) return;
        delete card.dataset.open;
        if (document.activeElement instanceof HTMLElement && card.contains(document.activeElement)) document.activeElement.blur();
    });
}
