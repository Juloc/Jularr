let initialized = false;
let current: HTMLElement | undefined;
let timer: ReturnType<typeof setTimeout> | undefined;

function stop(): void {
    clearTimeout(timer);
    timer = undefined;
    if (!current) return;
    current.querySelector('[data-hover-trailer]')?.replaceChildren();
    delete current.dataset.expanded;
    for (const name of ['--preview-left', '--preview-top', '--preview-width', '--preview-height']) current.style.removeProperty(name);
    current = undefined;
}

export function initializeCards(): void {
    if (initialized) return;
    initialized = true;

    // Delegation includes new result fragments.
    document.addEventListener('pointerover', event => {
        if (!(event.target instanceof Element) || event.pointerType !== 'mouse' || !matchMedia('(hover: hover) and (pointer: fine)').matches) return;
        const card = event.target.closest<HTMLElement>('[data-media-card]');
        if (!card || card === current || !card.dataset.trailerId) return;
        stop();
        current = card;
        timer = setTimeout(() => {
            const id = card.dataset.trailerId;
            const target = card.querySelector('[data-hover-trailer]');
            if (!card.isConnected || !target || !id || !/^[A-Za-z0-9_-]{11}$/.test(id)) { stop(); return; }

            // One muted official trailer.
            const frame = document.createElement('iframe');
            frame.title = card.dataset.trailerLabel ?? '';
            frame.src = `https://www.youtube.com/embed/${id}?autoplay=1&mute=1&playsinline=1&controls=0`;
            frame.allow = 'autoplay; encrypted-media';
            frame.referrerPolicy = 'strict-origin-when-cross-origin';
            frame.tabIndex = -1;
            target.replaceChildren(frame);

            // Fit above the scrolling shelf.
            const slot = card.parentElement!.getBoundingClientRect();
            const width = Math.min(420, window.innerWidth - 48);
            const left = Math.max(24, Math.min(slot.left + slot.width / 2 - width / 2, window.innerWidth - width - 24));
            card.style.setProperty('--preview-left', `${left}px`);
            card.style.setProperty('--preview-top', `${slot.top}px`);
            card.style.setProperty('--preview-width', `${width}px`);
            card.style.setProperty('--preview-height', `${slot.height}px`);
            card.dataset.expanded = 'true';
        }, 2000);
    });
    document.addEventListener('pointerout', event => {
        if (!current || !(event.target instanceof Element) || !current.contains(event.target)) return;
        if (event.relatedTarget instanceof Node && current.contains(event.relatedTarget)) return;
        stop();
    });

    // Native links and forms stay native.
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
        stop();
        delete card.dataset.open;
        if (document.activeElement instanceof HTMLElement && card.contains(document.activeElement)) document.activeElement.blur();
    });

    // Release playback on navigation.
    document.addEventListener('visibilitychange', () => { if (document.hidden) stop(); });
    document.addEventListener('scroll', stop, { capture: true, passive: true });
    window.addEventListener('resize', stop);
    window.addEventListener('pagehide', stop);

}
