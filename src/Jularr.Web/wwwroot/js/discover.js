(() => {
    "use strict";

    const root = document.querySelector("[data-discover]");
    if (!root) return;

    const form = root.querySelector("[data-dc-form]");
    const searchInput = root.querySelector("[data-dc-search]");
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

    const touchFirst = window.matchMedia("(hover: none), (pointer: coarse)");
    const SEARCH_DELAY = 250;

    let abortController = null;
    let requestVersion = 0;
    let debounceTimer = null;
    let loadFailed = false;
    // Changes the visitor made in a preview (follow, request), applied again when a card's preview reopens.
    const overrides = new Map();

    // ---- Address: the only state --------------------------------------------------------------

    // The canonical address of the form: defaults and empty fields are left out, so a bookmark stays short.
    function currentParams() {
        const data = new FormData(form);
        const params = new URLSearchParams();
        const value = name => String(data.get(name) || "").trim();
        if (value("q")) params.set("q", value("q"));
        if (value("category") && value("category") !== "all") params.set("category", value("category"));
        if (value("mode") && value("mode") !== "trending") params.set("mode", value("mode"));
        ["genre", "year", "status", "avail"].forEach(name => {
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
        const browseGroup = root.querySelector("[data-dc-browse-group]");
        if (browseGroup) browseGroup.hidden = q.length > 0;
    }

    // ---- Body: rows or results, fetched after first paint --------------------------------------

    async function loadBody() {
        clearTimeout(debounceTimer);
        abortController?.abort();
        abortController = new AbortController();
        const version = ++requestVersion;
        const params = new URLSearchParams(window.location.search);
        params.set("handler", "Body");

        body.setAttribute("aria-busy", "true");
        errorBox.hidden = true;
        try {
            const response = await fetch(`${window.location.pathname}?${params}`, {
                signal: abortController.signal,
                cache: "no-store",
                headers: { "X-Requested-With": "fetch" }
            });
            if (version !== requestVersion) return;

            const html = await response.text();
            // A failed body still renders its own unavailable state; anything else is a plain failure.
            if (!response.ok && !html.includes("data-dc-state")) {
                throw new Error(String(response.status));
            }

            hideHover();
            // Same-origin, server-rendered and HTML-encoded by Razor; injected as the page body.
            body.innerHTML = html;
            activateLiveRequests(body);
            loadFailed = false;
        } catch (error) {
            if (error?.name === "AbortError" || version !== requestVersion) return;
            loadFailed = true;
            body.replaceChildren();
            errorBox.hidden = false;
        } finally {
            if (version === requestVersion) body.setAttribute("aria-busy", "false");
        }
    }

    function scheduleSearch() {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            window.history.replaceState({}, "", addressOf(currentParams()));
            syncTabs();
            loadBody();
        }, SEARCH_DELAY);
    }

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

    function openSheet(card) {
        const content = cloneTemplate(card);
        if (!content) return false;
        hideHover();
        keyOf(card);
        sheet.dataset.for = card.dataset.dcKey;
        sheetContent.replaceChildren(content);
        activateLiveRequests(sheetContent);
        sheet.setAttribute("aria-label", card.querySelector(".dc-card-title")?.textContent?.trim() || "");
        if (typeof sheet.showModal === "function") {
            if (!sheet.open) sheet.showModal();
        } else {
            sheet.setAttribute("open", "");
        }
        return true;
    }

    function closeSheet() {
        if (typeof sheet.close === "function") sheet.close();
        else sheet.removeAttribute("open");
    }

    sheet.addEventListener("close", () => sheetContent.replaceChildren());
    sheet.querySelector("[data-dc-sheet-close]")?.addEventListener("click", closeSheet);
    sheet.addEventListener("click", event => {
        if (event.target === sheet) closeSheet();
    });

    // Click: the preview button always opens the sheet. A card that is not in the library opens it too, since
    // its only page is the provider's; a library card opens its page, except on touch where the sheet comes first.
    body.addEventListener("click", event => {
        const target = event.target instanceof Element ? event.target : null;
        const card = target?.closest("[data-dc-card]");
        if (!target || !card) return;

        if (target.closest("[data-dc-preview]")) {
            openSheet(card);
            return;
        }

        const link = target.closest("a");
        if (!link || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        if (link.closest(".dc-card-body, .dc-art") && (touchFirst.matches || !card.classList.contains("is-local"))) {
            if (openSheet(card)) event.preventDefault();
        }
    });

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
        const progress = Number.isFinite(payload.progress) ? Math.max(0, Math.min(100, payload.progress)) : 0;
        return payload.done || progress <= 0 ? stage : `${stage} · ${progress}%`;
    }

    function defaultProgress(status) {
        return {
            pending: 0,
            approved: 5,
            searching: 15,
            downloading: 35,
            importing: 90,
            completed: 100,
            rejected: 100,
            failed: 100
        }[status] ?? 0;
    }

    function renderRequestSlot(slot, payload) {
        if (!slot) return;
        const progress = Number.isFinite(payload.progress)
            ? Math.max(0, Math.min(100, payload.progress))
            : defaultProgress(payload.status);
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
        ring.style.setProperty("--dc-progress", `${progress}%`);
        const number = document.createElement("span");
        number.textContent = `${Math.round(progress)}%`;
        ring.append(number);

        const label = document.createElement("span");
        label.className = "dc-request-live-label";
        label.textContent = statusText(payload.status || "pending");

        live.append(ring, label);
        if (payload.message) live.title = payload.message;
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
        if (!response.ok) throw new Error(String(response.status));
        return response.json();
    }

    function pollRequest(requestId) {
        if (!requestId || requestPollers.has(requestId)) return;

        const tick = async () => {
            try {
                const payload = await fetchRequestProgress(requestId);
                updateRequestEverywhere(payload);
                if (payload.done) {
                    requestPollers.delete(requestId);
                    return;
                }
            } catch {
                // Keep the current visible state; transient navigation/network failures may recover.
            }

            const timer = window.setTimeout(tick, 1500);
            requestPollers.set(requestId, timer);
        };

        requestPollers.set(requestId, 0);
        void tick();
    }

    function activateLiveRequests(scope) {
        scope.querySelectorAll("[data-dc-live-request]").forEach(slot => {
            const requestId = slot.dataset.dcLiveRequest;
            if (!requestId) return;
            renderRequestSlot(slot, {
                requestId,
                status: slot.dataset.dcLiveStatus || "pending",
                progress: defaultProgress(slot.dataset.dcLiveStatus || "pending"),
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
        const slot = card.querySelector("[data-dc-card-request-slot]");
        if (slot) {
            slot.dataset.dcLiveRequest = payload.requestId;
            slot.dataset.dcLiveStatus = payload.status;
            renderRequestSlot(slot, payload);
        }

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
    loadBody();
})();
