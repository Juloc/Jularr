import "./Drawer.scss";

// The panels need script, so their buttons show with it (also in HTML a page script inserts later).
document.documentElement.classList.add('drawers-ready');

function selectPane(dialog: HTMLDialogElement, pane: string): void {
    dialog.querySelectorAll<HTMLElement>('[data-drawer-tab]').forEach(tab => {
        const selected = tab.dataset.drawerTab === pane;
        tab.classList.toggle('drawer__tab--selected', selected);
        tab.setAttribute('aria-selected', String(selected));
        if (selected) {
            const title = dialog.querySelector<HTMLElement>('.drawer__heading h2');
            if (title && tab.dataset.drawerTitle) title.textContent = tab.dataset.drawerTitle;
        }
    });
    dialog.querySelectorAll<HTMLElement>('[data-drawer-pane]').forEach(element => { element.hidden = element.dataset.drawerPane !== pane; });
}

document.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;
    const opener = event.target.closest<HTMLElement>('[data-drawer-open]');
    if (opener) {
        const dialog = document.querySelector<HTMLDialogElement>('#' + CSS.escape(opener.dataset.drawerOpen ?? ''));
        if (opener.dataset.drawerPane && dialog) selectPane(dialog, opener.dataset.drawerPane);
        dialog?.showModal();
        return;
    }

    const tab = event.target.closest<HTMLElement>('[data-drawer-tab]');
    if (tab) {
        const dialog = tab.closest<HTMLDialogElement>('dialog.drawer');
        if (dialog && tab.dataset.drawerTab) selectPane(dialog, tab.dataset.drawerTab);
        return;
    }

    // A click on the backdrop lands on the dialog itself.
    const dialog = event.target instanceof HTMLDialogElement ? event.target : event.target.closest<HTMLDialogElement>('dialog.drawer');
    if (dialog && (event.target === dialog || event.target.closest('[data-drawer-close]'))) dialog.close();
});
