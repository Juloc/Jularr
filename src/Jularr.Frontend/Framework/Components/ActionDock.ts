// Chosen rows of a list: boxes marked data-dock-select count; while any is chosen the dock shows its bulk zone, with the count, instead of the page's actions.

export function selectedCount(boxes: ArrayLike<{ checked: boolean }>): number {
    return Array.from(boxes).filter(box => box.checked).length;
}

export function update(dock: HTMLElement, boxes: ArrayLike<{ checked: boolean }>): void {
    const count = selectedCount(boxes);
    dock.toggleAttribute('data-selecting', count > 0);
    const bulk = dock.querySelector<HTMLElement>('.action-dock__bulk');
    if (bulk) bulk.hidden = count === 0;
    const label = dock.querySelector<HTMLElement>('[data-dock-count]');
    if (label?.dataset.format) label.textContent = label.dataset.format.replace('{0}', String(count));
}
