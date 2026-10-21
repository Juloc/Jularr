import { inserted, isRefreshing, refresh, visit } from "../../Modules/Navigation/PageSwap";

// Forms declared with Form.Save are saved together by the page's pill. A form differs from the loaded page by the values it
// would send; saving posts every changed form in turn, and a failing one keeps its answer with the error while the others save.

type Values = Map<string, string[]>;
const loaded = new WeakMap<HTMLFormElement, Values>();
// A form shown again as a draft (a row added, a choice applied) is unsaved even where it equals what it shows.
const drafts = new WeakSet<HTMLFormElement>();
let draftIndex: number | undefined;
let changes = 0;
let saving = false;

const pill = () => document.querySelector<HTMLElement>("[data-save-pill]");
const forms = () => Array.from(document.querySelectorAll<HTMLFormElement>("#app-content form[data-save]"));

function valuesOf(form: HTMLFormElement): Values {
    const values: Values = new Map();
    new FormData(form).forEach((value, name) => {
        if (name !== "__RequestVerificationToken") values.set(name, [...values.get(name) ?? [], typeof value === "string" ? value : value.name]);
    });
    // Boxes and switches that share a name are a set: the order they stand in is not a change.
    for (const [name, list] of values) {
        if (form.querySelector(`input[type=checkbox][name="${CSS.escape(name)}"]`)) values.set(name, [...list].sort());
    }
    return values;
}

function changedNames(form: HTMLFormElement): string[] {
    const before = loaded.get(form);
    if (!before) return [];
    const now = valuesOf(form);
    const names = [...new Set([...before.keys(), ...now.keys()])].filter(name => (before.get(name) ?? []).join("\u0000") !== (now.get(name) ?? []).join("\u0000"));
    return names.length === 0 && drafts.has(form) ? [""] : names;
}

export function initializeSavePill(): void {
    for (const form of forms()) loaded.set(form, valuesOf(form));
    const draft = draftIndex === undefined ? undefined : forms()[draftIndex];
    if (draft) drafts.add(draft);
    draftIndex = undefined;
    document.documentElement.toggleAttribute("data-save-ready", true);
    changes = 0;
    update();
}

function update(failed = 0): void {
    const element = pill();
    if (!element) return;
    let count = 0;
    document.querySelectorAll(".ui-field--changed").forEach(field => field.classList.remove("ui-field--changed"));
    for (const form of forms()) {
        for (const name of changedNames(form)) {
            count++;
            form.querySelectorAll(`[name="${CSS.escape(name)}"]`).forEach(control => control.closest(".ui-field")?.classList.add("ui-field--changed"));
        }
    }

    const label = element.querySelector<HTMLElement>(".save-pill__count")!;
    element.classList.toggle("save-pill--failed", failed > 0);
    label.textContent = failed > 0 ? text(label.dataset.failed, failed) : count === 1 ? label.dataset.one ?? "" : text(label.dataset.other, count);
    element.hidden = count === 0 && failed === 0;
    if (count > changes) {
        element.classList.remove("save-pill--bump");
        void element.offsetWidth;
        element.classList.add("save-pill--bump");
    }
    changes = count;
}

const text = (template: string | undefined, count: number) => (template ?? "").replace("{0}", count.toString());

async function save(): Promise<void> {
    const element = pill();
    if (!element || saving) return;
    saving = true;
    element.setAttribute("aria-busy", "true");
    const pending = forms().map((form, index) => ({ form, index })).filter(({ form }) => changedNames(form).length > 0);
    const failures: { index: number; form: HTMLFormElement; baseline: Values }[] = [];
    // A save that leads elsewhere (a new item's own page, the next setup step) is followed once everything saved.
    let destination: string | undefined;
    try {
        for (const { form, index } of pending) {
            const baseline = loaded.get(form)!;
            try {
                const response = await fetch(form.action, { method: "POST", body: new FormData(form), credentials: "same-origin", headers: { Accept: "text/html" } });
                if (response.ok) {
                    const reached = new URL(response.url);
                    if (response.redirected && reached.pathname + reached.search !== location.pathname + location.search) destination = reached.pathname + reached.search;
                    continue;
                }
                // The answer shows the form with the entered values and its error; without one the form stays as entered.
                const page = new DOMParser().parseFromString(await response.text(), "text/html");
                failures.push({ index, form: page.querySelectorAll<HTMLFormElement>("#app-content form[data-save]")[index] ?? form, baseline });
            } catch {
                failures.push({ index, form, baseline });
            }
        }

        if (destination && failures.length === 0) {
            changes = 0;
            await visit(destination);
            return;
        }
        await refresh();
        const current = forms();
        for (const failure of failures) {
            const target = current[failure.index];
            if (!target) continue;
            const replacement = document.importNode(failure.form, true);
            target.replaceWith(replacement);
            loaded.set(replacement, failure.baseline);
            inserted(replacement);
        }
    } finally {
        saving = false;
        pill()?.removeAttribute("aria-busy");
    }

    if (failures.length > 0) {
        update(failures.length);
        return;
    }
    confirmSaved();
}

function confirmSaved(): void {
    const element = pill();
    const button = element?.querySelector<HTMLElement>("[data-save-submit]");
    if (!element || !button) return;
    const label = button.textContent;
    element.hidden = false;
    element.classList.add("save-pill--saved");
    button.textContent = button.dataset.saved ?? "";
    setTimeout(() => {
        if (!element.isConnected) return;
        element.classList.remove("save-pill--saved");
        button.textContent = label;
        update();
    }, 1200);
}

// Discarding shows the saved page again in place; leaving the page with changes asks first.
async function discard(): Promise<void> {
    changes = 0;
    await refresh();
}

// Only going to another page leaves; a form's own actions (add a row, apply a choice) keep the draft in its answer.
function leaving(method: string): boolean {
    const label = pill()?.querySelector<HTMLElement>(".save-pill__count");
    return method !== "GET" || changes === 0 || saving || window.confirm(label?.dataset.leave ?? "");
}

let installed = false;

export function installSavePill(): void {
    if (installed) return;
    installed = true;
    document.addEventListener("input", event => { if ((event.target as Element).closest?.("form[data-save]")) update(); });
    document.addEventListener("change", event => { if ((event.target as Element).closest?.("form[data-save]")) update(); });
    // Enter in a field and the form's own save button save through the pill; other submit buttons keep their action.
    document.addEventListener("submit", event => {
        const form = event.target as HTMLFormElement;
        const submitter = (event as SubmitEvent).submitter;
        if (!form.matches("form[data-save]") || !pill()) return;
        if (submitter && !submitter.matches("[data-form-save]")) {
            draftIndex = forms().indexOf(form);
            return;
        }
        event.preventDefault();
        event.stopPropagation();
        void save();
    }, true);
    // A form a script showed again as a draft.
    document.addEventListener("jularr:draft", event => {
        const form = event.target as HTMLFormElement;
        if (!form.matches?.("form[data-save]")) return;
        if (!loaded.has(form)) loaded.set(form, valuesOf(form));
        drafts.add(form);
        update();
    });
    document.addEventListener("click", event => {
        const target = event.target as Element;
        if (target.closest("[data-save-submit]")) void save();
        else if (target.closest("[data-save-discard]")) void discard();
    });
    document.addEventListener("htmx:config:request", event => {
        const { request } = (event as CustomEvent<{ ctx: { request: { method: string } } }>).detail.ctx;
        if (!isRefreshing() && !leaving(request.method)) event.preventDefault();
    });
    window.addEventListener("beforeunload", event => { if (changes > 0 && !saving) event.preventDefault(); });
}
