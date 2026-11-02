import type { BookGroup, initializeBookRenderer } from './BookRenderer';
import type { ReaderSettings } from './Settings';
import { paperPageCount } from './PaperMeasure';
import { initializeBookPages } from './BookPages';
import { readerLifetime } from './Lifetime';
import { pageAt, pageFraction } from './ProgressMath';

// Canonical CSS columns own pagination; decorative faces never own progress.
export function initializePaperPages(reader: HTMLElement, content: HTMLElement, settings: ReaderSettings, changed: () => void, painted: () => void = () => {}) {
    const signal = readerLifetime();
    const paper = reader.querySelector<HTMLElement>('.reader__paper')!;
    const canvas = reader.querySelector<HTMLElement>('.reader__canvas')!;
    const motion = matchMedia('(prefers-reduced-motion: reduce)');
    const label = reader.querySelector<HTMLElement>('[data-role=pages]');
    const bookPages = initializeBookPages(reader, content);
    let columns = 1, pages = 1, spread = 0, step = 1, frame = 0, navigation = 0;
    let book: ReturnType<typeof initializeBookRenderer> | undefined;
    let loading: Promise<void> | undefined;
    let failed = false;
    // Recompute the chapter size only when a layout/content change happens, not on every page turn.
    let oversized = false;
    // A place asked for before the first layout waits for it; the pages it is measured in do not exist yet.
    let measured = false;
    let waiting: (() => void) | undefined;
    const edge = (delta: number) => reader.querySelector<HTMLAnchorElement>(`[data-role=${delta < 0 ? 'previous' : 'next'}]`);
    const blocks = () => Array.from(content.querySelectorAll<HTMLElement>('[data-b]'));
    // The first block that begins in view; a block that began on an earlier page and runs on into this one is not where the page starts.
    const current = (): HTMLElement | undefined => {
        const bounds = content.getBoundingClientRect();
        const visible = (box: DOMRect) => settings.mode === 'paged' ? box.right > bounds.left + 24 && box.left < bounds.right - 24 : box.bottom > bounds.top + 24;
        const all = blocks();
        if (settings.mode !== 'paged') return all.find(block => Array.from(block.getClientRects()).some(visible));
        return all.find(block => { const box = block.getClientRects()[0]; return box && visible(box); }) ?? all.find(block => Array.from(block.getClientRects()).some(visible));
    };
    const group = (target: number): BookGroup | undefined => {
        if (target >= 0 && target < Math.ceil(pages / columns)) return { source: content, page: target * columns, count: pages };
        const link = edge(target < 0 ? -1 : 1);
        if (!link) return;
        const placeholder = document.createElement('article');
        placeholder.className = 'reader__content';
        const caption = document.createElement('p');
        caption.textContent = link.textContent;
        placeholder.append(caption);
        return { source: placeholder, page: 0, count: 1 };
    };
    // Once the whole book is counted the numbers are the book's, else the chapter's.
    const labelPages = (): void => {
        if (!label) return;
        label.hidden = settings.mode !== 'paged';
        const counted = Number(reader.dataset.bookPageCount), before = Number(reader.dataset.bookPageBefore);
        const whole = reader.dataset.bookPageState === 'ready' && Number.isInteger(counted) && counted > 0 && Number.isInteger(before);
        const offset = whole ? before : 0;
        const template = whole ? reader.dataset.pageLabel! : reader.dataset.chapterPageLabel!;
        label.textContent = template.replace('{0}', String(offset + spread * columns + 1)).replace('{1}', String(offset + Math.min(pages, (spread + 1) * columns)))
            .replace('{2}', String(whole ? counted : pages));
    };
    new MutationObserver(labelPages).observe(reader, { attributes: true, attributeFilter: ['data-book-page-count', 'data-book-page-before'] });
    const paint = (): void => {
        const offset = spread * step * (settings.direction === 'rtl' ? -1 : 1);
        content.scrollTo({ left: settings.mode === 'paged' ? offset : 0, behavior: 'instant' });
        reader.dataset.page = String(spread * columns + 1);
        reader.dataset.pageCount = String(pages);
        reader.dataset.columns = String(columns);
        reader.querySelector<HTMLElement>('[data-role=turn-tools]')!.hidden = settings.mode !== 'paged';
        bookPages.position(spread * columns + 1);
        labelPages();
        reader.querySelectorAll<HTMLButtonElement>('[data-turn]').forEach(button => {
            const delta = Number(button.dataset.turn);
            button.disabled = delta < 0 ? spread === 0 && !edge(-1) : spread + 1 >= Math.ceil(pages / columns) && !edge(1);
        });
        const wantsBook = settings.mode === 'paged' && settings.paging === 'book' && reader.dataset.selecting !== 'true';
        reader.querySelector<HTMLElement>('[data-role=animation-error]')!.hidden = !failed && !(wantsBook && oversized);
        const active = wantsBook && !failed && !oversized;
        if (book && !book.update({ current: spread, columns, minimum: edge(-1) ? -1 : 0,
            maximum: Math.ceil(pages / columns) - (edge(1) ? 0 : 1), group,
            next: (at, delta) => at + delta, number: at => at * columns + 1, commit }, active)) failed = true;
        // Without the book view the flat page shows; it stays hidden only while the book is still to come.
        if (!active && !book && reader.dataset.bookReady !== 'false') reader.dataset.bookReady = 'false';
        if (active && !book && !loading) loading = import('./BookRenderer').then(module => {
            book = module.initializeBookRenderer(reader);
            paint();
        }).catch(() => {
            failed = true;
            reader.dataset.bookReady = 'false';
            reader.querySelector<HTMLElement>('.reader-book')?.setAttribute('hidden', 'hidden');
            reader.querySelector<HTMLElement>('[data-role=animation-error]')?.removeAttribute('hidden');
        });
        painted();
    };
    const commit = (target: number): void => {
        if (target < 0 || target >= Math.ceil(pages / columns)) { edge(target < 0 ? -1 : 1)?.click(); return; }
        navigation++;
        spread = target;
        paint();
        changed();
    };
    const turn = (delta: number): void => {
        if (settings.mode !== 'paged') { edge(delta)?.click(); return; }
        // Before the first layout the chapter counts as one page, so a quick click would leave it for the next one.
        if (!measured) return;
        if (!book?.turn(delta)) commit(spread + delta);
    };
    const reveal = (element: HTMLElement | Range): void => {
        navigation++;
        if (!measured) { waiting = () => reveal(element); return; }
        if (settings.mode === 'scroll') {
            content.scrollTop += element.getBoundingClientRect().top - content.getBoundingClientRect().top - 24;
            return;
        }
        const bounds = content.getBoundingClientRect();
        const box = element.getClientRects()[0];
        if (!box) return;
        const relative = settings.direction === 'rtl' ? bounds.right - box.right : box.left - bounds.left;
        spread = Math.max(0, Math.min(Math.ceil(pages / columns) - 1, spread + Math.floor((relative + 1) / step)));
        paint();
    };
    // The progress slider asks for a place as a share of this chapter; a page is its first page, a scrolled text its offset.
    const seek = (fraction: number): void => {
        navigation++;
        if (!measured) { waiting = () => seek(fraction); return; }
        if (settings.mode === 'paged') spread = Math.floor(pageAt(fraction, pages, columns) / columns);
        else content.scrollTop = Math.min(fraction * (content.scrollHeight - content.clientHeight), content.scrollHeight - content.clientHeight);
        paint();
        changed();
    };
    content.addEventListener('scroll', () => { if (settings.mode === 'scroll') painted(); }, { passive: true });
    const layout = (): void => {
        cancelAnimationFrame(frame);
        // A reader replaced by another chapter keeps observing its removed elements; it must no longer lay out or report.
        if (signal.aborted) return;
        const anchor = current(), version = navigation;
        frame = requestAnimationFrame(() => {
            // A pending view resize must not restore an anchor superseded by a seek/turn.
            const latest = version === navigation ? anchor : current();
            const width = canvas.clientWidth - parseFloat(getComputedStyle(canvas).paddingLeft) - parseFloat(getComputedStyle(canvas).paddingRight);
            const two = settings.layout === 'spread' || settings.layout === 'auto' && width >= Math.max(640, settings.size * 36 + 40);
            columns = two ? 2 : 1;
            reader.style.setProperty('--reader-columns', String(columns));
            // The single-page text inset must be active before calculating CSS-column pagination.
            reader.dataset.columns = String(columns);
            // Margins are a share of the page's width, so they follow the book's size; the inner ones form the gap between the two pages.
            const paperWidth = paper.getBoundingClientRect().width, page = paperWidth / columns;
            const margin = (share: number) => `${Math.round(page * share / 100)}px`;
            // The text box gets a whole-pixel width: pages are counted and turned in whole pixels, and a fraction of a pixel per
            // column would add up across a chapter to a blank last page.
            const outer = (paperWidth - Math.floor(paperWidth - 2 * Math.round(page * settings.marginOuter / 100))) / 2;
            reader.style.setProperty('--reader-margin-top', margin(settings.marginTop));
            reader.style.setProperty('--reader-margin-bottom', margin(settings.marginBottom));
            reader.style.setProperty('--reader-margin-outer', `${outer}px`);
            reader.style.setProperty('--reader-margin-inner', margin(settings.marginInner));
            reader.style.setProperty('--reader-margin-gap', margin(settings.marginInner * 2));
            content.removeAttribute('data-pad');
            const gap = parseFloat(getComputedStyle(content).columnGap) || 0;
            step = content.clientWidth + gap;
            pages = settings.mode === 'paged' ? paperPageCount(content, columns, gap, settings.direction === 'rtl') : 1;
            oversized = (content.textContent?.length ?? 0) > 100_000 || content.querySelectorAll('*').length > 5000;
            content.toggleAttribute('data-pad', columns === 2 && pages % 2 !== 0);
            spread = Math.min(spread, Math.ceil(pages / columns) - 1);
            bookPages.update({ width: content.clientWidth, height: content.clientHeight, columns, gap, pages, rtl: settings.direction === 'rtl' });
            measured = true;
            paint();
            const asked = waiting;
            waiting = undefined;
            if (asked) asked();
            else if (latest) reveal(latest);
        });
    };
    new ResizeObserver(layout).observe(canvas);
    // Translation, highlights and delayed illustrations reflow the canonical content.
    new MutationObserver(layout).observe(content, { childList: true, subtree: true, characterData: true });
    content.addEventListener('load', layout, true);
    content.addEventListener('error', layout, true);
    content.addEventListener('focusin', event => {
        if (event.target instanceof HTMLElement) reveal(event.target);
    });
    document.fonts.ready.then(layout);
    document.fonts.addEventListener('loadingdone', () => { bookPages.invalidate(); layout(); }, { signal });
    window.addEventListener('pageshow', event => { if (event.persisted) layout(); }, { signal });
    motion.addEventListener('change', layout);
    reader.querySelectorAll<HTMLElement>('[data-turn]').forEach(button => button.addEventListener('click', () => turn(Number(button.dataset.turn))));
    reader.addEventListener('keydown', event => {
        if (settings.mode !== 'paged' || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || getSelection()?.toString()
            || (event.target as Element).closest('a,button,input,select,textarea,[contenteditable=true]') || reader.querySelector('[data-panel]:not([data-panel=contents]):not([hidden])')) return;
        const delta = event.key === 'PageDown' ? 1 : event.key === 'PageUp' ? -1
            : event.key === 'ArrowRight' ? settings.direction === 'rtl' ? -1 : 1 : event.key === 'ArrowLeft' ? settings.direction === 'rtl' ? 1 : -1 : 0;
        if (delta) { event.preventDefault(); turn(delta); }
    });
    let start: { x: number; y: number } | undefined;
    canvas.addEventListener('pointerdown', event => {
        start = settings.mode === 'paged' && reader.dataset.selecting !== 'true' && !book?.enabled() && !(event.target as Element).closest('a,button,input,mark') ? { x: event.clientX, y: event.clientY } : undefined;
    });
    canvas.addEventListener('pointerup', event => {
        const from = start; start = undefined;
        if (!from || getSelection()?.toString()) return;
        const dx = event.clientX - from.x;
        if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(event.clientY - from.y) * 1.4) turn((dx < 0 ? 1 : -1) * (settings.direction === 'rtl' ? -1 : 1));
    });
    canvas.addEventListener('pointercancel', () => start = undefined);
    // Where the reader stands in this chapter, as a share from 0 to 1.
    const share = (): number => {
        if (settings.mode === 'scroll') return content.scrollHeight > content.clientHeight ? Math.min(1, Math.max(0, content.scrollTop / (content.scrollHeight - content.clientHeight))) : 0;
        return pageFraction(spread * columns, pages, spread + 1 >= Math.ceil(pages / columns));
    };
    return { layout, reveal, turn, current, seek, share, end: () => spread + 1 >= Math.ceil(pages / columns) };
}
