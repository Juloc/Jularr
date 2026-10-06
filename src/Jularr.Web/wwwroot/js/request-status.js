// The request status surface (docs/mockups/request-status-details/SPEC.md). On My requests a row opens the server-rendered status
// panel of its request in a dialog; on the deep link page the same panel is part of the page. Either way the panel is replaced in place
// after an action (Cancel request with its confirmation, Retry request, a saved edit of the shared Request dialog): the server decides
// what is allowed and answers with the panel as it is now, so nothing here knows a rule. Closing the dialog after a change reloads the
// list behind it, so its row and counts are current.
(() => {
    "use strict";

    const host = document.querySelector("[data-request-host]");
    if (!host) return;

    const dialog = host.querySelector("[data-rqs-dialog]");
    const body = dialog?.querySelector("[data-rqs-dialog-body]");
    const text = name => (dialog ?? host).dataset[`text${name.charAt(0).toUpperCase()}${name.slice(1)}`] || host.dataset[`text${name.charAt(0).toUpperCase()}${name.slice(1)}`] || "";
    const narrow = window.matchMedia("(max-width: 760px)");
    const timeoutMs = 15000;

    const focusKey = "rqs-focus";
    let opener = null;
    let changed = false;

    const element = (tag, className, content) => {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (content !== undefined) node.textContent = content;
        return node;
    };

    function prepare(panel) {
        // Mobile keeps the current state visible and the saved details folded away until asked for.
        if (narrow.matches) panel.querySelector("[data-rqs-details]")?.removeAttribute("open");
        window.JularrLocalTime?.apply(panel);
    }

    function showFailure(container, message, retry) {
        container.replaceChildren();
        const paragraph = element("p", "dc-rq-error", message);
        paragraph.setAttribute("role", "alert");
        container.append(paragraph);
        const actions = element("div", "rqs-failure-actions");
        if (retry) {
            const button = element("button", "button", text("retry"));
            button.type = "button";
            button.addEventListener("click", retry);
            actions.append(button);
        }

        const close = element("button", "button", text("close"));
        close.type = "button";
        close.addEventListener("click", () => dialog.close());
        actions.append(close);
        container.append(actions);
    }

    async function fetchPanel(url, init) {
        const response = await fetch(url, {
            credentials: "same-origin",
            headers: { Accept: "text/html" },
            signal: AbortSignal.timeout(timeoutMs),
            ...init
        });
        if (!response.ok) throw Object.assign(new Error("panel"), { status: response.status });

        // Same-origin, server-rendered and HTML-encoded by Razor.
        const view = document.createElement("div");
        view.innerHTML = await response.text();
        const panel = view.querySelector("[data-rqs-panel]");
        if (!panel) throw new Error("panel");
        return panel;
    }

    function swap(current, next, focus) {
        current.replaceWith(next);
        prepare(next);
        if (focus) next.querySelector("[data-rqs-title]")?.focus();
    }

    // ---- The dialog of My requests ------------------------------------------------------------------------

    async function openPanel(url) {
        body.replaceChildren(element("p", "rqs-loading", text("loading")));
        try {
            const panel = await fetchPanel(url);
            body.replaceChildren(panel);
            prepare(panel);
            panel.querySelector("[data-rqs-title]")?.focus();
        } catch (error) {
            showFailure(body, text("loadFailed"), error?.status === 404 ? null : () => openPanel(url));
        }
    }

    if (dialog) {
        host.addEventListener("click", event => {
            const link = event.target instanceof Element ? event.target.closest("a[data-rqs-open]") : null;
            if (!link || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;

            event.preventDefault();
            opener = link;
            changed = false;
            dialog.setAttribute("aria-label", text("label").replace("{title}", () => link.textContent.trim()));
            dialog.showModal();
            openPanel(link.dataset.panelUrl);
        });

        dialog.addEventListener("click", event => {
            if (event.target === dialog) dialog.close();
        });

        dialog.addEventListener("close", () => {
            body.replaceChildren();
            if (changed) {
                // The list behind is current after the reload; focus goes back to the row that was opened.
                if (opener?.dataset.panelUrl) sessionStorage.setItem(focusKey, opener.dataset.panelUrl);
                location.reload();
                return;
            }

            opener?.focus();
            opener = null;
        });
    }

    // ---- The panel: Cancel request, Retry request, close -------------------------------------------------

    host.addEventListener("click", event => {
        const target = event.target instanceof Element ? event.target : null;
        const panel = target?.closest("[data-rqs-panel]");
        if (!panel) return;

        if (target.closest("[data-rqs-close]")) {
            dialog?.close();
        } else if (target.closest("[data-rqs-cancel]")) {
            const confirm = panel.querySelector("[data-rqs-confirm]");
            confirm?.showModal();
            confirm?.querySelector("h3")?.focus();
        } else if (target.closest("[data-rqs-confirm-close]")) {
            panel.querySelector("[data-rqs-confirm]")?.close();
        }
    });

    host.addEventListener("click", event => {
        if (event.target instanceof HTMLDialogElement && event.target.matches("[data-rqs-confirm]")) event.target.close();
    });

    host.addEventListener("submit", async event => {
        const form = event.target instanceof Element ? event.target.closest("form[data-rqs-action]") : null;
        const panel = form?.closest("[data-rqs-panel]");
        if (!form || !panel) return;

        event.preventDefault();
        const buttons = [...panel.querySelectorAll("button")];
        for (const button of buttons) button.disabled = true;
        try {
            const url = new URL(form.action, location.href);
            url.searchParams.set("panel", "true");
            const next = await fetchPanel(url, { method: "POST", body: new FormData(form) });
            changed = true;
            swap(panel, next, true);
        } catch {
            for (const button of buttons) button.disabled = false;
            panel.querySelector("[data-rqs-confirm]")?.close();
            panel.querySelector(".dc-rq-error")?.remove();
            const message = element("p", "dc-rq-error", text("failed"));
            message.setAttribute("role", "alert");
            panel.querySelector(".rqs-body")?.prepend(message);
        }
    });

    // A saved edit of the shared Request dialog changed the request: show the panel as it is now.
    host.addEventListener("dc:request-created", async event => {
        const current = host.querySelector("[data-rqs-panel]");
        if (!current || event.detail?.payload?.requestId !== current.dataset.requestId) return;

        try {
            changed = true;
            swap(current, await fetchPanel(current.dataset.panelUrl), false);
        } catch {
            // The dialog behind keeps its last state; reopening the request reads it again.
        }
    });

    // The edit dialog opened from the panel is gone; focus returns to the (refreshed) panel's heading.
    host.addEventListener("dc:request-dialog-closed", event => {
        if (event.detail?.identity?.edit) host.querySelector("[data-rqs-panel] [data-rqs-title]")?.focus();
    });

    for (const panel of host.querySelectorAll("[data-rqs-panel]")) prepare(panel);

    const refocus = sessionStorage.getItem(focusKey);
    if (refocus) {
        sessionStorage.removeItem(focusKey);
        [...host.querySelectorAll("a[data-rqs-open]")].find(link => link.dataset.panelUrl === refocus)?.focus();
    }
})();
