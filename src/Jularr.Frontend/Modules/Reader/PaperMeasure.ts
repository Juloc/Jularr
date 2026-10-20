// The visible Reader and its background ruler use the same CSS-column measurement.
export function paperPageCount(content: HTMLElement, columns: number, gap: number, rtl: boolean): number {
    const range = document.createRange();
    range.selectNodeContents(content);
    const bounds = content.getBoundingClientRect();
    const scrolled = Math.abs(content.scrollLeft);
    let extent = 0;
    for (const box of Array.from(range.getClientRects())) extent = Math.max(extent, (rtl ? bounds.right - box.left : box.right - bounds.left) + scrolled);
    range.detach();
    const columnWidth = (content.clientWidth - gap * (columns - 1)) / columns;
    return Math.max(1, Math.ceil((extent + gap - 1) / (columnWidth + gap)));
}
