// The arithmetic of setting a Smart Page's text into its boxes, apart from the page so it can be tested.

// Cap height is about 0.72 of the font size: the measured size (cap or x-height, against the page's height) gives the font size on a page this high.
export const startSize = (base: number, pageHeight: number): number => Number.isFinite(base) && base > 0 ? base * pageHeight / 0.72 : 12;

// A block's place in fractions of the page: left, top, width and height, each between 0 and 1.
export function blockBox(value: string): { left: number; top: number; width: number; height: number } | undefined {
    const parts = value.trim().split(/\s+/).map(Number);
    if (parts.length !== 4 || parts.some(part => !Number.isFinite(part) || part < 0 || part > 1)) return undefined;
    const [left, top, width, height] = parts;
    return width > 0 && height > 0 ? { left, top, width, height } : undefined;
}

// A single line spans its box like the print by spreading its letters, at most 0.4 em; a translation only a little, so it does not look stretched.
export function letterSpacing(spare: number, letters: number, size: number, translated: boolean): number {
    if (spare <= 0 || letters < 2) return 0;
    return Math.min(size * (translated ? 0.05 : 0.4), spare / letters);
}

// A label printed spread out: a word or two in capitals.
export function spreads(text: string): boolean {
    const words = text.trim().split(/\s+/).filter(Boolean);
    return words.length >= 1 && words.length <= 2 && /\p{L}/u.test(text) && text === text.toUpperCase();
}

// A translation shared out over the boxes a sentence ran through, by their share of the original's letters, at word boundaries; every box gets a word
// while there are words.
export function splitText(text: string, lengths: number[]): string[] {
    const words = text.trim().split(/\s+/).filter(Boolean);
    const total = Math.max(1, lengths.reduce((sum, length) => sum + length, 0));
    const pieces: string[] = [];
    let start = 0, before = 0;
    lengths.forEach((length, index) => {
        before += length;
        const left = lengths.length - index - 1;
        const lower = Math.min(words.length, start + 1), upper = Math.max(lower, words.length - left);
        const end = index === lengths.length - 1 ? words.length : Math.min(upper, Math.max(lower, Math.round(words.length * before / total)));
        pieces.push(words.slice(start, end).join(' '));
        start = Math.max(start, end);
    });
    return pieces;
}
