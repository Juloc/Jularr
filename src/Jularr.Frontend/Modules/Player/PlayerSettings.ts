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

// A rejected browser probe can force the server's normal HLS planner without
// fabricating a codec capability or changing the actual playing media asset.
export interface PlaybackCapabilities {
    containers: string[];
    videoCodecs: string[];
    audioCodecs: string[];
}

export function hlsFallbackCapabilities(capabilities: PlaybackCapabilities): PlaybackCapabilities | null {
    return capabilities.containers.includes("hls") ? { ...capabilities, containers: ["hls"] } : null;
}

// An active WebVTT track can be shifted without rebuilding its PlaybackPlan.
// Original times are retained so repeated slider changes never accumulate drift.
export function shiftTextCues(track: Pick<TextTrack, "cues"> | null, seconds: number,
    originals: WeakMap<TextTrackCue, { start: number; end: number }>): void {
    if (!track?.cues || !Number.isFinite(seconds)) return;
    for (const cue of Array.from(track.cues)) {
        let original = originals.get(cue);
        if (!original) {
            original = { start: cue.startTime, end: cue.endTime };
            originals.set(cue, original);
        }
        const start = Math.max(0, original.start + seconds);
        const end = Math.max(start, original.end + seconds);
        if (end >= cue.endTime) cue.endTime = end;
        cue.startTime = start;
        cue.endTime = end;
    }
}
