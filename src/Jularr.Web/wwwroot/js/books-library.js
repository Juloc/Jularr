// Books page: client-side tabs, filters, sort and view over the server-rendered shelf, plus the
// book dialog (catalog search → Request through the access policy; manual import for managers). The Offline tab
// is applied by offline-library-ui.js through the shared data-library-filter-option buttons.
(() => {
    const library = document.querySelector("[data-books-library]");
    const grid = library?.querySelector("[data-books-grid]");
    const tiles = grid ? Array.from(grid.querySelectorAll(".book-tile")) : [];
    const textFilter = document.querySelector("[data-books-filter-text]");
    const genreFilter = library?.querySelector("[data-books-genre]");
    const sortSelect = library?.querySelector("[data-books-sort]");
    const noMatches = library?.querySelector("[data-books-no-matches]");
    const panels = library ? Array.from(library.querySelectorAll("[data-books-panel]")) : [];
    const tabs = library ? Array.from(library.querySelectorAll("[data-books-tab]")) : [];
    const viewKey = "jularr.books.view";
    let tab = "all";

    const normalize = (value) => (value || "").toLocaleLowerCase();

    const apply = () => {
        const text = normalize(textFilter?.value.trim());
        const genre = genreFilter?.value || "";
        let visible = 0;
        for (const tile of tiles) {
            const progress = Number(tile.dataset.progress || 0);
            const matchesTab = tab === "reading" ? progress > 0 && progress < 990
                : tab === "finished" ? progress >= 990
                : true;
            const matchesText = !text
                || normalize(tile.dataset.title).includes(text)
                || normalize(tile.dataset.author).includes(text)
                || normalize(tile.dataset.genres).includes(text);
            const matchesGenre = !genre || (tile.dataset.genres || "").split("|").includes(genre);
            const show = matchesTab && matchesText && matchesGenre;
            tile.classList.toggle("is-filter-hidden", !show);
            if (show && !tile.classList.contains("is-offline-hidden")) visible++;
        }
        if (noMatches) noMatches.hidden = tiles.length === 0 || visible > 0;
    };

    const sort = () => {
        if (!grid || !sortSelect) return;
        const key = sortSelect.value;
        const by = {
            recent: (a, b) => (b.dataset.lastRead || "").localeCompare(a.dataset.lastRead || ""),
            title: (a, b) => a.dataset.title.localeCompare(b.dataset.title),
            author: (a, b) => (a.dataset.author || "￿").localeCompare(b.dataset.author || "￿"),
            progress: (a, b) => Number(b.dataset.progress) - Number(a.dataset.progress)
        }[key];
        if (!by) return;
        for (const tile of [...tiles].sort(by)) grid.append(tile);
    };

    for (const button of tabs) {
        button.addEventListener("click", () => {
            tab = button.dataset.booksTab;
            for (const other of tabs) {
                const active = other === button;
                other.classList.toggle("active", active);
                other.setAttribute("aria-selected", String(active));
            }
            for (const panel of panels) {
                panel.hidden = panel.dataset.booksPanel !== (tab === "requests" ? "requests" : "library");
            }
            // The offline filter re-evaluates on its own click handler; re-apply after it ran.
            window.setTimeout(apply, 0);
        });
    }
    textFilter?.addEventListener("input", apply);
    genreFilter?.addEventListener("change", apply);
    sortSelect?.addEventListener("change", sort);

    const setView = (view) => {
        if (!grid) return;
        grid.dataset.view = view;
        for (const button of library.querySelectorAll("[data-books-view]")) {
            const active = button.dataset.booksView === view;
            button.classList.toggle("active", active);
            button.setAttribute("aria-pressed", String(active));
        }
        try { localStorage.setItem(viewKey, view); } catch { /* storage unavailable */ }
    };
    for (const button of library?.querySelectorAll("[data-books-view]") || []) {
        button.addEventListener("click", () => setView(button.dataset.booksView));
    }
    try {
        const stored = localStorage.getItem(viewKey);
        if (stored === "list" || stored === "grid") setView(stored);
    } catch { /* storage unavailable */ }

    // Close any open ⋮ menu when clicking elsewhere.
    document.addEventListener("click", event => {
        for (const menu of document.querySelectorAll(".book-tile-menu[open]")) {
            if (!menu.contains(event.target)) menu.open = false;
        }
    });

    sort();
    apply();
})();

