// Discover's filter row: a popup of choices applies them when it closes, and only when they changed.

export interface FilterField { name: string; value: string; type: string; checked: boolean }

// The fields' current choice as one text: checked boxes and radios, and filled other fields, in their order.
export function snapshot(fields: Iterable<FilterField>): string {
    const chosen: string[] = [];
    for (const field of fields) {
        if (!field.name || (field.type === 'checkbox' || field.type === 'radio') && !field.checked) continue;
        if (field.type !== 'checkbox' && field.type !== 'radio' && field.value === '') continue;
        chosen.push(`${field.name}=${field.value}`);
    }

    return chosen.join('&');
}
