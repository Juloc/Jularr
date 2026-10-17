// CSS fragments content; this owner moves the shared viewport.
export function initializeReaderPaging(reader: HTMLElement, chapter: () => HTMLElement, changeChapter: (delta: number) => boolean) {
    const canvas = reader.querySelector<HTMLElement>('.reader-preview__canvas')!;
    const paper = reader.querySelector<HTMLElement>('.reader-preview__paper')!;
    const label = reader.querySelector<HTMLElement>('[data-reader-page-position]')!;
    const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
    let spread = 0, pages = 1, columns = 1, step = 1;
    let lastChapter: HTMLElement | undefined;
    let openLast = false;
    let frame = 0;
    const paged = (): boolean => reader.dataset.readerReadingMode === 'paged';
    const rtl = (): boolean => reader.dataset.readerDirection === 'rtl';

    const render = (animate = false): void => {
        label.hidden = !paged();
        const first = spread * columns + 1;
        label.textContent = reader.dataset.pageTemplate!.replace('{0}', String(first)).replace('{1}', String(Math.min(pages, first + columns - 1))).replace('{2}', String(pages));
        reader.dataset.readerPage = String(first);
        reader.dataset.readerPageCount = String(pages);
        const index = Number(chapter().dataset.readerChapter);
        const count = reader.querySelectorAll('[data-reader-chapter]').length;
        reader.querySelectorAll<HTMLButtonElement>('[data-reader-step], [data-reader-turn]').forEach(button => {
            const delta = Number(button.dataset.readerStep ?? button.dataset.readerTurn);
            button.disabled = delta < 0 ? index === 0 && (!paged() || spread === 0) : index === count - 1 && (!paged() || spread >= Math.ceil(pages / columns) - 1);
        });
        if (paged()) paper.scrollTo({ left: (rtl() ? -1 : 1) * spread * step, behavior: animate && !reducedMotion.matches ? 'smooth' : 'instant' });
    };

    const layout = (): void => {
        cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => {
            const active = chapter();
            if (active !== lastChapter) spread = 0;
            lastChapter = active;
            const requested = reader.dataset.readerLayout;
            const minimum = Math.max(300, parseFloat(getComputedStyle(reader).getPropertyValue('--reader-font-size')) * 18);
            columns = requested === 'spread' || requested === 'auto' && canvas.clientWidth - 88 >= minimum * 2 + 40 ? 2 : 1;
            reader.style.setProperty('--reader-columns', String(columns));
            reader.dataset.readerColumns = String(columns);
            active.removeAttribute('data-reader-pad');
            if (!paged()) { paper.scrollLeft = 0; render(); return; }

            // Measure the last fragment, not every word.
            const gap = parseFloat(getComputedStyle(active).columnGap);
            step = active.clientWidth + gap;
            const width = (active.clientWidth - gap * (columns - 1)) / columns;
            const last = active.querySelector(reader.dataset.readerMode === 'pages' ? '.reader-preview__image-page:last-child' : '.reader-preview__prose p:last-child');
            const fragments = last?.getClientRects();
            const end = fragments?.[fragments.length - 1];
            const start = active.getBoundingClientRect();
            const extent = end ? rtl() ? start.right - end.left : end.right - start.left : active.scrollWidth;
            pages = Math.max(1, Math.ceil((extent + gap - 1) / (width + gap)));
            active.toggleAttribute('data-reader-pad', columns === 2 && pages % 2 !== 0);
            spread = openLast ? Math.ceil(pages / columns) - 1 : Math.min(spread, Math.ceil(pages / columns) - 1);
            openLast = false;
            render();
        });
    };
    new ResizeObserver(layout).observe(canvas);
    document.fonts.ready.then(layout);

    const turn = (delta: number): void => {
        if (!paged()) { changeChapter(delta); return; }
        const target = spread + delta;
        if (target >= 0 && target < Math.ceil(pages / columns)) { spread = target; render(true); }
        else { openLast = delta < 0; if (!changeChapter(delta)) openLast = false; }
    };
    const reveal = (element: HTMLElement): void => {
        if (!paged()) { element.scrollIntoView({ block: 'center' }); return; }
        const box = element.getClientRects()[0];
        const start = chapter().getBoundingClientRect();
        spread = Math.max(0, Math.floor((rtl() ? start.right - box.right : box.left - start.left) / step));
        render();
    };

    reader.addEventListener('keydown', event => {
        if (!paged() || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || getSelection()?.toString()
            || (event.target as Element).closest('a, button, input, select, textarea, [contenteditable=true]') || reader.querySelector(':popover-open')) return;
        const delta = event.key === 'PageDown' ? 1 : event.key === 'PageUp' ? -1
            : event.key === 'ArrowRight' ? rtl() ? -1 : 1 : event.key === 'ArrowLeft' ? rtl() ? 1 : -1 : 0;
        if (delta) { event.preventDefault(); turn(delta); }
    });

    // Margins turn; text stays selectable.
    const interactive = 'button, a, input, select, textarea, label';
    let pointer: { x: number; y: number; selected: boolean } | undefined;
    let swiped = false;
    canvas.addEventListener('pointerdown', event => {
        pointer = undefined;
        swiped = false;
        if (!paged() || (event.target as Element).closest(interactive)) return;
        pointer = { x: event.clientX, y: event.clientY, selected: Boolean(getSelection()?.toString()) };
    });
    canvas.addEventListener('pointerup', event => {
        const start = pointer;
        pointer = undefined;
        if (!start || !paged() || start.selected || getSelection()?.toString()) return;
        const x = event.clientX - start.x, y = event.clientY - start.y;
        if (Math.abs(x) > 50 && Math.abs(x) > Math.abs(y) * 1.4) { turn((x < 0 ? 1 : -1) * (rtl() ? -1 : 1)); swiped = true; }
    });
    canvas.addEventListener('pointercancel', () => { pointer = undefined; });
    canvas.addEventListener('click', event => {
        const target = event.target as Element;
        const button = target.closest<HTMLElement>('[data-reader-turn]');
        if (button) { turn(Number(button.dataset.readerTurn)); return; }
        if (!paged() || swiped || getSelection()?.toString() || target.closest(interactive + ', p, h1, small')) return;
        turn((event.clientX < canvas.getBoundingClientRect().left + canvas.clientWidth / 2 ? -1 : 1) * (rtl() ? -1 : 1));
    });
    return { layout, turn, reveal };
}
