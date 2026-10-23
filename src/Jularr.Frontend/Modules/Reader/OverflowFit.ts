// How many of the items, kept in this order, fit into a bar of the given width; the rest go to the More menu.
// `more` is the width of the More button, reserved when it is shown anyway (`always`) or as soon as one item has to leave.
export function visibleCount(widths: number[], space: number, gap: number, more: number, always: boolean): number {
    const used = (count: number): number => widths.slice(0, count).reduce((sum, width) => sum + width, 0) + Math.max(0, count - 1) * gap;
    const reserve = (count: number): number => count > 0 ? gap + more : more;
    if (!always && used(widths.length) <= space) return widths.length;
    let count = widths.length;
    while (count > 0 && used(count) + reserve(count) > space) count--;
    return count;
}
