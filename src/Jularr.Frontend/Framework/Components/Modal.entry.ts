import { onPage } from "../../Modules/Navigation/PageSwap";

// Openers, the X and Cancel, and a click on the backdrop work for every dialog of the page, also for those an action swapped in.
document.addEventListener("click", event => {
    if (!(event.target instanceof Element)) return;
    const opener = event.target.closest<HTMLElement>("[data-dialog-open]");
    if (opener) {
        (document.getElementById(opener.dataset.dialogOpen ?? "") as HTMLDialogElement | null)?.showModal();
        return;
    }

    if (event.target.closest("[data-dialog-close]")) {
        event.target.closest("dialog")?.close();
        return;
    }

    // A click on the backdrop lands on the dialog itself.
    if (event.target instanceof HTMLDialogElement && event.target.classList.contains("modal")) event.target.close();
});

// A dialog the server shows open (its form came back with an error) opens again with the page.
onPage(import.meta.url, () => {
    document.querySelectorAll<HTMLDialogElement>('dialog.modal[data-open="true"]').forEach(dialog => { if (!dialog.open) dialog.showModal(); });
}, { partial: true });
