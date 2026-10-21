import { isSamePage, matchForm, regionIdOf, selectorOf, type FormFingerprint, type RegionNode } from "./Action";
import { regionSwapped, stayHere } from "./PageSwap";

// Actions are forms that post (a switch, a search, a delete). The server answers with the same page; the client swaps only the region
// around the form (a container the page marked with data-region and an id, else the page content) and keeps the reader where they were:
// scroll position, opened <details>, the focused control. Without script the form posts and redirects as any form does.

interface Context {
    sourceElement?: Element;
    target?: Element;
    select?: string;
    swap?: string;
    push?: string;
    replace?: string;
    text?: string;
    request: { method: string; action: string };
    response?: { raw: Response };
}

const contextOf = (event: Event) => (event as CustomEvent<{ ctx: Context }>).detail.ctx;
const Content = "app-content";

interface Snapshot {
    region: string | null;
    same: boolean;
    scroll: { x: number; y: number };
    details: Map<string, boolean>;
    focus?: { form?: FormFingerprint; index: number; id: string; selector?: string };
}

const actions = new WeakMap<Context, Snapshot>();

const fingerprintOf = (form: HTMLFormElement): FormFingerprint => ({
    action: form.getAttribute("action") ?? "",
    values: new Map(Array.from(form.querySelectorAll<HTMLInputElement>('input[type="hidden"]')).filter(input => input.name !== "__RequestVerificationToken").map(input => [input.name, input.value]))
});

const controlsOf = (form: HTMLFormElement) => Array.from(form.querySelectorAll<HTMLElement>("button, input[type=submit], input[type=checkbox], select, textarea, input:not([type=hidden])"));

// The control that has focus is found again in the new elements: by its id, else by its form and its place in that form.
function focusOf(scope: Element): Snapshot["focus"] {
    const active = document.activeElement;
    if (!(active instanceof HTMLElement) || active === document.body || !scope.contains(active)) return undefined;
    if (active.id) return { id: active.id, index: -1 };
    const form = active.closest("form");
    return form ? { form: fingerprintOf(form), index: controlsOf(form).indexOf(active), id: "" } : undefined;
}

function restoreFocus(scope: ParentNode, focus: Snapshot["focus"]): void {
    if (!focus) return;
    const element = focus.id ? document.getElementById(focus.id) : (() => {
        const forms = Array.from(scope.querySelectorAll<HTMLFormElement>("form"));
        const found = focus.form ? forms[matchForm(focus.form, forms.map(fingerprintOf))] : undefined;
        return found ? controlsOf(found)[focus.index] : undefined;
    })();
    element?.focus({ preventScroll: true });
}

function restoreDetails(scope: ParentNode, details: Map<string, boolean>): void {
    details.forEach((open, id) => {
        const element = scope.querySelector<HTMLDetailsElement>(`details[id="${CSS.escape(id)}"]`);
        if (element) element.open = open;
    });
}

// An action runs inside the region of its form, or on the page content when the form is in none.
document.addEventListener("htmx:config:request", event => {
    const ctx = contextOf(event);
    const form = ctx.sourceElement instanceof HTMLFormElement ? ctx.sourceElement : ctx.sourceElement?.closest("form");
    if (ctx.request.method === "GET" || !form || ctx.target?.id !== Content) return;
    const region = regionIdOf(form as unknown as RegionNode);
    if (region && document.getElementById(region)) {
        ctx.target = document.getElementById(region)!;
        ctx.select = selectorOf(region);
    }

    // The page stays where it is; htmx would bring the top of the swapped element into view.
    ctx.swap = "outerHTML show:none";
    const scope = ctx.target ?? document.body;
    const details = new Map<string, boolean>();
    scope.querySelectorAll<HTMLDetailsElement>("details[id]").forEach(item => details.set(item.id, item.open));
    actions.set(ctx, { region, same: true, scroll: { x: window.scrollX, y: window.scrollY }, details, focus: focusOf(scope) });
});

// The answer is known before it is swapped. It is this page again, or the action led elsewhere (a redirect to the list, a new item, a sign-in):
// then the page content follows the answer like a link. This page again changes no history entry, and a different query only replaces it.
document.addEventListener("htmx:after:request", event => {
    const ctx = contextOf(event);
    const action = actions.get(ctx);
    if (!action) return;
    const answer = new URL(ctx.response?.raw.url || ctx.request.action, location.href);
    action.same = isSamePage(answer.href, location);
    if (action.region && (!action.same || !(ctx.text ?? "").includes(`id="${action.region}"`))) {
        ctx.target = document.getElementById(Content)!;
        ctx.select = selectorOf(Content);
        action.region = null;
    }

    if (!action.same) {
        ctx.swap = "outerHTML";
        return;
    }

    if (answer.pathname + answer.search === location.pathname + location.search) ctx.push = "false";
    else ctx.replace = "true";
});

document.addEventListener("htmx:before:swap", event => {
    const action = actions.get(contextOf(event));
    // A region swap restores the position itself once its sections are open again; a page content swap lets PageSwap do it.
    if (action?.same && !action.region) stayHere();
});

document.addEventListener("htmx:after:swap", event => {
    const ctx = contextOf(event);
    const action = actions.get(ctx);
    if (!action) return;
    actions.delete(ctx);
    if (!action.same) return;
    const scope: ParentNode = action.region ? document.getElementById(action.region) ?? document : document.getElementById(Content) ?? document;
    if (action.region) regionSwapped();
    restoreDetails(scope, action.details);
    restoreFocus(scope, action.focus);
    // The new region had a moment of its default sections, which can shorten the page and move the scroll position.
    if (action.region) window.scrollTo(action.scroll.x, action.scroll.y);
});

// A request that never arrived leaves the page as it is and says so.
document.addEventListener("htmx:error", event => {
    const ctx = contextOf(event);
    if (!actions.has(ctx)) return;
    actions.delete(ctx);
    document.dispatchEvent(new CustomEvent("jularr:hint", { detail: { text: document.querySelector<HTMLElement>("[data-action-toast]")?.dataset.failed ?? "", failed: true } }));
});
