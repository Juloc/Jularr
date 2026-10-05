// The one Request dialog of Discover (docs/mockups/add-request-flow/SPEC.md). A card or preview button opens it with
// the identity of the picked title; the server resolves that identity and returns only the settings groups its media
// kind has (Scope + Included content for series, language for anime). Submitting shows the success state in place and
// tells discover.js (dc:request-created) so the card behind the dialog shows the persisted request.
(() => {
    "use strict";

    const root = document.querySelector("[data-discover]");
    const dialog = root?.querySelector("[data-dc-rq]");
    if (!dialog) return;

    const form = dialog.querySelector("[data-dc-rq-form]");
    const title = dialog.querySelector("[data-dc-rq-title]");
    const meta = dialog.querySelector("[data-dc-rq-meta]");
    const cover = dialog.querySelector("[data-dc-rq-cover]");
    const body = dialog.querySelector("[data-dc-rq-body]");
    const foot = dialog.querySelector("[data-dc-rq-foot]");
    const submit = dialog.querySelector("[data-dc-rq-submit]");
    const token = root.querySelector("input[name='__RequestVerificationToken']")?.value || "";
    const text = name => dialog.dataset[`text${name.charAt(0).toUpperCase()}${name.slice(1)}`] || "";

    let identity = null;
    let opener = null;
    let loading = null;
    let submitting = false;

    const element = (tag, className, content) => {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (content !== undefined) node.textContent = content;
        return node;
    };

    function identityOf(trigger) {
        const preview = trigger.closest(".dc-pv");
        if (preview) {
            const data = preview.dataset;
            return {
                category: data.category,
                provider: data.provider,
                externalId: data.externalId,
                title: data.title,
                subtitle: data.nativeTitle,
                author: data.author,
                cover: data.cover,
                meta: data.subtitle
            };
        }

        const card = trigger.closest("[data-dc-card]");
        const data = card.dataset;
        return {
            category: data.dcCategory,
            provider: data.dcProvider,
            externalId: data.dcExternalId,
            title: data.dcTitle,
            subtitle: data.dcSubtitle,
            author: data.dcAuthor,
            cover: data.dcCover,
            meta: card.querySelector(".dc-card-meta")?.textContent.trim()
        };
    }

    function identityFields() {
        const data = new FormData();
        for (const [name, value] of Object.entries({
            category: identity.category,
            provider: identity.provider,
            externalId: identity.externalId,
            title: identity.title,
            subtitle: identity.subtitle,
            author: identity.author,
            coverImageUrl: identity.cover
        })) {
            if (value) data.set(name, value);
        }

        data.set("__RequestVerificationToken", token);
        return data;
    }

    function showIdentity() {
        title.textContent = identity.title;
        meta.textContent = identity.meta || "";
        dialog.setAttribute("aria-label", text("label").replace("{title}", () => identity.title));
        cover.replaceChildren();
        if (/^https?:\/\//i.test(identity.cover || "")) {
            const image = document.createElement("img");
            image.alt = "";
            image.decoding = "async";
            image.referrerPolicy = "no-referrer";
            image.src = identity.cover;
            cover.append(image);
        } else {
            cover.append(element("span", "dc-rq-initial", (identity.title || "?").charAt(0).toUpperCase()));
        }
    }

    function showMessage(message, retry) {
        body.replaceChildren();
        const paragraph = element("p", "dc-rq-error", message);
        paragraph.setAttribute("role", "alert");
        body.append(paragraph);
        if (retry) {
            const button = element("button", "button", text("retry"));
            button.type = "button";
            button.dataset.dcRqRetry = "";
            body.append(button);
        }
    }

    // A failed submit keeps every setting; the message sits above them and Request stays available.
    function showSubmitError(message) {
        body.querySelector(".dc-rq-error")?.remove();
        const paragraph = element("p", "dc-rq-error", message);
        paragraph.setAttribute("role", "alert");
        body.prepend(paragraph);
    }

    async function load() {
        loading?.abort();
        loading = new AbortController();
        const signal = loading.signal;
        body.setAttribute("aria-busy", "true");
        body.replaceChildren(element("p", "dc-rq-loading", text("loading")));
        submit.disabled = true;

        try {
            const response = await fetch(root.dataset.resolveUrl, {
                method: "POST",
                body: identityFields(),
                credentials: "same-origin",
                headers: { Accept: "text/html" },
                signal
            });
            if (response.status === 403 || response.status === 404) {
                showMessage(text("forbidden"), false);
                return;
            }

            if (!response.ok) {
                showMessage(text("loadFailed"), response.status !== 400);
                return;
            }

            // Same-origin, server-rendered and HTML-encoded by Razor; the settings of the dialog.
            body.innerHTML = await response.text();
            prepare();
        } catch (error) {
            if (error?.name === "AbortError") return;
            showMessage(text("loadFailed"), true);
        } finally {
            if (!signal.aborted) body.setAttribute("aria-busy", "false");
        }
    }

    // ---- Scope and Included content: one control, a derived tree ---------------------------------------

    const scopeSelect = () => body.querySelector("[data-dc-rq-scope]");
    const futureBox = () => body.querySelector("[data-dc-rq-future]");
    const seasonRows = () => [...body.querySelectorAll("[data-dc-rq-season]:not(.dc-rq-future)")];
    const episodeBoxes = row => [...row.querySelectorAll("[data-dc-rq-episode]")];

    function prepare() {
        const existing = body.querySelector("[data-dc-rq-existing]");
        if (existing) {
            // The title already has an open request: show it instead of creating a second one.
            submit.hidden = true;
            notify({ requestId: existing.dataset.requestId, status: existing.dataset.status, done: false });
            return;
        }

        submit.hidden = false;
        if (scopeSelect()) applyScope(scopeSelect().value);
        refreshSubmit();
    }

    function applyScope(value) {
        const all = value === "all";
        if (value !== "custom") {
            for (const row of seasonRows()) {
                const check = row.querySelector("[data-dc-rq-season-check]");
                check.checked = all;
                check.indeterminate = false;
                for (const box of episodeBoxes(row)) box.checked = all;
            }

            futureBox().checked = true;
        }

        body.querySelector("[data-dc-rq-scope-hint]").textContent = text(`scope${value.charAt(0).toUpperCase()}${value.slice(1)}`);
        refreshCount();
        refreshSubmit();
    }

    function refreshCount() {
        const count = body.querySelector("[data-dc-rq-count]");
        if (!count) return;
        const boxes = seasonRows().flatMap(episodeBoxes);
        count.hidden = boxes.length === 0;
        count.textContent = text("selected")
            .replace("{selected}", String(boxes.filter(box => box.checked).length))
            .replace("{total}", String(boxes.length));
    }

    function refreshSubmit() {
        const select = scopeSelect();
        const empty = select?.value === "custom"
            && !futureBox().checked
            && !seasonRows().some(row => row.querySelector("[data-dc-rq-season-check]").checked || row.querySelector("[data-dc-rq-season-check]").indeterminate);
        submit.disabled = submitting || empty;
    }

    // Any manual change of the tree makes the Scope Custom; parents and children stay consistent.
    function treeChanged(box) {
        const row = box.closest("[data-dc-rq-season]");
        const check = row.querySelector("[data-dc-rq-season-check]");
        if (box === check) {
            check.indeterminate = false;
            for (const episode of episodeBoxes(row)) episode.checked = check.checked;
        } else if (box.matches("[data-dc-rq-episode]")) {
            const episodes = episodeBoxes(row);
            const checked = episodes.filter(episode => episode.checked).length;
            check.checked = checked === episodes.length;
            check.indeterminate = checked > 0 && checked < episodes.length;
        }

        const select = scopeSelect();
        if (select.value !== "custom") {
            select.value = "custom";
            body.querySelector("[data-dc-rq-scope-hint]").textContent = text("scopeCustom");
        }

        refreshCount();
        refreshSubmit();
    }

    body.addEventListener("change", event => {
        const target = event.target;
        if (target.matches("[data-dc-rq-scope]")) {
            applyScope(target.value);
        } else if (target.matches("[data-dc-rq-season-check], [data-dc-rq-episode], [data-dc-rq-future]")) {
            treeChanged(target);
        }
    });

    body.addEventListener("click", event => {
        const target = event.target instanceof Element ? event.target : null;
        const expand = target?.closest("[data-dc-rq-expand]");
        if (expand) {
            const open = expand.getAttribute("aria-expanded") !== "true";
            expand.setAttribute("aria-expanded", String(open));
            document.getElementById(expand.getAttribute("aria-controls")).hidden = !open;
        } else if (target?.closest("[data-dc-rq-retry]")) {
            load();
        } else if (target?.closest("[data-dc-rq-close]")) {
            dialog.close();
        }
    });

    // ---- Submit -------------------------------------------------------------------------------------------

    function submissionFields() {
        const data = identityFields();
        const select = scopeSelect();
        if (select) {
            data.set("scope", select.value);
            if (select.value === "custom") {
                for (const row of seasonRows()) {
                    const check = row.querySelector("[data-dc-rq-season-check]");
                    const seasonId = row.dataset.seasonId;
                    if (seasonId && check.checked && !check.indeterminate) {
                        data.append("seasonIds", seasonId);
                    } else {
                        for (const box of episodeBoxes(row)) {
                            if (box.checked) data.append("episodeIds", box.value);
                        }
                    }
                }

                if (futureBox().checked) data.set("monitorFuture", "true");
            }
        }

        for (const name of ["audio", "subtitles"]) {
            const value = body.querySelector(`[name='${name}']`)?.value;
            if (value) data.set(name, value);
        }

        return data;
    }

    function notify(payload, owner = identity) {
        root.dispatchEvent(new CustomEvent("dc:request-created", { detail: { identity: owner, payload } }));
    }

    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (submitting || submit.disabled) return;

        // The dialog may be closed and reopened for another title while the request is in flight; only the title
        // that was submitted gets its card updated, and the dialog body is only touched while it still shows it.
        const current = identity;
        submitting = true;
        submit.disabled = true;
        body.querySelector(".dc-rq-error")?.remove();
        try {
            const response = await fetch(root.dataset.requestUrl, {
                method: "POST",
                body: submissionFields(),
                credentials: "same-origin",
                headers: { Accept: "text/html" }
            });
            if (response.status === 403 || response.status === 404) {
                if (identity === current) {
                    showMessage(text("forbidden"), false);
                    submit.hidden = true;
                }

                return;
            }

            if (!response.ok) {
                if (identity === current) showSubmitError(response.status === 400 ? text("invalid") : text("failed"));
                return;
            }

            const markup = await response.text();
            const view = document.createElement("div");
            view.innerHTML = markup;
            const result = view.querySelector("[data-dc-rq-result]");
            notify({
                requestId: result.dataset.requestId,
                status: result.dataset.status,
                progress: Number(result.dataset.progress),
                message: result.dataset.message || null,
                resultUrl: result.dataset.resultUrl || null,
                done: result.dataset.done === "true"
            }, current);
            if (identity === current) {
                body.replaceChildren(...view.childNodes);
                foot.hidden = true;
                body.querySelector("#dc-rq-result-title")?.focus();
            }
        } catch {
            if (identity === current) showSubmitError(text("failed"));
        } finally {
            if (identity === current) {
                submitting = false;
                refreshSubmit();
            }
        }
    });

    // ---- Open and close -------------------------------------------------------------------------------------

    root.addEventListener("click", event => {
        const trigger = event.target instanceof Element ? event.target.closest("[data-dc-card-request], [data-dc-request]") : null;
        if (!trigger) return;

        event.preventDefault();
        identity = identityOf(trigger);
        submitting = false;
        opener = trigger.closest(".dc-pv") ? null : trigger;
        // The preview sheet gives way to the dialog; closing the dialog returns to the card.
        root.querySelector("[data-dc-sheet]")?.close();
        foot.hidden = false;
        submit.hidden = false;
        showIdentity();
        dialog.showModal();
        title.focus();
        load();
    });

    for (const closer of dialog.querySelectorAll("[data-dc-rq-close]")) {
        closer.addEventListener("click", () => dialog.close());
    }

    dialog.addEventListener("click", event => {
        if (event.target === dialog) dialog.close();
    });

    dialog.addEventListener("close", () => {
        loading?.abort();
        body.replaceChildren();
        const card = [...root.querySelectorAll("[data-dc-card]")].find(item =>
            item.dataset.dcCategory === identity?.category && item.dataset.dcExternalId === identity?.externalId);
        // A button of the preview sheet is gone by now, as is a card button replaced by the live request state.
        const target = opener?.isConnected ? opener : card?.querySelector("[data-dc-title-link]");
        target?.focus();
        opener = null;
    });
})();
