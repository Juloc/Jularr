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
    const form = all?.closest("form") ?? null;
    const intent = form?.querySelector<HTMLElement>("[data-import-intent]");
    const globalAction = form?.querySelector<HTMLSelectElement>('select[name="Action"]');
    if (!all || all.dataset.ready !== undefined) return;
    all.dataset.ready = "";
    const update = () => {
        const selection = summarize(rows);
        all.checked = selection.all;
        if (count) {
            count.textContent = countText(count.dataset.template ?? "{0}", selection);
            count.hidden = false;
        }

        if (intent) {
            let monitored = 0;
            let searched = 0;
            for (const row of rows) {
                if (!row.checked) continue;
                const match = /^Choices\[(\d+)\]\.Include$/.exec(row.name);
                if (!match) continue;
                const own = form?.querySelector<HTMLSelectElement>('select[name="Choices[' + match[1] + '].Action"]');
                const action = own?.value || globalAction?.value || "1";
                if (action === "2" || action === "3") monitored++;
                if (action === "3") searched++;
            }
            const warning = intent.querySelector<HTMLElement>("[data-import-search-warning]");
            if (warning) warning.remove();
            intent.textContent = (intent.dataset.template ?? "{0} · {1} · {2}")
                .replace("{0}", String(selection.count))
                .replace("{1}", String(monitored))
                .replace("{2}", String(searched));
            // Keep a separate warning after the dynamic count; data stays on the template node.
            if (warning) { warning.hidden = searched === 0; intent.append(warning); }
        }
        if (apply) apply.disabled = selection.count === 0;
    };

    all.addEventListener("change", () => {
        for (const row of rows) row.checked = all.checked;
        update();
    });
    for (const row of rows) row.addEventListener("change", update);
    globalAction?.addEventListener("change", update);
    form?.querySelectorAll<HTMLSelectElement>('select[name$=".Action"]').forEach(select => select.addEventListener("change", update));
    update();
}

// Preview is still an ordinary POST, but it shows immediate feedback while provider checks run.
// A request-based import is not a durable server job. While it is committing,
// protect it from an accidental browser refresh or in-app page navigation.
// Do not claim that a reload is safe before the server owns the job independently.
let runningImportRequests = 0;
window.addEventListener("beforeunload", event => {
    if (runningImportRequests === 0) return;
    event.preventDefault();
    event.returnValue = "";
});
document.addEventListener("htmx:config:request", event => {
    if (runningImportRequests === 0) return;
    const ctx = (event as CustomEvent<{ ctx?: { target?: Element; request?: { method?: string } } }>).detail?.ctx;
    if (ctx?.target?.id === "app-content" && ctx.request?.method === "GET") event.preventDefault();
});
document.addEventListener("htmx:before:history:restore", event => {
    if (runningImportRequests !== 0) event.preventDefault();
});

