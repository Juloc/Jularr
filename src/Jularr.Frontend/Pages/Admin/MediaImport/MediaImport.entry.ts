import { onPage } from "../../../Modules/Navigation/PageSwap";
import { countText, listStatus, summarize } from "./MediaImport";
import { initializeDurableJobs } from "./MediaImport.jobs";

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
    // Form.Select uses segmented radio inputs for the three import actions.
    // Other versions render a select: support both without inventing a default.
    const globalAction = (): string =>
        form?.querySelector<HTMLInputElement>('input[name="Action"]:checked')?.value ??
        form?.querySelector<HTMLSelectElement>('select[name="Action"]')?.value ?? "1";
    const previewedAction = globalAction();
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
                if (!row.checked || row.dataset.canMonitor !== "true") continue;
                const match = /^Choices\[(\d+)\]\.Include$/.exec(row.name);
                if (!match) continue;
                const own = form?.querySelector<HTMLSelectElement>('select[name="Choices[' + match[1] + '].Action"]');
                const action = own?.value || globalAction();
                if (action === "2" || action === "3") monitored++;
                if (action === "3") searched++;
            }
            const summary = intent.querySelector<HTMLElement>("[data-import-summary]");
            if (summary) {
                summary.textContent = (intent.dataset.template ?? "{0} · {1} · {2}")
                    .replace("{0}", String(selection.count))
                    .replace("{1}", String(monitored))
                    .replace("{2}", String(searched));
            }
            const warning = intent.querySelector<HTMLElement>("[data-import-search-warning]");
            if (warning) warning.hidden = searched === 0;
        }
        // Existing unmonitored Works become actionable only after the server rerenders
        // the preview for the newly chosen global action.
        if (apply) apply.disabled = selection.count === 0 || globalAction() !== previewedAction;
    };

    all.addEventListener("change", () => {
        for (const row of rows) row.checked = all.checked;
        update();
    });
    for (const row of rows) row.addEventListener("change", update);
    form?.querySelectorAll<HTMLInputElement | HTMLSelectElement>('input[name="Action"],select[name="Action"]')
        .forEach(control => control.addEventListener("change", update));
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

    // Capture: htmx's boosted-form listener must not post the form alongside the stream.
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
        const editable = Array.from(form.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>("input:not([type=hidden]), select, textarea"));
        const originalStates = editable.map(control => control.disabled);
        for (const control of editable) control.disabled = true;
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
            const handle = (event: { kind: string; index?: number; stage?: string; count?: number; total?: number;
                source?: string | null; query?: string | null; reason?: string | null }): void => {
                if (event.kind === "row" && event.index !== undefined && event.stage !== undefined) {
                    const state = nodes.get(event.index);
                    if (state) {
                        const label = texts[event.stage] ?? event.stage;
                        // Only report the actual backend search parameters, never guessed client-side values.
                        if (event.source) state.dataset.source = event.source;
                        if (event.query) state.dataset.query = event.query;
                        if (event.reason) state.dataset.reason = event.reason;
                        else delete state.dataset.reason;
                        const source = state.dataset.source === "openlibrary" ? "Open Library" :
                            state.dataset.source === "catalog" ? "Metadata catalog" : state.dataset.source;
                        const trace = [source, state.dataset.query].filter(Boolean).join(" · ");
                        const reason = state.dataset.reason === "search_budget" ? labels?.phaseReasonSearchBudget :
                            state.dataset.reason === "rate_limited" ? labels?.phaseReasonRateLimited :
                            state.dataset.reason === "unavailable" ? labels?.phaseReasonUnavailable :
                            state.dataset.reason === "no_match" ? labels?.phaseNoMatch : state.dataset.reason;
                        state.textContent = (event.stage === "candidates" && event.count !== undefined
                            ? label + " (" + event.count + ")" : label) +
                            (trace ? " · " + trace : "") + (reason ? " · " + reason : "");
                    }
                    if (finishedStages.has(event.stage)) complete.add(event.index);
                    progress.value = complete.size;
                    const title = state?.parentElement?.firstElementChild?.textContent;
                    heading.textContent = (title ? title + " · " : "") + (texts[event.stage] ?? event.stage) +
                        (event.source ? " · " + (event.source === "openlibrary" ? "Open Library" : event.source) : "") +
                        (event.query ? " · " + event.query : "") + " · " + complete.size + " / " + progress.max;
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
                    if (line) handle(JSON.parse(line) as { kind: string; index?: number; stage?: string; count?: number; total?: number;
                        source?: string | null; query?: string | null; reason?: string | null });
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
            editable.forEach((control, index) => { control.disabled = originalStates[index]; });
            submitButtons.forEach((control, index) => { control.disabled = previousButtonStates[index]; });
            form.requestSubmit(button);
        }
    }, { capture: true });
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
        form?.querySelector<HTMLButtonElement>("[data-import-refresh]") ?? null,
        document.querySelector<HTMLElement>(".media-import [data-import-status]")?.dataset,
        null, null
    );
}


function initializeEditionComparison(): void {
    document.querySelectorAll<HTMLButtonElement>("[data-import-editions]").forEach(button => {
        if (button.dataset.ready !== undefined) return;
        button.dataset.ready = "";
        button.hidden = false;
        button.addEventListener("click", async () => {
            const parent = button.closest<HTMLElement>(".media-import__stack");
            const selected = parent?.querySelector<HTMLSelectElement>('select[name$=".Choice"]')?.value;
            let details = parent?.querySelector<HTMLElement>("[data-import-edition-details]");
            if (details) { details.remove(); return; }
            details = document.createElement("div");
            details.className = "media-import__edition-details";
            details.setAttribute("data-import-edition-details", "");
            details.setAttribute("role", "status");
            details.textContent = button.dataset.importEditionLoading ?? "Loading editions…";
            parent?.append(details);
            if (!selected?.startsWith("openlibrary:")) {
                details.textContent = button.dataset.importEditionChoose ?? "Select an Open Library book first.";
                return;
            }
            const workId = selected.slice("openlibrary:".length);
            if (!/^OL[0-9]{1,12}W$/.test(workId)) {
                details.textContent = button.dataset.importEditionChoose ?? "Select an Open Library book first.";
                return;
            }
            button.disabled = true;
            try {
                const url = new URL("/Admin/Import", window.location.href);
                url.searchParams.set("handler", "BookEditions");
                url.searchParams.set("workId", workId);
                const response = await fetch(url, { credentials: "same-origin" });
                if (!response.ok) throw new Error("Edition lookup failed");
                const markup = await response.text();
                // The endpoint renders an encoded, authorized HTML fragment using Table<T>.
                // No provider-supplied data is interpreted as client-side HTML.
                const fragment = new DOMParser().parseFromString(markup, "text/html");
                details.replaceChildren(...Array.from(fragment.body.childNodes));

            } catch {
                details.textContent = button.dataset.importEditionError ?? "Edition details could not be loaded.";
            } finally {
                button.disabled = false;
            }
        });
    });
}

onPage(import.meta.url, () => {
    initializeSource();
    initializePreview();
    initializePendingPreview();
    initializeRefreshPreview();
    initializeDurableJobs();
    initializeEditionComparison();
});
