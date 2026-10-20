import type { BookGroup, initializeBookRenderer } from './BookRenderer';
import type { ReaderSettings } from './Settings';
import { updateRangeValue } from '../../Framework/Components/Range';
import { paperPageCount } from './PaperMeasure';
import { initializeBookPages } from './BookPages';

// Canonical CSS columns own pagination; decorative faces never own progress.
export function initializePaperPages(reader: HTMLElement, content: HTMLElement, settings: ReaderSettings, changed: () => void) {
    const paper = reader.querySelector<HTMLElement>('.reader__paper')!;
    const canvas = reader.querySelector<HTMLElement>('.reader__canvas')!;
    const motion = matchMedia('(prefers-reduced-motion: reduce)');
    const label = reader.querySelector<HTMLElement>('[data-role=pages]');
    const seek = reader.querySelector<HTMLInputElement>('[data-role=seek] input[type=range]')!;
    reader.querySelector<HTMLElement>('[data-role=seek]')!.hidden = false;
    const bookPages = initializeBookPages(reader, content);
    let columns = 1, pages = 1, spread = 0, step = 1, frame = 0, navigation = 0;
    let book: ReturnType<typeof initializeBookRenderer> | undefined;
    let loading: Promise<void> | undefined;
    let failed = false;
    const edge = (delta: number) => reader.querySelector<HTMLAnchorElement>(`[data-role=${delta < 0 ? 'previous' : 'next'}]`);
    const blocks = () => Array.from(content.querySelectorAll<HTMLElement>('[data-b]'));
    const current = (): HTMLElement | undefined => {
        const bounds = content.getBoundingClientRect();
        return blocks().find(block => Array.from(block.getClientRects()).some(box => settings.mode === 'paged'
            ? box.right > bounds.left + 24 && box.left < bounds.right - 24 : box.bottom > bounds.top + 24));
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
    const paint = (): void => {
        const offset = spread * step * (settings.direction === 'rtl' ? -1 : 1);
        content.scrollTo({ left: settings.mode === 'paged' ? offset : 0, behavior: 'instant' });
        reader.dataset.page = String(spread * columns + 1);
        reader.dataset.pageCount = String(pages);
        reader.dataset.columns = String(columns);
        reader.querySelector<HTMLElement>('[data-role=turn-tools]')!.hidden = settings.mode !== 'paged';
        bookPages.position(spread * columns + 1);
        if (label) {
            label.hidden = settings.mode !== 'paged';
            label.textContent = reader.dataset.pageLabel!.replace('{0}', String(spread * columns + 1)).replace('{1}', String(Math.min(pages, (spread + 1) * columns))).replace('{2}', String(pages));
        }
        seek.max = String(settings.mode === 'paged' ? pages : Math.ceil(Math.max(0, content.scrollHeight - content.clientHeight) / Math.max(1, content.clientHeight)) + 1);
        seek.step = String(settings.mode === 'paged' ? columns : 1);
        seek.value = String(settings.mode === 'paged' ? spread * columns + 1 : Math.ceil(content.scrollTop / Math.max(1, content.clientHeight)) + 1);
        updateRangeValue(seek);
        seek.disabled = settings.mode === 'paged' ? pages <= columns : content.scrollHeight <= content.clientHeight;
        reader.querySelectorAll<HTMLButtonElement>('[data-turn]').forEach(button => {
            const delta = Number(button.dataset.turn);
            button.disabled = delta < 0 ? spread === 0 && !edge(-1) : spread + 1 >= Math.ceil(pages / columns) && !edge(1);
        });
        const wantsBook = settings.mode === 'paged' && reader.dataset.selecting !== 'true';
        const oversized = content.textContent!.length > 100_000 || content.querySelectorAll('*').length > 5000;
        reader.querySelector<HTMLElement>('[data-role=animation-error]')!.hidden = !failed && !(wantsBook && oversized);
        const active = wantsBook && !failed && !oversized;
        if (book && !book.update({ current: spread, columns, minimum: edge(-1) ? -1 : 0,
            maximum: Math.ceil(pages / columns) - (edge(1) ? 0 : 1), group,
            next: (at, delta) => at + delta, number: at => at * columns + 1, commit }, active)) failed = true;
        if (active && !book && !loading) loading = import('./BookRenderer').then(module => {
            book = module.initializeBookRenderer(reader);
            paint();
        }).catch(() => {
            failed = true;
            reader.dataset.bookReady = 'false';
            reader.querySelector<HTMLElement>('.reader-book')?.setAttribute('hidden', 'hidden');
            reader.querySelector<HTMLElement>('[data-role=animation-error]')?.removeAttribute('hidden');
        });
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
        if (!book?.turn(delta)) commit(spread + delta);
    };
    const reveal = (element: HTMLElement | Range): void => {
        navigation++;
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
    // Commit on release/change, not on every pointer sample. Existing progress keeps the semantic locator.
    seek.addEventListener('change', () => {
        const at = seek.valueAsNumber - 1;
        if (!Number.isFinite(at)) return;
        navigation++;
        if (settings.mode === 'paged') spread = Math.floor(at / columns);
        else content.scrollTop = Math.min(at * content.clientHeight, content.scrollHeight - content.clientHeight);
        paint();
        changed();
    });
    content.addEventListener('scroll', () => {
        if (settings.mode !== 'scroll') return;
        seek.value = String(Math.ceil(content.scrollTop / Math.max(1, content.clientHeight)) + 1);
        updateRangeValue(seek);
    }, { passive: true });
    const layout = (): void => {
        cancelAnimationFrame(frame);
        const anchor = current(), version = navigation;
        frame = requestAnimationFrame(() => {
            // A pending view resize must not restore an anchor superseded by a seek/turn.
            const latest = version === navigation ? anchor : current();
            const width = canvas.clientWidth - parseFloat(getComputedStyle(canvas).paddingLeft) - parseFloat(getComputedStyle(canvas).paddingRight);
            const two = settings.layout === 'spread' || settings.layout === 'auto' && width >= Math.max(640, settings.size * 36 + 40);
            columns = two ? 2 : 1;
            reader.style.setProperty('--reader-columns', String(columns));
            content.removeAttribute('data-pad');
            const gap = parseFloat(getComputedStyle(content).columnGap) || 40;
            step = content.clientWidth + gap;
            pages = settings.mode === 'paged' ? paperPageCount(content, columns, gap, settings.direction === 'rtl') : 1;
            content.toggleAttribute('data-pad', columns === 2 && pages % 2 !== 0);
            spread = Math.min(spread, Math.ceil(pages / columns) - 1);
            bookPages.update({ width: content.clientWidth, height: content.clientHeight, columns, gap, pages, rtl: settings.direction === 'rtl' });
            paint();
            if (latest) reveal(latest);
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
    document.fonts.addEventListener('loadingdone', () => { bookPages.invalidate(); layout(); });
    window.addEventListener('pageshow', event => { if (event.persisted) layout(); });
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
    return { layout, reveal, turn, current, end: () => spread + 1 >= Math.ceil(pages / columns) };
}
