import { progressReporter } from '../Tracking/Progress';
import { updateRangeValue } from '../../Framework/Components/Range';
import { initializeReaderSettings } from './Settings';
import { clampPage, clampZoom, pageAnchor, pageLocator, pageSize, pinchZoom, progression, renderScale, stepZoom, swipeDelta, turnTarget, wheelZoom } from './PageView';
import { choiceOf, nextLook, offersSmartBook, viewAddress, type ReadingView } from './ViewSwitch';
import { initializeViewChoice } from './ViewChoice';
import { drawPage, openPdf, pageWords, type PDFDocumentProxy, type PDFPageProxy, type RenderTask } from './PdfEngine';
import { smartPages } from './SmartPages';

// A page's bitmap stays within this many pixels, however far it is zoomed.
const MaximumPixels = 16_000_000;
const SettleDelay = 180;

// The original pages inside the same Reader frame: one page of the PDF at a time, drawn in the browser, with the same settings, chrome and progress store as every other book.
export function initializeOriginalPages(reader: HTMLElement): void {
    const stage = reader.querySelector<HTMLElement>('[data-role=stage]')!;
    const sheet = reader.querySelector<HTMLElement>('[data-role=sheet]')!;
    const status = reader.querySelector<HTMLElement>('[data-role=status]')!;
    const position = reader.querySelector<HTMLElement>('[data-role=position]');
    const pagePosition = reader.querySelector<HTMLElement>('[data-role=pages]')!;
    const seek = reader.querySelector<HTMLInputElement>('[data-role=seek] input[type=range]')!;
    const noteFields = { page: reader.querySelector<HTMLInputElement>('[name=PageNumber]'), anchor: reader.querySelector<HTMLInputElement>('[name=Anchor]') };
    const settings = initializeReaderSettings(reader, () => { layout(); settle(); });
    reader.dataset.mode = 'pages';
    // Scanned pages set anew show in place of the scan; every other page is drawn from the PDF.
    const smart = smartPages(reader);
    const save = progressReporter(reader.dataset.progressUrl!, reader.dataset.antiforgery!, failed => {
        reader.querySelector<HTMLElement>('[data-role=save-error]')!.hidden = !failed;
    });

    let document: PDFDocumentProxy | undefined;
    let count = 0, page = Math.max(1, Number(reader.dataset.page) || 1), zoom = 1;
    let natural = { width: 0, height: 0 };
    let proxy: PDFPageProxy | undefined;
    let task: RenderTask | undefined;
    let generation = 0, sequence = 0, timer = 0;
    let moved = false;
    const carried = new URLSearchParams(location.search).has('pg');

    const problem = (text: string): void => { status.textContent = text; status.hidden = false; };

    // The sheet takes the size the fit and the zoom give the page; the bitmap follows once zooming settles.
    function layout(): void {
        if (!natural.width) return;
        const size = pageSize({ width: stage.clientWidth, height: stage.clientHeight }, natural, settings.fit, zoom);
        sheet.style.setProperty('--sheet-width', `${size.width}px`);
        sheet.style.setProperty('--sheet-height', `${size.height}px`);
        stage.dataset.zoomed = String(zoom > 1 || size.height > stage.clientHeight + 1 || size.width > stage.clientWidth + 1);
    }
    function settle(): void {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => void paint(), SettleDelay);
    }

    // The page drawn off screen at the scale that is sharp on this device, then put in place, so a turn never shows a blank sheet.
    async function paint(): Promise<void> {
        if (!proxy || !natural.width) return;
        const token = ++generation;
        task?.cancel();
        if (smart?.has(page)) {
            const set = await smart.page(page);
            if (token !== generation) return;
            if (set) {
                status.hidden = true;
                if (sheet.firstElementChild !== set) sheet.replaceChildren(set);
                smart.fit(set);
                return;
            }
        }
        const cssWidth = sheet.getBoundingClientRect().width || stage.clientWidth;
        const next = globalThis.document.createElement('canvas');
        next.dataset.role = 'page';
        next.setAttribute('role', 'img');
        next.setAttribute('aria-label', `${reader.dataset.pageLabel} ${page}`);
        try {
            task = drawPage(proxy, next, renderScale(cssWidth, natural, devicePixelRatio, MaximumPixels));
            await task.promise;
        } catch (error) {
            if ((error as { name?: string }).name === 'RenderingCancelledException') return;
            problem(reader.dataset.failedLabel!);
            return;
        }
        if (token !== generation) return;
        status.hidden = true;
        sheet.replaceChildren(next);
    }

    // Shows a page: position, controls and address first, then the page itself.
    async function show(target: number, byReader = true): Promise<void> {
        if (!document) return;
        page = clampPage(target, count);
        updateChrome();
        const mine = ++sequence;
        try {
            const next = await document.getPage(page);
            if (mine !== sequence) return;
            proxy = next;
            const box = next.getViewport({ scale: 1 });
            natural = { width: box.width, height: box.height };
            if (byReader) { stage.scrollTo({ left: 0, top: 0, behavior: 'instant' }); }
            layout();
            await paint();
            // The first words of the page anchor a note to it; a page without text layer has none.
            void pageWords(next).then(words => { if (mine === sequence) setAnchor(words); }).catch(() => setAnchor([]));
            if (page < count) void document.getPage(page + 1);
        } catch {
            problem(reader.dataset.failedLabel!);
        }
    }
    function setAnchor(words: string[]): void {
        if (noteFields.anchor) noteFields.anchor.value = pageAnchor(words);
    }
    function updateChrome(): void {
        const text = `${page} / ${count}`;
        if (position) position.textContent = text;
        pagePosition.textContent = text;
        seek.max = String(count);
        seek.value = String(page);
        seek.disabled = count <= 1;
        updateRangeValue(seek);
        if (noteFields.page) noteFields.page.value = String(page);
        reader.dataset.page = String(page);
        reader.querySelectorAll<HTMLButtonElement>('[data-turn]').forEach(button => button.disabled = Number(button.dataset.turn) < 0 ? page <= 1 : page >= count);
        const address = new URL(location.href);
        address.searchParams.set('pg', String(page));
        history.replaceState(null, '', address.pathname + address.search);
    }
    function report(leaving: boolean): void {
        if (count > 0) save({ locator: pageLocator(page), progression: progression(page, count), isCompleted: page >= count }, leaving);
    }
    function turn(delta: number): void {
        const target = turnTarget(page, count, delta);
        if (target === page) return;
        moved = true;
        void show(target).then(() => report(false));
    }

    // Zoom keeps the point under the pointer where it is.
    function zoomTo(next: number, clientX?: number, clientY?: number): void {
        const target = clampZoom(next);
        if (target === zoom) return;
        const bounds = stage.getBoundingClientRect();
        const pointX = (clientX ?? bounds.left + bounds.width / 2) - bounds.left;
        const pointY = (clientY ?? bounds.top + bounds.height / 2) - bounds.top;
        const before = sheet.getBoundingClientRect();
        const fractionX = before.width > 0 ? (pointX - (before.left - bounds.left)) / before.width : 0.5;
        const fractionY = before.height > 0 ? (pointY - (before.top - bounds.top)) / before.height : 0.5;
        zoom = target;
        layout();
        const after = sheet.getBoundingClientRect();
        stage.scrollLeft += after.left - bounds.left + fractionX * after.width - pointX;
        stage.scrollTop += after.top - bounds.top + fractionY * after.height - pointY;
        settle();
    }
    const zoomed = (): boolean => stage.scrollWidth > stage.clientWidth + 1 || stage.scrollHeight > stage.clientHeight + 1;

    reader.querySelectorAll<HTMLButtonElement>('[data-turn]').forEach(button => button.addEventListener('click', () => turn(Number(button.dataset.turn))));
    reader.querySelectorAll<HTMLButtonElement>('[data-page-zoom]').forEach(button => button.addEventListener('click', () => {
        const mode = button.dataset.pageZoom;
        if (mode === 'fit') { zoom = 1; layout(); settle(); stage.scrollTo({ left: 0, top: 0 }); } else zoomTo(stepZoom(zoom, mode === 'in' ? 1 : -1));
    }));
    seek.addEventListener('change', () => {
        if (!Number.isFinite(seek.valueAsNumber)) return;
        moved = true;
        void show(seek.valueAsNumber).then(() => report(false));
    });
    reader.addEventListener('keydown', event => {
        // A focused button keeps Enter and Space; links and fields keep every key.
        const target = event.target as Element | null;
        if (event.altKey || event.ctrlKey || event.metaKey || target?.closest('a,input,select,textarea') || target?.closest('button') && (event.key === ' ' || event.key === 'Enter')
            || reader.querySelector('[data-panel]:not([data-panel=contents]):not([hidden])')) return;
        const key = event.key;
        if (key === 'ArrowRight' || key === 'PageDown' || key === ' ' && !event.shiftKey) turn(1);
        else if (key === 'ArrowLeft' || key === 'PageUp' || key === ' ' && event.shiftKey) turn(-1);
        else if (key === 'Home') turn(-count);
        else if (key === 'End') turn(count);
        else if (key === '+' || key === '=') zoomTo(stepZoom(zoom, 1));
        else if (key === '-') zoomTo(stepZoom(zoom, -1));
        else if (key === '0') { zoom = 1; layout(); settle(); }
        else return;
        event.preventDefault();
    });

    // The wheel with the control key (a trackpad pinch) zooms; the plain wheel scrolls a page that overflows and turns one that does not.
    let wheelAt = 0;
    stage.addEventListener('wheel', event => {
        if (event.ctrlKey || event.metaKey) {
            event.preventDefault();
            zoomTo(wheelZoom(zoom, event.deltaY, event.ctrlKey), event.clientX, event.clientY);
        } else if (!zoomed() && Math.abs(event.deltaY) > 20 && performance.now() - wheelAt > 350) {
            wheelAt = performance.now();
            event.preventDefault();
            turn(event.deltaY > 0 ? 1 : -1);
        }
    }, { passive: false });

    // One finger swipes to turn a page that fits, or drags one that overflows; two fingers pinch; the mouse drags too.
    const pointers = new Map<number, { x: number; y: number }>();
    let pinch = 0, start: { x: number; y: number } | undefined;
    const spread = (): number => { const [a, b] = [...pointers.values()]; return a && b ? Math.hypot(a.x - b.x, a.y - b.y) : 0; };
    stage.addEventListener('pointerdown', event => {
        if ((event.target as Element).closest('a,button')) return;
        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        pinch = pointers.size === 2 ? spread() : 0;
        start = pointers.size === 1 && !zoomed() ? { x: event.clientX, y: event.clientY } : undefined;
        if (pointers.size === 1 && event.pointerType === 'mouse' && zoomed()) stage.setPointerCapture(event.pointerId);
    });
    stage.addEventListener('pointermove', event => {
        const before = pointers.get(event.pointerId);
        if (!before) return;
        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        if (pointers.size === 2 && pinch > 0) {
            const now = spread();
            const [a, b] = [...pointers.values()];
            zoomTo(pinchZoom(zoom, pinch, now), (a.x + b.x) / 2, (a.y + b.y) / 2);
            pinch = now;
        } else if (pointers.size === 1 && event.pointerType === 'mouse' && zoomed()) {
            stage.scrollLeft -= event.clientX - before.x;
            stage.scrollTop -= event.clientY - before.y;
        }
    });
    const release = (event: PointerEvent): void => {
        pointers.delete(event.pointerId);
        pinch = pointers.size === 2 ? spread() : 0;
        const from = start; start = undefined;
        if (from && event.type === 'pointerup' && pointers.size === 0 && !getSelection()?.toString()) {
            const delta = swipeDelta(event.clientX - from.x, event.clientY - from.y, reader.dataset.direction === 'rtl');
            if (delta) turn(delta);
        }
    };
    stage.addEventListener('pointerup', release);
    stage.addEventListener('pointercancel', release);
    stage.addEventListener('dblclick', event => zoomTo(zoom > 1 ? 1 : 2.5, event.clientX, event.clientY));
    new ResizeObserver(() => { layout(); settle(); }).observe(stage);
    window.addEventListener('pagehide', () => { if (moved || carried) report(true); });
    globalThis.document.addEventListener('visibilitychange', () => { if (globalThis.document.hidden && (moved || carried)) report(true); });

    // Pages and a note's page are known once the document has opened.
    reader.querySelector<HTMLElement>('[data-role=turn-tools]')!.hidden = false;
    void openPdf(reader.dataset.fileUrl!).then(opened => {
        document = opened;
        count = opened.numPages;
        reader.querySelector<HTMLElement>('[data-role=seek]')!.hidden = false;
        void show(page, false).then(() => { if (carried) report(false); });
    }).catch(() => problem(reader.dataset.failedLabel!));

    initializeViewChoice(reader, () => page);
    offerBetterView(reader);
}

