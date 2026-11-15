// Opens the player over the page the viewer is on: the page stays where it is, so the player can shrink into the corner and keep playing while the app is browsed,
// and a song clicked in an album simply replaces the one that plays. Without script, or when anything goes wrong, a link to the player is the player's own page.

const PlayerPath = /^\/Player\/\d+$/;

function playerAddress(link: HTMLAnchorElement, event: MouseEvent): string | null {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return null;
    if (link.target && link.target !== "_self" || link.hasAttribute("download")) return null;
    const url = new URL(link.href, location.href);
    return url.origin === location.origin && PlayerPath.test(url.pathname) ? url.pathname : null;
}

document.addEventListener("click", event => {
    const link = (event.target as Element | null)?.closest<HTMLAnchorElement>("a[href]");
    const href = link ? playerAddress(link, event) : null;
    if (!href) return;
    event.preventDefault();
    void open(href);
}, true);

const sourceOf = (script: HTMLScriptElement) => new URL(script.getAttribute("src") ?? "", location.href).href;
const styleOf = (link: HTMLLinkElement) => new URL(link.getAttribute("href") ?? "", location.href).href;

async function open(href: string): Promise<void> {
    try {
        // A player is already here: it plays the item itself (the same one comes back to the front).
        if (document.querySelector(".player-main .player")) {
            window.dispatchEvent(new CustomEvent("jularr:player-open", { detail: { href } }));
            return;
        }

        const response = await fetch(href, { credentials: "same-origin", headers: { Accept: "text/html" } });
        const address = new URL(response.url);
        if (!response.ok || address.origin !== location.origin || !PlayerPath.test(address.pathname)) throw new Error("player");
        const page = new DOMParser().parseFromString(await response.text(), "text/html");
        const main = page.querySelector<HTMLElement>(".player-main");
        if (!main?.querySelector('.player[data-start-url]:not([data-start-url=""])')) throw new Error("player");

        // The player's styles come first, so it never shows unstyled; its script follows (once per document).
        const known = new Set(Array.from(document.querySelectorAll<HTMLLinkElement>('link[rel="stylesheet"]'), styleOf));
        const waiting = Array.from(page.querySelectorAll<HTMLLinkElement>('head link[rel="stylesheet"]')).filter(link => !known.has(styleOf(link))).map(link => {
            const copy = document.createElement("link");
            copy.rel = "stylesheet";
            copy.href = styleOf(link);
            const loaded = new Promise<void>(resolve => { copy.addEventListener("load", () => resolve(), { once: true }); copy.addEventListener("error", () => resolve(), { once: true }); });
            document.head.append(copy);
            return loaded;
        });
        await Promise.race([Promise.all(waiting), new Promise(resolve => setTimeout(resolve, 2000))]);

        // Songs open in the bar at the bottom, everything else on the whole screen.
        const insert = document.importNode(main, true);
        insert.removeAttribute("id");
        insert.dataset.start = insert.querySelector(".player--audio") ? "mini" : "full";
        document.body.append(insert);

        if (document.documentElement.dataset.playerReady !== undefined) {
            window.dispatchEvent(new CustomEvent("jularr:player-mount"));
            return;
        }

        const present = new Set(Array.from(document.querySelectorAll<HTMLScriptElement>("script[src]"), sourceOf));
        for (const script of Array.from(page.querySelectorAll<HTMLScriptElement>('script[type="module"][src]'))) {
            const source = sourceOf(script);
            if (present.has(source)) continue;
            const copy = document.createElement("script");
            copy.type = "module";
            copy.src = source;
            document.head.append(copy);
        }
    } catch {
        location.assign(href);
    }
}
