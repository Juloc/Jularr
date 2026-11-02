// Chosen rows of a list: boxes marked data-dock-select count; while any is chosen the dock shows its bulk zone, with the count, instead of the page's actions.
// A box marked data-dock-select-all (in the list's head) chooses every row of the page and shows whether all or some are chosen.

export function selectedCount(boxes: ArrayLike<{ checked: boolean }>): number {
    return Array.from(boxes).filter(box => box.checked).length;
}

// The state of the select-all box for the rows' boxes.
export function allState(boxes: ArrayLike<{ checked: boolean }>): { checked: boolean; indeterminate: boolean } {
    const count = selectedCount(boxes);
    return { checked: boxes.length > 0 && count === boxes.length, indeterminate: count > 0 && count < boxes.length };
}

export function update(dock: HTMLElement | null, boxes: ArrayLike<{ checked: boolean }>, all?: { checked: boolean; indeterminate: boolean } | null): void {
    const count = selectedCount(boxes);
    if (all) {
        const state = allState(boxes);
        all.checked = state.checked;
        all.indeterminate = state.indeterminate;
    }

    if (!dock) return;
    dock.toggleAttribute('data-selecting', count > 0);
    const bulk = dock.querySelector<HTMLElement>('.action-dock__bulk');
    if (bulk) bulk.hidden = count === 0;
    const label = dock.querySelector<HTMLElement>('[data-dock-count]');
    if (label?.dataset.format) label.textContent = label.dataset.format.replace('{0}', String(count));
}

// Shift and a click choose the rows between the box clicked before and this one as this one now is: the positions from one to the other, both included.
export function rangeBetween(from: number, to: number): number[] {
    if (from < 0 || to < 0) return [];
    const start = Math.min(from, to);
    return Array.from({ length: Math.abs(to - from) + 1 }, (_, index) => start + index);
}
