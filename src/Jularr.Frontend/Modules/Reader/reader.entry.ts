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

const reader = document.querySelector<HTMLElement>('.reader');
if (reader) {
    if (reader.dataset.format === 'pdf') initializeReaderChrome(reader);
    else if (reader.dataset.format === 'comic') initializeComicPages(reader);
    else if (reader.querySelector('[data-role=content]')) initializeText(reader);
}

function initializeText(reader: HTMLElement): void {
    const content = reader.querySelector<HTMLElement>('[data-role=content]')!;
    const position = reader.querySelector<HTMLElement>('[data-role=position]');
    const section = Number(reader.dataset.section);
    const blocks = Math.max(1, Number(reader.dataset.blocks));
    const before = Number(reader.dataset.before);
    const characters = Number(reader.dataset.characters);
    const total = Math.max(1, Number(reader.dataset.total));
    const sections = Number(reader.dataset.sections);
    let timer = 0;
    let paging: ReturnType<typeof initializePaperPages> | undefined;
    const settings = initializeReaderSettings(reader, () => paging?.layout());
    const save = progressReporter(reader.dataset.progressUrl!, reader.dataset.antiforgery!, failed => {
        const feedback = reader.querySelector<HTMLElement>('[data-role=save-error]');
        if (feedback) feedback.hidden = !failed;
    });
    highlights(reader, content, section);
    if (reader.dataset.query) findings(content, reader.dataset.query);
    paging = initializePaperPages(reader, content, settings, () => report(false));
    paging.layout();
    initializeReadAloud(reader, content, range => paging!.reveal(range), () => paging!.current());

    const requested = new URLSearchParams(location.search).get('b');
    const match = /^s(\d+)\/b(\d+)$/.exec(reader.dataset.locator ?? '');
    const block = requested && /^\d+$/.test(requested) ? requested : match && Number(match[1]) === section ? match[2] : null;
    requestAnimationFrame(() => {
        const target = location.hash ? document.getElementById(decodeURIComponent(location.hash.slice(1)))
            : block !== null ? content.querySelector<HTMLElement>(`[data-b="${block}"]`) : null;
        if (target && content.contains(target)) paging!.reveal(target);
    });

    // Existing translation and annotation services remain the only data owners.
    const translate = reader.dataset.translationUrl ? translator(reader, content, createTranslationWorker) : undefined;
    let reading = reader.dataset.readingLanguage ?? 'original';
    if (translate) void translate(reading === 'original' ? '' : reading);
    reader.querySelectorAll<HTMLButtonElement>('[data-language-choice]').forEach(button => button.addEventListener('click', async () => {
        reading = button.dataset.languageChoice!;
        reader.dataset.readingLanguage = reading;
        reader.querySelectorAll<HTMLButtonElement>('[data-language-choice]').forEach(choice => choice.setAttribute('aria-pressed', String(choice === button)));
        try {
            const response = await fetch(reader.dataset.languageUrl!, {
                method: 'PUT', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: reader.dataset.antiforgery! },
                body: JSON.stringify({ readingLanguage: reading })
            });
            if (!response.ok) throw new Error('Language preference was not saved.');
            if (translate) await translate(reading === 'original' ? '' : reading);
        } catch {
            const feedback = reader.querySelector<HTMLElement>('[data-role=translation-status]');
            if (feedback) { feedback.textContent = reader.dataset.translationFailedLabel!; feedback.hidden = false; }
        }
    }));

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
        if (position) position.textContent = `${Math.round(progression * 100)} %`;
        save({ locator: `s${section}/b${block}`, progression: Number(progression.toFixed(4)), isCompleted: completed }, leaving);
    }
}
