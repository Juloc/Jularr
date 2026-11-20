import { inserted, onPage } from "../../../Modules/Navigation/PageSwap";

// The search across all indexers. The bar is a plain GET form; with script the type changes the tokens (and clears the query, as in Prowlarr),
// the pickers name what is chosen, and the releases load into their skeleton. Sort, filter, columns and the chosen releases only change how the
// loaded rows and cards show: nothing is asked again. "Mehr laden" adds the next page of every indexer.
type Facts = { key: string; title: string; indexer: string; protocol: string; published: number; size: number; files: number; grabs: number; seeders: number; peers: number; category: number };
type Filter = { preset: string; title: string; indexer: string; maxAge: number; minSize: number; maxSize: number; minFiles: number; minGrabs: number; minSeeders: number; minPeers: number };

const columnsKey = "jularr.search.columns";
const numberOf = (value: string | undefined) => (value === undefined || value === "" ? NaN : Number(value));
const plain = (text: string) => text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();
const fill = (template: string | null | undefined, ...values: (string | number)[]) =>
    values.reduce<string>((text, value, index) => text.replaceAll(`{${index}}`, String(value)), template ?? "");

const facts = (element: HTMLElement): Facts => ({
    key: element.dataset.key ?? "", title: element.dataset.title ?? "", indexer: element.dataset.indexer ?? "", protocol: element.dataset.protocol ?? "",
    published: numberOf(element.dataset.published), size: numberOf(element.dataset.size), files: numberOf(element.dataset.files), grabs: numberOf(element.dataset.grabs),
    seeders: numberOf(element.dataset.seeders), peers: numberOf(element.dataset.peers), category: numberOf(element.dataset.category)
});

// Prowlarr's orders: age youngest first, peers by seeders then leechers; a missing value goes last.
function sortValue(item: Facts, key: string): number | string {
    switch (key) {
        case "age": return -item.published;
        case "title": return plain(item.title);
        case "indexer": return plain(item.indexer);
        case "protocol": return item.protocol;
        case "peers": return Number.isNaN(item.seeders) ? NaN : item.seeders * 1e6 + (item.peers - item.seeders);
        case "size": return item.size;
        case "files": return item.files;
        case "grabs": return item.grabs;
        default: return item.category;
    }
}

function compare(first: Facts, second: Facts, key: string, ascending: boolean): number {
    const a = sortValue(first, key);
    const b = sortValue(second, key);
    const missingA = typeof a === "number" && Number.isNaN(a);
    const missingB = typeof b === "number" && Number.isNaN(b);
    if (missingA || missingB) return missingA === missingB ? 0 : missingA ? 1 : -1;
    const order = a < b ? -1 : a > b ? 1 : 0;
    return ascending ? order : -order;
}

function readFilter(page: HTMLElement): Filter {
    const field = (name: string) => page.querySelector<HTMLInputElement>(`[data-filter-field="${name}"]`)?.value.trim() ?? "";
    const number = (name: string) => (field(name) === "" ? NaN : Number(field(name).replace(",", ".")));
    return { preset: page.querySelector<HTMLSelectElement>("[data-filter-preset]")?.value ?? "", title: plain(field("title")), indexer: plain(field("indexer")),
        maxAge: number("maxAge"), minSize: number("minSize"), maxSize: number("maxSize"), minFiles: number("minFiles"), minGrabs: number("minGrabs"),
        minSeeders: number("minSeeders"), minPeers: number("minPeers") };
}

function passes(item: Facts, filter: Filter): boolean {
    if (filter.preset === "usenet") return item.protocol === "usenet";
    if (filter.preset === "torrent") return item.protocol === "torrent";
    if (filter.preset !== "custom") return true;
    const within = (value: number, limit: number, least: boolean) => Number.isNaN(limit) || (!Number.isNaN(value) && (least ? value >= limit : value <= limit));
    if (filter.title && !filter.title.split(/\s+/).every(word => plain(item.title).includes(word))) return false;
    if (filter.indexer && !plain(item.indexer).includes(filter.indexer)) return false;
    const days = Number.isNaN(item.published) ? NaN : (Date.now() / 1000 - item.published) / 86400;
    return within(days, filter.maxAge, false) && within(item.size, filter.minSize * 1e9, true) && within(item.size, filter.maxSize * 1e9, false)
        && within(item.files, filter.minFiles, true) && within(item.grabs, filter.minGrabs, true) && within(item.seeders, filter.minSeeders, true)
        && within(item.peers, filter.minPeers, true);
}

