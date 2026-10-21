// The rules of a sortable list, apart from the page: where a dragged row belongs, how a move changes the order, and the numbers.

export type Move = "top" | "up" | "down" | "bottom";

// The new position of a row for a move; the ends stay where they are.
export function targetIndex(index: number, count: number, move: Move): number {
    switch (move) {
        case "top": return 0;
        case "up": return Math.max(0, index - 1);
        case "down": return Math.min(count - 1, index + 1);
        case "bottom": return count - 1;
    }
}

// Which moves a row can still make.
export function available(index: number, count: number): Record<Move, boolean> {
    return { top: index > 0, up: index > 0, down: index < count - 1, bottom: index < count - 1 };
}

// The position a dragged row takes while its centre is at a height: the number of the other rows whose centre lies above it.
export function dropIndex(centre: number, otherCentres: readonly number[]): number {
    return otherCentres.filter(other => other < centre).length;
}

export function moveItem<T>(items: readonly T[], from: number, to: number): T[] {
    const result = [...items];
    const [item] = result.splice(from, 1);
    result.splice(to, 0, item);
    return result;
}

// How far the page scrolls while a row is held near its top or bottom edge: faster the closer to the edge.
export function scrollSpeed(pointer: number, viewport: number, zone = 72, maximum = 18): number {
    if (pointer < zone) return -Math.ceil(maximum * (zone - Math.max(pointer, 0)) / zone);
    if (pointer > viewport - zone) return Math.ceil(maximum * (Math.min(pointer, viewport) - (viewport - zone)) / zone);
    return 0;
}

export function fill(template: string, ...values: (string | number)[]): string {
    return values.reduce<string>((text, value, index) => text.replace(`{${index}}`, String(value)), template);
}
