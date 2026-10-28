import { updateRangeValue } from '../../Framework/Components/Range';
import { bookFraction, chapterAt, fractionInSection, Resolution, sectionAt, type Chapter } from './ProgressMath';
import { openChapter } from './ChapterNavigation';

interface Paging { seek(fraction: number): void; share(): number }

// The bottom bar's progress slider for prose: the whole book from 0 to 1000, ticks where chapters begin, its percent and chapter beside it.
// Dragging previews the chapter and percent; releasing goes there, inside this chapter by page, to another by opening it.
export function initializeProgressBar(reader: HTMLElement, paging: Paging) {
    const seek = reader.querySelector<HTMLElement>('[data-role=seek]');
    const input = seek?.querySelector<HTMLInputElement>('input[type=range]');
    const ticks = seek?.querySelector<HTMLElement>('[data-role=ticks]');
    const percent = reader.querySelector<HTMLElement>('[data-role=position]');
    const caption = reader.querySelector<HTMLElement>('[data-role=caption] .truncated__text');
    if (!seek || !input) return { refresh: () => {} };
    const offsets = (reader.dataset.offsets ?? '').split(',').filter(Boolean).map(Number).filter(Number.isFinite);
    const total = Math.max(1, Number(reader.dataset.total));
    const before = Number(reader.dataset.before), characters = Number(reader.dataset.characters), section = Number(reader.dataset.section);
    let chapters: Chapter[] = [];
    try {
        const parsed: unknown = JSON.parse(reader.dataset.chapters ?? '[]');
        if (Array.isArray(parsed)) chapters = parsed.filter(item => item && typeof item.section === 'number' && typeof item.before === 'number').map(item => ({ section: item.section, title: String(item.title ?? ''), before: item.before }));
    } catch { /* Without chapters there are no ticks, the slider still seeks. */ }
    const title = caption?.textContent ?? '';
    let dragging = false;

    seek.hidden = false;
    input.min = '0'; input.max = String(Resolution); input.step = '1';
    for (const chapter of chapters) {
        const tick = document.createElement('span');
        tick.className = 'reader__tick';
        tick.style.setProperty('--tick', `${Math.min(100, Math.max(0, chapter.before * 100))}%`);
        ticks?.append(tick);
    }

    const show = (fraction: number): void => {
        const value = Math.round(fraction * Resolution);
        input.valueAsNumber = value;
        updateRangeValue(input);
        const label = `${Math.round(fraction * 100)} %`;
        input.setAttribute('aria-valuetext', label);
        if (percent) percent.textContent = label;
    };
    const refresh = (): void => {
        if (dragging) return;
        show(bookFraction(before, characters, total, paging.share()));
        if (caption) caption.textContent = title;
    };
    input.addEventListener('input', () => {
        dragging = true;
        const fraction = input.valueAsNumber / Resolution;
        show(fraction);
        // While it is dragged the caption names the chapter under the thumb.
        if (caption) caption.textContent = chapterAt(chapters, fraction)?.title || title;
    });
    input.addEventListener('change', () => {
        dragging = false;
        const fraction = input.valueAsNumber / Resolution;
        const target = offsets.length ? sectionAt(offsets, total, fraction) : section;
        // The last stretch of a chapter is its end.
        const near = fractionInSection(offsets, total, target, fraction);
        const inside = near >= .995 ? 1 : near;
        if (target === section) { paging.seek(inside); return; }
        openChapter(`?s=${target}&f=${inside.toFixed(4)}`);
    });
    input.addEventListener('blur', () => { dragging = false; refresh(); });
    return { refresh };
}