// The state of the book is looked at now and then; once the reading text is ready and the profile left the choice to Jularr, a quiet offer appears. The page never changes by itself.
function offerBetterView(reader: HTMLElement): void {
    const url = reader.dataset.stateUrl;
    const offer = reader.querySelector<HTMLElement>('[data-role=view-offer]');
    const progress = reader.querySelector<HTMLElement>('[data-role=recognition]');
    if (!url || !offer) return;
    const choice = choiceOf(reader.dataset.viewChosen);
    let dismissed = false, attempt = 0;
    const answer = (view: ReadingView | null): void => {
        // Smart Pages are a view of their own; only the scanned original pages are offered a better one.
        offer.hidden = !offersSmartBook(choice, reader.dataset.smartPages ? 'smartpages' : 'original', view, dismissed);
    };
    offer.querySelector('[data-view-offer-dismiss]')?.addEventListener('click', () => { dismissed = true; offer.hidden = true; });
    offer.querySelector<HTMLButtonElement>('[data-view-offer-open]')?.addEventListener('click', () => {
        location.href = viewAddress(location.pathname + location.search, 'smart', Number(reader.dataset.page) || null);
    });
    const look = async (): Promise<void> => {
        try {
            const response = await fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } });
            if (response.ok) {
                const state = await response.json() as { recommendedView?: number | string | null; recognitionPages?: number | null; recognizedPages?: number; isRecognizing?: boolean };
                const recommended = state.recommendedView === 1 || state.recommendedView === 'Smart' ? 'smart' : state.recommendedView === 2 || state.recommendedView === 'Original' ? 'original' : null;
                answer(recommended);
                if (progress) {
                    progress.hidden = !state.isRecognizing || !state.recognitionPages;
                    progress.textContent = (reader.dataset.recognitionLabel ?? '').replace('{0}', String(state.recognizedPages ?? 0)).replace('{1}', String(state.recognitionPages ?? 0));
                }
                // Nothing changes while no scan is being read and no text waits to be prepared.
                if (recommended === 'smart' || !state.isRecognizing && attempt > 2) return;
            }
        } catch { /* The offer is optional; try again later. */ }
        window.setTimeout(() => void look(), nextLook(attempt++));
    };
    window.setTimeout(() => void look(), nextLook(0));
}
