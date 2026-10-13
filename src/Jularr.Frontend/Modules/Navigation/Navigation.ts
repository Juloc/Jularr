// In-place navigation while something keeps playing: the app area is swapped, the player stays.
// Everything else (forms, pages without the app shell, failures) is an ordinary page load.

const FullPagePrefixes = ["/Player/", "/Read/", "/Account/", "/Setup", "/api/"];

let enabled = false;
let runs = 0;
let onEnter: ((url: URL) => boolean) | undefined;

// Links that may stay in the document.
export function isNavigable(anchor: HTMLAnchorElement, current: Location, event: MouseEvent): boolean {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return false;
    if (anchor.target && anchor.target !== "_self" || anchor.hasAttribute("download") || anchor.dataset.navigation === "full") return false;
    const url = new URL(anchor.href, current.href);
    if (url.origin !== current.origin) return false;
    if (url.pathname === current.pathname && url.search === current.search && url.hash) return false;
    return !FullPagePrefixes.some(prefix => url.pathname.startsWith(prefix));
}

// The player decides about its own addresses (e.g. returning to the full player).
export function enableNavigation(enter: (url: URL) => boolean): void {
    onEnter = enter;
    if (enabled) return;
    enabled = true;
    document.addEventListener("click", click);
    window.addEventListener("popstate", pop);
}

export function disableNavigation(): void {
    enabled = false;
    onEnter = undefined;
    document.removeEventListener("click", click);
    window.removeEventListener("popstate", pop);
}

export async function navigate(address: string, push: boolean): Promise<void> {
    const url = new URL(address, location.href);
    if (onEnter?.(url)) {
        if (push) history.pushState(null, "", url);
        return;
    }

    let response: Response;
    try {
        response = await fetch(url, { credentials: "same-origin", headers: { Accept: "text/html" } });
    } catch {
        location.assign(url);
        return;
    }

    const page = response.ok && response.headers.get("Content-Type")?.includes("text/html")
        ? new DOMParser().parseFromString(await response.text(), "text/html")
        : null;
    const layout = page?.querySelector(".app-layout");
    if (!page || !layout) {
        location.assign(response.url || url.href);
        return;
    }

    // Styles first, then the page's scripts against the new content; scripts run again on every visit.
    await Promise.all(Array.from(page.head.querySelectorAll<HTMLLinkElement>('link[rel="stylesheet"]'))
        .filter(link => !document.head.querySelector(`link[rel="stylesheet"][href="${CSS.escape(link.getAttribute("href") ?? "")}"]`))
        .map(link => new Promise<void>(resolve => {
            const added = document.createElement("link");
            added.rel = "stylesheet";
            added.href = link.getAttribute("href")!;
            added.addEventListener("load", () => resolve());
            added.addEventListener("error", () => resolve());
            document.head.append(added);
        })));
    document.querySelector(".app-layout")?.remove();
    document.body.insertBefore(document.importNode(layout, true), document.body.querySelector(".player-main"));
    document.body.classList.add("app-page");
    document.title = page.title;
    if (push) history.pushState(null, "", response.url || url.href);
    window.scrollTo(0, 0);
    document.querySelector<HTMLElement>("#main-content")?.focus({ preventScroll: true });

    runs++;
    for (const script of Array.from(page.head.querySelectorAll<HTMLScriptElement>('script[type="module"][src]'))) {
        const source = script.getAttribute("src")!;
        if (source.includes("/theme.entry") || source.includes("/player.entry")) continue;
        await import(/* @vite-ignore */ `${source}${source.includes("?") ? "&" : "?"}visit=${runs}`).catch(() => undefined);
    }
}

function click(event: MouseEvent): void {
    const anchor = (event.target as Element | null)?.closest?.("a[href]") as HTMLAnchorElement | null;
    if (!anchor) return;
    if (event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey && onEnter?.(new URL(anchor.href, location.href))) {
        event.preventDefault();
        history.pushState(null, "", anchor.href);
        return;
    }

    if (!isNavigable(anchor, location, event)) return;
    event.preventDefault();
    void navigate(anchor.href, true);
}

function pop(): void {
    void navigate(location.href, false);
}
