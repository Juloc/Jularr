// What read-aloud needs beyond the voices: the sentence around a spoken word, where a scrolling text follows it and the sleep timer's arithmetic.

// The start and length of the sentence that contains the offset.
export function sentenceAround(text: string, offset: number, segment: (text: string) => Iterable<{ index: number; segment: string }>): { start: number; length: number } {
    let found = { start: 0, length: text.length };
    for (const part of segment(text)) {
        if (part.index <= offset && offset < part.index + part.segment.length) { found = { start: part.index, length: part.segment.length }; break; }
    }

    // Trailing blanks are not painted.
    const trimmed = text.slice(found.start, found.start + found.length).trimEnd().length;
    return { start: found.start, length: Math.max(1, trimmed) };
}

// How far a scrolling text has to move to keep the spoken line a little above the middle; none while it sits in the comfortable band.
export function followDelta(top: number, bounds: { top: number; height: number }): number {
    const low = bounds.top + bounds.height * .15, high = bounds.top + bounds.height * .6;
    return top >= low && top <= high ? 0 : top - (bounds.top + bounds.height * .35);
}

// The remaining whole minutes of a timer, rounded up so that it never shows 0 while it still runs.
export function minutesLeft(endsAt: number, now: number): number {
    return Math.max(0, Math.ceil((endsAt - now) / 60_000));
}

export function timerMinutes(value: unknown): number {
    const minutes = Number(value);
    return Number.isInteger(minutes) && minutes > 0 && minutes <= 180 ? minutes : 0;
}
