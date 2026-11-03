import { placement } from "../../../Framework/Components/Dropdown";
import { inserted, onPage } from "../../../Modules/Navigation/PageSwap";

// The manual search of a target. Every source is asked on its own request, all at once; each answer replaces the skeleton of its source with the releases
// (a server-rendered tbody), and the chips, the bar and the status line follow. A chip, the filters and the sort only change how the loaded rows are shown
// (hidden, or moved to one list that is ordered across the sources); nothing is asked again.
const runs = new Map<HTMLElement, AbortController>();
const skeletons = new WeakMap<HTMLElement, Map<string, HTMLElement>>();

const fill = (template: string | undefined, ...values: (string | number)[]): string =>
    values.reduce<string>((text, value, index) => text.replaceAll(`{${index}}`, String(value)), template ?? "");

const bodies = (panel: HTMLElement) => Array.from(panel.querySelectorAll<HTMLElement>("tbody[data-source]"));
const chips = (panel: HTMLElement) => Array.from(panel.querySelectorAll<HTMLElement>(".search-chip"));

const rowsOf = (panel: HTMLElement) => Array.from(panel.querySelectorAll<HTMLElement>("tr.search-row[data-index]"));
const menu = (panel: HTMLElement, name: string) => panel.querySelector<HTMLSelectElement>(`select[data-filter="${name}"]`);
const sizeRanges: [number, number][] = [[0, 1e8], [1e8, 1e9], [1e9, 5e9], [5e9, 2e10], [2e10, Infinity]];

// Lower case without accents, so "Amélie" is found by "amelie".
const plain = (text: string) => text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();

const multi = (panel: HTMLElement) => panel.querySelector<HTMLElement>("[data-multi=quality]");
const checked = (panel: HTMLElement) => Array.from(multi(panel)?.querySelectorAll<HTMLInputElement>("input:checked") ?? []).map(box => box.value);

function criteria(panel: HTMLElement) {
    const typed = panel.querySelector<HTMLInputElement>("input[data-filter=title]")?.value ?? "";
    return { source: panel.dataset.filter ?? "", language: menu(panel, "language")?.value ?? "", protocol: menu(panel, "protocol")?.value ?? "",
        age: menu(panel, "age")?.value ?? "", size: menu(panel, "size")?.value ?? "", decision: menu(panel, "decision")?.value ?? "",
        words: plain(typed).split(/\s+/).filter(Boolean), qualities: checked(panel) };
}

// Whether any filter is set (the order is not one).
const narrowed = (wanted: ReturnType<typeof criteria>) => Object.values(wanted).some(value => Array.isArray(value) ? value.length > 0 : Boolean(value));

function matches(row: HTMLElement, wanted: ReturnType<typeof criteria>): boolean {
    if (wanted.source && row.dataset.source !== wanted.source) return false;
    if (wanted.protocol && row.dataset.protocol !== wanted.protocol) return false;
    if (wanted.decision && row.dataset.decision !== wanted.decision) return false;
    if (wanted.language && !(row.dataset.languages ?? "").split(";").some(pair => pair.split("=")[0] === wanted.language)) return false;

    // A release without a quality is "Unknown" (the empty value).
    if (wanted.qualities.length && !wanted.qualities.includes(row.dataset.quality ?? "")) return false;
    if (wanted.words.length) {
        const title = plain(row.dataset.title ?? "");
        if (!wanted.words.every(word => title.includes(word))) return false;
    }

    // A release without a date or a size cannot be shown to fit a limit on it.
    if (wanted.age) {
        const published = Number(row.dataset.published);
        if (!row.dataset.published || (Date.now() / 1000 - published) / 86400 > Number(wanted.age)) return false;
    }

    if (wanted.size) {
        const size = Number(row.dataset.size);
        const [low, high] = sizeRanges[Number(wanted.size)] ?? [0, Infinity];
        if (!row.dataset.size || size < low || size >= high) return false;
    }

    return true;
}

// The language list holds what the loaded releases carry; the chosen one stays chosen.
function listLanguages(panel: HTMLElement): void {
    const select = menu(panel, "language");
    if (!select) return;
    const found = new Map<string, string>();
    rowsOf(panel).forEach(row => (row.dataset.languages ?? "").split(";").filter(Boolean).forEach(pair => {
        const [code, ...name] = pair.split("=");
        found.set(code, name.join("=") || code);
    }));
    const listed = Array.from(select.options).slice(1).map(option => option.value).join("|");
    const next = Array.from(found.entries()).sort((first, second) => first[1].localeCompare(second[1]));
    if (listed === next.map(([code]) => code).join("|")) return;
    const chosen = select.value;
    select.replaceChildren(select.options[0], ...next.map(([code, name]) => Object.assign(document.createElement("option"), { value: code, textContent: name })));
    select.value = next.some(([code]) => code === chosen) ? chosen : "";
}

