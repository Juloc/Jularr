// The chosen rows of the wanted list: how many, whether all are chosen, and what the bulk bar offers for them.

export interface Choice {
    checked: boolean;
}

export interface Selection {
    count: number;
    all: boolean;
    some: boolean;
}

export function summarize(boxes: readonly Choice[]): Selection {
    const count = boxes.filter(box => box.checked).length;
    return { count, all: boxes.length > 0 && count === boxes.length, some: count > 0 && count < boxes.length };
}

// "3 selected" from the page's own template.
export function countText(template: string, count: number): string {
    return template.replace("{0}", String(count));
}

// Brings the bar in line with the boxes: the count, the select-all box, priorities only for chosen rows and the search naming what it will search.
export function applySelection(form: ParentNode, boxes: HTMLInputElement[]): void {
    const selection = summarize(boxes);
    const all = form.querySelector<HTMLInputElement>("[data-wanted-all]");
    if (all) {
        all.checked = selection.all;
        all.indeterminate = selection.some;
    }

    const count = form.querySelector<HTMLElement>("[data-wanted-count]");
    if (count) {
        count.textContent = countText(count.dataset.template ?? "{0}", selection.count);
        count.hidden = selection.count === 0;
    }

    form.querySelectorAll<HTMLButtonElement>("[data-wanted-priority]").forEach(button => { button.disabled = selection.count === 0; });
    const search = form.querySelector<HTMLElement>("[data-wanted-search]");
    const label = search?.querySelector<HTMLElement>("[data-wanted-search-label]");
    const text = selection.count > 0 ? search?.dataset.labelSelected : search?.dataset.labelAll;
    if (label && text) label.textContent = text;
}
