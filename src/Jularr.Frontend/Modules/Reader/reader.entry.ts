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

const reader = document.querySelector<HTMLElement>('.reader');
if (reader) {
    if (reader.dataset.format === 'pdf') initializeReaderChrome(reader);
    else if (reader.dataset.format === 'pages') initializeOriginalPages(reader);
    else if (reader.dataset.format === 'comic') initializeComicPages(reader);
    else if (reader.querySelector('[data-role=content]')) initializeText(reader);
    initializeNotes(reader);
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
    const provider = reader.querySelector<HTMLElement>('[data-translation-provider]');
    const showProvider = () => {
        if (!provider) return;
        const language = reader.dataset.readingLanguage ?? 'original';
        provider.textContent = language === 'original' ? (reader.dataset.providerOriginalLabel ?? '')
            : reader.dataset.translationEngine === 'gemma'
                ? (reader.dataset.providerLocalLabel ?? 'TranslateGemma') + ' · ' + (localModel ?? '')
                : reader.dataset.providerBrowserLabel ?? 'Mozilla/Bergamot';
    };
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
            showProvider();
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
            showProvider();
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

    showProvider();
    // Each chapter is a new document. Honor the persisted reading language on
    // initial load too; previously only clicking a language started translation,
    // so chapter navigation silently showed the original English text.
    if (translate && reading !== 'original') void translate(reading);

    content.addEventListener('scroll', schedule, { passive: true });
    reader.querySelector('.reader__canvas')!.addEventListener('wheel', schedule, { passive: true });
    reader.addEventListener('click', event => {
        if ((event.target as Element).closest('a[href]')) report(true);
    });
    window.addEventListener('pagehide', () => report(true));
    document.addEventListener('visibilitychange', () => { if (document.hidden) report(true); });
    function schedule(): void {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => report(false), 1500);
    }
    function report(leaving: boolean): void {
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
