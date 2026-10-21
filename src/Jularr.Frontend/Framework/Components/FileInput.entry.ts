import "./FileInput.scss";
import "./Truncated.scss";
import { onPage } from "../../Modules/Navigation/PageSwap";
import { acceptEntries, attach, formatSize, type FileInfo } from "./FileInput";
import { markTruncated } from "./Truncated";

function initializeFileInputs(root: ParentNode = document): void {
    for (const element of Array.from(root.querySelectorAll<HTMLElement>("[data-file-input]"))) {
        if (element.dataset.fileReady !== undefined) continue;
        const native = element.querySelector<HTMLInputElement>("input[type=file]");
        const chosen = element.querySelector<HTMLElement>("[data-file-chosen]");
        const name = element.querySelector<HTMLElement>("[data-file-name]");
        const tip = element.querySelector<HTMLElement>("[data-file-tip]");
        const size = element.querySelector<HTMLElement>("[data-file-size]");
        const error = element.querySelector<HTMLElement>("[data-file-error]");
        if (!native || !chosen || !name || !tip || !size || !error) continue;
        element.dataset.fileReady = "";

        // The button is the one stop of the keyboard; the field behind it only opens the dialog.
        native.tabIndex = -1;

        const maximumBytes = Number(element.dataset.fileMax ?? "0");
        const accept = element.dataset.fileAccept ?? "";
        const messages = {
            empty: element.dataset.fileEmpty ?? "",
            size: (element.dataset.fileLarge ?? "{0}").replace("{0}", formatSize(maximumBytes, document.documentElement.lang || undefined)),
            type: (element.dataset.fileType ?? "{0}").replace("{0}", acceptEntries(accept).map(entry => entry.replace(/^\./, "").toUpperCase()).join(", "))
        };
        const control = attach({
            native: {
                get files() { return native.files; },
                clear: () => { native.value = ""; },
                open: () => native.click(),
                assign(file: FileInfo) {
                    const transfer = new DataTransfer();
                    transfer.items.add(file as File);
                    native.files = transfer.files;
                },
                notify: () => { native.dispatchEvent(new Event("change", { bubbles: true })); }
            },
            show(file, text) {
                element.classList.toggle("file-input--chosen", file !== null);
                chosen.hidden = file === null;
                name.textContent = file?.name ?? "";
                tip.textContent = file?.name ?? "";
                size.textContent = text;
                markTruncated(chosen);
            },
            error(message) {
                error.hidden = message === null;
                error.textContent = message ?? "";
                if (message === null) native.removeAttribute("aria-invalid");
                else native.setAttribute("aria-invalid", "true");
            },
            dragging: active => element.classList.toggle("file-input--dragging", active)
        }, { accept, maximumBytes }, messages, document.documentElement.lang || undefined);

        element.querySelector("[data-file-choose]")?.addEventListener("click", control.choose);
        element.querySelector("[data-file-remove]")?.addEventListener("click", control.remove);
        native.addEventListener("change", control.changed);
        control.changed();

        // The whole input is the drop target, also while a file is chosen.
        element.addEventListener("dragenter", event => { event.preventDefault(); control.dragging(true); });
        element.addEventListener("dragover", event => { event.preventDefault(); control.dragging(true); });
        element.addEventListener("dragleave", event => {
            if (!element.contains(event.relatedTarget as Node | null)) control.dragging(false);
        });
        element.addEventListener("drop", event => {
            event.preventDefault();
            control.drop((event as DragEvent).dataTransfer?.files);
        });
    }
}

onPage(import.meta.url, () => initializeFileInputs());
