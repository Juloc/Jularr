import "./SortableList.scss";
import { onPage } from "../../Modules/Navigation/PageSwap";
import { placement } from "./Dropdown";
import { available, dropIndex, fill, scrollSpeed, targetIndex, type Move } from "./SortableList";

// Sortable lists: a row is dragged by its handle with the pointer (mouse, pen or finger), moved with the arrow keys on the handle or by its menu.
// The order is the order of the rows in the document, which is what the form sends; every change tells the form (the page's save dock listens).

const rowsOf = (list: Element) => Array.from(list.querySelectorAll<HTMLElement>(":scope > [data-sortable-row]"));
const calm = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;

function renumber(list: HTMLElement): void {
    rowsOf(list).forEach((row, index) => { row.querySelector(".sortable__number")!.textContent = String(index + 1); });
}

function announce(row: HTMLElement): void {
    const list = row.parentElement!;
    const status = list.closest<HTMLElement>("[data-sortable]")?.querySelector<HTMLElement>("[data-sortable-status]");
    if (status?.dataset.format) status.textContent = fill(status.dataset.format, rowsOf(list).indexOf(row) + 1, rowsOf(list).length);
}

function changed(row: HTMLElement): void {
    const list = row.parentElement!;
    renumber(list);
    announce(row);
    list.dispatchEvent(new Event("change", { bubbles: true }));
}

// The rows that moved glide to their new places instead of jumping.
function glide(list: HTMLElement, change: () => void, except?: HTMLElement): void {
    const before = new Map(rowsOf(list).map(row => [row, row.getBoundingClientRect().top]));
    change();
    if (calm()) return;
    for (const row of rowsOf(list)) {
        const shift = (before.get(row) ?? 0) - row.getBoundingClientRect().top;
        if (row !== except && shift !== 0) row.animate([{ transform: `translateY(${shift}px)` }, { transform: "none" }], { duration: 160, easing: "ease-out" });
    }
}

function place(list: HTMLElement, row: HTMLElement, index: number): void {
    const rows = rowsOf(list).filter(item => item !== row);
    list.insertBefore(row, rows[index] ?? null);
}

export function moveRow(row: HTMLElement, move: Move): void {
    const list = row.parentElement as HTMLElement;
    const rows = rowsOf(list);
    const from = rows.indexOf(row);
    const to = targetIndex(from, rows.length, move);
    if (to === from) return;
    glide(list, () => place(list, row, to));
    changed(row);
}

function drag(handle: HTMLElement, event: PointerEvent): void {
    const row = handle.closest<HTMLElement>("[data-sortable-row]");
    const list = row?.parentElement;
    if (!row || !list || (event.pointerType === "mouse" && event.button !== 0)) return;
    event.preventDefault();
    // The row moves in the document while it is dragged, which ends any pointer capture: the pointer is followed on the document instead.
    const pointerId = event.pointerId;
    const original = rowsOf(list);
    const grab = event.clientY - row.getBoundingClientRect().top;
    let pointer = event.clientY;
    let offset = 0;
    let frame = 0;
    row.classList.add("sortable__row--dragging");
    list.classList.add("sortable__rows--dragging");

    // The row follows the pointer; when its centre passes a neighbour's it takes that place.
    const follow = () => {
        const top = pointer - grab - list.getBoundingClientRect().top;
        const others = rowsOf(list).filter(item => item !== row);
        const index = dropIndex(top + row.offsetHeight / 2, others.map(item => item.offsetTop + item.offsetHeight / 2));
        if (index !== rowsOf(list).indexOf(row)) glide(list, () => place(list, row, index), row);
        offset = top - row.offsetTop;
        row.style.setProperty("--drag-offset", `${offset}px`);
    };
    const scroll = () => {
        const speed = scrollSpeed(pointer, window.innerHeight);
        if (speed !== 0) { window.scrollBy(0, speed); follow(); }
        frame = requestAnimationFrame(scroll);
    };
    const move = (moved: PointerEvent) => { if (moved.pointerId === pointerId) { pointer = moved.clientY; follow(); } };
    const finish = (cancel: boolean) => {
        cancelAnimationFrame(frame);
        document.removeEventListener("pointermove", move);
        document.removeEventListener("pointerup", up);
        document.removeEventListener("pointercancel", abort);
        document.removeEventListener("keydown", key);
        if (cancel) original.forEach(item => list.append(item));
        const settle = cancel ? 0 : offset;
        row.classList.remove("sortable__row--dragging");
        list.classList.remove("sortable__rows--dragging");
        row.style.removeProperty("--drag-offset");
        if (!calm() && settle !== 0) row.animate([{ transform: `translateY(${settle}px)` }, { transform: "none" }], { duration: 160, easing: "ease-out" });
        if (!cancel && rowsOf(list).some((item, index) => item !== original[index])) changed(row);
        else if (cancel) renumber(list);
    };
    const up = (released: PointerEvent) => { if (released.pointerId === pointerId) finish(false); };
    const abort = (cancelled: PointerEvent) => { if (cancelled.pointerId === pointerId) finish(true); };
    const key = (pressed: KeyboardEvent) => { if (pressed.key === "Escape") { pressed.preventDefault(); finish(true); } };
    document.addEventListener("pointermove", move);
    document.addEventListener("pointerup", up);
    document.addEventListener("pointercancel", abort);
    document.addEventListener("keydown", key);
    frame = requestAnimationFrame(scroll);
    follow();
}

