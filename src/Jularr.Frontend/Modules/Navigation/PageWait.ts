import { isRefreshing, isStaying } from "./PageSwap";

// A click that loads another page answers at once: the clicked link shows as the current one. When the new page
// takes longer than the reload budget (0.1 s), a thin bar runs at the top and the content gives way to a skeleton
// of the page kind, so nobody looks at a page that seems frozen. A search typed into the page or a refresh only shows the bar.

const ReloadBudget = 100;
const Content = "app-content";

interface Context {
    target?: Element;
    sourceElement?: Element;
    request: { method: string; action: string };
}
const contextOf = (event: Event) => (event as CustomEvent<{ ctx: Context }>).detail.ctx;

let timer: number | undefined;
let pendingLink: HTMLAnchorElement | undefined;

document.addEventListener("htmx:config:request", event => {
    const ctx = contextOf(event);
    if (event.defaultPrevented || ctx.target?.id !== Content || ctx.request.method !== "GET") return;
    begin(ctx.sourceElement, ctx.request.action, !isRefreshing() && !isStaying());
});

document.addEventListener("htmx:after:swap", end);
document.addEventListener("htmx:error", end);
document.addEventListener("htmx:after:request", event => {
    // A response that is not swapped (an error, a full navigation instead) ends the wait as well.
    const status = (event as CustomEvent<{ ctx: { response?: { raw: Response } } }>).detail.ctx.response?.raw.status ?? 0;
    if (status >= 400 || status === 0) end();
});
window.addEventListener("pagehide", end);

function begin(source: Element | undefined, address: string, skeleton: boolean): void {
    end();
    const link = source?.closest("a");
    if (link instanceof HTMLAnchorElement) {
        pendingLink = link;
        link.classList.add("is-pending");
    }
    timer = window.setTimeout(() => {
        timer = undefined;
        document.body.dataset.pageWait = "true";
        const main = document.getElementById("main-content");
        if (!skeleton || !main) return;
        main.setAttribute("aria-busy", "true");
        main.dataset.waitingFor = kindOf(address);
        main.append(pageSkeleton(main.dataset.waitingFor));
    }, ReloadBudget);
}

function end(): void {
    if (timer !== undefined) window.clearTimeout(timer);
    timer = undefined;
    delete document.body.dataset.pageWait;
    pendingLink?.classList.remove("is-pending");
    pendingLink = undefined;
    // After a failure the old page stays: its skeleton goes away again.
    const main = document.getElementById("main-content");
    if (!main?.dataset.waitingFor) return;
    main.querySelector(":scope > .page-skeleton")?.remove();
    main.removeAttribute("aria-busy");
    delete main.dataset.waitingFor;
}

// Admin pages are lists, user pages are covers.
export function kindOf(address: string): "rows" | "covers" {
    const path = new URL(address, location.href).pathname.toLowerCase();
    return path.startsWith("/admin") ? "rows" : "covers";
}

function pageSkeleton(kind: string): HTMLElement {
    const skeleton = document.createElement("div");
    skeleton.className = "page-skeleton page-skeleton--" + kind;
    skeleton.setAttribute("aria-hidden", "true");
    const count = kind === "rows" ? 8 : 12;
    for (let index = 0; index < count; index++) {
        const item = document.createElement("span");
        item.className = "skeleton-block page-skeleton__item";
        skeleton.append(item);
    }
    return skeleton;
}
