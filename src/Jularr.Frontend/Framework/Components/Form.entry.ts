// Refresh dependent fields from the server without saving the draft. The button remains usable without JavaScript.
function enhance(form: HTMLFormElement): void {
    if (form.dataset.refreshReady || !form.querySelector('[data-form-refresh]')) return;
    form.dataset.refreshReady = "true";
    form.querySelectorAll<HTMLButtonElement>('[data-form-refresh-submit]').forEach(button => { button.hidden = true; });
    let pending: AbortController | undefined;
    form.addEventListener("change", async event => {
        const field = event.target;
        if (!(field instanceof HTMLSelectElement || field instanceof HTMLInputElement) || !field.dataset.formRefresh) return;
        const action = new URL(field.dataset.formRefresh, location.href);
        if (action.origin !== location.origin) return;
        pending?.abort();
        const controller = pending = new AbortController();
        const values = new FormData(form);
        form.setAttribute("aria-busy", "true");
        try {
            const response = await fetch(action, { method: "POST", body: values, credentials: "same-origin", signal: controller.signal,
                headers: { Accept: "text/html" } });
            if (!response.headers.get("Content-Type")?.includes("text/html")) throw new Error("Expected a form.");
            const page = new DOMParser().parseFromString(await response.text(), "text/html");
            const replacement = page.querySelector<HTMLFormElement>(`form[data-form-kind="${CSS.escape(form.dataset.formKind ?? "")}"]`);
            if (!replacement) throw new Error("Expected a matching form.");
            if (controller.signal.aborted) return;
            form.replaceWith(replacement);
            enhance(replacement);
            replacement.querySelector<HTMLElement>(`[name="${CSS.escape(field.name)}"]`)?.focus({ preventScroll: true });
        } catch {
            if (!controller.signal.aborted) {
                const button = Array.from(form.querySelectorAll<HTMLButtonElement>('[data-form-refresh-submit]'))
                    .find(item => item.formAction === action.href);
                if (button) form.requestSubmit(button);
            }
        } finally {
            if (!controller.signal.aborted) form.removeAttribute("aria-busy");
        }
    });
}

document.querySelectorAll<HTMLFormElement>('form').forEach(enhance);
