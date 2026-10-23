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

        const source = new FormData(form);
        button.disabled = true;
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
            button.disabled = false;
            form.removeAttribute("aria-busy");
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
        if (submitter !== apply) {
            if (submitter) {
                submitter.disabled = true;
                form.setAttribute("aria-busy", "true");
            }
            return;
        }

        event.preventDefault();
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
        const waiting: number[] = [];
        const pending = (state: string) => {
            heading.textContent = state + " · " + processed + " / " + total;
        };
        pending((apply.textContent ?? "").trim());

        const addContinuation = (remaining: string): void => {
            const next = document.createElement("form");
            next.method = "post";
            const target = new URL(form.action, window.location.href);
            target.searchParams.set("handler", "Preview");
            next.action = target.toString();
            const hidden = (name: string, value: string): void => {
                const input = document.createElement("input");
                input.type = "hidden";
                input.name = name;
                input.value = value;
                next.append(input);
            };
            source.forEach((value, name) => {
                if (typeof value !== "string") return;
                if (name === "__RequestVerificationToken" || name === "Action" || name.startsWith("Defaults[")) hidden(name, value);
                const match = /^Choices\[(\d+)\]\.(.+)$/.exec(name);
                if (!match) return;
                const position = waiting.indexOf(Number(match[1]));
                if (position >= 0 && match[2] !== "Index") hidden("Choices[" + position + "]." + match[2], value);
            });
            for (let index = 0; index < waiting.length; index++) hidden("Choices[" + index + "].Index", String(index));
            hidden("Json", remaining);
            const button = document.createElement("button");
            button.type = "submit";
            button.className = "button button--primary";
            button.textContent = apply.dataset.importContinue ?? "Continue";
            next.append(button);
            panel.append(next);
        };

        try {
            const endpoint = new URL(form.action, window.location.href);
            endpoint.searchParams.set("handler", "ApplyStream");
            const response = await fetch(endpoint, { method: "POST", body: source, credentials: "same-origin", headers: { Accept: "application/x-ndjson" } });
            if (!response.ok || !response.body) throw new Error("Import request failed");

            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = "";
            const handle = (update: ImportEvent): void => {
                if (update.kind === "row") {
                    processed++;
                    progress.value = processed;
                    pending((apply.textContent ?? "").trim());
                    const entry = document.createElement("li");
                    if (update.workId !== null) {
                        const link = document.createElement("a");
                        link.href = "/Admin/Works/" + String(update.workId);
                        link.textContent = update.title || "#" + String(update.index + 1);
                        entry.append(link);
                    } else {
                        const title = document.createElement("span");
                        title.textContent = update.title || "#" + String(update.index + 1);
                        entry.append(title);
                    }
                    const status = document.createElement("span");
                    status.textContent = update.outcome;
                    status.className = "media-import__activity-outcome";
                    entry.append(status);
                    history.append(entry);
                    history.scrollTop = history.scrollHeight;
                    if (update.status === "Deferred" || update.status === "Unavailable") waiting.push(update.index);
                } else if (update.kind === "done") {
                    finished = true;
                    panel.classList.add("media-import__activity--done");
                    pending(apply.dataset.importComplete ?? "Completed");
                    if (update.remaining) addContinuation(update.remaining);
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
            if (!finished) throw new Error("Import did not finish");
        } catch {
            // Some rows may already have committed; never promise a rollback or blindly retry.
            panel.classList.add("media-import__activity--failed");
            pending(apply.dataset.importFailed ?? "Failed");
            for (const button of controls) button.disabled = false;
            apply.disabled = false;
        } finally {
            --runningImportRequests;
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
