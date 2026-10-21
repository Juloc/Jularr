// Sets every box to one state and tells each one that changed, which is what the dock listens to.
export function chooseAll(boxes: ArrayLike<{ checked: boolean; dispatchEvent(event: Event): boolean }>, checked: boolean): number {
    let changed = 0;
    for (const box of Array.from(boxes)) {
        if (box.checked === checked) continue;
        box.checked = checked;
        box.dispatchEvent(new Event("change", { bubbles: true }));
        changed++;
    }

    return changed;
}
