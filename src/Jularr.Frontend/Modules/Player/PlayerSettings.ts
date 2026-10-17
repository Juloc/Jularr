// Interaction helpers for the one live Player settings dialog.
export type SettingsTab = "speed" | "audio" | "subtitles" | "info";

export function normalizedSpeed(value: number): number | null {
    return Number.isFinite(value) ? Math.min(4, Math.max(0.25, Math.round(value * 20) / 20)) : null;
}

export function applyFit(video: Pick<HTMLVideoElement, "style">, value: string | undefined): boolean {
    if (value !== "contain" && value !== "cover" && value !== "scale-down") return false;
    video.style.objectFit = value;
    return true;
}

export function selectSettingsPane(settings: HTMLElement, name: string): name is SettingsTab {
    if (name !== "speed" && name !== "audio" && name !== "subtitles" && name !== "info") return false;
    const selected = Array.from(settings.querySelectorAll<HTMLElement>("[data-settings-pane]"))
        .some(pane => pane.dataset.settingsPane === name);
    if (!selected) return false;
    settings.hidden = false;
    settings.querySelectorAll<HTMLElement>("[data-settings-pane]").forEach(pane => {
        pane.hidden = pane.dataset.settingsPane !== name;
    });
    settings.querySelectorAll<HTMLButtonElement>("[data-settings-tab]").forEach(tab =>
        tab.setAttribute("aria-pressed", String(tab.dataset.settingsTab === name)));
    return true;
}
