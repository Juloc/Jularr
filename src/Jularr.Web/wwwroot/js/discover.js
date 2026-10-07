(() => {
    "use strict";

    const root = document.querySelector("[data-discover]");
    if (!root) return;

    const form = root.querySelector("[data-dc-form]");
    // The search field lives in the shell header, outside the page root.
    const searchInput = document.querySelector("[data-dc-search]");
    const body = root.querySelector("[data-dc-body]");
    const errorBox = root.querySelector("[data-dc-error]");
    const offlineNotice = root.querySelector("[data-dc-offline]");
    const hover = root.querySelector("[data-dc-hover]");
    const sheet = root.querySelector("[data-dc-sheet]");
    const sheetContent = root.querySelector("[data-dc-sheet-content]");
    const importDetails = root.querySelector("[data-dc-import]");
    const token = root.querySelector("input[name='__RequestVerificationToken']")?.value || "";
    const text = name => root.dataset[name] || "";
    const statusText = status =>
        root.dataset[`status${status.charAt(0).toUpperCase()}${status.slice(1)}`] || status;

    const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
    const SEARCH_DELAY = 250;

    let abortController = null;
    let requestVersion = 0;
    let debounceTimer = null;
    let loadFailed = false;
    // Changes the visitor made in a preview (follow, request), applied again when a card's preview reopens.
    const overrides = new Map();

    // ---- Shelves: the native scrollbar is hidden, the chevrons move a row by one view -----------------------------------------------------

    // A chevron shows only while there is more to see in its direction; a section replaced by a later generation is brought up to date when it is reached.
    function syncShelf(shelf) {
        const track = shelf.querySelector(".dc-track");
        if (!track) return;
        shelf.querySelector("[data-dc-shelf-nav='-1']").hidden = track.scrollLeft <= 1;
        shelf.querySelector("[data-dc-shelf-nav='1']").hidden = track.scrollLeft >= track.scrollWidth - track.clientWidth - 1;
    }

    const shelfOf = target => target instanceof Element ? target.closest("[data-dc-shelf]") : null;
    ["mouseover", "focusin", "scroll"].forEach(type => root.addEventListener(type, event => {
        const shelf = shelfOf(event.target);
        if (shelf) syncShelf(shelf);
    }, true));
    root.addEventListener("click", event => {
        const nav = event.target.closest("[data-dc-shelf-nav]");
        if (!nav) return;
        const track = nav.closest("[data-dc-shelf]").querySelector(".dc-track");
        track.scrollBy({ left: Number(nav.dataset.dcShelfNav) * track.clientWidth * 0.85, behavior: reducedMotion.matches ? "auto" : "smooth" });
    });

    // ---- Address: the only state --------------------------------------------------------------

    // The canonical address of the form: defaults and empty fields are left out, so a bookmark stays short.
    function currentParams() {
        const data = new FormData(form);
        const params = new URLSearchParams();
        const value = name => String(data.get(name) || "").trim();
        // The multi-valued filters repeat their field; the address keeps them as one comma separated value.
        const values = name => data.getAll(name).map(item => String(item).trim()).filter(Boolean).join(",");
        if (value("q")) params.set("q", value("q"));
        if (value("category") && value("category") !== "all") params.set("category", value("category"));
        if (value("mode") && value("mode") !== "all") params.set("mode", value("mode"));
        ["genre", "status", "avail"].forEach(name => {
            if (values(name)) params.set(name, values(name));
        });
        ["from", "to"].forEach(name => {
            if (value(name)) params.set(name, value(name));
        });
        if (value("pref") === "1") params.set("pref", "1");
        return params;
    }

    function addressOf(params) {
        const query = params.toString();
        return query ? `${window.location.pathname}?${query}` : window.location.pathname;
    }

    // Keeps the media-type links on the text that is being typed, since the links were built for the address
    // the page was opened with.
    function syncTabs() {
        const q = searchInput.value.trim();
        root.querySelectorAll("[data-dc-tab]").forEach(link => {
            const url = new URL(link.getAttribute("href"), window.location.origin);
            if (q) url.searchParams.set("q", q); else url.searchParams.delete("q");
            link.setAttribute("href", url.pathname + url.search);
        });
        // Typing turns the page into Search mode: the landing (Hero, Continue) and the browse views give way to the results.
        document.querySelectorAll("[data-dc-landing], .dc-browse, .dc-tokens").forEach(element => { element.hidden = q.length > 0; });
    }

    // ---- Body: sections of titles, fetched after first paint and completed by later generations ----------------------------------------
    //
    // The first response holds what the sources had ready within the first paint budget; a source that was not ready leaves a ghost section
    // of the final size. The page then asks for the next generation and the server answers as soon as one more source has settled. A
    // generation that only fills ghost rows is applied at once (nothing moves); every other change is staged until it is safe to apply it
    // (discover-staging.js), and the scroll anchor and the focus stay where they were.

    const staging = window.JularrDiscoverStaging;
    const interaction = { pointerDown: false, touchActive: false, lastScrollAt: 0, lastKeyAt: 0, x: -1, y: -1 };
    const syncNotice = root.querySelector("[data-dc-sync]");
    const maxRetryAfterMs = 30000;

    const bodyRoot = () => body.querySelector("[data-dc-state]");

    // How far the sources had answered in the newest generation received, whether it was applied or is still staged: the next request waits from there.
    let generation = { settled: 0, pending: 0 };
    const generationOf = (element) => ({ settled: Number(element?.dataset.dcSettled) || 0, pending: Number(element?.dataset.dcPending) || 0 });

    // The rows or groups of a generation: only what the viewer would notice changing is compared.
    const viewOf = (element) => ({
        state: element?.dataset.dcState || "",
        sections: [...(element?.querySelectorAll(":scope > [data-dc-section]") || [])].map(section => ({
            id: section.dataset.dcSection,
            sig: section.dataset.dcSig,
            state: section.dataset.dcSectionState,
            layout: section.dataset.dcLayout,
            note: section.dataset.dcNote === "true"
        }))
    });

    function sectionIn(container, id) {
        return container.querySelector(`:scope > [data-dc-section="${CSS.escape(id)}"]`);
    }

    // Only something to click counts as under the pointer: resting over a ghost or over empty space between cards blocks nothing.
    const actionable = "a, button, summary, select, input, [data-dc-card]";
    const sectionAt = (x, y) => (x < 0 ? null : document.elementFromPoint(x, y)?.closest(actionable)?.closest("[data-dc-section]")?.dataset.dcSection ?? null);

    function interactionContext() {
        return {
            pointerDown: interaction.pointerDown,
            touchActive: interaction.touchActive,
            modalOpen: sheet.open,
            lastScrollAt: interaction.lastScrollAt,
            lastKeyAt: interaction.lastKeyAt,
            pointerSection: sectionAt(interaction.x, interaction.y),
            focusSection: document.activeElement?.closest?.("[data-dc-section]")?.dataset.dcSection ?? null
        };
    }

    // What the viewer is looking at or aiming at: the card under the pointer, else the first card in view, else the first section in view. The section
    // is kept as a second reference, since a replaced section takes its cards with it.
    function findAnchor() {
        const inView = item => {
            const rect = item.getBoundingClientRect();
            return rect.bottom > 0 && rect.top < window.innerHeight;
        };
        const hovered = interaction.x < 0 ? null : document.elementFromPoint(interaction.x, interaction.y)?.closest("[data-dc-card]");
        const element = hovered || [...body.querySelectorAll("[data-dc-card]")].find(inView) || [...body.querySelectorAll("[data-dc-section]")].find(inView);
        const section = element?.closest("[data-dc-section]");
        return element ? { element, top: element.getBoundingClientRect().top, sectionId: section?.dataset.dcSection, sectionTop: section?.getBoundingClientRect().top } : null;
    }

    function restoreAnchor(anchor) {
        if (!anchor) return;
        const kept = anchor.element.isConnected;
        const target = kept ? anchor.element : anchor.sectionId ? sectionIn(bodyRoot(), anchor.sectionId) : null;
        if (!target) return;
        const shift = target.getBoundingClientRect().top - (kept ? anchor.top : anchor.sectionTop);
        if (Math.abs(shift) >= 1) window.scrollBy(0, shift);
    }

    // The focus is on a control of a section that is about to be replaced: afterwards it goes to the same control of the new section, else to the
    // first control of that section. A section that went away takes the focus with it, as it would on any page.
    function captureFocus() {
        const active = document.activeElement;
        const section = body.contains(active) ? active.closest("[data-dc-section]") : null;
        return section ? { element: active, sectionId: section.dataset.dcSection, retry: active.dataset?.dcRetrySources ?? null } : null;
    }

    function restoreFocus(focus) {
        if (!focus || focus.element.isConnected) return;
        const section = sectionIn(bodyRoot(), focus.sectionId);
        const same = focus.retry ? section?.querySelector(`[data-dc-retry-sources="${CSS.escape(focus.retry)}"]`) : null;
        (same || section?.querySelector("summary, a, button"))?.focus({ preventScroll: true });
    }

    // Replaces one section by its next version without losing what the viewer set on it: a collapsed group stays collapsed and a row keeps its scroll position.
    function swapSection(current, next) {
        const open = current.querySelector(":scope > details");
        const nextDetails = next.querySelector(":scope > details");
        if (open && nextDetails) nextDetails.open = open.open;
        const track = current.querySelector(".dc-track");
        const nextTrack = next.querySelector(".dc-track");
        const scrolled = track ? track.scrollLeft : 0;
        current.replaceWith(next);
        if (nextTrack && scrolled) nextTrack.scrollLeft = scrolled;
    }

    // The generation being applied; the document adapter below reads the sections it brings from it.
    let incoming = null;
    const incomingSection = (id) => document.importNode(sectionIn(incoming, id), true);

    const applier = staging.createApplier({
        captureAnchor: findAnchor,
        restoreAnchor,
        captureFocus,
        restoreFocus,
        hideHover: () => hideHover(),
        replaceAll() {
            body.replaceChildren(document.importNode(incoming, true));
            activateLiveRequests(body);
        },
        has: (id) => Boolean(sectionIn(bodyRoot(), id)),
        swap(id) {
            const fresh = incomingSection(id);
            swapSection(sectionIn(bodyRoot(), id), fresh);
            activateLiveRequests(fresh);
        },
        insertAfter(beforeId, id) {
            const fresh = incomingSection(id);
            if (beforeId) sectionIn(bodyRoot(), beforeId).after(fresh); else bodyRoot().prepend(fresh);
            activateLiveRequests(fresh);
        },
        remove: (id) => sectionIn(bodyRoot(), id)?.remove(),
        updateGeneration() {
            bodyRoot().dataset.dcSettled = incoming.dataset.dcSettled;
            bodyRoot().dataset.dcPending = incoming.dataset.dcPending;
        },
        view: () => viewOf(bodyRoot())
    });

    function applyOperations(operations, payload) {
        incoming = payload.querySelector("[data-dc-state]");
        return applier.apply(operations, [...incoming.querySelectorAll(":scope > [data-dc-section]")].map(section => section.dataset.dcSection));
    }

    // The ghost hint: a section that has a change waiting is marked; the stylesheet draws a faint mark in the gap between its first cards.
    function showStaged(operations) {
        const waiting = new Set(operations.filter(operation => !operation.neutral && operation.id !== "*").map(operation => operation.id));
        body.querySelectorAll("[data-dc-section]").forEach(section => {
            section.toggleAttribute("data-dc-staged", waiting.has(section.dataset.dcSection));
        });
    }

    const controller = staging.createController({
        timers: window,
        getContext: interactionContext,
        commit: applyOperations,
        show: showStaged
    });

    // A failed request is reported with what the server said about waiting (a 429 carries Retry-After), so the page never retries sooner than that.
    async function fetchBody(extra, signal) {
        const params = new URLSearchParams(window.location.search);
        params.set("handler", "Body");
        Object.entries(extra).forEach(([name, value]) => params.set(name, String(value)));
        const response = await fetch(`${window.location.pathname}?${params}`, {
            signal,
            cache: "no-store",
            headers: { "X-Requested-With": "fetch" }
        });
        const html = await response.text();
        // A failed body still renders its own unavailable state; anything else is a plain failure.
        if (!response.ok && !html.includes("data-dc-state")) {
            const error = new Error(String(response.status));
            error.retryAfterMs = Math.min(maxRetryAfterMs, (Number(response.headers.get("Retry-After")) || 0) * 1000);
            throw error;
        }

        return html;
    }

    function showSyncNotice(visible) {
        if (syncNotice) syncNotice.hidden = !visible;
    }

    let resumeTimer = null;

    // Asking for the next generation failed: what is on the page stays, the viewer is told and can ask again; a server that named a time to wait is asked once more then.
    function showUpdateFailure(error) {
        showSyncNotice(true);
        clearTimeout(resumeTimer);
        if (error?.retryAfterMs) {
            const version = requestVersion;
            resumeTimer = setTimeout(() => {
                if (version === requestVersion) resumeFollowUp();
            }, error.retryAfterMs);
        }
    }

    function resumeFollowUp() {
        clearTimeout(resumeTimer);
        showSyncNotice(false);
        void followUp.run(requestVersion);
    }

    function stageGeneration(html, explicit) {
        const payload = new DOMParser().parseFromString(html, "text/html");
        const next = payload.querySelector("[data-dc-state]");
        if (!next) return;
        generation = generationOf(next);
        showSyncNotice(false);
        controller.stage(viewOf(next), payload, explicit);
    }

    const followUp = staging.createFollowUp({
        fetchNext: settled => fetchBody({ after: settled }, abortController.signal),
        stage: html => stageGeneration(html, false),
        generation: () => generation,
        isCurrent: version => version === requestVersion,
        onFailure: showUpdateFailure
    });

    async function loadBody() {
        clearTimeout(debounceTimer);
        clearTimeout(resumeTimer);
        abortController?.abort();
        abortController = new AbortController();
        const version = ++requestVersion;

        body.setAttribute("aria-busy", "true");
        errorBox.hidden = true;
        showSyncNotice(false);
        try {
            const html = await fetchBody({}, abortController.signal);
            if (version !== requestVersion) return;

            hideHover();
            // Same-origin, server-rendered and HTML-encoded by Razor; injected as the page body.
            body.innerHTML = html;
            activateLiveRequests(body);
            controller.reset(viewOf(bodyRoot()));
            generation = generationOf(bodyRoot());
            loadFailed = false;
            body.setAttribute("aria-busy", "false");
            void followUp.run(version);
        } catch (error) {
            if (error?.name === "AbortError" || version !== requestVersion) return;
            showLoadFailure();
        }
    }

    // The new address could not be loaded: what is on the page stays under the message, so a hiccup never leaves an empty page.
    function showLoadFailure() {
        loadFailed = true;
        errorBox.hidden = false;
        body.setAttribute("aria-busy", "false");
    }

    // A retry the viewer asked for: the server fetches the failed sources again and the answer is applied as soon as it arrives. A source that fails
    // again leaves its section as it was, so the button only becomes available again.
    async function retrySources(button) {
        // Not disabled: a disabled control loses the keyboard focus, and a retry is often pressed with the keyboard.
        if (button.getAttribute("aria-disabled") === "true") return;
        const section = button.closest("[data-dc-section]");
        button.setAttribute("aria-disabled", "true");
        section?.setAttribute("aria-busy", "true");
        const version = requestVersion;
        try {
            const html = await fetchBody({ retry: button.dataset.dcRetrySources }, abortController?.signal);
            if (version !== requestVersion) return;
            stageGeneration(html, true);
            void followUp.run(version);
        } catch (error) {
            if (error?.name === "AbortError" || version !== requestVersion) return;
            showUpdateFailure(error);
        }

        button.removeAttribute("aria-disabled");
        section?.removeAttribute("aria-busy");
    }

    // What the viewer is doing right now, read by the staging rules before any staged change is applied.
    ["pointerdown", "pointerup", "pointercancel", "touchstart", "touchend", "touchcancel"].forEach(type => {
        window.addEventListener(type, event => {
            if (type.startsWith("pointer")) interaction.pointerDown = type === "pointerdown";
            else interaction.touchActive = type === "touchstart";
            if (type === "pointerdown" && event.pointerType === "mouse") {
                interaction.x = event.clientX;
                interaction.y = event.clientY;
            }
        }, { passive: true, capture: true });
    });
    window.addEventListener("pointermove", event => {
        if (event.pointerType === "mouse") {
            interaction.x = event.clientX;
            interaction.y = event.clientY;
        }
    }, { passive: true });
    document.documentElement.addEventListener("mouseleave", () => { interaction.x = -1; interaction.y = -1; });
    ["scroll", "wheel"].forEach(type => window.addEventListener(type, () => { interaction.lastScrollAt = Date.now(); }, { passive: true, capture: true }));
    window.addEventListener("keydown", event => {
        if (["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Tab", "PageUp", "PageDown", "Home", "End", " "].includes(event.key)) {
            interaction.lastKeyAt = Date.now();
        }
    }, { capture: true });

    function scheduleSearch() {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            window.history.replaceState({}, "", addressOf(currentParams()));
            syncTabs();
            loadBody();
        }, SEARCH_DELAY);
    }

    // ---- Filters: the genre list filters as the viewer types and a year preset only sets the range

    document.addEventListener("input", event => {
        const field = event.target instanceof Element ? event.target.closest("[data-dc-genre-search]") : null;
        if (!field) return;
        const needle = field.value.trim().toLowerCase();
        field.closest("fieldset")?.querySelectorAll("[data-dc-option]").forEach(option => {
            option.hidden = needle.length > 0 && !option.dataset.dcOption.includes(needle) && !option.textContent.toLowerCase().includes(needle);
        });
    });

    document.addEventListener("click", event => {
        const preset = event.target instanceof Element ? event.target.closest("[data-dc-year-preset]") : null;
        if (!preset) return;
        const [from, to] = preset.dataset.dcYearPreset.split("-");
        const group = preset.closest("fieldset");
        group.querySelector("[data-dc-year-from]").value = from;
        group.querySelector("[data-dc-year-to]").value = to;
    });

    // ---- Paging: a view of one media type loads provider pages as the viewer scrolls --------------------------------------------------------
    //
    // The body says when a full provider page came back (data-dc-has-more). A sentinel after the grid asks for the next page when it comes near;
    // the cards are appended without touching the ones above, titles the viewer already has are skipped, a failed page keeps what is loaded and
    // offers a retry, and the end of the source (or five pages in a row that add nothing) stops the loading.

    const pager = { address: "", page: 1, loading: false, done: false, empty: 0, sentinel: null, observer: null };
    const cardKey = card => `${card.dataset.dcCategory}|${card.dataset.dcProvider}|${card.dataset.dcExternalId}`;

    function gridList() {
        return body.querySelector("[data-dc-section][data-dc-layout='grid'] ul:not(.dc-ghosts)");
    }

    function stopPaging() {
        pager.observer?.disconnect();
        pager.sentinel?.remove();
        pager.sentinel = null;
    }

    function ensurePager() {
        const current = bodyRoot();
        const list = gridList();
        if (!current || !list || current.dataset.dcHasMore !== "true" || Number(current.dataset.dcPending) > 0) {
            stopPaging();
            return;
        }

        const address = window.location.pathname + window.location.search;
        if (pager.address !== address) {
            Object.assign(pager, { address, page: 1, loading: false, done: false, empty: 0 });
            stopPaging();
        }

        if (pager.done || pager.sentinel?.isConnected) return;
        const sentinel = document.createElement("div");
        sentinel.className = "dc-sentinel";
        sentinel.dataset.dcSentinel = "";
        list.closest("[data-dc-section]").after(sentinel);
        pager.sentinel = sentinel;
        pager.observer = new IntersectionObserver(entries => {
            if (entries.some(entry => entry.isIntersecting)) void loadMore();
        }, { rootMargin: "600px 0px" });
        pager.observer.observe(sentinel);
    }

    async function loadMore() {
        const list = gridList();
        if (pager.loading || pager.done || !list || !pager.sentinel) return;
        pager.loading = true;
        pager.sentinel.dataset.state = "loading";
        pager.sentinel.replaceChildren();
        const address = pager.address;
        try {
            const params = new URLSearchParams(window.location.search);
            params.set("handler", "Body");
            params.set("pg", String(pager.page + 1));
            const response = await fetch(`${window.location.pathname}?${params}`, { cache: "no-store", headers: { "X-Requested-With": "fetch" } });
            if (!response.ok) throw new Error(String(response.status));
            const more = new DOMParser().parseFromString(await response.text(), "text/html").querySelector("[data-dc-more-items]");
            if (address !== pager.address || !more) return;
            const known = new Set([...list.querySelectorAll("[data-dc-card]")].map(cardKey));
            let added = 0;
            more.querySelectorAll(":scope > li").forEach(item => {
                const card = item.querySelector("[data-dc-card]");
                const key = card ? cardKey(card) : "";
                if (!key || known.has(key)) return;
                known.add(key);
                list.append(document.importNode(item, true));
                added += 1;
            });
            pager.page += 1;
            pager.empty = added === 0 ? pager.empty + 1 : 0;
            pager.done = more.dataset.dcHasMore !== "true" || pager.empty >= 5;
            if (pager.done) stopPaging();
        } catch {
            if (address === pager.address && pager.sentinel) {
                const retry = document.createElement("button");
                retry.type = "button";
                retry.className = "button";
                retry.textContent = root.dataset.textRetry || "Retry";
                retry.addEventListener("click", () => { retry.remove(); pager.loading = false; void loadMore(); });
                pager.sentinel.replaceChildren(retry);
            }
            return;
        } finally {
            pager.loading = false;
            if (pager.sentinel) delete pager.sentinel.dataset.state;
        }

        // The sentinel may still be in view (a short page, a local filter that hid most of it): ask again.
        if (!pager.done && pager.sentinel) {
            pager.observer?.unobserve(pager.sentinel);
            pager.observer?.observe(pager.sentinel);
        }
    }

    new MutationObserver(() => ensurePager()).observe(body, { childList: true, subtree: true });

    searchInput.addEventListener("input", () => {
        syncTabs();
        scheduleSearch();
    });

    form.addEventListener("submit", event => {
        event.preventDefault();
        window.location.assign(addressOf(currentParams()));
    });

    root.addEventListener("click", event => {
        const target = event.target instanceof Element ? event.target : null;
        if (!target) return;

        if (target.closest("[data-dc-retry]")) {
            loadBody();
            return;
        }

        if (target.closest("[data-dc-sync-retry]")) {
            resumeFollowUp();
            return;
        }

        const retrySource = target.closest("[data-dc-retry-sources]");
        if (retrySource) {
            void retrySources(retrySource);
            return;
        }

        const tab = target.closest("[data-dc-tab]");
        if (tab && event.button === 0 && !(event.metaKey || event.ctrlKey || event.shiftKey || event.altKey)) {
            // Follow the link as it is now: it carries the text typed so far.
            syncTabs();
        }
    });

    // ---- Filters: a native <details> popover that also closes on outside click, Escape and its buttons

    const openPops = () => document.querySelectorAll("details[data-dc-pop][open]");

    document.addEventListener("click", event => {
        const target = event.target instanceof Element ? event.target : null;
        if (!target) return;

        const closer = target.closest("[data-dc-close]");
        if (closer) {
            const pop = closer.closest("details");
            if (pop) {
                pop.open = false;
                pop.querySelector("summary")?.focus();
            }
            return;
        }

        openPops().forEach(pop => {
            if (!pop.contains(target)) pop.open = false;
        });
    });

    document.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            hideHover();
            openPops().forEach(pop => {
                pop.open = false;
                pop.querySelector("summary")?.focus();
            });
            return;
        }

        if (event.key === "/" &&
            !(event.ctrlKey || event.metaKey || event.altKey) &&
            !/input|textarea|select/i.test(document.activeElement?.tagName || "")) {
            event.preventDefault();
            searchInput.focus();
        }
    });

    // ---- Preview: hover popover on a pointer, modal sheet on touch and from the preview button --------

    function cloneTemplate(card) {
        const template = card.querySelector("template[data-dc-template]");
        if (!template) return null;
        const fragment = template.content.cloneNode(true);
        const preview = fragment.querySelector(".dc-pv");
        const key = card.dataset.dcKey;
        const change = key ? overrides.get(key) : null;
        if (preview && change) applyOverrides(preview, change);
        return fragment;
    }

    function keyOf(card) {
        if (!card.dataset.dcKey) {
            card.dataset.dcKey = String(++keyCounter);
        }
        return card.dataset.dcKey;
    }
    let keyCounter = 0;

    function hideHover() {
        if (!hover.hidden) {
            hover.hidden = true;
            hover.replaceChildren();
        }
    }

    // The arrows of the preview open the title before or after the current one in its row or grid: the next card with a preview of its own.
    let sheetCard = null;

    function neighbourOf(card, step) {
        let item = card.closest("li");
        while (item) {
            item = step < 0 ? item.previousElementSibling : item.nextElementSibling;
            const next = item?.querySelector("[data-dc-card]");
            if (next?.querySelector("template[data-dc-template]")) return next;
        }

        return null;
    }

    function syncSheetNav() {
        sheetContent.querySelectorAll("[data-dc-sheet-nav]").forEach(button => {
            button.hidden = !sheetCard || !neighbourOf(sheetCard, Number(button.dataset.dcSheetNav));
        });
    }

    function stepSheet(step) {
        const next = sheetCard ? neighbourOf(sheetCard, step) : null;
        if (!next) return;
        openSheet(next);
        next.scrollIntoView({ block: "nearest", inline: "center" });
    }

    function openSheet(card) {
        const content = cloneTemplate(card);
        if (!content) return false;
        hideHover();
        keyOf(card);
        sheet.dataset.for = card.dataset.dcKey;
        sheetContent.replaceChildren(content);
        sheetCard = card;
        syncSheetNav();
        activateLiveRequests(sheetContent);
        sheet.setAttribute("aria-label", card.querySelector(".dc-card-title")?.textContent?.trim() || "");
        if (typeof sheet.showModal === "function") {
            if (!sheet.open) sheet.showModal();
        } else {
            sheet.setAttribute("open", "");
        }

        // The one trailer starts only now that the sheet is open and visible, muted and without taking the focus; closing the sheet removes its frame.
        // Reduced motion keeps the facade, so the viewer starts it on purpose.
        const facade = sheetContent.querySelector("[data-vd-trailer]");
        if (facade && !reducedMotion.matches) window.JularrWorkMetadata?.startTrailer(facade, document, { muted: true });
        return true;
    }

    function closeSheet() {
        if (typeof sheet.close === "function") sheet.close();
        else sheet.removeAttribute("open");
    }

    sheet.addEventListener("close", () => {
        sheetContent.replaceChildren();
        sheetCard = null;
    });
    sheet.addEventListener("click", event => {
        const nav = event.target instanceof Element ? event.target.closest("[data-dc-sheet-nav]") : null;
        if (nav) stepSheet(Number(nav.dataset.dcSheetNav));
    });
    sheet.addEventListener("keydown", event => {
        if ((event.key !== "ArrowLeft" && event.key !== "ArrowRight") || event.altKey || event.ctrlKey || event.metaKey || /input|textarea|select/i.test(document.activeElement?.tagName || "")) return;
        event.preventDefault();
        stepSheet(event.key === "ArrowLeft" ? -1 : 1);
    });
    sheet.querySelector("[data-dc-sheet-close]")?.addEventListener("click", closeSheet);
    sheet.addEventListener("click", event => {
        if (event.target === sheet) closeSheet();
    });

    // Click: a card opens the canonical Detail on every pointer, so a link just navigates. A title whose Work is created when it is opened asks the
    // server for it first; a title that only exists at a provider has no page here and opens the Quick View, as does the Quick View button itself.
    body.addEventListener("click", event => {
        const target = event.target instanceof Element ? event.target : null;
        const card = target?.closest("[data-dc-card]");
        if (!target || !card) return;

        if (target.closest("[data-dc-preview]")) {
            openSheet(card);
            return;
        }

        const activate = target.closest("[data-dc-activate]");
        if (!activate) return;
        if (activate.dataset.dcActivate === "preview") {
            openSheet(card);
        } else {
            void openDetail(card.dataset.dcCategory, card.dataset.dcProvider, card.dataset.dcExternalId, card, () => openSheet(card));
        }
    });

    // The server resolves the identity to its canonical Work (creating it once) and names its Detail. A refusal or a failure is never a dead end:
    // the card falls back to its Quick View, the Details button of the Quick View reports it.
    async function openDetail(category, provider, externalId, busy, onFailure) {
        if (busy.getAttribute("aria-busy") === "true") return;
        busy.setAttribute("aria-busy", "true");
        try {
            const payload = await postForm(root.dataset.openUrl, { category, provider, externalId });
            window.location.assign(payload.url);
        } catch {
            busy.removeAttribute("aria-busy");
            onFailure();
        }
    }

    // ---- Preview actions ---------------------------------------------------------------------------

    async function postForm(url, fields) {
        const data = new FormData();
        Object.entries(fields).forEach(([name, value]) => {
            if (value !== null && value !== undefined && value !== "") data.set(name, String(value));
        });
        data.set("__RequestVerificationToken", token);
        const response = await fetch(url, {
            method: "POST",
            body: data,
            credentials: "same-origin",
            headers: { Accept: "application/json" }
        });
        if (!response.ok) throw new Error(String(response.status));
        return response.json();
    }

    const requestedIcon =
        "<svg class=\"dc-icon\" viewBox=\"0 0 24 24\" width=\"16\" height=\"16\" aria-hidden=\"true\" focusable=\"false\" " +
        "fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" +
        "<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"M12 7.5V12l3 2\"/></svg>";

    function showRequested(scope, label) {
        scope.querySelectorAll(".dc-state").forEach(state => {
            state.className = "dc-state dc-state-requested";
            state.removeAttribute("title");
            state.innerHTML = requestedIcon;
            const span = document.createElement("span");
            span.textContent = label;
            state.append(span);
        });
    }

    function showFollowState(scope, followed) {
        const button = scope.querySelector("[data-dc-follow]");
        if (!button) return;
        button.setAttribute("aria-pressed", String(followed));
        button.title = followed ? text("textUnfollow") : "";
        const label = button.querySelector("[data-dc-follow-label]");
        if (label) label.textContent = followed ? text("textFollowed") : text("textFollow");
    }

    function showFranchise(scope, franchiseId) {
        const button = scope.querySelector("[data-dc-follow-franchise]");
        if (!button || !franchiseId) return;
        const link = document.createElement("a");
        link.className = "button";
        link.href = `/Franchises/${franchiseId}`;
        link.textContent = text("textFranchiseFollowed");
        button.replaceWith(link);
    }

    const requestPollers = new Map();

    function requestProgressLabel(payload) {
        const stage = statusText(payload.status || "pending");
        const progress = Number.isFinite(payload.progress) ? Math.max(0, Math.min(100, payload.progress)) : null;
        return payload.done || progress === null ? stage : `${stage} · ${progress}%`;
    }

    function renderRequestSlot(slot, payload) {
        if (!slot) return;
        // Only a percentage the server read from the transfer is ever shown; without one the ring is an empty outline.
        const progress = Number.isFinite(payload.progress) ? Math.max(0, Math.min(100, payload.progress)) : null;
        slot.dataset.dcLiveRequest = payload.requestId || slot.dataset.dcLiveRequest || "";
        slot.dataset.dcLiveStatus = payload.status || "";

        if (payload.status === "completed" && payload.resultUrl) {
            const link = document.createElement("a");
            link.className = "button button-primary dc-request-open";
            link.href = payload.resultUrl;
            link.textContent = text("textOpen");
            slot.replaceChildren(link);
            return;
        }

        const live = document.createElement("span");
        live.className = `button button-primary dc-request-live request-status-${payload.status || "pending"}`;
        live.setAttribute("role", "status");

        const ring = document.createElement("span");
        ring.className = "dc-request-ring";
        ring.style.setProperty("--dc-progress", `${progress ?? 0}%`);
        if (progress !== null) {
            const number = document.createElement("span");
            number.textContent = `${Math.round(progress)}%`;
            ring.append(number);
        }

        const label = document.createElement("span");
        label.className = "dc-request-live-label";
        label.textContent = statusText(payload.status || "pending");

        live.append(ring, label);
        slot.replaceChildren(live);
    }

    function updateRequestEverywhere(payload) {
        const id = payload.requestId;
        if (!id) return;

        root.querySelectorAll(`[data-dc-live-request="${id}"]`).forEach(slot =>
            renderRequestSlot(slot, payload));

        body.querySelectorAll(`[data-dc-request-id="${id}"]`).forEach(card => {
            card.dataset.dcRequestStatus = payload.status || "";
            showRequested(card, requestProgressLabel(payload));
        });
    }

    async function fetchRequestProgress(requestId) {
        const url = new URL(root.dataset.requestStatusUrl, window.location.origin);
        url.searchParams.set("id", requestId);
        const response = await fetch(url.pathname + url.search, {
            cache: "no-store",
            credentials: "same-origin",
            headers: { Accept: "application/json" }
        });
        if (!response.ok) {
            const error = new Error(String(response.status));
            error.retryAfterMs = (Number(response.headers.get("Retry-After")) || 0) * 1000;
            throw error;
        }

        return response.json();
    }

    // Only this many requested titles are watched at a time; the others show the state they were rendered with.
    const maxWatchedRequests = 8;

    // The live state of a requested title is read soon after it changed and less often while it does not; a server that asks to be left alone is left alone.
    function pollRequest(requestId) {
        if (!requestId || requestPollers.has(requestId) || requestPollers.size >= maxWatchedRequests) return;

        let delay = 0;
        let last = "";
        const tick = async () => {
            let retryAfterMs = 0;
            let changed = false;
            try {
                const payload = await fetchRequestProgress(requestId);
                updateRequestEverywhere(payload);
                if (payload.done) {
                    requestPollers.delete(requestId);
                    return;
                }

                const signature = `${payload.status}|${payload.progress}`;
                changed = signature !== last;
                last = signature;
            } catch (error) {
                // Keep the current visible state; transient navigation/network failures may recover.
                retryAfterMs = error?.retryAfterMs || 0;
            }

            delay = staging.nextPollDelay(delay, changed, retryAfterMs);
            requestPollers.set(requestId, window.setTimeout(tick, delay));
        };

        requestPollers.set(requestId, 0);
        void tick();
    }

    function activateLiveRequests(scope) {
        // A card shows the progress of its request in its indicator, so it only needs to be watched.
        scope.querySelectorAll("[data-dc-watch-request]").forEach(card => pollRequest(card.dataset.dcWatchRequest));
        scope.querySelectorAll("[data-dc-live-request]").forEach(slot => {
            const requestId = slot.dataset.dcLiveRequest;
            if (!requestId) return;
            renderRequestSlot(slot, {
                requestId,
                status: slot.dataset.dcLiveStatus || "pending",
                progress: null,
                done: false
            });
            pollRequest(requestId);
        });
    }

    function applyOverrides(scope, change) {
        if (change.followed !== undefined) showFollowState(scope, change.followed);
        if (change.franchiseId) showFranchise(scope, change.franchiseId);
        if (change.requestId) {
            const slot = scope.querySelector("[data-dc-request-slot]");
            if (slot) {
                slot.dataset.dcLiveRequest = change.requestId;
                slot.dataset.dcLiveStatus = change.status || "pending";
                renderRequestSlot(slot, change);
                pollRequest(change.requestId);
            }
        }
    }

    function remember(scope, change) {
        const key = scope.closest("[data-dc-sheet]")?.dataset.for;
        if (key) overrides.set(key, { ...(overrides.get(key) || {}), ...change });
    }

    // The Request dialog (discover-request.js) owns submitting; the persisted request it created is brought
    // to the card behind it and to the preview of that card here.
    root.addEventListener("dc:request-created", event => {
        const { identity, payload } = event.detail;
        const card = [...body.querySelectorAll("[data-dc-card]")].find(item =>
            item.dataset.dcCategory === identity.category && item.dataset.dcExternalId === identity.externalId);
        if (!card) return;

        card.dataset.dcRequestId = payload.requestId;
        card.dataset.dcRequestStatus = payload.status;
        card.querySelector("[data-dc-card-request]")?.remove();
        showRequested(card, requestProgressLabel(payload));
        const key = keyOf(card);
        overrides.set(key, { ...(overrides.get(key) || {}), ...payload });
        pollRequest(payload.requestId);
    });

    root.addEventListener("click", async event => {
        const target = event.target instanceof Element ? event.target : null;
        if (!target) return;

        const preview = target.closest(".dc-pv");
        if (!preview) return;
        const data = preview.dataset;

        const follow = target.closest("[data-dc-follow]");
        if (follow) {
            const next = follow.getAttribute("aria-pressed") !== "true";
            follow.disabled = true;
            try {
                const payload = await postForm(root.dataset.watchlistUrl, {
                    category: data.category,
                    provider: data.provider,
                    externalId: data.externalId,
                    title: data.title,
                    nativeTitle: data.nativeTitle,
                    coverImageUrl: data.cover,
                    format: data.format,
                    status: data.status,
                    year: data.year,
                    follow: next
                });
                showFollowState(preview, payload.followed === true);
                remember(preview, { followed: payload.followed === true });
            } catch {
                follow.title = text("textActionFailed");
            } finally {
                follow.disabled = false;
            }
            return;
        }

        const open = target.closest("[data-dc-activate='open']");
        if (open) {
            void openDetail(data.category, data.provider, data.externalId, open, () => { open.title = text("textActionFailed"); });
            return;
        }

        const franchise = target.closest("[data-dc-follow-franchise]");
        if (franchise) {
            franchise.disabled = true;
            try {
                const payload = await postForm(root.dataset.franchiseUrl, {
                    category: data.category,
                    provider: data.provider,
                    externalId: data.externalId
                });
                showFranchise(preview, payload.franchiseId);
                remember(preview, { franchiseId: payload.franchiseId });
            } catch {
                franchise.title = text("textActionFailed");
                franchise.disabled = false;
            }
            return;
        }

        if (target.closest("[data-dc-import-source]")) {
            openImport(data);
        }
    });

    // ---- Owner: import a light-novel source for a discovered title ----------------------------------

    function openImport(data) {
        if (!importDetails) return;
        closeSheet();
        hideHover();
        importDetails.hidden = false;
        importDetails.open = true;
        importDetails.querySelector("[data-import-provider]").value = data.provider || "";
        importDetails.querySelector("[data-import-external-id]").value = data.externalId || "";
        importDetails.querySelector("[data-import-title]").textContent =
            text("textSourceFor").replace("{title}", data.title || "");
        importDetails.scrollIntoView({ behavior: "smooth", block: "center" });
        setTimeout(() => importDetails.querySelector("[data-import-url]")?.focus(), 250);
    }

    importDetails?.querySelector("[data-import-clear]")?.addEventListener("click", () => {
        importDetails.querySelector("[data-import-provider]").value = "";
        importDetails.querySelector("[data-import-external-id]").value = "";
        importDetails.querySelector("[data-import-title]").textContent = text("textSourceUrl");
        importDetails.querySelector("[data-import-url]")?.focus();
    });

    // ---- Offline -----------------------------------------------------------------------------------

    function syncOnline() {
        offlineNotice.hidden = navigator.onLine;
        if (navigator.onLine && loadFailed) loadBody();
    }

    window.addEventListener("online", syncOnline);
    window.addEventListener("offline", syncOnline);

    // Coming back through the history restores the page as it was.
    window.addEventListener("pageshow", event => {
        if (event.persisted) syncTabs();
    });

    syncTabs();
    offlineNotice.hidden = navigator.onLine;

    // The first response already carries the titles when the server had an answer for them (fresh, stale or from its local snapshot): nothing is fetched
    // again, the browser only asks for what is renewed since, and a renewed row replaces the old one when it is safe to do so.
    const rendered = bodyRoot();
    if (rendered) {
        activateLiveRequests(body);
        controller.reset(viewOf(rendered));
        generation = generationOf(rendered);
        void followUp.run(requestVersion);
    } else {
        loadBody();
    }
})();
