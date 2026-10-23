// Where a position in the whole book lies. Positions are fractions from 0 to 1 of the book's characters;
// the slider runs from 0 to Resolution and its ticks stand where chapters begin.
export const Resolution = 1000;

export interface Chapter { section: number; title: string; before: number }

// The section a position falls into: the last one that starts at or before it.
export function sectionAt(offsets: number[], total: number, fraction: number): number {
    if (!offsets.length || total <= 0) return 0;
    const target = Math.min(1, Math.max(0, fraction)) * total;
    let low = 0, high = offsets.length - 1;
    while (low < high) {
        const middle = (low + high + 1) >> 1;
        if (offsets[middle] <= target) low = middle; else high = middle - 1;
    }
    return low;
}

// How far into its section a book position lies, from 0 to 1.
export function fractionInSection(offsets: number[], total: number, section: number, fraction: number): number {
    const start = offsets[section] ?? 0, end = offsets[section + 1] ?? total;
    return end > start ? Math.min(1, Math.max(0, (Math.min(1, Math.max(0, fraction)) * total - start) / (end - start))) : 0;
}

// The position of a place in a section within the whole book.
export function bookFraction(before: number, characters: number, total: number, inSection: number): number {
    return total > 0 ? Math.min(1, Math.max(0, (before + characters * Math.min(1, Math.max(0, inSection))) / total)) : 0;
}

// The chapter a position lies in: the last one that starts at or before it, else the first.
export function chapterAt(chapters: Chapter[], fraction: number): Chapter | undefined {
    let found: Chapter | undefined = chapters[0];
    for (const chapter of chapters) {
        if (chapter.before <= fraction + 1e-9) found = chapter; else break;
    }
    return found;
}

// A page's place in its section, so that a seek and the slider agree on it.
export function pageFraction(page: number, pages: number, atEnd: boolean): number {
    return atEnd ? 1 : pages > 1 ? Math.min(1, Math.max(0, page / pages)) : 0;
}

export function pageAt(fraction: number, pages: number, columns: number): number {
    const page = Math.min(pages - 1, Math.max(0, Math.floor(fraction * pages + 1e-6)));
    return page - page % Math.max(1, columns);
}
