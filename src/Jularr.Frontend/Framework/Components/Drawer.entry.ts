import "./Drawer.scss";

// The panels need script, so their buttons show with it (also in HTML a page script inserts later).
document.documentElement.classList.add('drawers-ready');

document.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;
    const opener = event.target.closest<HTMLElement>('[data-drawer-open]');
    if (opener) {
        document.querySelector<HTMLDialogElement>('#' + CSS.escape(opener.dataset.drawerOpen ?? ''))?.showModal();
        return;
    }

    // A click on the backdrop lands on the dialog itself.
    const dialog = event.target instanceof HTMLDialogElement ? event.target : event.target.closest<HTMLDialogElement>('dialog.drawer');
    if (dialog && (event.target === dialog || event.target.closest('[data-drawer-close]'))) dialog.close();
});
