// Automatic quality: when the picture stalls again and again on a device that plays in Automatic, the player asks for a lower quality, once per step.
// The rules only, so they can be tested without a video. The values are the server's PlaybackQuality numbers.
export const Automatic = 1;
export const High = 3;
export const Saver = 4;

// A wait of this long while playing counts as a stall; two of them within the window are trouble.
export const StallSeconds = 1.5;
export const StallWindowMs = 60_000;
export const StallLimit = 2;

// Below this measured rate the first step goes straight to the saver.
export const SaverBelowBits = 2_500_000;

export function recordStall(stalls: number[], now: number): number[] {
    return [...stalls.filter(at => now - at < StallWindowMs), now];
}

export function troubled(stalls: number[]): boolean {
    return stalls.length >= StallLimit;
}

// The next lower quality: Automatic goes to High, or to the saver when the measured rate is low; High goes to the saver; nothing goes lower, and an
// explicit Original or Saver is never changed by the player. The measured rate is the loader's estimate in bits per second, 0 when unknown.
export function nextQuality(current: number, measured: number): number | null {
    if (current === Automatic) return measured > 0 && measured < SaverBelowBits ? Saver : High;
    if (current === High) return Saver;
    return null;
}