(() => {
    const dialog = document.querySelector("[data-books-add]");
    if (!dialog) return;

    for (const opener of document.querySelectorAll("[data-open-dialog='books-add']")) {
        opener.addEventListener("click", () => {
            dialog.showModal();
            dialog.querySelector("[data-books-add-query]")?.focus();
            // Results kept from before show their current state right away.
            poll();
        });
    }

    const form = dialog.querySelector("[data-books-add-search]");
    const query = dialog.querySelector("[data-books-add-query]");
    const state = dialog.querySelector("[data-books-add-state]");
    const results = dialog.querySelector("[data-books-add-results]");
    const token = dialog.querySelector("input[name='__RequestVerificationToken']")?.value || "";
    const text = (key) => dialog.dataset[key] || "";
    const statusText = (status) => dialog.dataset[`status${status.charAt(0).toUpperCase()}${status.slice(1)}`] || status;
    const pollInterval = 4000;
    // Rows on screen by catalog id; each keeps its item (with the canonical state) and action slot.
    const rows = new Map();
    let controller = null;
    let searchNumber = 0;
    let pollTimer = null;
    let libraryChanged = false;

    const element = (tag, className, content) => {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (content !== undefined) node.textContent = content;
        return node;
    };

    const add = async (item, slot, button) => {
        button.disabled = true;
        // Adding resolves direct/free, OPDS and then Usenet acquisition; show that it is working.
        slot.prepend(element("span", "status-pill request-status-searching", statusText("searching")));
        const body = new FormData();
        body.set("catalogId", item.id);
        body.set("title", item.title);
        if (item.author) body.set("author", item.author);
        if (item.coverImageUrl) body.set("coverImageUrl", item.coverImageUrl);
        body.set("__RequestVerificationToken", token);
        try {
            const response = await fetch(dialog.dataset.addUrl, {
                method: "POST",
                body,
                credentials: "same-origin",
                headers: { Accept: "application/json" }
            });
            if (!response.ok) throw new Error(String(response.status));
            update(item, slot, await response.json());
        } catch {
            renderAction(item, slot);
            state.textContent = text("textFailed");
        }
    };

    const renderAction = (item, slot) => {
        slot.replaceChildren();
        const current = item.state || {};
        if (current.libraryWorkId) {
            const link = element("a", "button", text("textInLibrary"));
            link.href = `/Books/Library/${current.libraryWorkId}`;
            slot.append(link);
            return;
        }
        const failed = current.requestStatus === "failed";
        if (current.requestStatus) {
            const label = current.requestStatus === "downloading" && current.progress > 0
                ? `${statusText("downloading")} ${current.progress}%`
                : statusText(current.requestStatus);
            slot.append(element("span", `status-pill request-status-${current.requestStatus}`, label));
        }
        if (!current.requestStatus || failed) {
            const label = failed ? text("textRetry") : text("textRequest");
            const button = element("button", failed ? "button" : "button button-primary", label);
            button.type = "button";
            button.addEventListener("click", () => add(item, slot, button));
            slot.append(button);
        }
        if (current.message) slot.append(element("small", "books-add-note", current.message));
    };

    // Takes the slot directly rather than looking a row up by item.id: picking a different
    // edition (#405) changes item.id after the row was keyed in `rows`.
    const update = (item, slot, next) => {
        if (next.libraryWorkId && !item.state?.libraryWorkId) libraryChanged = true;
        item.state = next;
        renderAction(item, slot);
        schedulePoll();
    };

    // While the dialog is open, in-flight requests follow their canonical state on their own.
    const schedulePoll = () => {
        if (pollTimer || !dialog.open) return;
        if (![...rows.values()].some(row => row.item.state?.inFlight)) return;
        pollTimer = window.setTimeout(poll, pollInterval);
    };

    const poll = async () => {
        pollTimer = null;
        const pending = [...rows.values()].filter(row => row.item.state?.inFlight);
        if (!dialog.open || pending.length === 0) return;
        try {
            const url = new URL(dialog.dataset.statusUrl, window.location.origin);
            for (const row of pending) url.searchParams.append("ids", row.item.id);
            const response = await fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" } });
            if (response.ok) {
                const payload = await response.json();
                for (const row of pending) {
                    const next = payload.states?.[row.item.id];
                    if (next) update(row.item, row.slot, next);
                }
            }
        } catch { /* offline for a moment: the next poll catches up */ }
        schedulePoll();
    };

    // A work's own choice of primary edition is a sensible default (a free edition when one
    // exists); expanding lets the reader pick a different provider record instead (#405).
    let editionGroup = 0;
    const editionFormatText = (format) => text(`editionFormat${format.charAt(0).toUpperCase()}${format.slice(1)}`);
    const editionPicker = (item, group) => {
        const picker = element("details", "books-edition-picker");
        picker.append(element("summary", null, text("editionsCount").replace("{count}", item.editions.length)));
        const list = element("ul", "books-edition-list");
        for (const edition of item.editions) {
            const row = element("li");
            const label = document.createElement("label");
            const radio = document.createElement("input");
            radio.type = "radio";
            radio.name = `books-edition-${group}`;
            radio.value = edition.id;
            radio.checked = edition.id === item.id;
            radio.addEventListener("change", () => { item.id = edition.id; });
            const parts = [edition.year, editionFormatText(edition.format), edition.languageName, edition.publisher, edition.isbn, edition.source].filter(Boolean);
            label.append(radio, element("span", "books-edition-meta", parts.join(" · ")));
            row.append(label);
            list.append(row);
        }
        picker.append(list);
        return picker;
    };

    const render = (items) => {
        results.replaceChildren();
        rows.clear();
        for (const item of items) {
            const row = element("li", "books-add-result");
            const cover = element("div", "books-add-cover");
            // The chosen cover first; a cover that does not load gives way to the next one,
            // and the one that shows is the one a request keeps.
            const covers = [item.coverImageUrl, ...(item.covers || [])].filter((url, index, all) => url && all.indexOf(url) === index);
            if (covers.length > 0) {
                const image = document.createElement("img");
                image.alt = "";
                image.loading = "lazy";
                image.referrerPolicy = "no-referrer";
                image.addEventListener("error", () => {
                    covers.shift();
                    item.coverImageUrl = covers[0] || null;
                    if (covers.length > 0) {
                        image.src = covers[0];
                    } else {
                        image.remove();
                        cover.append(element("span", "book-tile-placeholder", item.title));
                    }
                });
                image.src = covers[0];
                cover.append(image);
            } else {
                // No cover from any provider: the same paper-tile placeholder as the shelf and
                // the library grid, never a blank box (#405).
                cover.append(element("span", "book-tile-placeholder", item.title));
            }
            const copy = element("div", "books-add-copy");
            copy.append(element("strong", null, item.title));
            const meta = [item.author, item.year].filter(Boolean).join(" · ");
            if (meta) copy.append(element("span", "books-add-meta", meta));
            if (item.listState) copy.append(element("small", "books-add-list-state", item.listState));
            if (item.summary) copy.append(element("p", "books-add-summary", item.summary));
            if (item.freeEdition) copy.append(element("small", "books-add-free", text("textFree")));
            const externalAvailability = [];
            if (item.availability?.opds) externalAvailability.push("OPDS");
            if (item.availability?.usenet) externalAvailability.push("Usenet");
            if (externalAvailability.length > 0) {
                copy.append(element("small", "books-add-free", externalAvailability.join(" · ")));
            }
            if (item.editions?.length > 1) copy.append(editionPicker(item, editionGroup++));
            const slot = element("div", "books-add-action");
            rows.set(item.id, { item, slot });
            renderAction(item, slot);
            row.append(cover, copy, slot);
            results.append(row);
        }
        schedulePoll();
    };

    // Placeholder rows keep the list from jumping while the providers answer.
    const showSkeleton = () => {
        results.replaceChildren();
        for (let index = 0; index < 4; index++) {
            const row = element("li", "books-add-result books-add-skeleton");
            row.setAttribute("aria-hidden", "true");
            const copy = element("div", "books-add-copy");
            copy.append(element("span"), element("span"));
            row.append(element("div", "books-add-cover"), copy);
            results.append(row);
        }
    };

    // Typing searches after a short pause; Enter searches at once.
    let searched = "";
    let debounce = null;
    query?.addEventListener("input", () => {
        window.clearTimeout(debounce);
        const value = query.value.trim();
        if (value.length < 3 || value === searched) return;
        debounce = window.setTimeout(() => form.requestSubmit(), 450);
    });

    form?.addEventListener("submit", async event => {
        event.preventDefault();
        window.clearTimeout(debounce);
        const value = query.value.trim();
        if (value.length < 2) return;
        searched = value;
        controller?.abort();
        controller = new AbortController();
        const number = ++searchNumber;
        state.textContent = text("textSearching");
        rows.clear();
        showSkeleton();
        try {
            const url = new URL(dialog.dataset.searchUrl, window.location.origin);
            url.searchParams.set("q", value);
            const response = await fetch(url, { credentials: "same-origin", signal: controller.signal, headers: { Accept: "application/json" } });
            const payload = await response.json();
            // A slower answer for an older query never replaces the newer one.
            if (number !== searchNumber) return;
            state.textContent = payload.error || (payload.results.length === 0 ? text("textNoResults") : "");
            render(payload.results);
        } catch (error) {
            if (error.name !== "AbortError" && number === searchNumber) {
                state.textContent = text("textFailed");
                results.replaceChildren();
            }
        }
    });

    dialog.addEventListener("close", () => {
        window.clearTimeout(pollTimer);
        pollTimer = null;
        // A book arrived while the dialog was open: show it on the shelf.
        if (libraryChanged) window.location.reload();
    });

    // Deep links like /Books?q=dune open the dialog with the query.
    const initial = new URLSearchParams(window.location.search).get("q");
    if (initial && query) {
        dialog.showModal();
        query.value = initial;
        form.requestSubmit();
    }
})();

