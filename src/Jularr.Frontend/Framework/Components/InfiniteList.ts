import "./InfiniteList.scss";

interface Position { address: string; pages: number; y: number }
interface InfiniteListOptions {
    historyState?: Record<string, unknown>;
    scrollRoot?: HTMLElement;
    requestAddress?: (href: string) => string;
    inserted?: (target: HTMLElement) => void;
}

// Grey cards fill the rest of the grid's last row and one more where the next page arrives.
function skeletons(grid: HTMLElement): HTMLElement[] {
    const columns = getComputedStyle(grid).gridTemplateColumns.split(" ").filter(Boolean).length || 1;
    const count = columns + (columns - grid.children.length % columns) % columns;
    return Array.from({ length: count }, () => {
        const card = document.createElement("div");
        card.className = "media-skeleton";
        card.setAttribute("aria-hidden", "true");
        return card;
    });
}

// One bounded HTML-list loader, also used inside the standalone Reader. While pages load by themselves its button stays unseen
// (a grid of cards shows grey cards where the page arrives); after a failure the button is the retry, and a grid says where it ends.
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
    const cards = target.classList.contains("media-grid");
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
        // The grey cards say it to the eye; the text stays for assistive technology.
        status.classList.toggle("visually-hidden", cards);
        const waiting = cards ? skeletons(target) : [];
        target.append(...waiting);
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
            // The page takes the grey cards' place.
            for (const card of waiting) card.remove();
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
            root.removeAttribute("data-infinite-manual");
            status.classList.remove("visually-hidden");
            status.textContent = !next && cards ? root.dataset.end ?? "" : "";
            save();
            return true;
        } catch {
            if (!controller.signal.aborted) {
                failed = true;
                status.classList.remove("visually-hidden");
                status.textContent = root.dataset.error!;
                root.setAttribute("data-infinite-manual", "");
                if (next && root.dataset.retryLabel) next.textContent = root.dataset.retryLabel;
            }
            return false;
        } finally {
            for (const card of waiting) card.remove();
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
