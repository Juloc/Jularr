// What the player does at skippable stretches: the rules only, so they can be tested without a video.
export type SkipMode = "Off" | "Button" | "Automatic";

export interface Marker {
    kind: string;
    start: number;
    end: number;
}

export interface Chapter {
    start: number;
}

export type SkipPlan = { type: "seek"; to: number } | { type: "next" };

const Kinds = ["Intro", "Credits", "Recap", "Preview"];
const Modes: SkipMode[] = ["Off", "Button", "Automatic"];

// Automatic skipping applies where playback arrives at a stretch, not where a viewer seeks into the middle of one.
const ArrivalSeconds = 3;

// The end of the video counts as reached this close to it.
const EndSeconds = 3;

// The markers the page rendered; anything that is not a known kind with a sane range is dropped.
export function parseMarkers(json: string | undefined): Marker[] {
    try {
        const parsed: unknown = JSON.parse(json || "[]");
        if (!Array.isArray(parsed)) return [];
        return parsed.slice(0, 16).filter((item): item is Marker => typeof item === "object" && item !== null
            && Kinds.includes(item.kind) && Number.isFinite(item.start) && Number.isFinite(item.end) && item.start >= 0 && item.end > item.start);
    } catch {
        return [];
    }
}

export function parseMode(value: string | undefined): SkipMode {
    return Modes.find(mode => mode === value) ?? "Button";
}

// An intro or recap follows the intro choice; credits and a preview follow the outro choice.
export function modeFor(kind: string, intro: SkipMode, outro: SkipMode): SkipMode {
    return kind === "Intro" || kind === "Recap" ? intro : outro;
}

// The stretch the position is in, if the profile wants it skipped or offered.
export function activeMarker(markers: Marker[], time: number, intro: SkipMode, outro: SkipMode): Marker | null {
    return markers.find(marker => modeFor(marker.kind, intro, outro) !== "Off" && time >= marker.start && time < marker.end - 0.5) ?? null;
}

export function markerKey(marker: Marker): string {
    return `${marker.kind}:${marker.start}`;
}

// A marker is skipped by itself once, when playback arrives at it.
export function shouldAutoSkip(marker: Marker, time: number, handled: ReadonlySet<string>): boolean {
    return !handled.has(markerKey(marker)) && time - marker.start < ArrivalSeconds;
}

// Credits that run to the end of the video give way to the next episode when it follows by itself; anything else jumps to the end of the stretch.
export function skipPlan(marker: Marker, duration: number, hasNext: boolean, autoplay: boolean): SkipPlan {
    const toEnd = Number.isFinite(duration) && duration > 0 && marker.end >= duration - EndSeconds;
    if ((marker.kind === "Credits" || marker.kind === "Preview") && toEnd && hasNext && autoplay) return { type: "next" };
    return { type: "seek", to: Number.isFinite(duration) && duration > 0 ? Math.min(marker.end, duration) : marker.end };
}

// The chapter a position belongs to: the last one that has begun.
export function chapterIndexAt(chapters: Chapter[], time: number): number {
    let index = -1;
    chapters.forEach((chapter, position) => { if (chapter.start <= time) index = position; });
    return index;
}
