import htmx from "htmx.org";
import { staysInApp } from "./Navigation";

// The settle step temporarily copies every old attribute, including control values.
// Focused inputs then keep that stale value. Native SCSS transitions need no such step.
htmx.config.defaultSettleDelay = 0;

// In-place navigation with htmx: links and forms of the app shell replace #app-content; the shell and a playing player stay.
// Back and Forward load the page again from the server; no page HTML is kept in the browser.

interface Context {
    target?: Element;
    text?: string;
    request: { method: string; action: string };
    response?: { raw: Response };
}
const contextOf = (event: Event) => (event as CustomEvent<{ ctx: Context }>).detail.ctx;
const Content = "app-content";
const isPageSwap = (ctx: Context) => ctx.target?.id === Content;

// Other shells, files and new windows keep the browser's own navigation; links to the playing player stay to show it again.
document.addEventListener("htmx:before:init", event => {
    const element = event.target;
    if (element instanceof HTMLAnchorElement && !staysInApp(element, element.href, location) && !isOwn(element.href)
        || element instanceof HTMLFormElement && !staysInApp(element, element.getAttribute("action") ?? "", location)) {
        event.preventDefault();
    }
});

// A response that is not an app page (sign-in after expiry, a redirect to another shell) is loaded as a page.
document.addEventListener("htmx:before:swap", event => {
    const ctx = contextOf(event);
    if (!isPageSwap(ctx) || /id="app-content"[^>]*data-shell="app"/.test(ctx.text ?? "")) return;
    event.preventDefault();
    location.assign(ctx.response?.raw.url || ctx.request.action);
});

document.addEventListener("htmx:after:swap", event => {
    const content = document.getElementById(Content);
    if (!isPageSwap(contextOf(event)) || !content) return;
    document.body.classList.add("app-page");
    hideUntilStyled(content);
    if (staying) {
        window.scrollTo(staying.x, staying.y);
        staying = undefined;
    } else if (!refreshing) {
        window.scrollTo(0, 0);
        content.querySelector<HTMLElement>("#main-content")?.focus({ preventScroll: true });
    }
    pageChanged();
});

// An action that shows the same page again keeps the reader where they were.
let staying: { x: number; y: number } | undefined;

export function stayHere(): void {
    staying = { x: window.scrollX, y: window.scrollY };
}

// Page styles arrive with the content; it stays invisible until they apply, at most briefly.
function hideUntilStyled(content: HTMLElement): void {
    const pending = Array.from(content.querySelectorAll<HTMLLinkElement>('link[rel="stylesheet"]')).filter(link => !link.sheet);
    if (pending.length === 0) return;
    content.classList.add("app-content--loading");
    let left = pending.length;
    const reveal = () => content.classList.remove("app-content--loading");
    const done = () => { if (--left === 0) reveal(); };
    pending.forEach(link => {
        link.addEventListener("load", done, { once: true });
        link.addEventListener("error", done, { once: true });
    });
    setTimeout(reveal, 1500);
}

// Page scripts run once per document; their setup runs again for every new page they belong to.
// A setup that is safe to run again over a page that stays (guarded per element) opts in with partial: it also runs after an action
// swapped only a region of the page.
const setups = new Map<string, { setup: () => void; partial: boolean; visit?: number }>();
let visits = 0;
let partial = false;

export function onPage(module: string, setup: () => void, options: { partial?: boolean } = {}): void {
    setups.set(module, { setup, partial: options.partial === true });
    run(module);
}

// True while the setups run after an action swapped a region: what the page already had is still there.
export const isPartialSwap = () => partial;

// An action swapped a region of the page: the setups that opted in run over the new elements.
export function regionSwapped(): void {
    visits++;
    partial = true;
    try {
        const present = new Set(Array.from(document.querySelectorAll<HTMLScriptElement>('script[type="module"][src]'), script => script.src));
        for (const [module, entry] of setups) if (entry.partial && present.has(module)) run(module);
    } finally {
        partial = false;
    }
}

function run(module: string): void {
    const entry = setups.get(module)!;
    if (entry.visit === visits) return;
    entry.visit = visits;
    entry.setup();
}

function pageChanged(): void {
    visits++;
    const present = new Set(Array.from(document.querySelectorAll<HTMLScriptElement>('script[type="module"][src]'), script => script.src));
    for (const module of setups.keys()) if (present.has(module)) run(module);
}

// HTML inserted by a script gets the same link and form behaviour.
export function inserted(element: Element): void {
    htmx.process(element);
}

export async function visit(address: string): Promise<void> {
    await htmx.ajax("GET", address, { target: "#" + Content, select: "#" + Content, swap: "outerHTML", push: "true" });
}

// The current page again from the server, in place and at the same position (after saving or discarding).
let refreshing = false;

export async function refresh(): Promise<void> {
    refreshing = true;
    try {
        await htmx.ajax("GET", location.pathname + location.search, { target: "#" + Content, select: "#" + Content, swap: "outerHTML" });
    } finally {
        refreshing = false;
    }
}

export const isRefreshing = () => refreshing;

export function pushAddress(address: string): void {
    history.pushState({ htmx: true }, "", address);
}

// The player owns its address while it plays: going there, by link or history, only shows it again.
let own: { address: string; enter: () => void; leave: () => void } | undefined;

function isOwn(path: string): boolean {
    if (!own) return false;
    const url = new URL(path, location.href);
    return url.pathname + url.search === own.address;
}

export function keepAddress(address: string, enter: () => void, leave: () => void): void {
    own = { address, enter, leave };
}

export function releaseAddress(): void {
    own = undefined;
}

document.addEventListener("htmx:config:request", event => {
    const { request } = contextOf(event);
    if (request.method !== "GET" || !own || !isOwn(request.action)) return;
    event.preventDefault();
    pushAddress(own.address);
    own.enter();
});

document.addEventListener("htmx:before:history:restore", event => {
    if (!own) return;
    if (isOwn((event as CustomEvent<{ path: string }>).detail.path)) {
        event.preventDefault();
        own.enter();
    } else {
        own.leave();
    }
});
