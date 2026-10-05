(() => {
    "use strict";

    // Optional columns are a presentation preference kept in this browser: switching one off drops its track from the grid
    // template and hides its cells; no row data changes. The default is every column.
    const widths = {
        status: "120px",
        quality: "110px",
        audio: "minmax(70px, .8fr)",
        subtitles: "minmax(70px, .8fr)",
        files: "44px",
        size: "76px"
    };
    const storageKey = "jularr.admin.media.columns";

    const readHidden = () => {
        try {
            const stored = JSON.parse(localStorage.getItem(storageKey) ?? "[]");
            return new Set(Array.isArray(stored) ? stored.filter(name => name in widths) : []);
        } catch {
            return new Set();
        }
    };

    const apply = (groups, hidden) => {
        groups.querySelectorAll("[data-col]").forEach(cell => cell.classList.toggle("is-col-hidden", hidden.has(cell.dataset.col)));
        const isVersions = groups.classList.contains("admmd-groups-versions");
        const ordered = ["status", "quality", "audio", "subtitles", "files", "size"].filter(name => !hidden.has(name)).map(name => widths[name]);
        const track = isVersions
            ? ["minmax(200px, 1.8fr)", ...ordered].join(" ")
            : ["64px", "minmax(140px, 1.6fr)", ...ordered, "52px"].join(" ");
        groups.style.setProperty("--admmd-template", track);
    };

    const groups = document.querySelector("[data-columns-key]");
    if (groups) {
        const hidden = readHidden();
        groups.querySelectorAll("input[data-column-toggle]").forEach(toggle => {
            toggle.checked = !hidden.has(toggle.dataset.columnToggle);
            toggle.addEventListener("change", () => {
                toggle.checked ? hidden.delete(toggle.dataset.columnToggle) : hidden.add(toggle.dataset.columnToggle);
                try {
                    localStorage.setItem(storageKey, JSON.stringify([...hidden]));
                } catch {
                    // A browser without storage keeps the choice for this page view only.
                }

                apply(groups, hidden);
            });
        });
        apply(groups, hidden);
    }

    // After a monitoring change the page comes back with the season or episode in the address: open it and its season.
    const target = location.hash.length > 1 ? document.getElementById(decodeURIComponent(location.hash.slice(1))) : null;
    if (target instanceof HTMLDetailsElement) {
        target.open = true;
        target.parentElement?.closest("details")?.setAttribute("open", "");
        target.scrollIntoView({ block: "nearest" });
    }
})();
