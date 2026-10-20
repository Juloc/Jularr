import { onPage } from '../../../Modules/Navigation/PageSwap';
import { compareValues, moveItem, normalizeColumns, type ColumnState } from './Work';

const storagePrefix = 'jularr.work.columns.';

function initializeSeasons(): void {
    document.querySelectorAll<HTMLButtonElement>('[data-season-toggle]').forEach(button => {
        if (button.dataset.workReady) return;
        button.dataset.workReady = 'true';
        const panel = document.getElementById(button.dataset.seasonToggle ?? '');
        if (!panel) return;
        const set = (open: boolean): void => {
            panel.hidden = !open;
            button.setAttribute('aria-expanded', String(open));
            button.closest('.work-season')?.classList.toggle('work-season--open', open);
        };
        set(panel.dataset.open === 'true');
        button.addEventListener('click', () => set(panel.hidden));
    });

    document.querySelectorAll<HTMLButtonElement>('[data-collapse-all]').forEach(button => {
        if (button.dataset.workReady) return;
        button.dataset.workReady = 'true';
        button.addEventListener('click', () => {
            const toggles = Array.from(document.querySelectorAll<HTMLButtonElement>('[data-season-toggle]'));
            const anyOpen = toggles.some(toggle => toggle.getAttribute('aria-expanded') === 'true');
            toggles.forEach(toggle => { if ((toggle.getAttribute('aria-expanded') === 'true') === anyOpen) toggle.click(); });
        });
    });
}

function initializeVersions(): void {
    document.querySelectorAll<HTMLButtonElement>('[data-versions-toggle]').forEach(button => {
        if (button.dataset.workReady) return;
        button.dataset.workReady = 'true';
        button.addEventListener('click', () => {
            const open = button.getAttribute('aria-expanded') !== 'true';
            button.setAttribute('aria-expanded', String(open));
            button.closest('tbody, .work-strip-group')?.querySelectorAll<HTMLElement>('tr.work-file, .work-strip--file').forEach(row => { row.hidden = !open; });
        });
    });
}

function initializeDialogs(): void {
    document.querySelectorAll<HTMLElement>('[data-dialog-open]').forEach(opener => {
        if (opener.dataset.workReady) return;
        opener.dataset.workReady = 'true';
        opener.addEventListener('click', () => (document.getElementById(opener.dataset.dialogOpen ?? '') as HTMLDialogElement | null)?.showModal());
    });
    document.querySelectorAll<HTMLElement>('[data-dialog-close]').forEach(closer => {
        if (closer.dataset.workReady) return;
        closer.dataset.workReady = 'true';
        closer.addEventListener('click', () => closer.closest('dialog')?.close());
    });
    document.querySelectorAll<HTMLDialogElement>('dialog.work-dialog[data-open="true"]').forEach(dialog => { if (!dialog.open) dialog.showModal(); });
}

function tablesOfKind(kind: string): HTMLTableElement[] {
    return Array.from(document.querySelectorAll<HTMLTableElement>('[data-work-table]')).filter(table => table.dataset.workTable === kind);
}

function readState(kind: string, table: HTMLTableElement): ColumnState {
    const heads = Array.from(table.querySelectorAll<HTMLElement>('thead th[data-col]')).filter(head => head.dataset.col !== 'actions');
    const available = heads.map(head => head.dataset.col!);
    const hiddenByDefault = heads.filter(head => head.dataset.hidden === 'true').map(head => head.dataset.col!);
    let stored: unknown = null;
    try { stored = JSON.parse(localStorage.getItem(storagePrefix + kind) ?? 'null'); } catch { stored = null; }
    return normalizeColumns(stored, available, hiddenByDefault);
}

// Every row follows the order of the columns, and the hidden ones leave the screen.
function applyState(kind: string, state: ColumnState): void {
    for (const table of tablesOfKind(kind)) {
        for (const row of Array.from(table.querySelectorAll<HTMLTableRowElement>('tr'))) {
            const cells = new Map<string, HTMLElement>();
            for (const cell of Array.from(row.children) as HTMLElement[]) if (cell.dataset.col) cells.set(cell.dataset.col, cell);
            for (const column of [...state.order, 'actions']) {
                const cell = cells.get(column);
                if (!cell) continue;
                row.appendChild(cell);
                cell.hidden = state.hidden.includes(column);
            }
        }
    }

    // The facts of a strip follow the same choice: its order, and the hidden columns leave.
    document.querySelectorAll<HTMLElement>(`[data-work-strips="${CSS.escape(kind)}"] .work-strip__facts`).forEach(facts => {
        const items = new Map<string, HTMLElement>();
        for (const item of Array.from(facts.children) as HTMLElement[]) if (item.dataset.col) items.set(item.dataset.col, item);
        for (const column of state.order) {
            const item = items.get(column);
            if (!item) continue;
            facts.appendChild(item);
            item.hidden = state.hidden.includes(column);
        }
    });
}