// Both initial preview and "Search more/Refresh" use the same provider stream,
// including existing per-title selection, provider query and per-media defaults.
function registerPreviewStream(
    form: HTMLFormElement | null,
    button: HTMLButtonElement | null,
    labels: DOMStringMap | undefined,
    text: HTMLTextAreaElement | null,
    file: HTMLInputElement | null
): void {
    if (!form || !button || form.dataset.importPending !== undefined) return;
    form.dataset.importPending = "";
    let useNormalPost = false;

    form.addEventListener("submit", async event => {
        const submitter = (event as SubmitEvent).submitter as HTMLElement | null;
        if (useNormalPost || submitter !== button) return;
        event.preventDefault();
        event.stopImmediatePropagation();

        // Freeze form actions while candidates are being re-evaluated; never allow
        // a stale selection to be applied against a new provider snapshot.
        if (form.dataset.importBusy === "true") return;
        form.dataset.importBusy = "true";
        const submitButtons = Array.from(form.querySelectorAll<HTMLButtonElement>('button[type="submit"]'));
        const previousButtonStates = submitButtons.map(control => control.disabled);
        for (const control of submitButtons) control.disabled = true;
        const source = new FormData(form);
        ++runningImportRequests;
        form.setAttribute("aria-busy", "true");
        const panel = document.createElement("section");
        panel.className = "media-import__activity";
        const heading = document.createElement("strong");
        heading.setAttribute("role", "status");
        heading.setAttribute("aria-live", "polite");
        const progress = document.createElement("progress");
        const list = document.createElement("ol");
        list.className = "media-import__activity-list";
        panel.append(heading, progress, list);
        form.before(panel);
        heading.textContent = labels?.phaseMatching ?? "Matching library works";

        const nodes = new Map<number, HTMLElement>();
        const complete = new Set<number>();
        const finishedStages = new Set(["New", "InLibrary", "Duplicate", "Open", "Invalid"]);
        const texts: Record<string, string> = {
            isbn: labels?.phaseIsbn ?? "Resolving ISBN",
            searching: labels?.phaseSearching ?? "Searching provider",
            candidates: labels?.phaseCandidates ?? "Candidates found",
            no_match: labels?.phaseNoMatch ?? "No candidates",
            waiting: labels?.phaseWaiting ?? "Waiting for next preview",
            New: labels?.stageNew ?? "New",
            InLibrary: labels?.stageInLibrary ?? "In library",
            Duplicate: labels?.stageDuplicate ?? "Duplicate",
            Open: labels?.stageOpen ?? "Open",
            Invalid: labels?.stageInvalid ?? "Invalid"
        };

        try {
            const raw = file?.files?.[0] ? await file.files[0].text() : text?.value ?? String(source.get("Json") ?? "");
            const parsed: unknown = JSON.parse(raw.replace(/^\uFEFF/, ""));
            const items = parsed !== null && typeof parsed === "object" && !Array.isArray(parsed) && "items" in parsed
                ? (parsed as { items?: unknown }).items : undefined;
            if (Array.isArray(items)) {
                progress.max = Math.max(items.length, 1);
                items.forEach((item, index) => {
                    if (item === null || typeof item !== "object") return;
                    const data = item as { title?: unknown; ids?: unknown };
                    const row = document.createElement("li");
                    const name = document.createElement("span");
                    name.textContent = typeof data.title === "string" ? data.title : "#" + String(index + 1);
                    const status = document.createElement("span");
                    status.className = "media-import__activity-outcome";
                    status.textContent = labels?.phaseWaiting ?? "Waiting";
                    row.append(name, status);
                    list.append(row);
                    nodes.set(index, status);
                });
            } else {
                progress.max = 1;
            }
        } catch {
            progress.max = 1;
        }

        let finished = false;
        try {
            const endpoint = new URL(form.action, window.location.href);
            endpoint.searchParams.set("handler", "PreviewStream");
            const response = await fetch(endpoint, { method: "POST", body: source, credentials: "same-origin", headers: { Accept: "application/x-ndjson" } });
            if (!response.ok || !response.body) throw new Error("Preview request failed");
            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = "";
            const handle = (event: { kind: string; index?: number; stage?: string; count?: number; total?: number }): void => {
                if (event.kind === "row" && event.index !== undefined && event.stage !== undefined) {
                    const state = nodes.get(event.index);
                    if (state) {
                        const label = texts[event.stage] ?? event.stage;
                        state.textContent = event.stage === "candidates" && event.count !== undefined
                            ? label + " (" + event.count + ")" : label;
                    }
                    if (finishedStages.has(event.stage)) complete.add(event.index);
                    progress.value = complete.size;
                    const title = state?.parentElement?.firstElementChild?.textContent;
                    heading.textContent = (title ? title + " · " : "") + (texts[event.stage] ?? event.stage) + " · " + complete.size + " / " + progress.max;
                } else if (event.kind === "done") {
                    finished = true;
                    heading.textContent = labels?.phaseComplete ?? "Preview ready";
                } else if (event.kind === "error") {
                    throw new Error("Preview interrupted");
                }
            };
            for (;;) {
                const chunk = await reader.read();
                if (chunk.done) break;
                buffer += decoder.decode(chunk.value, { stream: true });
                let cut: number;
                while ((cut = buffer.indexOf("\n")) !== -1) {
                    const line = buffer.slice(0, cut).trim();
                    buffer = buffer.slice(cut + 1);
                    if (line) handle(JSON.parse(line) as { kind: string; index?: number; stage?: string; count?: number; total?: number });
                }
            }
            buffer += decoder.decode();
            if (buffer.trim()) handle(JSON.parse(buffer) as { kind: string });
            if (!finished) throw new Error("Preview incomplete");
        } catch {
            // The preview never writes to the library. Standard HTML posting exposes validation errors.
            heading.textContent = labels?.phaseWaiting ?? "Preview interrupted";
        } finally {
            // Warm provider results are cached, so the regular HTML preview can render without redoing lookups.
            useNormalPost = true;
            --runningImportRequests;
            form.removeAttribute("aria-busy");
            form.dataset.importBusy = "false";
            submitButtons.forEach((control, index) => { control.disabled = previousButtonStates[index]; });
            form.requestSubmit(button);
        }
    });
}
function initializePendingPreview(): void {
    const form = document.querySelector<HTMLFormElement>(".media-import__upload form");
    registerPreviewStream(
        form,
        form?.querySelector<HTMLButtonElement>('button[type="submit"]') ?? null,
        form?.querySelector<HTMLElement>("[data-import-status]")?.dataset,
        form?.querySelector<HTMLTextAreaElement>("textarea") ?? null,
        form?.querySelector<HTMLInputElement>('input[type="file"]') ?? null
    );
}

