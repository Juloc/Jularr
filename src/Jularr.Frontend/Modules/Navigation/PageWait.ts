import { isRefreshing, isStaying } from "./PageSwap";

// A click that loads another page answers at once: the clicked link shows as the current one. When the new page
// takes longer than the reload budget (0.1 s), a thin bar runs at the top and the content gives way to a skeleton
// of the page kind, so nobody looks at a page that seems frozen. A search typed into the page or a refresh only shows the bar.

const ReloadBudget = 100;
// A page that has not arrived by then gives the old page back: the skeleton never stays for a request that does not answer.
const GiveUp = 20000;
const Content = "app-content";

interface Context {
    target?: Element;
    sourceElement?: Element;
    request: { method: string; action: string };
}
const contextOf = (event: Event) => (event as CustomEvent<{ ctx: Context }>).detail.ctx;

let timer: number | undefined;
let giveUp: number | undefined;
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
        const main = document.querySelector<HTMLElement>("#app-content #main-content");
        if (!skeleton || !main) return;
        main.setAttribute("aria-busy", "true");
        main.dataset.waitingFor = kindOf(address);
        // The same page with other choices (Discover in another mode, a filtered list) keeps its layout: only its pictures turn grey until the new ones arrive.
        // Another page's skeleton keeps the height of the page it covers, so the scrollbar stays and nothing moves sideways.
        if (main.dataset.waitingFor !== "same") {
            main.style.minHeight = main.offsetHeight + "px";
            main.append(pageSkeleton(main.dataset.waitingFor));
        }
    }, ReloadBudget);
    giveUp = window.setTimeout(end, GiveUp);
}

function end(): void {
    if (timer !== undefined) window.clearTimeout(timer);
    if (giveUp !== undefined) window.clearTimeout(giveUp);
    timer = undefined;
    giveUp = undefined;
    delete document.body.dataset.pageWait;
    pendingLink?.classList.remove("is-pending");
    pendingLink = undefined;
    // After a failure the old page stays: its skeleton goes away again.
    const main = document.querySelector<HTMLElement>("#app-content #main-content");
    if (!main?.dataset.waitingFor) return;
    main.querySelector(":scope > .page-skeleton")?.remove();
    main.style.removeProperty("min-height");
    main.removeAttribute("aria-busy");
    delete main.dataset.waitingFor;
}

// Admin pages are lists, user pages are covers; a user page that stays the same page keeps its own layout.
export function kindOf(address: string): "rows" | "covers" | "same" {
    const path = new URL(address, location.href).pathname.toLowerCase();
    if (path.startsWith("/admin")) return "rows";
    return path === location.pathname.toLowerCase() ? "same" : "covers";
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