const state = new WeakMap<HTMLElement, { sort: string; ascending: boolean; chosen: Set<string>; last: string | null }>();
function stateOf(page: HTMLElement) {
    let found = state.get(page);
    if (!found) { found = { sort: "age", ascending: true, chosen: new Set(), last: null }; state.set(page, found); }
    return found;
}

const loaded = (page: HTMLElement) => page.querySelector<HTMLElement>("[data-search-results][data-loaded]");
const rows = (scope: ParentNode) => Array.from(scope.querySelectorAll<HTMLElement>("tr[data-key]"));
const cards = (scope: ParentNode) => Array.from(scope.querySelectorAll<HTMLElement>(".search-card[data-key]"));

// Orders and filters rows and cards alike, marks the sorted header, the chosen releases and the count of the footer.
function apply(page: HTMLElement): void {
    const results = loaded(page);
    if (!results) return;
    const { sort, ascending, chosen } = stateOf(page);
    const filter = readFilter(page);
    for (const list of [rows(results), cards(results)]) {
        const parent = list[0]?.parentElement;
        list.map(element => ({ element, item: facts(element) })).sort((a, b) => compare(a.item, b.item, sort, ascending))
            .forEach(({ element, item }) => { element.hidden = !passes(item, filter); parent?.append(element); });
    }

    results.querySelectorAll<HTMLElement>("th[data-col]").forEach(head => head.setAttribute("aria-sort", head.dataset.col === sort ? (ascending ? "ascending" : "descending") : "none"));
    const all = rows(results);
    const message = results.querySelector<HTMLElement>("[data-search-state]");
    if (message && !message.hasAttribute("data-empty")) message.hidden = all.length === 0 || all.some(row => !row.hidden);
    results.querySelectorAll<HTMLInputElement>("[data-select]").forEach(box => { box.checked = chosen.has(box.dataset.select ?? ""); });
    results.querySelectorAll<HTMLElement>("[data-key]").forEach(element => element.classList.toggle("is-chosen", chosen.has(element.dataset.key ?? "")));
    const count = page.querySelector<HTMLElement>("[data-search-count]");
    if (count) {
        count.textContent = chosen.size > 0 ? fill(count.dataset.chosen, chosen.size, all.length)
            : all.length === 1 ? count.dataset.foundOne ?? "" : fill(count.dataset.found, all.length);
    }
}

// The hidden columns, kept in this browser; Dateien is hidden until a person shows it.
function applyColumns(page: HTMLElement): void {
    let hidden: string[] = ["files"];
    try { hidden = JSON.parse(localStorage.getItem(columnsKey) ?? '["files"]') as string[]; } catch { /* the default */ }
    page.querySelectorAll<HTMLElement>(".search-table").forEach(table => table.setAttribute("data-hide", hidden.join(" ")));
    page.querySelectorAll<HTMLInputElement>("[data-column-toggle][data-optional]").forEach(box => { box.checked = !hidden.includes(box.dataset.columnToggle ?? ""); });
}

function saveColumns(page: HTMLElement): void {
    const hidden = Array.from(page.querySelectorAll<HTMLInputElement>("[data-column-toggle][data-optional]")).filter(box => !box.checked).map(box => box.dataset.columnToggle ?? "");
    try { localStorage.setItem(columnsKey, JSON.stringify(hidden)); } catch { /* the choice then only lasts for this page */ }
    applyColumns(page);
}

// The bar's pickers name what is chosen; the categories follow the chosen indexers.
function updateBar(form: HTMLFormElement): void {
    const chosen = Array.from(form.querySelectorAll<HTMLInputElement>("input[name=indexer]:checked")).map(box => box.value);
    const label = (name: string, count: number) => {
        const value = form.querySelector<HTMLElement>(`[data-picker-value="${name}"]`);
        if (value) value.textContent = count === 0 ? form.getAttribute(`data-${name}-all`) ?? "" : count === 1 ? form.getAttribute(`data-${name}-one`) ?? "" : fill(form.getAttribute(`data-${name}-some`), count);
    };
    label("indexers", chosen.length);
    label("categories", form.querySelectorAll("input[name=cat]:checked").length);
    form.querySelectorAll<HTMLElement>("[data-offered-by]").forEach(option => {
        option.hidden = chosen.length > 0 && !(option.dataset.offeredBy ?? "").split(" ").some(id => chosen.includes(id));
    });
}

