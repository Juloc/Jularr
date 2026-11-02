import { replaceWith } from "../../Modules/Navigation/PageSwap";
import { formAddress, withQuery } from "./LiveSearch";

// A search field of a list keeps its words: a field marked data-search-live shows the results shortly after typing (the page with the query replaces this history entry,
// the field keeps its focus and what was typed meanwhile), and every link or GET form of the same page that changes a filter, the order or the view takes the words typed last.
// Without script the field searches on Enter and the links keep the query the page was shown with.

const Delay = 300;
let typed: { id: string; path: string; value: string } | undefined;
let timer: ReturnType<typeof setTimeout> | undefined;
let running = false;

const searchField = (target: EventTarget | null) =>
    target instanceof HTMLInputElement && target.name === "q" && target.type === "search" && target.form?.method.toLowerCase() === "get" ? target : undefined;

const isLive = (field: HTMLInputElement) => field.form?.hasAttribute("data-search-live") === true;

// The words of the field typed into last, while its form leads to this page; a page shown since then has put its own query into it.
function current(): string | undefined {
    if (!typed || typed.path !== location.pathname) return undefined;
    const field = document.getElementById(typed.id);
    return field instanceof HTMLInputElement ? field.value : undefined;
}

async function search(): Promise<void> {
    clearTimeout(timer);
    const field = typed && document.getElementById(typed.id);
    if (!(field instanceof HTMLInputElement) || !field.form || !isLive(field)) return;
    if (running) return;

    running = true;
    const focused = document.activeElement === field;
    const asked = field.value;
    try {
        await replaceWith(formAddress(field.form.action, new FormData(field.form), location.href));
    } finally {
        running = false;
    }

    // The new page's field takes what was typed while it loaded and searches again for it.
    const next = typed && document.getElementById(typed.id);
    if (!(next instanceof HTMLInputElement)) return;
    if (next.value !== typed!.value) next.value = typed!.value;
    if (focused) {
        next.focus({ preventScroll: true });
        next.setSelectionRange(next.value.length, next.value.length);
    }
    if (next.value.trim() !== asked.trim()) void search();
}

document.addEventListener("input", event => {
    const field = searchField(event.target);
    if (!field?.id || !field.form) return;
    typed = { id: field.id, path: new URL(field.form.action, location.href).pathname, value: field.value };
    if (!isLive(field)) return;
    clearTimeout(timer);
    timer = setTimeout(() => void search(), Delay);
});

// Enter searches at once and stays in the field.
document.addEventListener("submit", event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    const field = form.querySelector<HTMLInputElement>('input[type="search"][name="q"]');
    if (field && isLive(field) && field.id) {
        event.preventDefault();
        typed = { id: field.id, path: new URL(form.action, location.href).pathname, value: field.value };
        void search();
        return;
    }

    // Another GET form of this page (a filter drawer, the order) takes the words typed last.
    const words = current();
    if (words === undefined || form.method.toLowerCase() !== "get" || field || new URL(form.action, location.href).pathname !== location.pathname) return;
    clearTimeout(timer);
    let kept = form.querySelector<HTMLInputElement>('input[type="hidden"][name="q"]');
    if (!kept) {
        kept = document.createElement("input");
        kept.type = "hidden";
        kept.name = "q";
        form.append(kept);
    }
    kept.value = words.trim();
    kept.disabled = !kept.value;
}, true);

// A link to this page (a chip, a type, the order, the view, a reset) takes them as well; a link that clears the search is marked data-search-clear.
document.addEventListener("click", event => {
    const words = current();
    const link = (event.target as Element | null)?.closest?.("a[href]");
    if (words === undefined || !(link instanceof HTMLAnchorElement) || link.closest("form[role=search]")) return;
    if (link.hasAttribute("data-search-clear")) {
        clearTimeout(timer);
        return;
    }
    if (new URL(link.href, location.href).pathname !== location.pathname || link.target || link.hasAttribute("download")) return;

    // The link goes instead of the search that waits.
    clearTimeout(timer);
    const { href, changed } = withQuery(link.getAttribute("href")!, words, location.href);
    if (changed) link.setAttribute("href", href);
}, true);
