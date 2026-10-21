// The page filter of a list: every word of the query must occur in a row's filter text. Rows that do not match are hidden, the list's empty state shows when none is left.

export function matches(text: string, query: string): boolean {
    return query.toLowerCase().split(/\s+/).filter(Boolean).every(word => text.includes(word));
}

export function applyFilter(list: ParentNode, query: string): number {
    let shown = 0;
    list.querySelectorAll<HTMLElement>('.list-row[data-list-text]').forEach(row => {
        const visible = matches(row.dataset.listText ?? '', query);
        row.hidden = !visible;
        if (visible) shown++;
    });
    const empty = list.querySelector<HTMLElement>('.list-empty');
    if (empty) empty.hidden = shown > 0;
    return shown;
}