async function fragment(address: string): Promise<HTMLElement> {
    const response = await fetch(address, { headers: { Accept: "text/html" }, credentials: "same-origin" });
    if (!response.ok) throw new Error(String(response.status));
    const holder = document.createElement("div");
    holder.innerHTML = await response.text();
    return holder;
}

function ready(page: HTMLElement): void {
    applyColumns(page);
    apply(page);
    const more = page.querySelector<HTMLButtonElement>("[data-search-more-button]");
    if (more) more.disabled = !loaded(page)?.dataset.next;
}

async function load(page: HTMLElement, results: HTMLElement): Promise<void> {
    const address = results.dataset.searchResults;
    if (!address || results.dataset.loading) return;
    results.dataset.loading = "true";
    try {
        const answer = (await fragment(address)).firstElementChild as HTMLElement | null;
        if (!answer) throw new Error("empty");
        results.replaceWith(answer);
        inserted(answer);
        ready(page);
    } catch {
        delete results.dataset.loading;
        results.removeAttribute("aria-busy");
        const retry = Object.assign(document.createElement("button"), { type: "button", className: "button button--compact", textContent: results.dataset.retry ?? "" });
        retry.addEventListener("click", () => location.reload());
        results.replaceChildren(Object.assign(document.createElement("p"), { className: "search-results__state", textContent: results.dataset.failed ?? "" }), retry);
    }
}

// The next page of every indexer: new releases join the list (a release already shown stays once), the chips show the last answer.
async function loadMore(page: HTMLElement, button: HTMLButtonElement): Promise<void> {
    const results = loaded(page);
    const address = results?.dataset.next;
    if (!results || !address) return;
    button.disabled = true;
    button.setAttribute("aria-busy", "true");
    try {
        const holder = await fragment(address);
        const known = new Set(rows(results).map(row => row.dataset.key));
        const added = rows(holder).filter(row => !known.has(row.dataset.key));
        added.forEach(row => results.querySelector("tbody[data-search-rows]")?.append(row));
        cards(holder).filter(card => !known.has(card.dataset.key)).forEach(card => results.querySelector("[data-search-cards]")?.append(card));
        const chips = holder.querySelector(".search-status");
        if (chips) results.querySelector(".search-status")?.replaceWith(chips);
        const next = new URL(address, location.href);
        next.searchParams.set("offset", String(Number(next.searchParams.get("offset") ?? "0") + 100));
        results.dataset.next = added.length > 0 ? next.pathname + next.search : "";
        inserted(results);
        apply(page);
    } catch {
        /* the button stays for another try */
    } finally {
        button.removeAttribute("aria-busy");
        button.disabled = !results.dataset.next;
    }
}

// Shift-click chooses the shown releases between the last choice and this one, as in Prowlarr.
function choose(page: HTMLElement, box: HTMLInputElement, range: boolean): void {
    const current = stateOf(page);
    const key = box.dataset.select ?? "";
    const results = loaded(page);
    const list = results ? (box.closest(".search-card") ? cards(results) : rows(results)).filter(element => !element.hidden).map(element => element.dataset.key ?? "") : [];
    if (range && current.last && list.includes(current.last) && list.includes(key)) {
        const [from, to] = [list.indexOf(current.last), list.indexOf(key)].sort((a, b) => a - b);
        list.slice(from, to + 1).forEach(item => (box.checked ? current.chosen.add(item) : current.chosen.delete(item)));
    } else if (box.checked) {
        current.chosen.add(key);
    } else {
        current.chosen.delete(key);
    }

    current.last = key;
    apply(page);
}

const pageOf = (element: Element) => element.closest<HTMLElement>("#app-content") ?? document.body;