function saveState(kind: string, state: ColumnState): void {
    try { localStorage.setItem(storagePrefix + kind, JSON.stringify(state)); } catch { /* the choice then only lasts for this page */ }
}

function sortTables(kind: string, column: string, direction: 'ascending' | 'descending'): void {
    for (const table of tablesOfKind(kind)) {
        const bodies = Array.from(table.tBodies);
        const value = (body: HTMLTableSectionElement): string => {
            const cell = body.querySelector<HTMLElement>(`tr:first-child [data-col="${CSS.escape(column)}"]`);
            return cell?.dataset.value ?? cell?.textContent?.trim() ?? '';
        };
        bodies.sort((left, right) => (direction === 'ascending' ? 1 : -1) * compareValues(value(left), value(right)));
        bodies.forEach(body => table.appendChild(body));
        table.querySelectorAll<HTMLElement>('thead th[data-col]').forEach(head => {
            if (head.dataset.col === column) head.setAttribute('aria-sort', direction);
            else head.removeAttribute('aria-sort');
        });
    }
}

function renderPicker(picker: HTMLElement, kind: string, table: HTMLTableElement, state: ColumnState): void {
    const list = picker.querySelector<HTMLUListElement>('.work-columns__list');
    if (!list) return;
    const labels = new Map<string, string>();
    table.querySelectorAll<HTMLElement>('thead th[data-col]').forEach(head => labels.set(head.dataset.col!, head.dataset.label ?? head.dataset.col!));
    list.replaceChildren();
    state.order.forEach((column, index) => {
        const item = document.createElement('li');
        item.dataset.col = column;
        const grip = document.createElement('button');
        grip.type = 'button';
        grip.className = 'work-columns__grip';
        grip.textContent = '⋮⋮';
        grip.setAttribute('aria-label', `${picker.dataset.moveLabel ?? ''}: ${labels.get(column) ?? column}`);
        const label = document.createElement('label');
        const box = document.createElement('input');
        box.type = 'checkbox';
        box.checked = !state.hidden.includes(column);
        box.addEventListener('change', () => {
            state.hidden = box.checked ? state.hidden.filter(key => key !== column) : [...state.hidden, column];
            saveState(kind, state);
            applyState(kind, state);
        });
        label.append(box, document.createTextNode(' ' + (labels.get(column) ?? column)));
        item.append(grip, label);
        const move = (to: number): void => {
            state.order = moveItem(state.order, state.order.indexOf(column), to);
            saveState(kind, state);
            applyState(kind, state);
            renderPicker(picker, kind, table, state);
            picker.querySelectorAll<HTMLElement>('.work-columns__list li')[to]?.querySelector<HTMLElement>('.work-columns__grip')?.focus();
        };
        grip.addEventListener('keydown', event => {
            if (event.key === 'ArrowUp') { event.preventDefault(); move(index - 1); }
            if (event.key === 'ArrowDown') { event.preventDefault(); move(index + 1); }
        });
        grip.addEventListener('pointerdown', event => {
            event.preventDefault();
            grip.setPointerCapture(event.pointerId);
            let current = state.order.indexOf(column);
            const over = (moveEvent: PointerEvent): void => {
                const target = document.elementFromPoint(moveEvent.clientX, moveEvent.clientY)?.closest('li');
                const to = target ? Array.from(list.children).indexOf(target) : -1;
                if (to < 0 || to === current) return;
                state.order = moveItem(state.order, current, to);
                list.insertBefore(item, to > current ? list.children[to].nextSibling : list.children[to]);
                current = to;
            };
            const end = (): void => {
                grip.removeEventListener('pointermove', over);
                grip.removeEventListener('pointerup', end);
                grip.removeEventListener('pointercancel', end);
                saveState(kind, state);
                applyState(kind, state);
            };
            grip.addEventListener('pointermove', over);
            grip.addEventListener('pointerup', end);
            grip.addEventListener('pointercancel', end);
        });
        list.appendChild(item);
    });
}

function initializeTable(table: HTMLTableElement): void {
    if (table.dataset.workReady) return;
    table.dataset.workReady = 'true';
    const kind = table.dataset.workTable ?? 'units';
    const state = readState(kind, table);
    applyState(kind, state);

    let direction: 'ascending' | 'descending' = 'descending';
    table.querySelectorAll<HTMLButtonElement>('button[data-sort]').forEach(button => button.addEventListener('click', () => {
        const column = button.dataset.sort!;
        direction = table.querySelector(`th[data-col="${CSS.escape(column)}"]`)?.getAttribute('aria-sort') === 'descending' ? 'ascending' : 'descending';
        sortTables(kind, column, direction);
    }));

    const picker = document.querySelector<HTMLElement>('[data-column-picker]');
    if (!picker) return;
    const gears = [table.querySelector<HTMLButtonElement>('[data-columns-open]'), ...Array.from(document.querySelectorAll<HTMLButtonElement>(`[data-columns-kind="${CSS.escape(kind)}"]`))];
    for (const gear of gears) {
        if (!gear || gear.dataset.workReady) continue;
        gear.dataset.workReady = 'true';
        gear.addEventListener('click', event => {
            event.stopPropagation();
            if (!picker.hidden) { picker.hidden = true; return; }
            renderPicker(picker, kind, table, state);
            picker.hidden = false;

            // Beside narrow strips the list is a sheet from the bottom; otherwise it hangs below the gear.
            const sheet = document.querySelector('.work-strips') !== null && getComputedStyle(document.querySelector('.work-strips')!).display !== 'none';
            picker.classList.toggle('work-columns--sheet', sheet);
            picker.style.left = picker.style.top = '';
            if (sheet) return;
            const box = gear.getBoundingClientRect();
            picker.style.left = `${Math.max(8, box.right - 290)}px`;
            picker.style.top = `${Math.max(8, Math.min(box.bottom + 6, window.innerHeight - picker.offsetHeight - 8))}px`;
        });
    }
}

