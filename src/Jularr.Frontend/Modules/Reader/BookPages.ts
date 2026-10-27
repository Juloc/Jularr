import { paperPageCount } from './PaperMeasure';
import { readBookPageCounts, writeBookPageCounts } from './BookPageCache';

interface Layout { width: number; height: number; columns: number; gap: number; pages: number; rtl: boolean }
const MaximumBytes = 2 * 1024 * 1024;

// One additional chapter, one request and one inert ruler. Only numeric counts survive a job.
export function initializeBookPages(reader: HTMLElement, content: HTMLElement) {
    const section = Number(reader.dataset.section), sections = Number(reader.dataset.sections);
    const status = reader.querySelector<HTMLElement>('[data-role=book-pages]')!;
    const measurement = document.createElement('div');
    measurement.className = 'reader__measurement';
    measurement.inert = true; measurement.setAttribute('aria-hidden', 'true');
    const ruler = content.cloneNode(false) as HTMLElement;
    ruler.removeAttribute('id'); ruler.removeAttribute('data-role'); ruler.removeAttribute('data-pad');
    ruler.inert = true; ruler.setAttribute('aria-hidden', 'true');
    measurement.append(ruler); reader.append(measurement);
    const pointers = new Set<number>();
    let counts: (number | undefined)[] = [];
    let remaining = 0, pendingIndex = 0, totalCount = 0, pagesBefore = 0, readyText = '';
    let layout: Layout | undefined, key = '', savedKey = '', generation = 0, localPage = 1;
    let cachePending = false, cacheTimer = 0;
    let running = false, failed = false, disposed = false, suspended = false, busyUntil = performance.now() + 450;
    let timer = 0, idle = 0, request: AbortController | undefined;
    // The book's drawn thickness keeps the last complete count of this book, also across chapters and while a new layout is
    // counted: it only needs to look right, never to be exact, so the book never loses its thickness while pages turn.
    const thicknessKey = 'jularr.reader.thickness:' + reader.dataset.sectionsUrl;
    let rememberedCounts = '';
    const thickness = (values: number[]): void => {
        const total = values.reduce((sum, value) => sum + value, 0), before = values.slice(0, section).reduce((sum, value) => sum + value, 0);
        if (reader.dataset.bookThickPages !== String(total)) reader.dataset.bookThickPages = String(total);
        if (reader.dataset.bookThickBefore !== String(before)) reader.dataset.bookThickBefore = String(before);
    };
    try {
        const remembered: unknown = JSON.parse(localStorage.getItem(thicknessKey) ?? 'null');
        if (Array.isArray(remembered) && remembered.length === sections && remembered.every(value => Number.isSafeInteger(value) && value > 0)) thickness(remembered);
    } catch { /* Without storage the book shows its thickness once counted. */ }
    const clearTotal = () => {
        delete reader.dataset.bookPageCount;
        delete reader.dataset.bookPageBefore;
        delete reader.dataset.bookPage;
    };
    const state = (value: string): void => {
        if (reader.dataset.bookPageState !== value) reader.dataset.bookPageState = value;
        status.hidden = value === 'off';
        if (value !== 'ready') status.textContent = status.dataset[value + 'Label'] ?? '';
    };
    const clearRuler = (): void => {
        ruler.querySelectorAll('img').forEach(image => { image.removeAttribute('src'); image.removeAttribute('srcset'); });
        ruler.replaceChildren();
    };
    const clearScheduled = (): void => {
        clearTimeout(timer); timer = 0;
        if (idle) window.cancelIdleCallback(idle);
        idle = 0;
    };
    const cancel = (): void => {
        generation++;
        clearTimeout(cacheTimer); cacheTimer = 0; cachePending = false;
        clearScheduled(); request?.abort(); clearRuler();
    };
    const resetCounts = (): void => {
        counts = Array(sections).fill(undefined);
        remaining = sections; pendingIndex = totalCount = pagesBefore = 0; readyText = savedKey = '';
    };
    const count = (index: number, value: number | undefined): void => {
        const old = counts[index];
        if (old === value) return;
        remaining += (value === undefined ? 1 : 0) - (old === undefined ? 1 : 0);
        const difference = (value ?? 0) - (old ?? 0);
        totalCount += difference;
        if (index < section) pagesBefore += difference;
        counts[index] = value; readyText = '';
    };
    const publish = (): boolean => {
        if (!counts.length || remaining) return false;
        if (reader.dataset.bookPageCount !== String(totalCount)) reader.dataset.bookPageCount = String(totalCount);
        if (reader.dataset.bookPageBefore !== String(pagesBefore)) reader.dataset.bookPageBefore = String(pagesBefore);
        if (reader.dataset.bookPage !== String(pagesBefore + localPage)) reader.dataset.bookPage = String(pagesBefore + localPage);
        state('ready');
        if (!readyText) readyText = status.dataset.readyLabel!.replace('{0}', new Intl.NumberFormat(document.documentElement.lang || undefined).format(totalCount));
        if (status.textContent !== readyText) status.textContent = readyText;
        if (key && reader.dataset.documentRevision && savedKey !== key) {
            savedKey = key;
            void writeBookPageCounts(key, counts as number[]);
        }
        const remembered = JSON.stringify(counts);
        if (remembered !== rememberedCounts) {
            rememberedCounts = remembered; thickness(counts as number[]);
            try { localStorage.setItem(thicknessKey, remembered); } catch { /* Storage is optional. */ }
        }
        return true;
    };
    const available = () => !disposed && !suspended && !failed && !!layout && !document.hidden && !pointers.size && performance.now() >= busyUntil;
    const schedule = (): void => {
        clearScheduled();
        if (disposed || suspended || failed || !layout || publish()) return;
        if (cachePending) { state('paused'); return; }
        if (document.hidden || pointers.size || performance.now() < busyUntil) {
            state('paused');
            if (!document.hidden && !pointers.size) timer = window.setTimeout(schedule, Math.max(1, busyUntil - performance.now()));
            return;
        }
        state('calculating');
        if (running) return;
        if (typeof window.requestIdleCallback === 'function') idle = window.requestIdleCallback(deadline => {
            idle = 0;
            if (!available() || deadline.timeRemaining() < 8) { timer = window.setTimeout(schedule, 50); return; }
            void measure();
        });
        else timer = window.setTimeout(() => { timer = 0; if (available()) void measure(); else schedule(); }, 50);
    };
    const idleBeforeLayout = (signal: AbortSignal): Promise<void> => new Promise((resolve, reject) => {
        let handle = 0, fallback = 0;
        const abort = () => { if (handle) window.cancelIdleCallback(handle); clearTimeout(fallback); signal.removeEventListener('abort', abort); reject(signal.reason); };
        const done = () => { signal.removeEventListener('abort', abort); resolve(); };
        const wait = () => {
            if (signal.aborted) { abort(); return; }
            if (typeof window.requestIdleCallback === 'function') handle = window.requestIdleCallback(deadline => {
                handle = 0;
                if (deadline.timeRemaining() >= 8) done(); else fallback = window.setTimeout(wait, 50);
            });
            else fallback = window.setTimeout(done, 50);
        };
        signal.addEventListener('abort', abort, { once: true }); wait();
    });
    async function measure(): Promise<void> {
        if (!available() || running) return;
        while (pendingIndex < sections && counts[pendingIndex] !== undefined) pendingIndex++;
        const index = pendingIndex;
        if (index >= sections) { publish(); return; }
        const version = generation, dimensions = layout!;
        const controller = new AbortController(); request = controller; running = true;
        const timeout = window.setTimeout(() => controller.abort(new Error('Page measurement timed out.')), 15_000);
        try {
            const response = await fetch(reader.dataset.sectionsUrl! + index, { credentials: 'same-origin', signal: controller.signal });
            if (!response.ok || !response.body) throw new Error('Chapter unavailable.');
            const stream = response.body.getReader(), decoder = new TextDecoder();
            let text = '', bytes = 0;
            try {
                while (true) {
                    const chunk = await stream.read();
                    if (chunk.done) break;
                    bytes += chunk.value.byteLength;
                    if (bytes > MaximumBytes) throw new Error('Chapter exceeds background measurement budget.');
                    text += decoder.decode(chunk.value, { stream: true });
                }
                text += decoder.decode();
            } finally { await stream.cancel(); stream.releaseLock(); }
            await idleBeforeLayout(controller.signal);
            if (version !== generation || disposed || suspended || failed || !layout) return;
            const data: unknown = JSON.parse(text);
            if (!data || typeof data !== 'object' || !('html' in data) || typeof data.html !== 'string' || !('sectionIndex' in data) || data.sectionIndex !== index) throw new Error('Invalid chapter response.');
            // The existing reading service owns sanitization, exactly as for ReaderSection.
            const template = document.createElement('template'); template.innerHTML = data.html;
            if (template.content.querySelectorAll('*').length > 5000) throw new Error('Chapter exceeds background node budget.');
            template.content.querySelectorAll('[id]').forEach(element => element.removeAttribute('id'));
            if (reader.dataset.illustrations === 'false') template.content.querySelectorAll('img,picture,figure').forEach(element => element.remove());
            const images = Array.from(template.content.querySelectorAll('img'));
            if (images.length > 32) throw new Error('Chapter exceeds background image budget.');
            images.forEach(image => { image.loading = 'eager'; image.decoding = 'async'; });
            ruler.style.width = `${dimensions.width}px`; ruler.style.height = `${dimensions.height}px`;
            ruler.replaceChildren(template.content);
            let abortImages: () => void = () => {};
            try {
                await Promise.race([Promise.all([document.fonts.ready, ...images.map(image => image.decode())]), new Promise<never>((_, reject) => {
                    abortImages = () => reject(controller.signal.reason);
                    controller.signal.addEventListener('abort', abortImages, { once: true });
                    if (controller.signal.aborted) abortImages();
                })]);
            } finally { controller.signal.removeEventListener('abort', abortImages); }
            await idleBeforeLayout(controller.signal);
            if (version !== generation || disposed || suspended || failed || !layout) return;
            count(index, paperPageCount(ruler, dimensions.columns, dimensions.gap, dimensions.rtl));
        } catch {
            if (version === generation && !disposed) { failed = true; clearTotal(); state('unavailable'); }
        } finally {
            clearTimeout(timeout);
            if (request === controller) { request = undefined; clearRuler(); }
            running = false; schedule();
        }
    }
    const interact = (): void => {
        busyUntil = performance.now() + 450;
        // A page turn pauses the next chapter, but does not discard an in-flight request.
        schedule();
    };
    reader.addEventListener('pointerdown', event => { pointers.add(event.pointerId); interact(); }, { passive: true });
    window.addEventListener('pointerup', event => { if (pointers.delete(event.pointerId)) interact(); }, { passive: true });
    window.addEventListener('pointercancel', event => { if (pointers.delete(event.pointerId)) interact(); }, { passive: true });
    reader.addEventListener('pointermove', event => { if (pointers.has(event.pointerId)) interact(); }, { passive: true });
    reader.addEventListener('wheel', interact, { passive: true });
    reader.addEventListener('keydown', interact, { passive: true });
    reader.addEventListener('input', interact, { passive: true });
    document.addEventListener('visibilitychange', () => { cancel(); schedule(); });
    window.addEventListener('blur', () => { pointers.clear(); interact(); });
    window.addEventListener('pagehide', event => {
        suspended = true; cancel();
        if (!event.persisted) { disposed = true; measurement.remove(); }
    });
    window.addEventListener('pageshow', event => { if (event.persisted) { suspended = false; cachePending = false; schedule(); } });
    const update = (next: Layout): void => {
        const chosen = reader.querySelector<HTMLElement>('[data-language-choice][aria-pressed=true]')?.dataset.languageChoice ?? reader.dataset.readingLanguage;
        const original = reader.dataset.language?.toLowerCase().split(/[-_]/)[0];
        const supported = reader.dataset.mode === 'paged' && reader.dataset.translated !== 'true'
            && (!chosen || chosen === 'original' || chosen.toLowerCase().split(/[-_]/)[0] === original);
        if (!supported || !reader.dataset.sectionsUrl || !Number.isInteger(sections) || sections < 1 || sections > 100_000 || !Number.isInteger(section) || section < 0 || section >= sections
            || next.width <= 0 || next.height <= 0) {
            cancel(); layout = undefined; key = ''; cachePending = false; counts = []; clearTotal();
            state(reader.dataset.mode === 'paged' ? 'unavailable' : 'off'); return;
        }
        // An undecoded illustration can change the visible chapter's page count.
        // Defer cache validation until it finishes, then retry the saved layout.
        const images = reader.dataset.illustrations === 'false' ? [] : Array.from(content.querySelectorAll('img'));
        if (images.some(image => !image.complete || !image.naturalWidth)) {
            cancel(); layout = undefined; key = ''; resetCounts(); clearTotal();
            if (images.some(image => image.complete && !image.naturalWidth)) { failed = true; state('unavailable'); }
            else state('paused');
            return;
        }
        const style = getComputedStyle(content);
        const nextKey = JSON.stringify(['book-page-layout-v4', reader.dataset.sectionsUrl, reader.dataset.documentRevision, sections, next.width, next.height, next.columns, next.gap, next.rtl, style.font, style.letterSpacing,
            style.getPropertyValue('--reader-text-width'), style.getPropertyValue('--reader-paragraph'),
            style.getPropertyValue('--reader-margin-top'), style.getPropertyValue('--reader-margin-bottom'), style.getPropertyValue('--reader-margin-outer'), style.getPropertyValue('--reader-margin-inner'), reader.dataset.indent, reader.dataset.hyphenation, reader.dataset.illustrations, document.documentElement.lang]);
        if (key !== nextKey) {
            cancel(); key = nextKey; failed = false; resetCounts(); clearTotal();
            cachePending = true;
            const version = generation;
            // An unavailable/blocked IndexedDB must not postpone normal reading indefinitely.
            cacheTimer = window.setTimeout(() => {
                if (version !== generation || !cachePending) return;
                cachePending = false; cacheTimer = 0; schedule();
            }, 300);
            void (reader.dataset.documentRevision ? readBookPageCounts(key, sections) : Promise.resolve(undefined)).then(saved => {
                if (version !== generation || disposed || suspended || !cachePending) return;
                clearTimeout(cacheTimer); cacheTimer = 0; cachePending = false;
                // A late image, font or content change can alter this section without changing a CSS key.
                if (saved && layout && saved[section] === layout.pages) {
                    saved.forEach((value, index) => { if (index !== section) count(index, value); });
                    savedKey = key;
                }
                schedule();
            }).catch(() => {
                if (version !== generation || disposed || suspended || !cachePending) return;
                clearTimeout(cacheTimer); cacheTimer = 0; cachePending = false;
                schedule();
            });
        }
        layout = next;
        if (counts[section] !== undefined && counts[section] !== next.pages) {
            // The visible section is authoritative even when the layout fingerprint is unchanged.
            cancel(); resetCounts(); clearTotal(); cachePending = false;
        }
        count(section, next.pages);
        schedule();
    };
    return {
        update,
        invalidate(): void { cancel(); key = ''; layout = undefined; cachePending = false; counts = []; clearTotal(); state('paused'); },
        position(page: number): void {
            localPage = page;
            if (reader.dataset.bookPageState === 'ready' && reader.dataset.bookPage !== String(pagesBefore + page)) reader.dataset.bookPage = String(pagesBefore + page);
        }
    };
}
