import { onPage, stayHere } from "../../../Modules/Navigation/PageSwap";
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

// A large list is deliberately handled in bounded server requests (60 preview
// searches / 40 new works per import). Keep the real counts visible and continue
// each request only after the previous result arrived. No estimated per-item progress.
let previewRunning = false;
let lastUnsearched = Number.POSITIVE_INFINITY;
let previewSteps = 0;
let importRunning = false;
let importTotal = 0;
let importProcessed = 0;
let continuePending = false;
const completedRows = new Map<number, HTMLTableRowElement>();

function initializeProgress(): void {
    const page = document.querySelector<HTMLElement>(".media-import");
    const feedback = page?.querySelector<HTMLElement>("[data-import-feedback]");
    if (!page || !feedback) return;
    const show = (template: string, first = "", second = "") => {
        feedback.hidden = false;
        feedback.textContent = template.replace("{0}", first).replace("{1}", second);
    };
    const previewHead = page.querySelector<HTMLElement>("[data-import-unsearched]");
    const resultHead = page.querySelector<HTMLElement>("[data-import-processed]");
    const upload = page.querySelector<HTMLFormElement>(".media-import__upload form");
    const previewForm = page.querySelector<HTMLFormElement>(".media-import__preview form");
    const searchMore = page.querySelector<HTMLButtonElement>("[data-import-more]");
    const apply = page.querySelector<HTMLButtonElement>("[data-import-apply]");
    const continuation = page.querySelector<HTMLButtonElement>("[data-import-continue]");

    upload?.addEventListener("submit", () => {
        previewRunning = true;
        lastUnsearched = Number.POSITIVE_INFINITY;
        previewSteps = 0;
        importRunning = false;
        completedRows.clear();
        show(feedback.dataset.waiting ?? "");
    });
    previewForm?.addEventListener("submit", event => {
        const submitter = (event as SubmitEvent).submitter as HTMLElement | null;
        if (submitter?.hasAttribute("data-import-apply")) {
            if (!importRunning) {
                importTotal = summarize(Array.from(previewForm.querySelectorAll<HTMLInputElement>("[data-import-row]"))).count;
                importProcessed = 0;
                completedRows.clear();
            }
            importRunning = true;
            previewRunning = false;
            continuePending = false;
            show(feedback.dataset.applying ?? "", String(importProcessed), String(importTotal));
        } else {
            previewRunning = true;
            show(feedback.dataset.waiting ?? "");
        }
    });

    if (resultHead && importRunning) {
        // The server returns one bounded batch per request. Preserve every
        // returned work row, replacing earlier deferred/unavailable states by index.
        const tbody = page.querySelector<HTMLTableSectionElement>(".media-import__rows tbody");
        if (tbody) {
            for (const row of Array.from(tbody.querySelectorAll<HTMLTableRowElement>("tr"))) {
                const index = Number(row.querySelector<HTMLElement>("[data-import-result-index]")?.dataset.importResultIndex);
                if (Number.isInteger(index) && index >= 0) completedRows.set(index, row.cloneNode(true) as HTMLTableRowElement);
            }
            tbody.replaceChildren(...Array.from(completedRows.entries()).sort(([a], [b]) => a - b).map(([, row]) => row.cloneNode(true)));
        }
        const processed = Number(resultHead.dataset.importProcessed ?? "0");
        importProcessed += Math.max(0, processed);
        show(feedback.dataset.applying ?? "", String(importProcessed), String(importTotal));
        if (resultHead.dataset.importHasMore === "true" && continuation && processed > 0 && importProcessed < importTotal) {
            continuePending = true;
            stayHere();
            window.setTimeout(() => { if (continuation.isConnected) continuation.click(); }, 80);
        } else {
            const unfinished = resultHead.dataset.importHasMore === "true";
            importRunning = false;
            continuePending = false;
            show(feedback.dataset[unfinished ? "paused" : "finished"] ?? "", String(importProcessed), String(importTotal));
        }
        return;
    }

    if (!previewHead) return;
    const total = Number(previewHead.dataset.importTotal ?? "0");
    const unsearched = Number(previewHead.dataset.importUnsearched ?? "0");
    if (continuePending && importRunning) {
        // The continuation returns to a preview of the remaining work. Apply
        // only actionable rows; never submit an empty or unresolved preview.
        continuePending = false;
        if (apply && !apply.disabled && unsearched === 0) {
            show(feedback.dataset.applying ?? "", String(importProcessed), String(importTotal));
            stayHere();
            window.setTimeout(() => { if (apply.isConnected) apply.click(); }, 80);
        } else {
            importRunning = false;
            show(feedback.dataset.paused ?? "", String(importProcessed), String(importTotal));
        }
        return;
    }
    if (!previewRunning) return;

    const checked = Math.max(0, total - unsearched);
    show(feedback.dataset.previewing ?? "", String(checked), String(total));
    const progress = unsearched < lastUnsearched;
    lastUnsearched = unsearched;
    // Cached results are reused, so each subsequent call can search the next 60.
    // Stop after an unchanged pass (provider failure) or a bounded number of passes.
    if (unsearched > 0 && progress && searchMore && previewSteps < Math.ceil(total / 60) + 2) {
        previewSteps++;
        stayHere();
        window.setTimeout(() => { if (searchMore.isConnected) searchMore.click(); }, 80);
    } else {
        previewRunning = false;
        if (unsearched > 0) show(feedback.dataset.paused ?? "", String(checked), String(total));
    }
}

document.addEventListener("htmx:response:error", event => {
    if (!(event.target instanceof Element) || !event.target.closest(".media-import")) return;
    previewRunning = false;
    importRunning = false;
    continuePending = false;
    const feedback = document.querySelector<HTMLElement>("[data-import-feedback]");
    if (feedback) {
        feedback.hidden = false;
        feedback.textContent = feedback.dataset.paused ?? "";
    }
});

onPage(import.meta.url, () => {
    initializeSource();
    initializePreview();
    initializeProgress();
});