// The quality list holds what the loaded releases carry, each with a checkbox; the checked ones stay checked, and the button tells how many are.
function listQualities(panel: HTMLElement): void {
    const root = multi(panel);
    const popup = root?.querySelector<HTMLElement>("[data-multi-popup]");
    const button = root?.querySelector<HTMLButtonElement>("[data-multi-button]");
    if (!root || !popup || !button) return;
    if (!button.popoverTargetElement) button.popoverTargetElement = popup;
    const found = Array.from(new Set(rowsOf(panel).map(row => row.dataset.quality ?? ""))).sort((first, second) => !first ? 1 : !second ? -1 : first.localeCompare(second));
    const listed = Array.from(popup.querySelectorAll<HTMLInputElement>("input")).map(box => box.value);
    const chosen = new Set(checked(panel));
    if (listed.join("|") !== found.join("|")) {
        popup.replaceChildren(...found.map(quality => {
            const box = Object.assign(document.createElement("input"), { type: "checkbox", value: quality, checked: chosen.has(quality) });
            const label = document.createElement("label");
            label.className = "search-multi__option";
            label.append(box, Object.assign(document.createElement("span"), { textContent: quality || root.dataset.unknown }));
            return label;
        }));
    }

    const count = checked(panel).length;
    const name = count === 1 ? checked(panel)[0] || root.dataset.unknown : fill(root.dataset.some, count);
    const value = button.querySelector<HTMLElement>(".ui-dropdown__value");
    if (value) {
        value.textContent = count ? name! : root.dataset.all ?? "";
        value.classList.toggle("ui-dropdown__value--empty", count === 0);
    }
}

// Rows move only when their order differs, so a focused button of a release keeps its focus while other sources answer.
function place(parent: HTMLElement, list: HTMLElement[]): void {
    if (parent.children.length === list.length && list.every((row, index) => parent.children[index] === row)) return;
    parent.append(...list);
}

// By source keeps each release in the list of its source; any other order puts every release into one list above the skeletons.
function arrange(panel: HTMLElement): void {
    const order = menu(panel, "sort")?.value ?? "source";
    const table = panel.querySelector("table")!;
    let merged = table.querySelector<HTMLElement>("tbody[data-merged]");
    const rows = rowsOf(panel);
    const sourceRank = new Map(chips(panel).map((chip, index) => [chip.dataset.source!, index]));
    const byIndex = (first: HTMLElement, second: HTMLElement) => (sourceRank.get(first.dataset.source!) ?? 0) - (sourceRank.get(second.dataset.source!) ?? 0) || Number(first.dataset.index) - Number(second.dataset.index);
    const key = (row: HTMLElement, name: "size" | "published") => row.dataset[name] ? Number(row.dataset[name]) : NaN;
    const by = (name: "size" | "published", direction: 1 | -1) => (first: HTMLElement, second: HTMLElement) => {
        const [one, two] = [key(first, name), key(second, name)];
        return Number.isNaN(one) || Number.isNaN(two) ? Number(Number.isNaN(one)) - Number(Number.isNaN(two)) || byIndex(first, second) : (two - one) * direction || byIndex(first, second);
    };

    if (order === "source") {
        merged?.remove();
        bodies(panel).forEach(body => place(body, rows.filter(row => row.dataset.source === body.dataset.source).sort(byIndex)));
        return;
    }

    if (!merged) {
        merged = document.createElement("tbody");
        merged.dataset.merged = "true";
        table.querySelector("thead")!.after(merged);
    }

    const compare = order === "score" ? (first: HTMLElement, second: HTMLElement) => Number(second.dataset.score) - Number(first.dataset.score) || byIndex(first, second)
        : order === "newest" ? by("published", 1) : order === "largest" ? by("size", 1) : by("size", -1);
    place(merged, rows.sort(compare));
}

