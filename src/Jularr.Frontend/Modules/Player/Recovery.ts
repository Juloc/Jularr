// A playback that stopped plans itself again without end: the pauses between fresh sessions, when the player tells the person, when the picture counts as healed.
export const AttemptsBeforeNotice = 6;
export const HealedSeconds = 20;
const FirstDelayMs = 1_000;
const LongestDelayMs = 15_000;

// A picture that stands still without any error: the loader is nudged first, a fresh session follows.
export const ResumeStallSeconds = 20;
export const ReplanStallSeconds = 60;

export function stallStep(stalledSeconds: number): "none" | "resume" | "replan" {
    return stalledSeconds >= ReplanStallSeconds ? "replan" : stalledSeconds >= ResumeStallSeconds ? "resume" : "none";
}

// The wait before the given fresh session (0 is the first): one second, doubling up to fifteen.
export function replanDelay(attempt: number): number {
    return Math.min(FirstDelayMs * 2 ** Math.max(0, attempt), LongestDelayMs);
}

// A picture that played on for a while after a repair starts the counts over.
export function healed(from: number, now: number): boolean {
    return now - from >= HealedSeconds;
}
