import { formatLocalTime, localTodayIso, releaseCountdown, timeZoneCookieHeader, type LocalTimeDisplay } from "./LocalTime";

// Shared page and fragment enhancement; never reload or reflow a whole screen to correct a clock.
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
            const label = element.querySelector(".local-time__label") ?? element;
            if (label.textContent !== formatted) label.textContent = formatted;
            if (element.getAttribute("title") !== iso) element.setAttribute("title", iso);
        }
    }
}

function updateCivilDates(root: ParentNode, today: string): void {
    const nodes: Element[] = [];
    if (root instanceof Element && root.hasAttribute("data-calendar-day")) nodes.push(root);
    nodes.push(...Array.from(root.querySelectorAll("[data-calendar-day]")));
    for (const element of nodes) {
        const isToday = element.getAttribute("data-calendar-day") === today;
        if (element.classList.contains("calendar__cell")) {
            element.classList.toggle("calendar__cell--today", isToday);
            if (isToday) element.setAttribute("aria-current", "date");
            else element.removeAttribute("aria-current");
        } else if (element.classList.contains("calendar__week-day")) {
            element.classList.toggle("calendar__week-day--today", isToday);
            if (isToday) element.setAttribute("aria-current", "date");
            else element.removeAttribute("aria-current");
        } else if (element.classList.contains("calendar__strip-date")) {
            // The strip's aria-current belongs to its selected day, not its today marker.
            element.classList.toggle("calendar__strip-date--today", isToday);
        }
    }

    const releases: Element[] = [];
    if (root instanceof Element && root.hasAttribute("data-work-released-on")) releases.push(root);
    releases.push(...Array.from(root.querySelectorAll("[data-work-released-on]")));
    for (const element of releases) {
        const date = element.getAttribute("data-work-released-on");
        const format = element.getAttribute("data-work-airs-in");
        const fallback = element.getAttribute("data-work-unaired");
        if (!date || !format || !fallback) continue;
        const label = releaseCountdown(date, today, format, fallback);
        const text = element.querySelector("[class~=work-status__label]");
        if (text && text.textContent !== label) text.textContent = label;
    }
}

// Browser zone is a UI hint sent before every htmx request; no HTTP refresh is needed.
function syncBrowserTimeZone(): void {
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const cookie = timeZoneCookieHeader(document.cookie, zone, location.protocol === "https:");
    if (cookie) document.cookie = cookie;
}

let observedZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
let observedDay = localTodayIso(new Date());

function refreshTimeContext(): void {
    syncBrowserTimeZone();
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    const day = localTodayIso(new Date());
    if (zone !== observedZone) localize(document);
    if (zone !== observedZone || day !== observedDay) updateCivilDates(document, day);
    observedZone = zone;
    observedDay = day;
}

function midnightWake(): void {
    // One timer until next local midnight, not a poll every minute on every page.
    const now = new Date();
    const next = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1, 0, 0, 1);
    window.setTimeout(() => {
        refreshTimeContext();
        midnightWake();
    }, Math.max(100, next.getTime() - now.getTime()));
}

syncBrowserTimeZone();
localize(document);
updateCivilDates(document, observedDay);

new MutationObserver(records => {
    for (const record of records) {
        for (const node of Array.from(record.addedNodes)) {
            if (!(node instanceof Element)) continue;
            localize(node);
            updateCivilDates(node, localTodayIso(new Date()));
        }
    }
}).observe(document.documentElement, { childList: true, subtree: true });

document.addEventListener("htmx:config:request", syncBrowserTimeZone);
document.addEventListener("visibilitychange", () => { if (!document.hidden) refreshTimeContext(); });
window.addEventListener("focus", refreshTimeContext);
window.addEventListener("pageshow", refreshTimeContext);
midnightWake();
