// A durable import is owned by the server's operation queue, not by this page
// or a long-lived fetch. Polling may stop freely on navigation and reconnects.
type JobEvent = {
    stage: string; source: string | null; query: string | null; detail: string | null;
    candidateCount: number | null; attempt: number; at: string;
    matches?: { title: string; author: string | null; source: string; choice: string; isSuggested: boolean }[];
};
type JobRow = { index: number; title: string | null; stage: string; stageKey: string; outcome: string | null; outcomeKey: string | null;
    workId: number | null; source: string | null; query: string | null; detail: string | null; candidateCount: number | null;
    attempt: number; history: JobEvent[] };
type JobState = {
    jobId: string;
    state: "Queued" | "Running" | "Completed" | "Failed";
    total: number;
    processed: number;
    sequence: number;
    error: string | null;
    rows: JobRow[];
};

// Only the short enqueue request needs navigation protection. After the job ID
// is returned, the browser may navigate away without interrupting the worker.
let pendingEnqueues = 0;
window.addEventListener("beforeunload", event => {
    if (pendingEnqueues === 0) return;
    event.preventDefault();
    event.returnValue = "";
});
document.addEventListener("htmx:config:request", event => {
    if (pendingEnqueues === 0) return;
    const ctx = (event as CustomEvent<{ ctx?: { target?: Element; request?: { method?: string } } }>).detail?.ctx;
    if (ctx?.target?.id === "app-content" && ctx.request?.method === "GET") event.preventDefault();
});

