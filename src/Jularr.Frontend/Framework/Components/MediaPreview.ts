let opener: HTMLElement | null = null;
let delegated = false;

const current = () => document.querySelector<HTMLDialogElement>('[data-media-preview]');

// Each page brings its own dialog; the delegated handlers are installed once per document.
export function initializePreview(): void {
    const dialog = current();
    const content = dialog?.querySelector<HTMLElement>('[data-preview-content]');
    if (!dialog || !content) return;

    // Native close releases the player.
    dialog.addEventListener('close', () => {
        content.replaceChildren();
        delete dialog.dataset.trailerOpen;
        dialog.removeAttribute('aria-labelledby');
        if (opener?.isConnected) opener.focus({ preventScroll: true });
        opener = null;
    });

    // Only clicks outside the panel.
    dialog.addEventListener('click', event => {
        if (event.target !== dialog) return;
        const bounds = dialog.getBoundingClientRect();
        if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) dialog.close();
    });

    if (delegated) return;
    delegated = true;
    document.addEventListener('click', open);

    // Leave no playback on navigation.
    window.addEventListener('pagehide', () => {
        const dialog = current();
        dialog?.querySelector('[data-preview-content]')?.replaceChildren();
        if (dialog?.open) dialog.close();
    });
}

function open(event: MouseEvent): void {
    const dialog = current();
    const content = dialog?.querySelector<HTMLElement>('[data-preview-content]');
    if (!(event.target instanceof Element) || !dialog || !content) return;
    const trigger = event.target.closest<HTMLAnchorElement>('[data-preview-open]');
    if (trigger) {
        // Modified links retain navigation.
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const template = document.getElementById(trigger.dataset.previewOpen ?? '');
        if (!(template instanceof HTMLTemplateElement)) return;
        event.preventDefault();
        content.replaceChildren(template.content.cloneNode(true));
        delete dialog.dataset.trailerOpen;
        dialog.setAttribute('aria-labelledby', template.id + '-title');
        opener = trigger;
        if (!dialog.open) dialog.showModal();
        return;
    }

    if (!dialog.open || !dialog.contains(event.target)) return;
    if (event.target.closest('[data-preview-close]')) {
        dialog.close();
        return;
    }

    const play = event.target.closest<HTMLButtonElement>('[data-trailer-id]');
    if (play) {
        // Load an official embed on demand.
        const id = play.dataset.trailerId;
        if (!id || !/^[A-Za-z0-9_-]{11}$/.test(id) || dialog.dataset.trailerOpen) return;
        const frame = document.createElement('iframe');
        frame.title = play.getAttribute('aria-label') ?? '';
        frame.src = `https://www.youtube.com/embed/${id}?autoplay=1&playsinline=1`;
        frame.allow = 'autoplay; encrypted-media; picture-in-picture; fullscreen';
        frame.referrerPolicy = 'strict-origin-when-cross-origin';
        frame.allowFullscreen = true;
        play.parentElement!.replaceChildren(frame);
        dialog.dataset.trailerOpen = 'true';
        dialog.querySelector<HTMLButtonElement>('[data-preview-close]')?.focus({ preventScroll: true });
        return;
    }

    // Native action links end the preview.
    if (event.target.closest('a[href]')) dialog.close();
}
