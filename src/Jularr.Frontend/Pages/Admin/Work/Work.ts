// What a person chose for the columns of a title's tables: their order and which are hidden.
export interface ColumnState {
    order: string[];
    hidden: string[];
}

// A stored choice cut down to the columns that exist; new columns follow the known ones and a first visit hides the default ones.
export function normalizeColumns(stored: unknown, available: string[], hiddenByDefault: string[]): ColumnState {
    const source = (stored && typeof stored === 'object' ? stored : {}) as { order?: unknown; hidden?: unknown };
    const known = Array.isArray(source.order) ? source.order.filter((key): key is string => typeof key === 'string' && available.includes(key)) : [];
    const order = [...new Set([...known, ...available])];
    const chosen = Array.isArray(source.hidden) ? source.hidden.filter((key): key is string => typeof key === 'string') : null;
    const hidden = (chosen ?? hiddenByDefault).filter(key => available.includes(key));
    return { order, hidden: [...new Set(hidden)] };
}

// Numbers compare as numbers, everything else as text in the person's language.
export function compareValues(left: string, right: string): number {
    const number = /^-?\d+(\.\d+)?$/;
    if (number.test(left) && number.test(right)) return Number(left) - Number(right);
    return left.localeCompare(right, undefined, { numeric: true, sensitivity: 'base' });
}

export function moveItem<T>(items: T[], from: number, to: number): T[] {
    if (from === to || from < 0 || to < 0 || from >= items.length || to >= items.length) return items;
    const moved = [...items];
    const [item] = moved.splice(from, 1);
    moved.splice(to, 0, item);
    return moved;
}
