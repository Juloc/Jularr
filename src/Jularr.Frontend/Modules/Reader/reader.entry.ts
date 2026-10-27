import './reader.scss';
import '../Speech/settings.entry.scss';
import { progressReporter } from '../Tracking/Progress';
import { findings, highlights } from './highlights';
import { translator } from './Translation/translation';
import { createTranslationWorker } from './Translation/worker';
import { initializeReaderChrome, initializeReaderSettings } from './Settings';
import { initializePaperPages } from './PaperPages';
import { initializeComicPages } from './ComicPages';
import { initializeReadAloud } from './Speech';
import { initializeFigures } from './Figures';
import { initializeBookmarks } from './bookmarks';
import { initializeOriginalPages } from './OriginalPages';
import { initializeViewChoice } from './ViewChoice';
import { initializeProgressBar } from './ProgressBar';
import { initializeNotes } from './Notes';
import { endReaderLifetime, readerLifetime } from './Lifetime';
import { handleChapters } from './ChapterNavigation';
import { hasNewBuild } from '../Navigation/Navigation';
import { regionSwapped } from '../Navigation/PageSwap';

const first = document.querySelector<HTMLElement>('.reader');
if (first) start(first);
if (first && !first.dataset.format && first.querySelector('[data-role=content]')) openChaptersInPlace(location.pathname);

function start(reader: HTMLElement): void {
    if (reader.dataset.format === 'pdf') initializeReaderChrome(reader);
    else if (reader.dataset.format === 'pages') initializeOriginalPages(reader);
    else if (reader.dataset.format === 'comic') initializeComicPages(reader);
    else if (reader.querySelector('[data-role=content]')) initializeText(reader);
    initializeNotes(reader);
}

