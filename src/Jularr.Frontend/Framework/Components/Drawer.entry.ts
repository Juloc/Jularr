import "./Drawer.scss";
import { onPage } from "../../Modules/Navigation/PageSwap";

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

// A panel the server rendered open (its address names the row) is shown from script as the modal it is; without script it stands open on the page.
function showModal(dialog: HTMLDialogElement): void {
    if (dialog.open && dialog.matches(':modal')) return;
    dialog.removeAttribute('open');
    dialog.showModal();
}

onPage(import.meta.url, () => {
    document.querySelectorAll<HTMLDialogElement>('dialog.drawer[open], dialog.drawer[data-drawer-auto]').forEach(showModal);
}, { partial: true });

// A panel that belongs to an address (a row's `?operation=`) leaves it behind when it closes, without loading the page again.
document.addEventListener('close', event => {
    const dialog = event.target;
    if (dialog instanceof HTMLDialogElement && dialog.dataset.drawerReturn) history.replaceState(history.state, '', dialog.dataset.drawerReturn);
}, true);
document.addEventListener('click', event => {
    if (!(event.target instanceof Element)) return;
    const opener = event.target.closest<HTMLElement>('[data-drawer-open]');
    if (opener) {
        const dialog = document.querySelector<HTMLDialogElement>('#' + CSS.escape(opener.dataset.drawerOpen ?? ''));
        if (opener.dataset.drawerPane && dialog) selectPane(dialog, opener.dataset.drawerPane);

        // A link that leads to the panel's address opens the panel in place instead (the page's own navigation never sees the click).
        if (dialog) {
            event.preventDefault();
            event.stopPropagation();
        }

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
    if (dialog && (event.target === dialog || event.target.closest('[data-drawer-close]'))) {
        // The close link of an address-bound panel works without script; with it the page stays.
        if (event.target.closest('a[data-drawer-close]')) {
            event.preventDefault();
            event.stopPropagation();
        }

        dialog.close();
    }
}, true);
