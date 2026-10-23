import { formatLocalTime, isDateSensitivePage, localTodayIso, timeZoneCookieHeader, type LocalTimeDisplay } from "./LocalTime";

// Initial pages, htmx swaps, and asynchronously inserted fragments all share one presentation rule.
function localize(root: ParentNode): void {
    const elements: Element[] = [];
    if (root instanceof Element && root.matches("[data-local-time]")) elements.push(root);
    elements.push(...Array.from(root.querySelectorAll("[data-local-time]")));
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

// The cookie is advisory UI state; it never changes persisted UTC timestamps or release dates.
function syncBrowserTimeZone(): boolean {
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const header = timeZoneCookieHeader(document.cookie, zone, location.protocol === "https:");
    if (!header) return false;
    document.cookie = header;
    // If cookies are blocked, do not keep reloading a page that cannot persist the zone.
    return timeZoneCookieHeader(document.cookie, zone, location.protocol === "https:") === null;
}

let observedDay = localTodayIso(new Date());
const dateSensitive = isDateSensitivePage(location.pathname);

function refreshTimeContext(): void {
    const changedZone = syncBrowserTimeZone();
    const day = localTodayIso(new Date());
    const changedDay = day !== observedDay;
    observedDay = day;

    if (dateSensitive && (changedZone || changedDay)) {
        location.reload();
        return;
    }
    localize(document);
}

// A direct URL to Calendar/Work may arrive before the first timezone cookie exists.
if (syncBrowserTimeZone() && dateSensitive) {
    location.reload();
} else {
    localize(document);
    new MutationObserver(records => {
        for (const record of records) {
            for (const node of Array.from(record.addedNodes)) if (node instanceof Element) localize(node);
        }
    }).observe(document.documentElement, { childList: true, subtree: true });

    document.addEventListener("visibilitychange", () => { if (!document.hidden) refreshTimeContext(); });
    window.addEventListener("pageshow", refreshTimeContext);
    if (dateSensitive) window.setInterval(refreshTimeContext, 60_000);
}