document.addEventListener("click", event => {
    if (!(event.target instanceof Element)) return;
    const page = pageOf(event.target);
    const box = event.target instanceof HTMLInputElement ? event.target : null;
    if (box?.matches("[data-select]")) { choose(page, box, (event as MouseEvent).shiftKey); return; }
    if (box?.matches("[data-select-all]")) {
        const current = stateOf(page);
        rows(loaded(page) ?? page).filter(row => !row.hidden).forEach(row => (box.checked ? current.chosen.add(row.dataset.key ?? "") : current.chosen.delete(row.dataset.key ?? "")));
        apply(page);
        return;
    }

    const sort = event.target.closest<HTMLElement>("[data-sort]");
    if (sort) {
        const current = stateOf(page);
        current.ascending = current.sort === sort.dataset.sort ? !current.ascending : true;
        current.sort = sort.dataset.sort ?? "age";
        const select = page.querySelector<HTMLSelectElement>("[data-sort-select]");
        if (select) select.value = current.sort;
        apply(page);
        return;
    }

    const token = event.target.closest<HTMLElement>("[data-token]");
    const query = token?.closest<HTMLFormElement>(".search-bar")?.querySelector<HTMLInputElement>("input[name=q]");
    if (token && query) {
        query.value = `${query.value.trimEnd()} ${token.dataset.token}`.trim();
        query.focus();
        return;
    }

    const more = event.target.closest<HTMLButtonElement>("[data-search-more-button]");
    if (more) void loadMore(page, more);
});

document.addEventListener("change", event => {
    const target = event.target;
    if (!(target instanceof HTMLInputElement || target instanceof HTMLSelectElement)) return;
    const page = pageOf(target);
    const form = target.closest<HTMLFormElement>(".search-bar");
    if (form && target instanceof HTMLInputElement && target.name === "type") {
        // Another type clears the query and offers its own tokens, as in Prowlarr.
        const query = form.querySelector<HTMLInputElement>("input[name=q]");
        if (query) query.value = "";
        form.querySelectorAll<HTMLElement>("[data-token]").forEach(token => token.classList.toggle("is-hidden", token.dataset.for !== target.value));
        const label = form.querySelector<HTMLElement>("[data-type-label]");
        if (label) label.textContent = target.closest("label")?.textContent?.trim() ?? "";
        return;
    }

    if (form) { updateBar(form); return; }
    if (target.matches("[data-column-toggle]")) { saveColumns(page); return; }
    if (target.matches("[data-sort-select]")) {
        const current = stateOf(page);
        current.sort = target.value;
        current.ascending = !["size", "grabs", "peers", "files"].includes(target.value);
        apply(page);
        return;
    }

    if (target.matches("[data-filter-preset]")) apply(page);
});

// A custom filter applies while it is typed; typing in it chooses it.
document.addEventListener("input", event => {
    const field = event.target instanceof HTMLInputElement && event.target.matches("[data-filter-field]") ? event.target : null;
    if (!field) return;
    const page = pageOf(field);
    const preset = page.querySelector<HTMLSelectElement>("[data-filter-preset]");
    if (preset) preset.value = "custom";
    apply(page);
});

// Herunterladen is sent without leaving the page; the release keeps a mark of what became of it.
document.addEventListener("submit", event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || !form.matches("[data-search-send]")) return;
    event.preventDefault();
    event.stopPropagation();
    const button = form.querySelector<HTMLButtonElement>("button");
    if (button) button.disabled = true;
    void (async () => {
        let answer = { ok: false, message: form.dataset.failed ?? "" };
        try {
            const response = await fetch(form.action, { method: "POST", body: new FormData(form), headers: { Accept: "application/json" }, credentials: "same-origin" });
            answer = await response.json() as typeof answer;
        } catch { /* the failed text */ }
        const mark = Object.assign(document.createElement("span"), { className: answer.ok ? "search-sent" : "search-sent search-sent--failed", textContent: answer.message || form.dataset.failed || "" });
        mark.setAttribute("role", "status");
        if (answer.ok) {
            form.replaceWith(mark);
        } else {
            form.parentElement?.querySelector(".search-sent")?.remove();
            form.after(mark);
            if (button) button.disabled = false;
        }
    })();
}, true);

onPage(import.meta.url, () => {
    const page = document.getElementById("app-content") ?? document.body;
    const form = page.querySelector<HTMLFormElement>(".search-bar");
    if (form) updateBar(form);
    const results = page.querySelector<HTMLElement>("[data-search-results]");
    if (results?.hasAttribute("data-loaded")) ready(page);
    else if (results) void load(page, results);
}, { partial: true });
