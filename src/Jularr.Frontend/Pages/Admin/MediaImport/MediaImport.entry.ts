import { onPage } from "../../../Modules/Navigation/PageSwap";
import { countText, listStatus, summarize } from "./MediaImport";

// Says whether the pasted text or the chosen file reads as a list; a chosen file is the list, as on the server.
function initializeSource(): void {
    const status = document.querySelector<HTMLElement>("[data-import-status]");
    const text = document.querySelector<HTMLTextAreaElement>(".media-import__upload textarea");
    const file = document.querySelector<HTMLInputElement>(".media-import__upload input[type=file]");
    if (!status || !text || status.dataset.ready !== undefined) return;
    status.dataset.ready = "";
    let latest = 0;
    const refresh = async () => {
        const turn = ++latest;
        const chosen = file?.files?.[0];
        const state = listStatus(chosen ? await chosen.text() : text.value);
        if (turn !== latest) return;
        status.dataset.state = state.state;
        status.hidden = false;
        status.textContent = (status.dataset[state.state] ?? "").replace("{0}", state.state === "valid" ? String(state.count) : "");
    };

    text.addEventListener("input", refresh);
    file?.addEventListener("change", refresh);
    refresh();
}

// The switch of every row, the switch for all of them, the count and the import button that waits for a choice.
function initializePreview(): void {
    const all = document.querySelector<HTMLInputElement>("[data-import-all]");
    const count = document.querySelector<HTMLElement>("[data-import-count]");
    const apply = document.querySelector<HTMLButtonElement>("[data-import-apply]");
    const rows = Array.from(document.querySelectorAll<HTMLInputElement>("[data-import-row]"));
    if (!all || all.dataset.ready !== undefined) return;
    all.dataset.ready = "";
    const update = () => {
        const selection = summarize(rows);
        all.checked = selection.all;
        if (count) {
            count.textContent = countText(count.dataset.template ?? "{0}", selection);
            count.hidden = false;
        }

        if (apply) apply.disabled = selection.count === 0;
    };

    all.addEventListener("change", () => {
        for (const row of rows) row.checked = all.checked;
        update();
    });
    for (const row of rows) row.addEventListener("change", update);
    update();
}

onPage(import.meta.url, () => {
    initializeSource();
    initializePreview();
});
