import type { CurlGroup, initializePaperCurl } from './PaperCurl';
import type { ReaderSettings } from './Settings';

// Canonical CSS columns own pagination; decorative faces never own progress.
export function initializePaperPages(reader: HTMLElement, content: HTMLElement, settings: ReaderSettings, changed: () => void) {
    const paper = reader.querySelector<HTMLElement>('.reader__paper')!;
    const canvas = reader.querySelector<HTMLElement>('.reader__canvas')!;
    const motion = matchMedia('(prefers-reduced-motion: reduce)');
    const label = reader.querySelector<HTMLElement>('[data-role=pages]');
    let columns = 1, pages = 1, spread = 0, step = 1, frame = 0;
    let curl: ReturnType<typeof initializePaperCurl> | undefined;
    let loading: Promise<void> | undefined;
    let failed = false;
    const edge = (delta: number) => reader.querySelector<HTMLAnchorElement>(`[data-role=${delta < 0 ? 'previous' : 'next'}]`);
    const blocks = () => Array.from(content.querySelectorAll<HTMLElement>('[data-b]'));
    const current = (): HTMLElement | undefined => {
        const bounds = content.getBoundingClientRect();
        return blocks().find(block => Array.from(block.getClientRects()).some(box => settings.mode === 'paged'
            ? box.right > bounds.left + 24 && box.left < bounds.right - 24 : box.bottom > bounds.top + 24));
    };
    const neighbour = (delta: number): CurlGroup | undefined => {
        const target = spread + delta;
        if (target >= 0 && target < Math.ceil(pages / columns)) return { source: content, page: target * columns, count: pages };
        const link = edge(delta);
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
        if (label) {
            label.hidden = settings.mode !== 'paged';
            label.textContent = reader.dataset.pageLabel!.replace('{0}', String(spread * columns + 1)).replace('{1}', String(Math.min(pages, (spread + 1) * columns))).replace('{2}', String(pages));
        }
        // Whole-book proportions use the supplied character weights, not invented page totals.
        const before = Number(reader.dataset.before), characters = Number(reader.dataset.characters), total = Math.max(1, Number(reader.dataset.total));
        const left = (before + characters * spread * columns / pages) / total;
        const right = Math.max(0, total - before - characters * Math.min(pages, (spread + 1) * columns) / pages) / total;
        reader.style.setProperty('--reader-stack-left', `${1 + 14 * (settings.direction === 'rtl' ? right : left)}px`);
        reader.style.setProperty('--reader-stack-right', `${1 + 14 * (settings.direction === 'rtl' ? left : right)}px`);
        reader.querySelectorAll<HTMLButtonElement>('[data-turn]').forEach(button => {
            const delta = Number(button.dataset.turn);
            button.disabled = delta < 0 ? spread === 0 && !edge(-1) : spread + 1 >= Math.ceil(pages / columns) && !edge(1);
        });
        const wantsAnimation = settings.mode === 'paged' && reader.dataset.selecting !== 'true'
            && (settings.animation === 'on' || settings.animation === 'system' && !motion.matches);
        const oversized = content.textContent!.length > 100_000 || content.querySelectorAll('*').length > 5000;
        reader.querySelector<HTMLElement>('[data-role=animation-error]')!.hidden = !failed && !(wantsAnimation && oversized);
        const enabled = wantsAnimation && !failed && !oversized;
        curl?.update([neighbour(-1), neighbour(0), neighbour(1)], columns, enabled);
        if (enabled && !curl && !loading) loading = import('./PaperCurl').then(module => {
            curl = module.initializePaperCurl(reader, move);
            paint();
        }).catch(() => {
            failed = true;
            reader.dataset.curl = 'false';
            reader.querySelector<HTMLElement>('.reader__curl')?.setAttribute('hidden', 'hidden');
            reader.querySelector<HTMLElement>('[data-role=animation-error]')?.removeAttribute('hidden');
        });
    };
    const move = (delta: number): void => {
        const target = spread + delta;
        if (target < 0 || target >= Math.ceil(pages / columns)) { edge(delta)?.click(); return; }
        spread = target;
        paint();
        changed();
    };
    const turn = (delta: number): void => {
        if (settings.mode !== 'paged') { edge(delta)?.click(); return; }
        if (!curl?.turn(delta)) move(delta);
    };
    const reveal = (element: HTMLElement): void => {
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
    const layout = (): void => {
        cancelAnimationFrame(frame);
        const anchor = current();
        frame = requestAnimationFrame(() => {
            const width = paper.clientWidth;
            const two = settings.layout === 'spread' || settings.layout === 'auto' && width >= Math.max(640, settings.size * 36 + 40);
            columns = two ? 2 : 1;
            reader.style.setProperty('--reader-columns', String(columns));
            content.removeAttribute('data-pad');
            const gap = parseFloat(getComputedStyle(content).columnGap) || 40;
            step = content.clientWidth + gap;
            const columnWidth = (content.clientWidth - gap * (columns - 1)) / columns;
            const range = document.createRange();
            range.selectNodeContents(content);
            const bounds = content.getBoundingClientRect();
            const extent = Math.max(0, ...Array.from(range.getClientRects(), box => settings.direction === 'rtl' ? bounds.right - box.left : box.right - bounds.left));
            pages = settings.mode === 'paged' ? Math.max(1, Math.ceil((extent + gap - 1) / (columnWidth + gap))) : 1;
            content.toggleAttribute('data-pad', columns === 2 && pages % 2 !== 0);
            spread = Math.min(spread, Math.ceil(pages / columns) - 1);
            paint();
            if (anchor) reveal(anchor);
        });
    };
    new ResizeObserver(layout).observe(canvas);
    // Translation, highlights and delayed illustrations reflow the canonical content.
    new MutationObserver(layout).observe(content, { childList: true, subtree: true, characterData: true });
    content.addEventListener('load', layout, true);
    content.addEventListener('focusin', event => {
        if (event.target instanceof HTMLElement) reveal(event.target);
    });
    document.fonts.ready.then(layout);
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
        start = settings.mode === 'paged' && reader.dataset.selecting !== 'true' && !curl?.enabled() && !(event.target as Element).closest('a,button,input,mark') ? { x: event.clientX, y: event.clientY } : undefined;
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
