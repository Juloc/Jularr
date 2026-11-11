import { inserted } from "../../Modules/Navigation/PageSwap";

// The header search on every page: from two letters, titles Discover keeps open under the field grouped by type (Pages/User/Discover, handler Suggestions),
// asked once typing pauses and only the latest answer shown. Each group shows as many titles as the room allows, from six down to two. Arrow keys walk them,
// Escape and leaving close them, Enter searches every type. The shell is swapped with every page, so the handlers are delegated.
const pause = 150;
const most = 6;
const fewest = 2;
let timer: ReturnType<typeof setTimeout> | undefined;
let request: AbortController | undefined;

function field(target: EventTarget | null): HTMLInputElement | null {
    return target instanceof HTMLInputElement && target.matches(".app-search .search-box__input") ? target : null;
}

function popup(search: Element): HTMLElement {
    let list = search.querySelector<HTMLElement>(".app-search__popup");
    if (!list) {
        list = document.createElement("div");
        list.className = "app-search__popup";
        list.id = "app-search-popup";
        list.hidden = true;
        search.append(list);
        search.querySelector(".search-box__input")?.setAttribute("aria-controls", list.id);
    }
    return list;
}

function close(search: Element | null): void {
    request?.abort();
    clearTimeout(timer);
    const list = search?.querySelector<HTMLElement>(".app-search__popup");
    if (!list) return;
    list.hidden = true;
    list.replaceChildren();
}

async function suggest(input: HTMLInputElement): Promise<void> {
    const search = input.closest(".app-search");
    if (!search || !input.form || !input.isConnected) return;
    if (input.value.trim().length < 2) {
        close(search);
        return;
    }

    request?.abort();
    request = new AbortController();
    const signal = request.signal;
    const address = new URL(input.form.action);
    new FormData(input.form).forEach((value, name) => {
        if (typeof value === "string" && value.trim()) address.searchParams.set(name, value.trim());
    });
    address.searchParams.set("handler", "Suggestions");
    try {
        const response = await fetch(address, { signal, headers: { Accept: "text/html" } });
        if (!response.ok || response.redirected) return;
        const results = new DOMParser().parseFromString(await response.text(), "text/html").querySelector<HTMLElement>(".app-search__results");
        if (!results || signal.aborted) return;
        const list = popup(search);
        list.replaceChildren(results);
        list.hidden = false;
        inserted(results);
        fit(list);
    } catch {
        // Suggestions are optional; Enter still searches.
    }
}

// Every group shows the same number of titles: the most the popup holds without scrolling (its height is the room below the field).
export function fit(list: HTMLElement): void {
    const groups = Array.from(list.querySelectorAll(".app-search__group"), group => Array.from(group.querySelectorAll<HTMLElement>(".app-search__hit")));
    for (let count = most; count >= fewest; count--) {
        groups.forEach(hits => hits.forEach((hit, index) => { hit.hidden = index >= count; }));
        if (list.scrollHeight <= list.clientHeight) return;
    }
}

document.addEventListener("input", event => {
    const input = field(event.target);
    if (!input) return;
    request?.abort();
    clearTimeout(timer);
    timer = setTimeout(() => void suggest(input), pause);
});

// The chip that widens the search to every mode asks again.
document.addEventListener("change", event => {
    if (!(event.target instanceof HTMLInputElement) || !event.target.matches(".app-search__scope input")) return;
    const input = event.target.closest(".app-search")?.querySelector<HTMLInputElement>(".search-box__input");
    if (input && input.value.trim().length >= 2) void suggest(input);
});

document.addEventListener("keydown", event => {
    if (!(event.target instanceof Element)) return;
    const search = event.target.closest(".app-search");
    const list = search?.querySelector<HTMLElement>(".app-search__popup");
    if (!search) return;
    const open = list && !list.hidden;
    const input = search.querySelector<HTMLInputElement>(".search-box__input");
    if (event.key === "Escape") {
        // The first Escape closes the suggestions, the next one leaves the field (on a phone it closes the search).
        if (open) {
            close(search);
            input?.focus();
        } else {
            input?.blur();
        }
        return;
    }

    if (!open || (event.key !== "ArrowDown" && event.key !== "ArrowUp")) return;
    event.preventDefault();
    const links = Array.from(list.querySelectorAll<HTMLAnchorElement>("a:not([hidden])"));
    const index = links.indexOf(document.activeElement as HTMLAnchorElement);
    const next = index + (event.key === "ArrowDown" ? 1 : -1);
    (next < 0 ? input : links[Math.min(next, links.length - 1)])?.focus();
});

// A press in the suggestions or on the back button keeps the focus in the field until the click.
document.addEventListener("pointerdown", event => {
    if (event.target instanceof Element && event.target.closest(".app-search__popup, [data-search-close]")) event.preventDefault();
});

document.addEventListener("click", event => {
    if (!(event.target instanceof Element) || !event.target.closest("[data-search-close]")) return;
    const search = event.target.closest(".app-search");
    close(search);
    (document.activeElement as HTMLElement | null)?.blur();
});

document.addEventListener("focusout", event => {
    if (!(event.target instanceof Element)) return;
    const search = event.target.closest(".app-search");
    if (search && !search.contains(event.relatedTarget as Node | null)) close(search);
});

document.addEventListener("submit", event => {
    if (event.target instanceof Element) close(event.target.closest(".app-search"));
});
