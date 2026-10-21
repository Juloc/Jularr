// The import page: whether the list reads as one, and what of the preview is chosen. The server decides everything else.

export type ListState = { state: "empty" } | { state: "invalid" } | { state: "valid"; count: number };

// A list is an object with its entries under "items"; the server checks the rest and names each entry's problem in the preview.
export function listStatus(text: string): ListState {
    if (text.trim() === "") return { state: "empty" };
    try {
        const value: unknown = JSON.parse(text);
        const items = value !== null && typeof value === "object" && !Array.isArray(value) ? (value as { items?: unknown }).items : undefined;
        return Array.isArray(items) ? { state: "valid", count: items.length } : { state: "invalid" };
    } catch {
        return { state: "invalid" };
    }
}

export interface Choice {
    checked: boolean;
}

export interface Selection {
    count: number;
    total: number;
    all: boolean;
}

export function summarize(boxes: readonly Choice[]): Selection {
    const count = boxes.filter(box => box.checked).length;
    return { count, total: boxes.length, all: boxes.length > 0 && count === boxes.length };
}

// "3 of 6 selected" from the page's own template.
export function countText(template: string, selection: Selection): string {
    return template.replace("{0}", String(selection.count)).replace("{1}", String(selection.total));
}
