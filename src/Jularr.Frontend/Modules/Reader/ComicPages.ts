import { progressReporter } from '../Tracking/Progress';
import { initializeReaderSettings } from './Settings';
import type { CurlGroup, initializePaperCurl } from './PaperCurl';

export function comicSpread(page: number, count: number, two: boolean, wide: ReadonlySet<number>): number[] {
    return !two || page === 0 || wide.has(page) || page + 1 >= count || wide.has(page + 1) ? [page] : [page, page + 1];
}

// Image content adapter inside the same Reader frame, settings and paper surface.
export function initializeComicPages(reader: HTMLElement): void {
    const stage = reader.querySelector<HTMLElement>('[data-role=stage]')!;
    const canvas = reader.querySelector<HTMLElement>('.reader__canvas')!;
    const previous = reader.querySelector<HTMLAnchorElement>('[data-role=previous]')!;
    const next = reader.querySelector<HTMLAnchorElement>('[data-role=next]')!;
    const position = reader.querySelector<HTMLElement>('[data-role=position]')!;
    const count = Number(reader.dataset.count);
    const images = new Map<number, HTMLImageElement>();
    const wide = new Set<number>();
    const motion = matchMedia('(prefers-reduced-motion: reduce)');
    let page = Number(reader.dataset.page), first = page, last = page;
    let curl: ReturnType<typeof initializePaperCurl> | undefined;
    let loading: Promise<void> | undefined;
    let failed = false, moved = false;
    const settings = initializeReaderSettings(reader, render);
    const save = progressReporter(reader.dataset.progressUrl!, reader.dataset.antiforgery!, failed => {
        reader.querySelector<HTMLElement>('[data-role=save-error]')!.hidden = !failed;
    });
    const two = () => settings.layout === 'spread' || settings.layout === 'auto' && stage.clientWidth >= 640;
    const shown = (at: number): number[] => comicSpread(at, count, two(), wide);
    const target = (delta: number): number => {
        const at = delta > 0 ? shown(page).at(-1)! + 1 : page - 1;
        return delta < 0 && two() && at > 1 && !wide.has(at) && !wide.has(at - 1) ? at - 1 : at;
    };
    const image = (index: number): HTMLImageElement => {
        let element = images.get(index);
        if (!element) {
            element = new Image();
            element.decoding = 'async';
            element.dataset.index = String(index);
            element.alt = `${reader.dataset.pageLabel} ${index + 1}`;
            element.src = reader.dataset.pages! + encodeURIComponent(`:page/${String(index).padStart(5, '0')}`);
            element.addEventListener('load', () => {
                if (element!.naturalWidth > element!.naturalHeight && !wide.has(index)) {
                    wide.add(index);
                    if (settings.mode === 'paged' && (index === page || index === page + 1)) render();
                }
            });
            images.set(index, element);
        }
        return element;
    };
    function group(at: number): CurlGroup | undefined {
        if (at < 0 || at >= count) return;
        const source = at === page ? stage : stage.cloneNode(false) as HTMLElement;
        if (source !== stage) source.append(...shown(at).map(index => image(index).cloneNode(true)));
        return { source, page: 0, count: shown(at).length };
    }
    function render(): void {
        const current = shown(page);
        const columns = current.length;
        reader.dataset.columns = String(columns);
        reader.style.setProperty('--reader-columns', String(columns));
        reader.dataset.page = String(page);
        if (settings.mode === 'scroll') {
            first = Math.max(0, page - 1); last = Math.min(count - 1, page + 3);
            stage.replaceChildren(...Array.from({ length: last - first + 1 }, (_, offset) => image(first + offset)));
            requestAnimationFrame(() => { stage.scrollTop = image(page).offsetTop - stage.offsetTop; });
        } else stage.replaceChildren(...current.map(image));
        updatePosition();
        const active = settings.mode === 'paged' && !failed && (settings.animation === 'on' || settings.animation === 'system' && !motion.matches);
        curl?.update([group(target(-1)), group(page), group(target(1))], columns, active);
        if (active && !curl && !loading) loading = import('./PaperCurl').then(module => {
            curl = module.initializePaperCurl(reader, go); render();
        }).catch(() => {
            failed = true; reader.dataset.curl = 'false';
            reader.querySelector<HTMLElement>('.reader__curl')?.setAttribute('hidden', 'hidden');
            reader.querySelector<HTMLElement>('[data-role=animation-error]')!.hidden = false;
        });
        for (let index = current.at(-1)! + 1; index <= Math.min(count - 1, current.at(-1)! + 3); index++) image(index);
        for (const index of images.keys()) if ((index < page - 8 || index > page + 8) && !stage.contains(images.get(index)!)) images.delete(index);
    }
    function updatePosition(): void {
        const current = settings.mode === 'paged' ? shown(page) : [page];
        position.textContent = `${current.map(index => index + 1).join('–')} / ${count}`;
        previous.hidden = page === 0;
        next.hidden = current.at(-1)! + 1 >= count;
        reader.querySelectorAll<HTMLButtonElement>('[data-turn]').forEach(button => button.disabled = Number(button.dataset.turn) < 0 ? previous.hidden : next.hidden);
        reader.style.setProperty('--reader-stack-left', `${1 + 14 * (settings.direction === 'rtl' ? count - current.at(-1)! - 1 : page) / count}px`);
        reader.style.setProperty('--reader-stack-right', `${1 + 14 * (settings.direction === 'rtl' ? page : count - current.at(-1)! - 1) / count}px`);
        const address = new URL(location.href);
        address.searchParams.set('p', String(page));
        history.replaceState(null, '', address.pathname + address.search);
    }
    function go(delta: number): void {
        const at = target(delta);
        if (at < 0 || at >= count) return;
        page = at; moved = true;
        render(); report(false);
    }
    const turn = (delta: number) => { if (!curl?.turn(delta)) go(delta); };
    previous.addEventListener('click', event => { event.preventDefault(); turn(-1); });
    next.addEventListener('click', event => { event.preventDefault(); turn(1); });
    reader.querySelectorAll<HTMLElement>('[data-turn]').forEach(button => button.addEventListener('click', () => turn(Number(button.dataset.turn))));
    reader.addEventListener('keydown', event => {
        if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || (event.target as Element)?.closest('a,button,input,select,textarea')
            || reader.querySelector('[data-panel]:not([data-panel=contents]):not([hidden])')) return;
        const delta = event.key === 'PageDown' || event.key === ' ' ? 1 : event.key === 'PageUp' ? -1
            : event.key === 'ArrowLeft' ? settings.direction === 'rtl' ? 1 : -1 : event.key === 'ArrowRight' ? settings.direction === 'rtl' ? -1 : 1 : 0;
        if (delta) { event.preventDefault(); turn(delta); }
    });
    let start: { x: number; y: number } | undefined;
    stage.addEventListener('pointerdown', event => { start = settings.mode === 'paged' ? { x: event.clientX, y: event.clientY } : undefined; });
    stage.addEventListener('pointerup', event => {
        const from = start; start = undefined;
        if (!from) return;
        const dx = event.clientX - from.x;
        if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(event.clientY - from.y) * 1.4) turn((dx < 0 ? 1 : -1) * (settings.direction === 'rtl' ? -1 : 1));
    });
    stage.addEventListener('pointercancel', () => start = undefined);
    stage.addEventListener('scroll', () => {
        if (settings.mode !== 'scroll') return;
        const bounds = stage.getBoundingClientRect();
        const visible = Array.from(stage.querySelectorAll<HTMLImageElement>('img')).find(image => image.getBoundingClientRect().bottom > bounds.top + 24);
        if (visible) {
            if (page !== Number(visible.dataset.index)) { page = Number(visible.dataset.index); updatePosition(); }
            moved = true;
            report(false);
        }
        // A bounded sliding window: load ahead, release distant image nodes, preserve the visible anchor.
        const anchor = visible, top = anchor?.getBoundingClientRect().top;
        if (stage.scrollTop + stage.clientHeight >= stage.scrollHeight - 200 && last + 1 < count) {
            stage.append(image(++last));
            if (last - first >= 12) image(first++).remove();
        } else if (stage.scrollTop < 100 && first > 0) {
            stage.prepend(image(--first));
            if (last - first >= 12) image(last--).remove();
        }
        if (anchor && top !== undefined) stage.scrollTop += anchor.getBoundingClientRect().top - top;
        for (const index of images.keys()) if (index < first - 3 || index > last + 3) images.delete(index);
    }, { passive: true });
    new ResizeObserver(() => { if (settings.mode === 'paged') render(); }).observe(canvas);
    motion.addEventListener('change', render);
    window.addEventListener('pagehide', () => { if (moved) report(true); });
    function report(leaving: boolean): void {
        const last = settings.mode === 'paged' ? shown(page).at(-1)! : page;
        const finalImage = images.get(count - 1);
        const isCompleted = last + 1 >= count && (settings.mode === 'paged' || !!finalImage
            && stage.contains(finalImage) && finalImage.complete && finalImage.naturalHeight > 0
            && finalImage.getBoundingClientRect().bottom <= stage.getBoundingClientRect().bottom + 1);
        const progression = (last + (settings.mode === 'paged' || isCompleted ? 1 : 0)) / count;
        save({ locator: `p${page}`, progression: Number(progression.toFixed(4)), isCompleted }, leaving);
    }
    render();
}
