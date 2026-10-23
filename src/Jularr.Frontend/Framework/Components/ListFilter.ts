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

// The find field of the administration menu: an entry stays while every word occurs in its text (its name and the pages behind it) or in its group's name;
// a group with no entry left is hidden. Returns how many entries are shown.
export function applyMenuFilter(menu: ParentNode, query: string): number {
    let shown = 0;
    menu.querySelectorAll<HTMLElement>('.menu-group').forEach(group => {
        const groupText = group.dataset.menuText ?? '';
        let any = false;
        group.querySelectorAll<HTMLElement>('.menu-entry').forEach(entry => {
            const visible = matches(((entry.dataset.menuText ?? '') + ' ' + groupText).toLowerCase(), query);
            entry.hidden = !visible;
            if (visible) {
                any = true;
                shown++;
            }
        });
        group.hidden = !any;
    });
    return shown;
}
