// Formats <time data-local-time> with the viewer's time zone and language. The server cannot know either, so machine-readable
// UTC values are rendered first and then localized here; "stamp", "date", and "parts" control how visible child text is replaced.
(() => {
    "use strict";

    const language = document.documentElement.lang || undefined;
    let full;
    let stamp;
    let clock;
    let dateParts;
    try {
        full = new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" });
        stamp = new Intl.DateTimeFormat(language, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
        clock = new Intl.DateTimeFormat(language, { hour: "2-digit", minute: "2-digit" });
        dateParts = new Intl.DateTimeFormat(language, { year: "numeric", month: "2-digit", day: "2-digit" });
    } catch {
        // An unknown language tag leaves the server rendering as it is.
        return;
    }

    function apply(root = document) {
        for (const node of root.querySelectorAll("time[data-local-time]")) {
            const date = new Date(node.getAttribute("datetime"));
            if (Number.isNaN(date.getTime())) continue;

            node.title = full.format(date);
            if (node.dataset.localTime === "stamp") {
                node.textContent = stamp.format(date);
                continue;
            }

            if (node.dataset.localTime === "date" || node.dataset.localTime === "parts") {
                const parts = Object.fromEntries(dateParts.formatToParts(date).map(part => [part.type, part.value]));
                const localDate = node.querySelector("[data-local-date]");
                if (localDate) localDate.textContent = `${parts.year}-${parts.month}-${parts.day}`;
                if (node.dataset.localTime === "parts") {
                    const localClock = node.querySelector("[data-local-clock]");
                    if (localClock) localClock.textContent = clock.format(date);
                }
                continue;
            }

            const hidden = node.querySelector(".request-sr-only");
            if (hidden) hidden.textContent = `, ${full.format(date)}`;
        }
    }

    window.JularrLocalTime = {
        apply,
        format: value => {
            const date = new Date(value);
            return Number.isNaN(date.getTime()) ? "" : full.format(date);
        }
    };
    apply();
})();
