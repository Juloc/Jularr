// A playback that stopped plans itself again: the pauses between fresh sessions and when the picture counts as healed.
export const ReplanAttempts = 6;
export const HealedSeconds = 20;
const FirstDelayMs = 1_000;
const LongestDelayMs = 15_000;

// The wait before the given fresh session (0 is the first): one second, doubling up to fifteen.
export function replanDelay(attempt: number): number {
    return Math.min(FirstDelayMs * 2 ** Math.max(0, attempt), LongestDelayMs);
}

// A picture that played on for a while after a repair starts the counts over.
export function healed(from: number, now: number): boolean {
    return now - from >= HealedSeconds;
}
