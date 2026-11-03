import { inserted, onPage } from "../../../Modules/Navigation/PageSwap";

// The manual search of a target. Every source is asked on its own request, all at once; each answer replaces the skeleton of its source with the releases
// (a server-rendered tbody), and the chips, the bar and the status line follow. A chip only hides the releases of the other sources.
const runs = new Map<HTMLElement, AbortController>();
const skeletons = new WeakMap<HTMLElement, Map<string, HTMLElement>>();

const fill = (template: string | undefined, ...values: (string | number)[]): string =>
    values.reduce<string>((text, value, index) => text.replaceAll(`{${index}}`, String(value)), template ?? "");

const bodies = (panel: HTMLElement) => Array.from(panel.querySelectorAll<HTMLElement>("tbody[data-source]"));
const chips = (panel: HTMLElement) => Array.from(panel.querySelectorAll<HTMLElement>(".search-chip"));

function summarize(panel: HTMLElement): void {
    const all = chips(panel);
    const finished = all.filter(chip => chip.dataset.state !== "loading").length;
    const failed = all.filter(chip => chip.dataset.state === "failed").length;
    const total = bodies(panel).reduce((sum, body) => sum + Number(body.dataset.count ?? 0), 0);
    const filter = panel.dataset.filter ?? "";
    const chosen = all.find(chip => chip.dataset.source === filter);
    const shown = chosen ? Number(bodies(panel).find(body => body.dataset.source === filter)?.dataset.count ?? 0) : total;
    const running = finished < all.length;

    // The status line, the bar, and the notice for a search that found nothing (never when a source failed to answer).
    const status = panel.querySelector<HTMLElement>("[data-search-status]");
    if (status) {
        status.textContent = running ? fill(panel.dataset.running, finished, all.length)
            : chosen ? fill(panel.dataset.filtered, shown, total, chosen.querySelector(".search-chip__name")?.textContent ?? "")
            : failed > 0 ? fill(panel.dataset.finishedFailed, total, all.length - failed, failed) : fill(panel.dataset.finished, total, all.length);
    }

    const bar = panel.querySelector<HTMLProgressElement>("progress");
    if (bar) bar.value = finished;
    panel.setAttribute("aria-busy", running ? "true" : "false");
    const empty = panel.querySelector<HTMLElement>("[data-search-empty]");
    if (empty) empty.hidden = running || shown > 0 || (chosen ? chosen.dataset.state === "failed" : failed > 0);
}

// The chip of a source: its state while it searches, then the number of releases, or the short reason.
function settle(panel: HTMLElement, source: string, state: "done" | "failed", result: string, detail?: string): void {
    const chip = panel.querySelector<HTMLElement>(`.search-chip[data-source="${source}"]`);
    if (!chip) return;
    chip.dataset.state = state;
    const icon = panel.querySelector<HTMLTemplateElement>("template")?.content.querySelector(`[data-icon="${state === "done" ? "ok" : "failed"}"]`);
    chip.querySelector(".search-chip__state")?.replaceChildren(...(icon ? [icon.cloneNode(true)] : []));
    const text = chip.querySelector<HTMLElement>(".search-chip__result");
    if (text) text.textContent = result;
    if (detail) chip.title = detail; else chip.removeAttribute("title");
}

function filter(panel: HTMLElement, source: string): void {
    panel.dataset.filter = source;
    chips(panel).forEach(chip => chip.setAttribute("aria-pressed", String(chip.dataset.source === source && source !== "")));
    bodies(panel).forEach(body => { body.hidden = source !== "" && body.dataset.source !== source; });
    summarize(panel);
}

async function ask(panel: HTMLElement, form: HTMLFormElement, source: string, signal: AbortSignal): Promise<void> {
    const body = new FormData(form);
    body.set("connectionId", source);
    body.set("returnUrl", panel.dataset.returnUrl ?? "");
    let next: HTMLElement | null = null;
    try {
        const response = await fetch(panel.dataset.sourceUrl!, { method: "POST", body, signal, credentials: "same-origin", headers: { Accept: "text/html" } });
        if (response.ok) {
            const template = document.createElement("template");
            template.innerHTML = await response.text();
            next = template.content.querySelector<HTMLElement>("tbody[data-source]");
        }
    } catch {
        // An aborted search leaves quietly; any other failure shows as the source's own.
        if (signal.aborted) return;
    }

    if (signal.aborted || !panel.isConnected) return;
    const current = panel.querySelector<HTMLElement>(`tbody[data-source="${source}"]`);
    if (!current) return;
    if (!next) {
        const failed = document.createElement("tbody");
        failed.dataset.source = source;
        failed.dataset.state = "failed";
        failed.dataset.count = "0";
        failed.dataset.label = panel.dataset.failedText ?? "";
        next = failed;
    }

    next.dataset.count ??= "0";
    next.hidden = (panel.dataset.filter ?? "") !== "" && panel.dataset.filter !== source;
    current.replaceWith(next);
    inserted(next);
    if (next.dataset.state === "failed") settle(panel, source, "failed", next.dataset.label ?? "", next.dataset.detail);
    else settle(panel, source, "done", next.dataset.count);
    summarize(panel);
}

