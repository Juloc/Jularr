import { buildPalette, CleanAccent, OriginalAccent } from "./Palette";

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
let accent = "";
try { accent = localStorage.getItem("jularr-ui-accent") ?? ""; } catch { /* Storage is optional. */ }
if (!/^#[a-f\d]{6}$/i.test(accent)) accent = "";
accent = accent.toLowerCase();
// The main profile is the server's single source of truth for signed-in branding.
// Anonymous visitors retain their device-local preview preference.
const mainAccent = /^#[a-f\d]{6}$/i.test(root.dataset.pwaAccent ?? "") ? root.dataset.pwaAccent!.toLowerCase() : "";
if (mainAccent) accent = mainAccent;
let lastSavedAccent = mainAccent;
let saveTimer: ReturnType<typeof setTimeout> | undefined;
let savingAccent: Promise<void> = Promise.resolve();
let palette: ReturnType<typeof buildPalette>;

function applyPalette(): void {
    const seed = accent || (root.dataset.skin === "original" ? OriginalAccent : CleanAccent);
    palette = buildPalette(seed);
    root.dataset.accent = seed;
    for (const [name, light] of Object.entries(palette.light)) {
        root.style.setProperty("--" + name, `light-dark(${light}, ${palette.dark[name]})`);
    }
    root.style.setProperty("--art-hue-rotate", palette.rotation);
    root.style.setProperty("--art-saturate", palette.saturation);
    updateBrowserColor();
    updateBrandIcons(seed);
}

function updateBrandIcons(seed: string): void {
    const color = seed.toLowerCase().slice(1);
    // Public icon URLs include only the chosen hex color, never an account/profile/session ID.
    document.querySelector<HTMLLinkElement>('link[data-pwa-favicon="svg"]')
        ?.setAttribute("href", "/pwa/icons/favicon/" + color + ".svg");
    document.querySelector<HTMLLinkElement>('link[data-pwa-favicon="png"]')
        ?.setAttribute("href", "/pwa/icons/favicon-32/" + color + ".png");
    if (mainAccent) {
        document.querySelector<HTMLLinkElement>('link[rel="apple-touch-icon"]')
            ?.setAttribute("href", "/pwa/icons/apple-180/" + color + ".png");
    }
}

const systemMode = window.matchMedia("(prefers-color-scheme: dark)");
function updateBrowserColor(): void {
    const dark = root.dataset.mode === "dark" || root.dataset.mode === "system" && systemMode.matches;
    document.querySelector("meta[name=theme-color]")?.setAttribute("content", (dark ? palette.dark : palette.light).background);
}
systemMode.addEventListener("change", updateBrowserColor);

function refreshControls(): void {
    document.querySelectorAll<HTMLButtonElement>("[data-set-skin], [data-set-mode]").forEach(button => {
        const selected = button.dataset.setSkin
            ? button.dataset.setSkin === root.dataset.skin
            : button.dataset.setMode === root.dataset.mode;
        button.setAttribute("aria-pressed", String(selected));
    });

    document.querySelectorAll<HTMLInputElement>("[data-accent-input]").forEach(input => { input.value = root.dataset.accent!; });
    document.querySelectorAll<HTMLElement>("[data-accent-value]").forEach(label => { label.textContent = root.dataset.accent!; });
    document.querySelectorAll<HTMLButtonElement>("[data-set-accent]").forEach(button => {
        button.setAttribute("aria-pressed", String(button.dataset.setAccent === root.dataset.accent));
    });
}

applyPalette();
refreshControls();
// Pages swapped in place show the current choice too.
document.addEventListener("htmx:after:swap", refreshControls);

// Update shared controls.
document.addEventListener("click", event => {
    if (!(event.target instanceof Element)) return;
    const button = event.target.closest<HTMLButtonElement>("[data-set-skin], [data-set-mode], [data-set-accent], [data-reset-accent], [data-toggle-mode], [data-cycle-skin], [data-cycle-mode]");
    if (!button) return;

    if (button.dataset.setAccent || button.dataset.resetAccent) {
        setAccent(button.dataset.resetAccent ? "" : button.dataset.setAccent!);
        return;
    }

    // The header's switch: the other brightness than the one in effect (the device's own when the choice is System).
    const toggle = button.dataset.toggleMode !== undefined;
    const dark = root.dataset.mode === "dark" || root.dataset.mode === "system" && systemMode.matches;
    // The sign-in page's round buttons: the next skin, the next brightness (system, light, dark), then round again.
    const cycle = button.dataset.cycleSkin !== undefined ? "skin" : button.dataset.cycleMode !== undefined ? "mode" : null;
    const kind = cycle ?? (!toggle && button.dataset.setSkin ? "skin" : "mode");
    const order = [...(kind === "skin" ? skins : modes)];
    const value = cycle ? order[(order.indexOf(root.dataset[kind] ?? "") + 1) % order.length]
        : toggle ? (dark ? "light" : "dark") : button.dataset.setSkin || button.dataset.setMode;
    const allowed = kind === "skin" ? skins : modes;
    if (!value || !allowed.has(value)) return;

    root.dataset[kind] = value;
    try {
        localStorage.setItem("jularr-ui-" + kind, value);
    } catch {
        // Storage is optional.
    }
    if (kind === "skin") applyPalette();
    else updateBrowserColor();
    refreshControls();
});

// Native picker, shared palette.
document.addEventListener("input", event => {
    if (!(event.target instanceof HTMLInputElement)) return;
    if (event.target.dataset.accentInput !== undefined) setAccent(event.target.value);
});

function setAccent(value: string): void {
    if (value !== "" && !/^#[a-f\d]{6}$/i.test(value)) return;
    const requested = value.toLowerCase();
    accent = mainAccent && !requested ? (root.dataset.skin === "original" ? OriginalAccent : CleanAccent) : requested;
    if (!mainAccent) {
        try {
            if (accent) localStorage.setItem("jularr-ui-accent", accent);
            else localStorage.removeItem("jularr-ui-accent");
        } catch {
            // Storage is optional for signed-out visitors.
        }
    }
    applyPalette();
    refreshControls();
    if (mainAccent) {
        clearTimeout(saveTimer);
        // Native color pickers emit many input events. Only save the settled color.
        const selected = accent;
        saveTimer = setTimeout(() => {
            savingAccent = savingAccent.then(() => persistMainAccent(selected));
        }, 350);
    }
}

// Never replace the main profile's saved color with a local-only preference.
// Mutations are sequential, same-origin and antiforgery checked. A failed save rolls
// back the optimistic palette and favicons, so their displayed state stays truthful.
async function persistMainAccent(next: string): Promise<void> {
    const status = document.querySelector<HTMLElement>("[data-accent-save-error]");
    if (status) status.hidden = true;
    try {
        const auth = await fetch("/api/v1/auth", { cache: "no-store", credentials: "same-origin" });
        const token = auth.headers.get("RequestVerificationToken");
        if (!auth.ok || !token) throw new Error("Authentication expired");
        const response = await fetch("/api/v1/profile/pwa/accent", {
            method: "PUT", credentials: "same-origin",
            headers: { "Content-Type": "application/json", RequestVerificationToken: token },
            body: JSON.stringify({ accentHex: next })
        });
        if (!response.ok) throw new Error("Accent could not be saved");
        lastSavedAccent = next;
        root.dataset.pwaAccent = next;
    } catch {
        if (accent === next) {
            accent = lastSavedAccent;
            applyPalette();
            refreshControls();
        }
        if (status) status.hidden = false;
    }
}