// Refresh / Search more must show the same live stages rather than silently
// submitting a full-page form. The apply action is handled separately.
function initializeRefreshPreview(): void {
    const form = document.querySelector<HTMLFormElement>(".media-import__preview form");
    registerPreviewStream(
        form,
        form?.querySelector<HTMLButtonElement>('button[type="submit"]:not([data-import-apply])') ?? null,
        document.querySelector<HTMLElement>(".media-import [data-import-status]")?.dataset,
        null, null
    );
}


type ImportEvent =
    | { kind: "started" }
    | { kind: "row"; index: number; title: string | null; outcome: string; status: string; workId: number | null }
    | { kind: "done"; remaining: string | null }
    | { kind: "error" };

// When supported, each row is reported as soon as the backend actually processes it.
// The plain HTML POST remains available if JavaScript or streaming is not supported.
function initializeLiveImport(): void {
    const form = document.querySelector<HTMLFormElement>(".media-import__preview form");
    const apply = form?.querySelector<HTMLButtonElement>("[data-import-apply]");
    if (!form || !apply || form.dataset.importLive !== undefined) return;
    form.dataset.importLive = "";

    form.addEventListener("submit", async event => {
        const submitter = (event as SubmitEvent).submitter as HTMLButtonElement | null;
        if (form.dataset.importBusy === "true") {
            event.preventDefault();
            event.stopImmediatePropagation();
            return;
        }
        if (submitter !== apply) {
            if (submitter) {
                submitter.disabled = true;
                form.setAttribute("aria-busy", "true");
            }
            return;
        }

        event.preventDefault();
        form.dataset.importBusy = "true";
        apply.disabled = true;
        form.setAttribute("aria-busy", "true");
        const controls = Array.from(form.querySelectorAll<HTMLButtonElement>('button[type="submit"]'));
        for (const button of controls) button.disabled = true;

        const source = new FormData(form);
        ++runningImportRequests;
        const total = form.querySelectorAll('input[name$=".Index"]').length;
        const panel = document.createElement("section");
        panel.className = "media-import__activity";
        const heading = document.createElement("strong");
        const progress = document.createElement("progress");
        progress.max = Math.max(total, 1);
        progress.value = 0;
        const history = document.createElement("ol");
        history.className = "media-import__activity-list";
        history.setAttribute("aria-live", "off");
        heading.setAttribute("role", "status");
        heading.setAttribute("aria-live", "polite");
        panel.append(heading, progress, history);
        form.before(panel);

        let processed = 0;
        let finished = false;
        const pending = (state: string) => {
            heading.textContent = state + " · " + processed + " / " + total;
        };
        pending((apply.textContent ?? "").trim());

        // The server limits provider imports to 40 new titles per pass. Continue
        // automatically only while a batch made forward progress, mapping each
        // deferred row back to its original position and preserving its decisions.
        // An unavailable provider / stalled run is left for explicit retry.
        const makeContinuation = (current: FormData, waiting: number[], remaining: string): FormData => {
            const next = new FormData();
            current.forEach((value, name) => {
                if (typeof value !== "string") return;
                if (name === "__RequestVerificationToken" || name === "Action" || name.startsWith("Defaults[")) {
                    next.append(name, value);
                    return;
                }
                const match = /^Choices\[(\d+)\]\.(.+)$/.exec(name);
                if (!match) return;
                const position = waiting.indexOf(Number(match[1]));
                if (position !== -1 && match[2] !== "Index") next.append("Choices[" + position + "]." + match[2], value);
            });
            for (let index = 0; index < waiting.length; index++) next.append("Choices[" + index + "].Index", String(index));
            next.set("Json", remaining);
            return next;
        };

        const addManualContinuation = (next: FormData, caption?: string): void => {
            const continuation = document.createElement("form");
            continuation.method = "post";
            const target = new URL(form.action, window.location.href);
            target.searchParams.set("handler", "Preview");
            continuation.action = target.toString();
            next.forEach((value, name) => {
                if (typeof value !== "string") return;
                const input = document.createElement("input");
                input.type = "hidden";
                input.name = name;
                input.value = value;
                continuation.append(input);
            });
            const button = document.createElement("button");
            button.type = "submit";
            button.className = "button button--primary";
            button.textContent = caption ?? apply.dataset.importContinue ?? "Continue";
            continuation.append(button);
            panel.append(continuation);
        };

        const itemNodes = new Map<number, { name: HTMLElement; status: HTMLElement }>();
        try {
            const list = JSON.parse(String(source.get("Json") ?? "")) as { items?: { title?: string }[] };
            list.items?.forEach((item, index) => {
                const entry = document.createElement("li");
                const name = document.createElement("span");
                name.textContent = item?.title || "#" + String(index + 1);
                const status = document.createElement("span");
                status.className = "media-import__activity-outcome";
                entry.append(name, status);
                history.append(entry);
                itemNodes.set(index, { name, status });
            });
        } catch {
            // Server validation still owns the list.
        }

        let hasRemaining = false;
        const failedIndexes = new Set<number>();
        const outcomes = new Map<number, { title: string; status: string; result: string; workId: number | null }>();
        let current = source;
        let indices = Array.from({ length: total }, (_, index) => index);
        const settled = new Set<number>();
        const maximumWaves = Math.ceil(total / 40) + 2;
        try {
            for (let wave = 0; wave < maximumWaves; wave++) {
                const endpoint = new URL(form.action, window.location.href);
                endpoint.searchParams.set("handler", "ApplyStream");
                const response = await fetch(endpoint, { method: "POST", body: current, credentials: "same-origin", headers: { Accept: "application/x-ndjson" } });
                if (!response.ok || !response.body) throw new Error("Import request failed");

                const reader = response.body.getReader();
                const decoder = new TextDecoder();
                let buffer = "";
                let waveFinished = false;
                let remaining: string | null = null;
                let added = 0;
                let unavailable = false;
                const waiting: number[] = [];

                const handle = (update: ImportEvent): void => {
                    if (update.kind === "row") {
                        const originalIndex = indices[update.index];
                        if (originalIndex === undefined) throw new Error("Invalid result index");
                        const node = itemNodes.get(originalIndex);
                        if (node) {
                            if (update.title) node.name.textContent = update.title;
                            node.status.textContent = update.outcome;
                            if (update.workId !== null && node.name.tagName !== "A") {
                                const link = document.createElement("a");
                                link.href = "/Admin/Works/" + String(update.workId);
                                link.textContent = node.name.textContent;
                                node.name.replaceWith(link);
                                node.name = link;
                            }
                        }
                        outcomes.set(originalIndex, {
                            title: update.title || node?.name.textContent || "#" + String(originalIndex + 1),
                            status: update.status, result: update.outcome, workId: update.workId
                        });
                        if (update.status === "Failed" || update.status === "NotFound") failedIndexes.add(originalIndex);
                        if (update.status === "Deferred" || update.status === "Unavailable") {
                            waiting.push(update.index);
                            if (update.status === "Unavailable") unavailable = true;
                        } else {
                            settled.add(originalIndex);
                        }
                        if (update.status === "Added" || update.status === "AddedAndMonitored") added++;
                        processed = settled.size;
                        progress.value = processed;
                        pending((apply.textContent ?? "").trim());
                        if (node) node.status.scrollIntoView({ block: "nearest" });
                    } else if (update.kind === "done") {
                        waveFinished = true;
                        remaining = update.remaining;
                    } else if (update.kind === "error") {
                        throw new Error("Import interrupted");
                    }
                };

                for (;;) {
                    const chunk = await reader.read();
                    if (chunk.done) break;
                    buffer += decoder.decode(chunk.value, { stream: true });
                    let cut: number;
                    while ((cut = buffer.indexOf("\n")) !== -1) {
                        const line = buffer.slice(0, cut).trim();
                        buffer = buffer.slice(cut + 1);
                        if (line) handle(JSON.parse(line) as ImportEvent);
                    }
                }
                buffer += decoder.decode();
                if (buffer.trim()) handle(JSON.parse(buffer) as ImportEvent);
                if (!waveFinished) throw new Error("Import did not finish");

                if (!remaining) {
                    finished = true;
                    break;
                }

                const next = makeContinuation(current, waiting, remaining);
                if (added === 0 || unavailable || wave === maximumWaves - 1) {
                    addManualContinuation(next);
                    hasRemaining = true;
                    finished = true;
                    break;
                }

                indices = waiting.map(index => indices[index]);
                current = next;
            }

            if (!finished) throw new Error("Import did not finish");
            panel.classList.add(hasRemaining ? "media-import__activity--partial" : "media-import__activity--done");
            pending(hasRemaining ? (apply.dataset.importPartial ?? "Some titles are waiting") : (apply.dataset.importComplete ?? "Completed"));
            // A failed row is never retried automatically. Offer a preview containing
            // only those rows, with their original choices mapped to the new indices.
            if (failedIndexes.size > 0) {
                const original = JSON.parse(String(source.get("Json") ?? "")) as { version: number; items: unknown[] };
                if (Array.isArray(original.items)) {
                    const failed = [...failedIndexes].sort((a, b) => a - b);
                    const retry = JSON.stringify({ version: 1, items: failed.map(index => original.items[index]) });
                    addManualContinuation(makeContinuation(source, failed, retry), apply.dataset.importRetryFailed ?? "Review failed titles");
                }
            }

            const exportButton = document.createElement("button");
            exportButton.type = "button";
            exportButton.className = "button";
            exportButton.textContent = apply.dataset.importDownloadReport ?? "Download report";
            exportButton.addEventListener("click", () => {
                const report = [...outcomes.entries()].sort(([a], [b]) => a - b)
                    .map(([index, result]) => ({ index, ...result }));
                const blob = new Blob([JSON.stringify({ version: 1, results: report }, null, 2)], { type: "application/json" });
                const url = URL.createObjectURL(blob);
                const link = document.createElement("a");
                link.href = url;
                link.download = "jularr-import-report.json";
                link.click();
                URL.revokeObjectURL(url);
            });
            panel.append(exportButton);

        } catch {
            // Some rows may already have committed; never promise a rollback or blindly retry.
            panel.classList.add("media-import__activity--failed");
            pending(apply.dataset.importFailed ?? "Failed");
            // The server may still be committing the last item. Re-preview to
            // reconcile committed Works before another write; never blindly retry.
            for (const button of controls) if (button !== apply) button.disabled = false;
            apply.disabled = true;
        } finally {
            --runningImportRequests;
            form.dataset.importBusy = "false";
            form.removeAttribute("aria-busy");
        }
    });
}

onPage(import.meta.url, () => {
    initializeSource();
    initializePreview();
    initializePendingPreview();
    initializeRefreshPreview();
    initializeLiveImport();
});