export function initializeDurableJobs(): void {
    const panel = document.querySelector<HTMLElement>("[data-import-job-panel]");
    if (!panel || panel.dataset.ready !== undefined) return;
    panel.dataset.ready = "";
    const form = document.querySelector<HTMLFormElement>(".media-import__preview form");
    const apply = form?.querySelector<HTMLButtonElement>("[data-import-apply]");
    const count = panel.querySelector<HTMLElement>("[data-import-job-count]");
    const status = panel.querySelector<HTMLElement>("[data-import-job-state]");
    const progress = panel.querySelector<HTMLProgressElement>("[data-import-job-progress]");
    const rows = panel.querySelector<HTMLOListElement>("[data-import-job-rows]");
    const reportButton = panel.querySelector<HTMLButtonElement>("[data-import-job-report]");
    if (!count || !status || !progress || !rows) return;
    const active = document.createElement("section");
    active.className = "media-import__job-active";
    active.setAttribute("role", "status");
    active.setAttribute("aria-live", "polite");
    active.hidden = true;
    const filter = document.createElement("select");
    filter.setAttribute("aria-label", panel.dataset.jobFilterLabel ?? "Filter results");
    for (const category of ["All", "Queued", "Running", "Done", "Review", "Problem"]) {
        const option = document.createElement("option");
        option.value = category.toLowerCase();
        option.textContent = panel.dataset["jobFilter" + category] ?? category;
        filter.append(option);
    }
    progress.after(active, filter);

    let current: string | null = null;
    let requestGeneration = 0;
    const existing = new Map<number, HTMLLIElement>();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let lastSequence = -1;
    let lastSnapshot: JobState | null = null;

    const endpoint = (handler: string, jobId?: string): string => {
        const url = new URL("/Admin/Import", window.location.href);
        url.searchParams.set("handler", handler);
        if (jobId) url.searchParams.set("jobId", jobId);
        return url.toString();
    };

    const labelStage = (stage: string): string => panel.dataset["jobStage" + stage] ?? stage;
    const labelReason = (reason: string): string => {
        const key = "jobReason" + reason.split("_").map(x => x.charAt(0).toUpperCase() + x.slice(1)).join("");
        return panel.dataset[key] ?? reason;
    };
    const sourceName = (source: string): string => ({
        library: "Jularr", local: "Jularr", list: panel.dataset.jobSourceList ?? "List data", openlibrary: "Open Library", catalog: "Metadata catalog",
        tmdb_movie: "TMDB", tmdb_tv: "TMDB", anilist: "AniList",
        igdb: "IGDB", musicbrainz: "MusicBrainz"
    } as Record<string, string>)[source] ?? source;
    const classification = (row: JobRow): string => {
        if (!row.outcomeKey) return row.stageKey === "Queued" ? "queued" : "running";
        if (["NotFound", "Unavailable", "Failed"].includes(row.outcomeKey)) return "problem";
        if (["NeedsReview", "Excluded", "Deferred"].includes(row.outcomeKey)) return "review";
        return "done";
    };
    const applyFilter = (): void => {
        for (const [index, entry] of existing) {
            const row = lastSnapshot?.rows.find(item => item.index === index);
            entry.hidden = filter.value !== "all" && row !== undefined && classification(row) !== filter.value;
        }
    };
    filter.addEventListener("change", applyFilter);
    const addField = (parent: HTMLElement, title: string, value: string | null | undefined): void => {
        if (!value) return;
        const p = document.createElement("p");
        const name = document.createElement("strong");
        name.textContent = title + ": ";
        p.append(name, document.createTextNode(value));
        parent.append(p);
    };

    const render = (job: JobState): void => {
        if (current !== job.jobId) return;
        panel.hidden = false;
        lastSnapshot = job;
        if (reportButton) reportButton.hidden = job.state !== "Completed" && job.state !== "Failed";
        const state = job.state.toLowerCase();
        status.textContent = panel.dataset["job" + state[0].toUpperCase() + state.slice(1)] ?? job.state;
        count.textContent = job.processed + " / " + job.total;
        progress.max = Math.max(job.total, 1);
        progress.value = job.processed;
        const working = job.state === "Running" ? job.rows.find(row =>
            !row.outcomeKey && row.stageKey !== "Queued") : undefined;
        active.hidden = !working;
        active.textContent = working ? [
            panel.dataset.jobCurrent, working.title ?? "#" + (working.index + 1),
            working.stage, working.source ? sourceName(working.source) : null, working.query
        ].filter(Boolean).join(" · ") : "";

        if (job.sequence !== lastSequence) {
            lastSequence = job.sequence;
            for (const item of job.rows) {
                let entry = existing.get(item.index);
                if (!entry) {
                    entry = document.createElement("li");
                    entry.className = "media-import__job-row";
                    const details = document.createElement("details");
                    const summary = document.createElement("summary");
                    const name = document.createElement("span");
                    name.setAttribute("data-job-title", "");
                    const outcome = document.createElement("span");
                    outcome.setAttribute("data-job-outcome", "");
                    summary.append(name, outcome);
                    const content = document.createElement("div");
                    content.className = "media-import__job-details";
                    content.setAttribute("data-job-details", "");
                    details.append(summary, content);
                    entry.append(details);
                    existing.set(item.index, entry);
                    rows.append(entry);
                }
                const name = entry.querySelector<HTMLElement>("[data-job-title]");
                const outcome = entry.querySelector<HTMLElement>("[data-job-outcome]");
                const content = entry.querySelector<HTMLElement>("[data-job-details]");
                if (name) name.textContent = item.title ?? "#" + (item.index + 1);
                if (outcome) outcome.textContent = item.outcome ?? item.stage;
                if (!content) continue;
                content.replaceChildren();
                addField(content, panel.dataset.jobSource ?? "Source", item.source ? sourceName(item.source) : null);
                addField(content, panel.dataset.jobQuery ?? "Query", item.query);
                addField(content, panel.dataset.jobCandidates ?? "Candidates",
                    item.candidateCount == null ? null : String(item.candidateCount));
                addField(content, panel.dataset.jobAttempt ?? "Retry",
                    item.attempt > 0 ? String(item.attempt) : null);
                addField(content, panel.dataset.jobHistory ?? "Details", item.detail ? labelReason(item.detail) : null);
                if (!item.history?.length) continue;
                const heading = document.createElement("strong");
                heading.textContent = panel.dataset.jobHistory ?? "Search history";
                const history = document.createElement("ol");
                history.className = "media-import__job-history";
                for (const event of item.history) {
                    const li = document.createElement("li");
                    li.textContent = [
                        event.at ? new Date(event.at).toLocaleTimeString() : null,
                        labelStage(event.stage),
                        event.source ? sourceName(event.source) : null,
                        event.query,
                        event.candidateCount == null ? null : (panel.dataset.jobCandidates ?? "Candidates") + ": " + event.candidateCount,
                        event.detail ? labelReason(event.detail) : null,
                        event.attempt > 0 ? (panel.dataset.jobAttempt ?? "Retry") + ": " + event.attempt : null
                    ].filter(Boolean).join(" · ");
                    if (event.matches?.length) {
                        const proposals = document.createElement("ul");
                        proposals.className = "media-import__job-candidates";
                        for (const candidate of event.matches) {
                            const proposal = document.createElement("li");
                            proposal.textContent = [candidate.title, candidate.author,
                                sourceName(candidate.source), candidate.choice].filter(Boolean).join(" · ");
                            proposals.append(proposal);
                        }
                        li.append(proposals);
                    }
                    history.append(li);
                }
                content.append(heading, history);
            }
            applyFilter();
        }
        if (job.error) status.textContent += " · " + job.error;
    };

    const poll = async (generation: number): Promise<void> => {
        if (!panel.isConnected || generation !== requestGeneration || !current) return;
        try {
            const response = await fetch(endpoint("Job", current), { credentials: "same-origin", cache: "no-store" });
            if (!response.ok) throw new Error("Job status unavailable");
            const job = await response.json() as JobState;
            if (generation !== requestGeneration || !panel.isConnected) return;
            render(job);
            if (job.state === "Queued" || job.state === "Running") {
                timer = setTimeout(() => { void poll(generation); }, 1500);
            } else {
                if (form) form.removeAttribute("aria-busy");
            }
        } catch {
            if (!panel.isConnected || generation !== requestGeneration) return;
            status.textContent = panel.dataset.jobDisconnected ?? "Connection lost; progress is saved on the server.";
            timer = setTimeout(() => { void poll(generation); }, 4000);
        }
    };

    const follow = (id: string): void => {
        if (timer !== undefined) clearTimeout(timer);
        requestGeneration++;
        current = id;
        lastSnapshot = null;
        if (reportButton) reportButton.hidden = true;
        lastSequence = -1;
        existing.clear();
        rows.replaceChildren();
        active.hidden = true;
        active.textContent = "";
        filter.value = "all";
        panel.hidden = false;
        status.textContent = panel.dataset.jobQueued ?? "Queued";
        void poll(requestGeneration);
    };

    reportButton?.addEventListener("click", () => {
        if (!lastSnapshot) return;
        const report = { version: 2, jobId: lastSnapshot.jobId, state: lastSnapshot.state, results: lastSnapshot.rows };
        const file = new Blob([JSON.stringify(report, null, 2)], { type: "application/json" });
        const uri = URL.createObjectURL(file);
        const link = document.createElement("a");
        link.href = uri;
        link.download = "jularr-import-report.json";
        link.click();
        URL.revokeObjectURL(uri);
    });

    // Capture runs before htmx's boosted-form listener, which a page swap registers first;
    // otherwise the apply button would also post the synchronous import.
    form?.addEventListener("submit", async event => {
        const submitter = (event as SubmitEvent).submitter;
        if (submitter !== apply || !apply) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        if (form.dataset.importBusy === "true") return;

        const payload = new FormData(form);
        form.dataset.importBusy = "true";
        form.setAttribute("aria-busy", "true");
        apply.disabled = true;
        ++pendingEnqueues;
        status.textContent = panel.dataset.jobQueued ?? "Queued";
        panel.hidden = false;
        try {
            const response = await fetch(endpoint("StartJob"), {
                method: "POST", body: payload, credentials: "same-origin", headers: { Accept: "application/json" }
            });
            if (!response.ok) throw new Error("Could not queue import");
            const result = await response.json() as { jobId: string };
            follow(result.jobId);
        } catch {
            form.dataset.importBusy = "false";
            form.removeAttribute("aria-busy");
            apply.disabled = false;
            status.textContent = panel.dataset.jobStartFailed ?? "Could not start the import.";
        } finally {
            --pendingEnqueues;
        }
    }, { capture: true });

    // A new visit uses the same owner-scoped job ID from the database. No local
    // storage or tab lifetime is involved, and an older completed job is visible.
    void (async () => {
        try {
            const response = await fetch(endpoint("LatestJob"), { credentials: "same-origin", cache: "no-store" });
            if (!response.ok) return;
            const latest = await response.json() as { jobId: string };
            if (current || !latest.jobId || latest.jobId === "00000000-0000-0000-0000-000000000000") return;
            follow(latest.jobId);
        } catch {
            // The next visit checks the persisted job again.
        }
    })();
}
