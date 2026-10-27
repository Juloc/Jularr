// Decorative print only. CSS columns, accessible text and progress stay canonical.
// Freeze visible paint runs instead of cloning an entire chapter for every bend band.
const Limit = 512;
const FontProperties = ['font-family', 'font-size', 'font-weight', 'font-style', 'font-stretch',
    'font-variant', 'font-feature-settings', 'font-kerning', 'letter-spacing', 'word-spacing',
    'text-transform', 'text-decoration', 'text-shadow', 'color', 'direction', 'unicode-bidi'] as const;

export function pagePrint(source: HTMLElement, page: number, width: number, height: number, columnWidth: number, gap: number, rtl: boolean): HTMLElement {
    const viewport = document.createElement('div');
    viewport.className = 'reader__page-print';
    viewport.inert = true;
    viewport.setAttribute('aria-hidden', 'true');
    Object.assign(viewport.style, { width: `${columnWidth}px`, height: `${height}px` });
    // Image neighbours have not been mounted by their canonical adapter yet.
    // Measure one bounded group, never a second document/chapter clone.
    let measurement: HTMLElement | undefined;
    if (!source.isConnected) {
        measurement = document.createElement('div');
        measurement.className = 'reader__measurement';
        measurement.inert = true;
        Object.assign(source.style, { width: `${width}px`, height: `${height}px` });
        measurement.append(source);
        document.querySelector('.reader')!.append(measurement);
    }
    try {
        const bounds = source.getBoundingClientRect();
        const columns = Math.round((width + gap) / (columnWidth + gap));
        // The page margins are the source's padding: the columns lie inside them.
        const inset = getComputedStyle(source);
        const insetLeft = parseFloat(inset.paddingLeft) || 0, insetRight = parseFloat(inset.paddingRight) || 0;
        columnWidth = (bounds.width - insetLeft - insetRight - gap * (columns - 1)) / columns;
        viewport.style.width = `${columnWidth}px`;
        const pitch = columnWidth + gap;
        const left = rtl ? bounds.right - insetRight - source.scrollLeft - columnWidth - page * pitch
            : bounds.left + insetLeft - source.scrollLeft + page * pitch;
        const right = left + columnWidth;
        const visible = (box: DOMRect) => box.width > .01 && box.height > .01
            && box.right > left + .5 && box.left < right - .5 && box.bottom > bounds.top && box.top < bounds.top + height;
        let runs = 0;
        const append = (element: HTMLElement, box: DOMRect, top = box.top) => {
            if (++runs > Limit) throw new Error('Page print exceeds its visible-run limit');
            Object.assign(element.style, { position: 'absolute', left: `${box.left - left}px`, top: `${top - bounds.top}px` });
            viewport.append(element);
        };
        // Static table/rule paint and native list markers are bounded to this page too.
        // They must not disappear merely because their text is printed separately.
        for (const element of Array.from(source.querySelectorAll<HTMLElement>('td,th,table,hr,li'))) {
            const style = getComputedStyle(element);
            const boxes = Array.from(element.getClientRects());
            for (const box of boxes.filter(visible)) {
                const paint = document.createElement('div');
                Object.assign(paint.style, { width: `${box.width}px`, height: `${box.height}px`,
                    boxSizing: 'border-box', backgroundColor: style.backgroundColor,
                    borderTop: style.borderTop, borderRight: style.borderRight, borderBottom: style.borderBottom, borderLeft: style.borderLeft });
                append(paint, box);
                if (element.tagName !== 'LI' || box !== boxes[0] || style.listStyleType === 'none') continue;
                const marker = document.createElement('li');
                for (const property of FontProperties) marker.style.setProperty(property, style.getPropertyValue(property));
                const list = element.parentElement;
                const items = Array.from(list?.children ?? []).filter(item => item.tagName === 'LI');
                const reversed = list?.hasAttribute('reversed');
                let ordinal = Number(list?.getAttribute('start') ?? (reversed ? items.length : 1));
                for (const item of items) {
                    if (item.hasAttribute('value')) ordinal = Number(item.getAttribute('value'));
                    if (item === element) break;
                    ordinal += reversed ? -1 : 1;
                }
                marker.value = ordinal;
                Object.assign(marker.style, { listStyleType: style.listStyleType, listStylePosition: style.listStylePosition,
                    width: `${box.width}px`, lineHeight: style.lineHeight, margin: '0', padding: '0' });
                marker.textContent = '\u200b';
                append(marker, box);
            }
        }
        // Baseline offsets are measured once per font, not once per character or frame.
        const metrics = new Map<string, number>();
        const ruler = document.createElement('span');
        ruler.textContent = 'Hg';
        Object.assign(ruler.style, { position: 'absolute', top: '0', left: '0', whiteSpace: 'pre', lineHeight: '1' });
        const metricHost = document.createElement('div');
        metricHost.className = 'reader__measurement';
        metricHost.append(ruler); source.parentElement!.append(metricHost);
        const range = document.createRange();
        try {
            const walker = document.createTreeWalker(source, NodeFilter.SHOW_TEXT);
            while (walker.nextNode()) {
                const node = walker.currentNode as Text;
                if (!node.textContent?.trim()) continue;
                range.selectNodeContents(node);
                const rects = Array.from(range.getClientRects());
                const lines = rects.filter(visible);
                if (!lines.length) continue;
                const style = getComputedStyle(node.parentElement!);
                if (!measurement && style.visibility !== 'visible' || style.display === 'none') continue;
                const block = node.parentElement!.closest<HTMLElement>('[data-b]');
                let blockOffset = 0;
                if (block) {
                    const before = document.createRange(); before.setStart(block, 0); before.setEnd(node, 0);
                    blockOffset = before.toString().length;
                }
                const flow = (box: DOMRect) => Math.floor(((rtl ? bounds.right - source.scrollLeft - box.right
                    : box.left - bounds.left + source.scrollLeft) + .1) / pitch) * (height + 1) + box.top - bounds.top;
                // Character flow is monotonic by column/line, including RTL text.
                // Binary searches read only the requested line boundaries, not each glyph.
                // A character after an automatic hyphenation break reports the hyphen on the previous line as its first
                // rectangle; its own glyph is the last one.
                const glyphs = (index: number) => {
                    range.setStart(node, index); range.setEnd(node, index + 1);
                    return Array.from(range.getClientRects());
                };
                const at = (index: number) => {
                    const boxes = glyphs(index);
                    return flow(boxes.length > 1 && !/\s/.test(node.data[index]) ? boxes[boxes.length - 1] : range.getBoundingClientRect());
                };
                const lower = (value: number) => {
                    let low = 0, high = node.length;
                    while (low < high) {
                        const middle = (low + high) >>> 1;
                        if (at(middle) < value - .1) low = middle + 1; else high = middle;
                    }
                    return low;
                };
                // Every line of the node, on this page or not: a line ends where the next one begins. Its own box height is no
                // measure of that, because a tight line spacing makes the next line start inside it, and the end of this line
                // would be printed again as the beginning of the next (a long word broken at the line end showed twice).
                const keys = Array.from(new Set(rects.map(flow))).sort((a, b) => a - b);
                const seen = new Set<number>();
                for (const line of lines) {
                    const key = flow(line);
                    if (seen.has(key)) continue;
                    seen.add(key);
                    const following = keys.find(value => value > key + 1);
                    const start = lower(key), end = following === undefined ? node.length : lower(following);
                    if (end <= start) continue;
                    range.setStart(node, start); range.setEnd(node, end);
                    // Only the rectangles on this line: the previous line's hyphen is not part of it.
                    const parts = Array.from(range.getClientRects()).filter(part => Math.abs(flow(part) - key) < .5);
                    const box = parts.length ? new DOMRect(Math.min(...parts.map(part => part.left)), Math.min(...parts.map(part => part.top)),
                        Math.max(...parts.map(part => part.right)) - Math.min(...parts.map(part => part.left)), Math.max(...parts.map(part => part.bottom)) - Math.min(...parts.map(part => part.top)))
                        : range.getBoundingClientRect();
                    const ink = document.createElement('span');
                    for (const property of FontProperties) ink.style.setProperty(property, style.getPropertyValue(property));
                    Object.assign(ink.style, { whiteSpace: 'pre', lineHeight: '1', margin: '0', padding: '0', border: '0' });
                    const preserved = ['pre', 'pre-wrap', 'break-spaces'].includes(style.whiteSpace);
                    const text = preserved ? range.toString() : range.toString().replace(/\s+/g, ' ');
                    // The line's own break shows its hyphen: automatic, or a soft hyphen in the text.
                    const broken = end < node.length && !/\s/.test(node.data[end]) && glyphs(end).length > 1;
                    ink.textContent = broken ? text + '-' : text.endsWith('­') ? text.slice(0, -1) + '-' : text;
                    if (block) {
                        ink.dataset.printBlock = block.dataset.b;
                        ink.dataset.printStart = String(blockOffset + start);
                        ink.dataset.printEnd = String(blockOffset + end);
                        ink.dataset.printPreserved = String(preserved);
                    }
                    const mark = node.parentElement!.closest('mark');
                    if (mark) ink.style.backgroundColor = getComputedStyle(mark).backgroundColor;
                    const signature = FontProperties.map(property => style.getPropertyValue(property)).join('|');
                    let offset = metrics.get(signature);
                    if (offset === undefined) {
                        for (const property of FontProperties) ruler.style.setProperty(property, style.getPropertyValue(property));
                        const metricRange = document.createRange(); metricRange.selectNodeContents(ruler);
                        offset = metricRange.getBoundingClientRect().top - ruler.getBoundingClientRect().top;
                        metrics.set(signature, offset);
                    }
                    append(ink, box, box.top - offset);
                }
            }
        } finally { metricHost.remove(); }
        for (const image of Array.from(source.querySelectorAll<HTMLImageElement>('img'))) {
            const box = image.getBoundingClientRect();
            if (!visible(box) || getComputedStyle(image).display === 'none') continue;
            const copy = image.cloneNode(false) as HTMLImageElement;
            copy.removeAttribute('id'); copy.removeAttribute('data-index'); copy.alt = '';
            copy.removeAttribute('tabindex'); copy.removeAttribute('role'); copy.removeAttribute('aria-label');
            const figure = image.closest<HTMLElement>('[data-figure]');
            if (figure?.dataset.figure) copy.dataset.figure = figure.dataset.figure;
            Object.assign(copy.style, { width: `${box.width}px`, height: `${box.height}px`, maxWidth: 'none', maxHeight: 'none', objectFit: getComputedStyle(image).objectFit });
            append(copy, box);
        }
        viewport.dataset.printRuns = String(runs);
        return viewport;
    } finally { measurement?.remove(); }
}
