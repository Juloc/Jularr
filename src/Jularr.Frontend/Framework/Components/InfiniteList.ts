import "./InfiniteList.scss";

interface Position { address: string; pages: number; y: number }
interface InfiniteListOptions {
    historyState?: Record<string, unknown>;
    scrollRoot?: HTMLElement;
    requestAddress?: (href: string) => string;
    inserted?: (target: HTMLElement) => void;
}

// One bounded HTML-list loader, also used inside the standalone Reader.
export function initializeInfiniteList(rootElement: HTMLElement, options: InfiniteListOptions = {}): (() => void) | undefined {
    const targetElement = document.querySelector<HTMLElement>(rootElement.dataset.infiniteList ?? "__none__");
    if (!targetElement) return;
    const root = rootElement;
    const target = targetElement;
    const address = location.pathname + location.search;
    const remembered = options.historyState ? history.state?.infinite as Position | undefined : undefined;
    const restore = remembered?.address === address ? remembered : undefined;
    const controller = new AbortController();
    const status = root.querySelector<HTMLElement>("[data-infinite-status]")!;
    let next = root.querySelector<HTMLAnchorElement>("[data-infinite-next]");
    let pages = 1;
    let busy = false;
    let failed = false;
    let restoring = !!restore;
    let saveTimer: number | undefined;
    const keys = new Set(Array.from(target.children, child => child.getAttribute("data-list-key")).filter(key => key !== null));
    const save = () => {
        if (!options.historyState || !root.isConnected || restoring) return;
        // A direct visit may reach this setup before htmx marks its initial history entry.
        history.replaceState({ ...history.state, ...options.historyState, infinite: { address, pages, y: scrollY } satisfies Position }, "");
    };
    const observer = new IntersectionObserver(entries => {
        if (entries.some(entry => entry.isIntersecting) && !restoring && !failed) void load();
    }, { root: options.scrollRoot, rootMargin: "400px" });

    async function load(): Promise<boolean> {
        if (!next || busy || !root.isConnected) return false;
        busy = true;
        root.setAttribute("aria-busy", "true");
        status.textContent = root.dataset.loading!;
        try {
            const response = await fetch(options.requestAddress?.(next.href) ?? next.href, { signal: controller.signal, headers: { "X-Jularr-Infinite": "1" }, credentials: "same-origin" });
            if (!response.ok || response.redirected) throw new Error("Page unavailable");
            const html = new DOMParser().parseFromString(await response.text(), "text/html");
            const incoming = html.querySelector<HTMLElement>("[data-infinite-list]");
            if (!incoming || incoming.dataset.infiniteList !== root.dataset.infiniteList) throw new Error("List unavailable");
            const grid = html.querySelector(root.dataset.infiniteList!);
            const pageAddress = new URL(response.url);
            // Card actions return to the whole list being read, rather than the fetched chunk alone.
            for (const form of Array.from(grid?.querySelectorAll<HTMLFormElement>("form[action]") ?? [])) {
                const action = new URL(form.action, location.href);
                if (action.origin !== location.origin || action.searchParams.get("returnUrl") !== pageAddress.pathname + pageAddress.search) continue;
                action.searchParams.set("returnUrl", address);
                form.action = action.pathname + action.search;
            }
            // A shorter or empty final page has no next link.
            for (const child of Array.from(grid?.children ?? [])) {
                const key = child.getAttribute("data-list-key");
                if (key && keys.has(key)) continue;
                if (key) keys.add(key);
                target.append(child);
            }
            options.inserted?.(target);
            const following = incoming.querySelector<HTMLAnchorElement>("[data-infinite-next]");
            if (following) next.href = following.href;
            else { next.remove(); next = null; observer.disconnect(); }
            pages++;
            failed = false;
            status.textContent = "";
            save();
            return true;
        } catch {
            if (!controller.signal.aborted) { failed = true; status.textContent = root.dataset.error!; }
            return false;
        } finally {
            busy = false;
            root.removeAttribute("aria-busy");
        }
    }

    root.addEventListener("click", event => {
        if (!(event.target instanceof Element) || !event.target.closest("[data-infinite-next]")) return;
        event.preventDefault();
        event.stopPropagation();
        void load();
    }, { signal: controller.signal });
    window.addEventListener("scroll", () => {
        saveTimer ??= window.setTimeout(() => { saveTimer = undefined; save(); }, 500);
    }, { passive: true, signal: controller.signal });
    document.addEventListener("click", save, { capture: true, signal: controller.signal });
    window.addEventListener("pagehide", save, { signal: controller.signal });
    const dispose = () => { controller.abort(); observer.disconnect(); clearTimeout(saveTimer); };
    void (async () => {
        if (restore) {
            while (pages < restore.pages && next) if (!await load()) break;
            // Layout must include the appended cards before restoring the old position.
            await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
            if (!controller.signal.aborted) window.scrollTo(0, restore.y);
        }
        restoring = false;
        save();
        if (next && !controller.signal.aborted) observer.observe(root);
    })();
    return dispose;
}