let installed = false;

function install(): void {
    if (installed) return;
    installed = true;
    document.addEventListener("pointerdown", event => {
        const handle = (event.target as Element).closest<HTMLElement>("[data-sortable-handle]");
        if (handle) drag(handle, event);
    });
    document.addEventListener("keydown", event => {
        const handle = (event.target as Element).closest<HTMLElement>("[data-sortable-handle]");
        const moves: Record<string, Move> = { ArrowUp: "up", ArrowDown: "down", Home: "top", End: "bottom" };
        if (!handle || !(event.key in moves)) return;
        event.preventDefault();
        moveRow(handle.closest<HTMLElement>("[data-sortable-row]")!, moves[event.key]);
        handle.focus();
    });
    document.addEventListener("click", event => {
        const item = (event.target as Element).closest<HTMLElement>("[data-sortable-move]");
        if (!item || item.hasAttribute("disabled")) return;
        const menu = item.closest<HTMLElement>(".sortable__menu")!;
        moveRow(item.closest<HTMLElement>("[data-sortable-row]")!, item.dataset.sortableMove as Move);
        menu.hidePopover();
    });
    // A row's menu opens beside its button and offers only the moves that change something.
    document.addEventListener("beforetoggle", event => {
        const menu = event.target as HTMLElement;
        if (!menu.matches?.(".sortable__menu") || (event as ToggleEvent).newState !== "open") return;
        const row = menu.closest<HTMLElement>("[data-sortable-row]")!;
        const rows = rowsOf(row.parentElement!);
        const moves = available(rows.indexOf(row), rows.length);
        menu.querySelectorAll<HTMLButtonElement>("[data-sortable-move]").forEach(item => { item.disabled = !moves[item.dataset.sortableMove as Move]; });
        const anchor = row.querySelector<HTMLElement>("[data-sortable-menu]")!.getBoundingClientRect();
        const width = 200;
        const placed = placement({ top: anchor.top, bottom: anchor.bottom, left: anchor.right - width, width }, window.innerHeight, window.innerWidth, 170);
        menu.style.setProperty("--menu-top", `${placed.top}px`);
        menu.style.setProperty("--menu-left", `${placed.left}px`);
    }, true);
    window.addEventListener("resize", () => document.querySelectorAll<HTMLElement>(".sortable__menu:popover-open").forEach(menu => menu.hidePopover()));
}

onPage(import.meta.url, () => {
    install();
    document.querySelectorAll<HTMLElement>("[data-sortable]").forEach(list => { list.dataset.sortableReady = "true"; });
});