// Asks every source again: the old releases give way to the skeletons, and the filter stays.
async function run(panel: HTMLElement): Promise<void> {
    const form = (panel.closest(".search-host") ?? document).querySelector<HTMLFormElement>(".search-scope form");
    if (!form) return;
    runs.get(panel)?.abort();
    const controller = new AbortController();
    runs.set(panel, controller);

    const saved = skeletons.get(panel) ?? new Map(bodies(panel).map(body => [body.dataset.source!, body.cloneNode(true) as HTMLElement]));
    skeletons.set(panel, saved);
    bodies(panel).forEach(body => {
        const fresh = saved.get(body.dataset.source!)?.cloneNode(true) as HTMLElement | undefined;
        if (!fresh) return;
        fresh.hidden = (panel.dataset.filter ?? "") !== "" && panel.dataset.filter !== body.dataset.source;
        body.replaceWith(fresh);
    });
    chips(panel).forEach(chip => {
        chip.dataset.state = "loading";
        chip.removeAttribute("title");
        chip.querySelector(".search-chip__state")?.replaceChildren();
        chip.querySelector(".search-chip__result")?.replaceChildren();
    });

    summarize(panel);
    await Promise.all(chips(panel).map(chip => ask(panel, form, chip.dataset.source!, controller.signal)));
}

function start(panel: HTMLElement): void {
    if (panel.dataset.searchStarted) return;
    panel.dataset.searchStarted = "true";
    void run(panel);
}

// A link of a page opens the search over it; without script, or when the dialog cannot be had, the link goes to the page itself.
let opening = false;
document.addEventListener("click", event => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || !(event.target instanceof Element)) return;
    const link = event.target.closest<HTMLAnchorElement>("a[data-search-dialog]");
    if (!link) return;
    event.preventDefault();
    event.stopPropagation();
    if (!opening) void open(link);
}, true);

async function open(link: HTMLAnchorElement): Promise<void> {
    opening = true;
    try {
        const address = new URL(link.href, location.href);
        address.searchParams.set("handler", "Dialog");
        address.searchParams.set("returnUrl", location.pathname + location.search);
        const response = await fetch(address, { credentials: "same-origin", headers: { Accept: "text/html" } });
        const template = document.createElement("template");
        template.innerHTML = response.ok ? await response.text() : "";
        const dialog = template.content.querySelector<HTMLDialogElement>("dialog.search-dialog");
        if (!dialog) {
            location.assign(link.href);
            return;
        }

        document.getElementById(dialog.id)?.remove();
        (document.getElementById("app-content") ?? document.body).append(dialog);
        inserted(dialog);
        dialog.addEventListener("close", () => {
            dialog.querySelectorAll<HTMLElement>("[data-search-panel]").forEach(panel => { runs.get(panel)?.abort(); runs.delete(panel); });
            dialog.remove();
        });
        dialog.showModal();
        const panel = dialog.querySelector<HTMLElement>("[data-search-panel]");
        if (panel) start(panel);
    } catch {
        location.assign(link.href);
    } finally {
        opening = false;
    }
}

// The chips filter what is loaded; the form asks the sources again instead of posting the page.
document.addEventListener("click", event => {
    if (!(event.target instanceof Element)) return;
    const chip = event.target.closest<HTMLElement>(".search-chip");
    const panel = chip?.closest<HTMLElement>("[data-search-panel]");
    if (!chip || !panel) return;
    filter(panel, panel.dataset.filter === chip.dataset.source ? "" : chip.dataset.source ?? "");
});

document.addEventListener("submit", event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || !form.closest(".search-scope")) return;
    const panel = form.closest(".search-host")?.querySelector<HTMLElement>("[data-search-panel]");
    if (!panel) return;
    event.preventDefault();
    event.stopPropagation();
    panel.dataset.searchStarted = "true";
    void run(panel);
}, true);

// The page itself shows the same panel and starts it.
onPage(import.meta.url, () => {
    runs.forEach((controller, panel) => { if (!panel.isConnected) { controller.abort(); runs.delete(panel); } });
    document.querySelectorAll<HTMLElement>("[data-search-panel]").forEach(start);
}, { partial: true });
