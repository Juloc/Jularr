import { formatLocalTime, type LocalTimeDisplay } from "./LocalTime";

// Initial pages, htmx swaps, and asynchronously inserted fragments all share one presentation rule.
function localize(root: ParentNode): void {
    const elements: Element[] = [];
    if (root instanceof Element && root.matches("[data-local-time]")) elements.push(root);
    elements.push(...root.querySelectorAll("[data-local-time]"));
    const locale = document.documentElement.lang || navigator.language;
    for (const element of elements) {
        const iso = element.getAttribute("datetime") ?? element.getAttribute("data-utc");
        const display = element.getAttribute("data-local-time") as LocalTimeDisplay | null;
        if (!iso || !display) continue;
        const formatted = formatLocalTime(iso, display, locale);
        if (formatted !== null) {
            element.textContent = formatted;
            element.setAttribute("title", iso);
        }
    }
}

localize(document);
new MutationObserver(records => {
    for (const record of records) {
        for (const node of record.addedNodes) if (node instanceof Element) localize(node);
    }
}).observe(document.documentElement, { childList: true, subtree: true });
