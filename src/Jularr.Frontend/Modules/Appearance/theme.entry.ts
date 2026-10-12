// Restore device preferences.
const root = document.documentElement;
const skins = new Set(["clean", "original"]);
const modes = new Set(["system", "light", "dark"]);

function preference(name: string, allowed: Set<string>, fallback: string): string {
    try {
        const value = localStorage.getItem(name);
        return value && allowed.has(value) ? value : fallback;
    } catch {
        return fallback;
    }
}

root.dataset.skin = preference("jularr-ui-skin", skins, "clean");
root.dataset.mode = preference("jularr-ui-mode", modes, "system");

function refreshControls(): void {
    document.querySelectorAll<HTMLButtonElement>("[data-set-skin], [data-set-mode]").forEach(button => {
        const selected = button.dataset.setSkin
            ? button.dataset.setSkin === root.dataset.skin
            : button.dataset.setMode === root.dataset.mode;
        button.setAttribute("aria-pressed", String(selected));
    });
}

refreshControls();

// Update shared controls.
document.addEventListener("click", event => {
    if (!(event.target instanceof Element)) return;
    const button = event.target.closest<HTMLButtonElement>("[data-set-skin], [data-set-mode]");
    if (!button) return;

    const kind = button.dataset.setSkin ? "skin" : "mode";
    const value = button.dataset.setSkin || button.dataset.setMode;
    const allowed = kind === "skin" ? skins : modes;
    if (!value || !allowed.has(value)) return;

    root.dataset[kind] = value;
    try {
        localStorage.setItem("jularr-ui-" + kind, value);
    } catch {
        // Storage is optional.
    }
    refreshControls();
});
