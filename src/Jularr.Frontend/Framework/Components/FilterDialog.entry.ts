import { onPage } from "../../Modules/Navigation/PageSwap";

// Openers and closers of the page's filter dialogs; a click on the backdrop closes too.
function initializeFilterDialogs(): void {
    document.querySelectorAll<HTMLElement>('[data-filter-open]').forEach(opener => {
        if (opener.dataset.filterReady) return;
        opener.dataset.filterReady = 'true';
        opener.addEventListener('click', () => (document.getElementById(opener.dataset.filterOpen ?? '') as HTMLDialogElement | null)?.showModal());
    });
    document.querySelectorAll<HTMLElement>('[data-filter-close]').forEach(closer => {
        if (closer.dataset.filterReady) return;
        closer.dataset.filterReady = 'true';
        closer.addEventListener('click', () => closer.closest('dialog')?.close());
    });
    document.querySelectorAll<HTMLDialogElement>('dialog.filter-dialog').forEach(dialog => {
        if (dialog.dataset.filterReady) return;
        dialog.dataset.filterReady = 'true';
        dialog.addEventListener('click', event => { if (event.target === dialog) dialog.close(); });
    });
}

onPage(import.meta.url, initializeFilterDialogs);