// A mouse drags the strips sideways as a finger does; touch scrolls them natively, and they all slide together because a season is one scroller.
function initializeStrips(): void {
    document.querySelectorAll<HTMLElement>('.work-strips').forEach(scroller => {
        if (scroller.dataset.workReady) return;
        scroller.dataset.workReady = 'true';
        scroller.addEventListener('pointerdown', event => {
            if (event.pointerType !== 'mouse' || event.button !== 0 || (event.target as HTMLElement).closest('button, a, input, select, label')) return;
            const startX = event.clientX;
            const startScroll = scroller.scrollLeft;
            scroller.setPointerCapture(event.pointerId);
            scroller.classList.add('work-strips--dragging');
            const move = (moveEvent: PointerEvent): void => { scroller.scrollLeft = startScroll - (moveEvent.clientX - startX); };
            const end = (): void => {
                scroller.classList.remove('work-strips--dragging');
                scroller.removeEventListener('pointermove', move);
                scroller.removeEventListener('pointerup', end);
                scroller.removeEventListener('pointercancel', end);
            };
            scroller.addEventListener('pointermove', move);
            scroller.addEventListener('pointerup', end);
            scroller.addEventListener('pointercancel', end);
        });
    });
}

// The files an edit applies to are written into the dialog when it opens: the one of a row, or the ticked boxes.
function initializeFileEdit(): void {
    const dialog = document.getElementById('work-files-edit') as HTMLDialogElement | null;
    const holder = dialog?.querySelector<HTMLElement>('[data-edit-ids]');
    if (!dialog || !holder) return;
    const open = (ids: string[]): void => {
        if (ids.length === 0) return;
        holder.replaceChildren(...ids.map(id => Object.assign(document.createElement('input'), { type: 'hidden', name: 'storedFileIds', value: id })));
        dialog.showModal();
    };
    document.querySelectorAll<HTMLElement>('[data-edit-file]').forEach(button => {
        if (button.dataset.workReady) return;
        button.dataset.workReady = 'true';
        button.addEventListener('click', () => open([button.dataset.editFile ?? '']));
    });
    document.querySelectorAll<HTMLElement>('[data-edit-selected]').forEach(button => {
        if (button.dataset.workReady) return;
        button.dataset.workReady = 'true';
        button.addEventListener('click', () => open(Array.from(document.querySelectorAll<HTMLInputElement>('input[name="storedFileIds"]:checked')).map(box => box.value)));
    });
}

function initializeSelection(): void {
    document.querySelectorAll<HTMLInputElement>('[data-select-all]').forEach(all => {
        if (all.dataset.workReady) return;
        all.dataset.workReady = 'true';
        const boxes = (): HTMLInputElement[] => Array.from(all.closest('table')?.querySelectorAll<HTMLInputElement>('input[name="storedFileIds"]') ?? []);
        all.addEventListener('change', () => boxes().forEach(box => { box.checked = all.checked; }));
        all.closest('table')?.addEventListener('change', event => {
            if (event.target === all) return;
            const items = boxes();
            all.checked = items.length > 0 && items.every(box => box.checked);
            all.indeterminate = !all.checked && items.some(box => box.checked);
        });
    });
}

function initializePicker(): void {
    document.addEventListener('click', event => {
        const picker = document.querySelector<HTMLElement>('[data-column-picker]');
        if (picker && !picker.hidden && !picker.contains(event.target as Node)) picker.hidden = true;
    });
    document.addEventListener('keydown', event => {
        const picker = document.querySelector<HTMLElement>('[data-column-picker]');
        if (event.key === 'Escape' && picker && !picker.hidden) picker.hidden = true;
    });
}

let pickerReady = false;
onPage(import.meta.url, () => {
    initializeSeasons();
    initializeVersions();
    initializeDialogs();
    initializeSelection();
    initializeFileEdit();
    initializeStrips();
    document.querySelectorAll<HTMLTableElement>('[data-work-table]').forEach(initializeTable);
    if (!pickerReady) { pickerReady = true; initializePicker(); }
});
