// Shows <time data-local-time> in the viewer's own time zone and language. The server cannot know either, so it renders a UTC
// fallback and a relative text; this gives every such element the full local date and time as its title and as hidden text for
// assistive technology (the .request-sr-only child), and replaces the text itself for data-local-time="stamp".
(() => {
    "use strict";

    const language = document.documentElement.lang || undefined;
    let full;
    let stamp;
    try {
        full = new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short" });
        stamp = new Intl.DateTimeFormat(language, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
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

            const hidden = node.querySelector(".request-sr-only");
            if (hidden) hidden.textContent = `, ${full.format(date)}`;
        }
    }

    window.JularrLocalTime = { apply };
    apply();
})();
