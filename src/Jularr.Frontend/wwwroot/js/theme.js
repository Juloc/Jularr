(() => {
    const root = document.documentElement;
    const skins = new Set(["clean", "original"]);
    const modes = new Set(["system", "light", "dark"]);

    function preference(name, allowed, fallback) {
        try {
            const value = localStorage.getItem(name);
            return allowed.has(value) ? value : fallback;
        } catch {
            return fallback;
        }
    }

    root.dataset.skin = preference("jularr-ui-skin", skins, "clean");
    root.dataset.mode = preference("jularr-ui-mode", modes, "system");

    document.addEventListener("click", event => {
        const button = event.target.closest("[data-set-skin], [data-set-mode]");
        if (!button) return;

        const kind = button.dataset.setSkin ? "skin" : "mode";
        const value = button.dataset.setSkin || button.dataset.setMode;
        const allowed = kind === "skin" ? skins : modes;
        if (!allowed.has(value)) return;

        root.dataset[kind] = value;
        try {
            localStorage.setItem("jularr-ui-" + kind, value);
        } catch {
            // Presentation preferences still apply for this page.
        }
    });
})();
