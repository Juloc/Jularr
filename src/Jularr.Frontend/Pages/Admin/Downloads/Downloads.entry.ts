import { onPage } from "../../../Modules/Navigation/PageSwap";
import { dropIndex, scrollSpeed } from "../../../Framework/Components/SortableList";
import { beforeAfterMove } from "./Downloads";

// The queue of a client that reorders: a row is dragged by its handle (or moved with Alt and the arrow keys on it) and the page's move form tells the server
// which download it now stands before; the answer is the page in its new order.

const rowsOf = (body: Element) => Array.from(body.querySelectorAll<HTMLElement>(":scope > .list-row"));
const idOf = (row: HTMLElement | undefined) => row?.querySelector<HTMLElement>("[data-move-id]")?.dataset.moveId ?? "";

function submit(id: string, before: string): void {
    const form = document.querySelector<HTMLFormElement>("#downloads-move");
    if (!form || !id) return;
    form.querySelector<HTMLInputElement>('input[name="operationId"]')!.value = id;
    form.querySelector<HTMLInputElement>('input[name="beforeOperationId"]')!.value = before;
    form.requestSubmit();
}

function drag(handle: HTMLElement, event: PointerEvent): void {
    const row = handle.closest<HTMLElement>(".list-row");
    const body = row?.parentElement;
    if (!row || !body || (event.pointerType === "mouse" && event.button !== 0)) return;
    event.preventDefault();
    const pointerId = event.pointerId;
    const original = rowsOf(body);
    const grab = event.clientY - row.getBoundingClientRect().top;
    let pointer = event.clientY;
    let frame = 0;
    row.classList.add("downloads-row--dragging");

    // The row takes the place its centre has reached among the others.
    const follow = () => {
        const top = pointer - grab - body.getBoundingClientRect().top;
        const others = rowsOf(body).filter(item => item !== row);
        const index = dropIndex(top + row.offsetHeight / 2, others.map(item => item.offsetTop + item.offsetHeight / 2));
        if (others[index] !== row.nextElementSibling) body.insertBefore(row, others[index] ?? null);
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
        row.classList.remove("downloads-row--dragging");
        const rows = rowsOf(body);
        if (cancel || rows.every((item, index) => item === original[index])) {
            original.forEach(item => body.append(item));
            return;
        }

        submit(idOf(row), idOf(rows[rows.indexOf(row) + 1]));
    };
    const up = (released: PointerEvent) => { if (released.pointerId === pointerId) finish(false); };
    const abort = (cancelled: PointerEvent) => { if (cancelled.pointerId === pointerId) finish(true); };
    document.addEventListener("pointermove", move);
    document.addEventListener("pointerup", up);
    document.addEventListener("pointercancel", abort);
    frame = requestAnimationFrame(scroll);
}

let installed = false;

function install(): void {
    if (installed) return;
    installed = true;
    document.addEventListener("pointerdown", event => {
        const handle = (event.target as Element).closest<HTMLElement>(".downloads-grip[data-move-id]");
        if (handle) drag(handle, event);
    });

    // Alt and an arrow move the row one place; the handle keeps the focus for the next step after the page comes back.
    document.addEventListener("keydown", event => {
        const handle = (event.target as Element).closest<HTMLElement>(".downloads-grip[data-move-id]");
        if (!handle || !event.altKey || (event.key !== "ArrowUp" && event.key !== "ArrowDown")) return;
        const row = handle.closest<HTMLElement>(".list-row");
        if (!row?.parentElement) return;
        event.preventDefault();
        const rows = rowsOf(row.parentElement);
        const target = beforeAfterMove(rows.indexOf(row), rows.length, event.key === "ArrowUp" ? -1 : 1);
        if (target !== null) submit(idOf(row), target < rows.length ? idOf(rows[target]) : "");
    });
}

onPage(import.meta.url, install);