// Another chapter of this book opens in place: its reader is built hidden behind the current one and takes its place once the
// book view has decided, so the page neither reloads nor flashes. Whatever cannot open this way loads as a page.
function openChaptersInPlace(book: string): void {
    let opening = 0;
    const sameBook = (address: string) => {
        const url = new URL(address, location.href);
        return url.origin === location.origin && url.pathname === book;
    };
    const ready = (reader: HTMLElement) => new Promise<void>(resolve => {
        const started = performance.now();
        const check = () => {
            const decided = reader.dataset.mode !== 'paged' || reader.dataset.bookReady !== undefined;
            if (decided || performance.now() - started > 2000) window.setTimeout(resolve, 50);
            else window.setTimeout(check, 16);
        };
        check();
    });
    const open = async (address: string, push: boolean): Promise<void> => {
        const url = new URL(address, location.href);
        const ticket = ++opening;
        try {
            const response = await fetch(url, { credentials: 'same-origin', headers: { Accept: 'text/html' } });
            const html = await response.text();
            if (!response.ok || new URL(response.url).pathname !== url.pathname || hasNewBuild(document.documentElement.dataset.build, html)) throw new Error('Not a chapter of this book.');
            const page = new DOMParser().parseFromString(html, 'text/html');
            const next = page.querySelector<HTMLElement>('.reader');
            const current = document.querySelector<HTMLElement>('.reader');
            if (!current || !next || next.dataset.format || !next.querySelector('[data-role=content]')) throw new Error('Not a text chapter.');
            if (ticket !== opening) return;
            const fullscreen = document.fullscreenElement === current;
            const box = current.getBoundingClientRect();
            const incoming = document.adoptNode(next);
            incoming.style.visibility = 'hidden';
            // The current reader stays on screen, out of the flow, while the next one lays out in its place.
            Object.assign(current.style, { position: 'fixed', left: `${box.left}px`, top: `${box.top}px`, width: `${box.width}px`, height: `${box.height}px` });
            current.before(incoming);
            if (push) history.pushState({ readerChapter: true }, '', url);
            document.title = page.title;
            endReaderLifetime();
            start(incoming);
            await ready(incoming);
            current.remove();
            incoming.style.visibility = '';
            regionSwapped();
            if (fullscreen) await incoming.requestFullscreen().catch(() => undefined);
        } catch {
            location.assign(url);
        }
    };
    handleChapters(address => { if (sameBook(address)) void open(address, true); else location.assign(address); });
    document.addEventListener('click', event => {
        const link = (event.target as Element | null)?.closest?.<HTMLAnchorElement>('a[href]');
        if (!link || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        if (link.target && link.target !== '_self' || link.hasAttribute('download') || !sameBook(link.href)) return;
        if (!document.querySelector('.reader')?.contains(link)) return;
        const url = new URL(link.href);
        // A place in this very chapter is not another chapter.
        if (url.search === location.search && url.hash) return;
        event.preventDefault();
        void open(link.href, true);
    });
    // Back and Forward between chapters are the reader's own; the app navigation must not restore an old copy of the page.
    document.addEventListener('htmx:before:history:restore', event => { if (location.pathname === book) event.preventDefault(); });
    window.addEventListener('popstate', () => { if (location.pathname === book) void open(location.href, false); });
}

// The page of the PDF a block stands on: its own anchor, the nearest one before it, or where its section begins; none for a book that is no PDF.
function pdfPageOf(block: HTMLElement | undefined, reader: HTMLElement): number | null {
    for (let element: Element | null = block ?? null; element; element = element.previousElementSibling) {
        const page = Number((element as HTMLElement).dataset.pdfPage ?? (element as HTMLElement).dataset.page);
        if (Number.isInteger(page) && page >= 1) return page;
    }
    const from = Number(reader.dataset.pdfFrom);
    return Number.isInteger(from) && from >= 1 ? from : null;
}

function initializeText(reader: HTMLElement): void {
    const content = reader.querySelector<HTMLElement>('[data-role=content]')!;
    const section = Number(reader.dataset.section);
    const blocks = Math.max(1, Number(reader.dataset.blocks));
    const before = Number(reader.dataset.before);
    const characters = Number(reader.dataset.characters);
    const total = Math.max(1, Number(reader.dataset.total));
    const sections = Number(reader.dataset.sections);
    const signal = readerLifetime();
    let timer = 0;
    let paging: ReturnType<typeof initializePaperPages> | undefined;
    let progress = before / total;
    let marks: ReturnType<typeof initializeBookmarks> | undefined;
    let bar: ReturnType<typeof initializeProgressBar> | undefined;
    const settings = initializeReaderSettings(reader, () => paging?.layout());
    const save = progressReporter(reader.dataset.progressUrl!, reader.dataset.antiforgery!, failed => {
        const feedback = reader.querySelector<HTMLElement>('[data-role=save-error]');
        if (feedback) feedback.hidden = !failed;
    });
    highlights(reader, content, section);
    if (reader.dataset.query) findings(content, reader.dataset.query);
    initializeFigures(reader, content);
    paging = initializePaperPages(reader, content, settings, () => report(false), () => bar?.refresh());
    bar = initializeProgressBar(reader, { seek: share => paging!.seek(share), share: () => paging!.share() });
    paging.layout();
    marks = initializeBookmarks(reader, () => ({
        section, block: Number(paging!.current()?.dataset.b ?? 0), progression: progress,
        title: reader.querySelector('.reader__chapter .truncated__text')?.textContent?.trim() || null
    }));
    initializeReadAloud(reader, content, range => paging!.reveal(range), () => paging!.current());

    initializeViewChoice(reader, () => pdfPageOf(paging!.current(), reader));
    const query = new URLSearchParams(location.search);
    const requested = query.get('b') ?? reader.dataset.startBlock ?? null;
    const fraction = query.has('f') ? Number(query.get('f')) : NaN;
    const match = /^s(\d+)\/b(\d+)$/.exec(reader.dataset.locator ?? '');
    const block = requested && /^\d+$/.test(requested) ? requested : match && Number(match[1]) === section ? match[2] : null;
    requestAnimationFrame(() => {
        const target = location.hash ? document.getElementById(decodeURIComponent(location.hash.slice(1)))
            : block !== null ? content.querySelector<HTMLElement>(`[data-b="${block}"]`) : null;
        if (target && content.contains(target)) paging!.reveal(target);
    });
    // The progress slider sends a share of the chapter, which is a page the same way as its own seek, once the fonts have settled the pages.
    if (Number.isFinite(fraction)) void document.fonts.ready.then(() => requestAnimationFrame(() => requestAnimationFrame(() => paging!.seek(Math.min(1, Math.max(0, fraction))))));

    // Existing translation and annotation services remain the only data owners.
    const translate = reader.dataset.translationUrl ? translator(reader, content, createTranslationWorker) : undefined;
    const quality = reader.querySelector<HTMLButtonElement>('[data-local-quality-toggle]');
    const browserTargets = new Set((reader.dataset.browserTranslationTargets ?? '').split(',').filter(Boolean));
    const localModel = reader.dataset.localModel;
    if (quality && translate && localModel) {
        quality.hidden = false;
        const initialLanguage = reader.dataset.readingLanguage ?? 'original';
        if (initialLanguage !== 'original' && !browserTargets.has(initialLanguage)) {
            reader.dataset.translationEngine = 'gemma';
            quality.setAttribute('aria-pressed', 'true');
        }
        quality.addEventListener('click', () => {
            const readingLanguage = reader.dataset.readingLanguage ?? 'original';
            const gemma = reader.dataset.translationEngine !== 'gemma';
            if (!gemma && readingLanguage !== 'original' && !browserTargets.has(readingLanguage)) return;
            reader.dataset.translationEngine = gemma ? 'gemma' : 'bergamot';
            quality.setAttribute('aria-pressed', String(gemma));
            void translate(readingLanguage === 'original' ? '' : readingLanguage);
        });
    }
    let reading = reader.dataset.readingLanguage ?? 'original';
    const choices = Array.from(reader.querySelectorAll<HTMLButtonElement>('[data-language-choice]'));
    let savingLanguage = false;
    choices.forEach(button => button.addEventListener('click', async () => {
        if (savingLanguage) return;
        const next = button.dataset.languageChoice!;
        if (next === reading) return;
        savingLanguage = true;
        choices.forEach(choice => choice.disabled = true);
        if (quality) quality.disabled = true;
        const panel = reader.querySelector<HTMLElement>('[data-panel=language]');
        panel?.setAttribute('aria-busy', 'true');
        const feedback = reader.querySelector<HTMLElement>('[data-role=translation-status]');
        if (feedback) {
            feedback.textContent = reader.dataset.languageSavingLabel ?? '';
            feedback.hidden = false;
        }
        try {
            const response = await fetch(reader.dataset.languageUrl!, {
                method: 'PUT', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: reader.dataset.antiforgery! },
                body: JSON.stringify({ readingLanguage: next })
            });
            if (!response.ok) throw new Error('Language preference was not saved.');
            reading = next;
            reader.dataset.readingLanguage = next;
            if (localModel && next !== 'original' && !browserTargets.has(next)) {
                reader.dataset.translationEngine = 'gemma';
                quality?.setAttribute('aria-pressed', 'true');
            }
            choices.forEach(choice => choice.setAttribute('aria-pressed', String(choice === button)));
            if (translate) void translate(next === 'original' ? '' : next);
            else if (feedback) feedback.hidden = true;
        } catch {
            if (feedback) {
                feedback.textContent = reader.dataset.translationFailedLabel ?? '';
                feedback.hidden = false;
            }
        } finally {
            savingLanguage = false;
            choices.forEach(choice => choice.disabled = false);
            if (quality) quality.disabled = false;
            panel?.removeAttribute('aria-busy');
        }
    }));

    // Each chapter is a new document. Honor the persisted reading language on
    // initial load too; previously only clicking a language started translation,
    // so chapter navigation silently showed the original English text.
    if (translate && reading !== 'original') void translate(reading);

    content.addEventListener('scroll', schedule, { passive: true });
    reader.querySelector('.reader__canvas')!.addEventListener('wheel', schedule, { passive: true });
    reader.addEventListener('click', event => {
        if ((event.target as Element).closest('a[href]')) report(true);
    });
    window.addEventListener('pagehide', () => report(true), { signal });
    document.addEventListener('visibilitychange', () => { if (document.hidden) report(true); }, { signal });
    // Opening another chapter in place leaves this one as a page would: its place is saved once, then it stays silent.
    signal.addEventListener('abort', () => { window.clearTimeout(timer); report(true); });
    function schedule(): void {
        window.clearTimeout(timer);
        if (signal.aborted) return;
        timer = window.setTimeout(() => report(false), 1500);
    }
    function report(leaving: boolean): void {
        if (signal.aborted && !leaving) return;
        const block = Number(paging?.current()?.dataset.b ?? 0);
        const atEnd = settings.mode === 'paged' ? paging?.end() : content.scrollTop + content.clientHeight >= content.scrollHeight - 4;
        const completed = section === sections - 1 && Boolean(atEnd);
        const progression = completed ? 1 : Math.min(1, (before + characters * (block / blocks)) / total);
        progress = progression;
        marks?.refresh();
        bar?.refresh();
        save({ locator: `s${section}/b${block}`, progression: Number(progression.toFixed(4)), isCompleted: completed }, leaving);
    }
}