// Orders the loaded releases, hides those the filters leave out, and tells the numbers: the status line, the notices and the clear button.
function apply(panel: HTMLElement): void {
    listLanguages(panel);
    listQualities(panel);
    arrange(panel);
    const wanted = criteria(panel);
    const rows = rowsOf(panel);
    rows.forEach(row => { row.hidden = !matches(row, wanted); });
    const filtered = narrowed(wanted);
    const clear = panel.querySelector<HTMLButtonElement>("[data-filters-clear]");
    if (clear) clear.disabled = !filtered;

    const all = chips(panel);
    const finished = all.filter(chip => chip.dataset.state !== "loading").length;
    const failed = all.filter(chip => chip.dataset.state === "failed").length;
    const shown = rows.filter(row => !row.hidden).length;
    const running = finished < all.length;

    // The status line, the bar, and the notices: nothing found (never when a source failed to answer) or nothing fitting the filters.
    const status = panel.querySelector<HTMLElement>("[data-search-status]");
    if (status) {
        status.textContent = running ? fill(panel.dataset.running, finished, all.length)
            : filtered ? fill(panel.dataset.filtered, shown, rows.length)
            : failed > 0 ? fill(panel.dataset.finishedFailed, rows.length, all.length - failed, failed) : fill(panel.dataset.finished, rows.length, all.length);
    }

    const bar = panel.querySelector<HTMLProgressElement>("progress");
    if (bar) bar.value = finished;
    panel.setAttribute("aria-busy", running ? "true" : "false");
    const empty = panel.querySelector<HTMLElement>("[data-search-empty]");
    if (empty) empty.hidden = running || rows.length > 0 || failed > 0;
    const none = panel.querySelector<HTMLElement>("[data-search-nomatch]");
    if (none) none.hidden = running || rows.length === 0 || shown > 0;
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
    apply(panel);
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
    current.replaceWith(next);
    inserted(next);
    if (next.dataset.state === "failed") settle(panel, source, "failed", next.dataset.label ?? "", next.dataset.detail);
    else settle(panel, source, "done", next.dataset.count);
    apply(panel);
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
        if (fresh) body.replaceWith(fresh);
    });
    panel.querySelector("tbody[data-merged]")?.replaceChildren();
    chips(panel).forEach(chip => {
        chip.dataset.state = "loading";
        chip.removeAttribute("title");
        chip.querySelector(".search-chip__state")?.replaceChildren();
        chip.querySelector(".search-chip__result")?.replaceChildren();
    });

    apply(panel);
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

// The filters and the order are the selects of the panel; the button clears the filters and the chosen chip.
const filtersChanged = (event: Event) => {
    const panel = event.target instanceof Element && event.target.closest(".search-filters") ? event.target.closest<HTMLElement>("[data-search-panel]") : null;
    if (panel) apply(panel);
};

document.addEventListener("change", filtersChanged);
document.addEventListener("input", event => { if (event.target instanceof HTMLInputElement && event.target.type === "search") filtersChanged(event); });

// The popup of the qualities opens under its button like a list of the dropdowns; it closes when the focus leaves it.
document.addEventListener("beforetoggle", event => {
    const popup = event.target instanceof HTMLElement && event.target.matches("[data-multi-popup]") ? event.target : null;
    const button = popup?.parentElement?.querySelector<HTMLElement>("[data-multi-button]");
    if (!popup || !button || (event as ToggleEvent).newState !== "open") return;
    const placed = placement(button.getBoundingClientRect(), window.innerHeight, window.innerWidth, popup.scrollHeight);
    popup.style.setProperty("--dropdown-top", `${placed.top}px`);
    popup.style.setProperty("--dropdown-left", `${placed.left}px`);
    popup.style.setProperty("--dropdown-width", `${placed.width}px`);
    popup.style.setProperty("--dropdown-max-height", `${placed.maxHeight}px`);
    popup.classList.toggle("ui-dropdown__popup--above", placed.above);
}, true);

document.addEventListener("toggle", event => {
    const popup = event.target instanceof HTMLElement && event.target.matches("[data-multi-popup]") ? event.target : null;
    if (!popup) return;
    const open = (event as ToggleEvent).newState === "open";
    popup.parentElement?.querySelector("[data-multi-button]")?.setAttribute("aria-expanded", String(open));
    if (open) popup.querySelector<HTMLInputElement>("input")?.focus({ preventScroll: true });
}, true);

document.addEventListener("focusout", event => {
    const popup = event.target instanceof Element ? event.target.closest<HTMLElement>("[data-multi-popup]") : null;
    const next = event.relatedTarget;
    if (popup?.matches(":popover-open") && next instanceof Node && !popup.contains(next) && next !== popup.parentElement?.querySelector("[data-multi-button]")) popup.hidePopover();
});

document.addEventListener("click", event => {
    if (!(event.target instanceof Element)) return;
    const clear = event.target.closest<HTMLElement>("[data-filters-clear]");
    const panel = clear?.closest<HTMLElement>("[data-search-panel]");
    if (!clear || !panel) return;
    panel.dataset.filter = "";
    chips(panel).forEach(chip => chip.setAttribute("aria-pressed", "false"));
    ["language", "protocol", "age", "size", "decision"].forEach(name => {
        const select = menu(panel, name);
        if (select && select.value !== "") { select.value = ""; select.dispatchEvent(new Event("change", { bubbles: true })); }
    });
    const title = panel.querySelector<HTMLInputElement>("input[data-filter=title]");
    if (title) title.value = "";
    multi(panel)?.querySelectorAll<HTMLInputElement>("input:checked").forEach(box => { box.checked = false; });
    apply(panel);
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
