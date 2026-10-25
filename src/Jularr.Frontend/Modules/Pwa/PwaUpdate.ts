// Updates are offered only when the installed app comes back to the foreground.
// No polling, offline HTML cache, automatic reload, or interruption of media sessions.
export const VersionAddress = "/pwa/version";

export function hasAnotherBuild(current: string | undefined, next: unknown): next is string {
    return typeof current === "string" && /^[A-F0-9]{16}$/.test(current)
        && typeof next === "string" && /^[A-F0-9]{16}$/.test(next) && next !== current;
}

export function watchAppVersion(): void {
    const standalone = window.matchMedia("(display-mode: standalone)").matches
        || (navigator as Navigator & { standalone?: boolean }).standalone === true;
    if (!standalone) return;

    const current = document.documentElement.dataset.build;
    const banner = document.querySelector<HTMLElement>("[data-pwa-update]");
    const apply = banner?.querySelector<HTMLButtonElement>("[data-pwa-apply]");
    const later = banner?.querySelector<HTMLButtonElement>("[data-pwa-later]");
    if (!current || !banner || !apply || !later) return;

    const dismissedKey = "jularr-pwa-dismissed-build";
    let checking = false;
    let lastCheck = Number.NEGATIVE_INFINITY;
    let offered: string | undefined;

    // Only the person may replace the running document. An active Reader, Player or unsaved form is not reloaded by a background event.
    apply.addEventListener("click", () => location.reload());
    later.addEventListener("click", () => {
        banner.hidden = true;
        if (!offered) return;
        try { sessionStorage.setItem(dismissedKey, offered); }
        catch { /* Storage is optional. */ }
    });

    async function check(): Promise<void> {
        if (document.visibilityState !== "visible" || checking || !banner!.hidden) return;
        const now = Date.now();
        if (now - lastCheck < 5 * 60_000) return;
        lastCheck = now;
        checking = true;
        try {
            const response = await fetch(VersionAddress, { cache: "no-store", credentials: "same-origin" });
            if (!response.ok) return;
            const result: unknown = await response.json();
            const build = typeof result === "object" && result !== null && "build" in result ? result.build : undefined;
            if (!hasAnotherBuild(current, build)) return;
            let dismissed: string | null = null;
            try { dismissed = sessionStorage.getItem(dismissedKey); }
            catch { /* Storage is optional. */ }
            if (dismissed === build) return;
            offered = build;
            banner!.hidden = false;
        } catch {
            // Offline or a restarting server: keep the currently running screen unchanged.
        } finally {
            checking = false;
        }
    }

    document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") void check(); });
    window.addEventListener("pageshow", () => { void check(); });
    void check();
}
