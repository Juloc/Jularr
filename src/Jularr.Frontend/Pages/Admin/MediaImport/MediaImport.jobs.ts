// A durable import is owned by the server's operation queue, not by this page
// or a long-lived fetch. Polling may stop freely on navigation and reconnects.
type JobRow = { index: number; title: string | null; outcome: string | null; workId: number | null };
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

    const render = (job: JobState): void => {
        if (current !== job.jobId) return;
        panel.hidden = false;
        lastSnapshot = job;
        if (reportButton) reportButton.hidden = job.state !== "Completed" && job.state !== "Failed";
        const state = job.state.toLowerCase();
        status.textContent = panel.dataset["job" + state[0].toUpperCase() + state.slice(1)]
            ?? job.state;
        count.textContent = job.processed + " / " + job.total;
        progress.max = Math.max(job.total, 1);
        progress.value = job.processed;

        if (job.sequence !== lastSequence) {
            lastSequence = job.sequence;
            for (const row of job.rows) {
                let entry = existing.get(row.index);
                if (!entry) {
                    entry = document.createElement("li");
                    entry.className = "media-import__job-row";
                    const name = document.createElement("span");
                    name.setAttribute("data-job-title", "");
                    const outcome = document.createElement("span");
                    outcome.setAttribute("data-job-outcome", "");
                    entry.append(name, outcome);
                    existing.set(row.index, entry);
                    rows.append(entry);
                }
                const name = entry.querySelector<HTMLElement>("[data-job-title]");
                const outcome = entry.querySelector<HTMLElement>("[data-job-outcome]");
                if (name) name.textContent = row.title || "#" + (row.index + 1);
                if (outcome) outcome.textContent = row.outcome ?? "";
            }
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
        panel.hidden = false;
        status.textContent = panel.dataset.jobQueued ?? "Queued";
        void poll(requestGeneration);
    };

    reportButton?.addEventListener("click", () => {
        if (!lastSnapshot) return;
        const report = { version: 1, jobId: lastSnapshot.jobId, state: lastSnapshot.state, results: lastSnapshot.rows };
        const file = new Blob([JSON.stringify(report, null, 2)], { type: "application/json" });
        const uri = URL.createObjectURL(file);
        const link = document.createElement("a");
        link.href = uri;
        link.download = "jularr-import-report.json";
        link.click();
        URL.revokeObjectURL(uri);
    });

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
    });

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
