// Sets every box to one state and tells each one that changed, which is what the dock listens to; a box that cannot be chosen keeps its state.
export function chooseAll(boxes: ArrayLike<{ checked: boolean; disabled?: boolean; dispatchEvent(event: Event): boolean }>, checked: boolean): number {
    let changed = 0;
    for (const box of Array.from(boxes)) {
        if (box.checked === checked || box.disabled) continue;
        box.checked = checked;
        box.dispatchEvent(new Event("change", { bubbles: true }));
        changed++;
    }

    return changed;
}

// A text of the page with its numbered places filled: "{0} of {1}".
export const fill = (template: string | null | undefined, ...values: (string | number)[]): string =>
    values.reduce<string>((text, value, index) => text.replaceAll(`{${index}}`, String(value)), template ?? "");

// The figures of a run: how many went in, how many failed, how many choosable files were left out.
export function tally(outcomes: ArrayLike<string>, choosable: number): { imported: number; failed: number; left: number } {
    const all = Array.from(outcomes);
    const imported = all.filter(outcome => outcome === "imported").length;
    const failed = all.length - imported;
    return { imported, failed, left: Math.max(0, choosable - all.length) };
}
