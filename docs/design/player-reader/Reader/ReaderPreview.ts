import { initializePopovers } from '../Framework/Popover';

export function initializeReaderPreview(root: HTMLElement): void {
    const reader = root.querySelector<HTMLElement>('[data-reader-preview]');
    if (!reader) return;
    initializePopovers(reader);
    const chapters = Array.from(reader.querySelectorAll<HTMLElement>('[data-reader-chapter]'));
    const progress = reader.querySelector<HTMLInputElement>('[data-reader-progress]')!;
    const search = reader.querySelector<HTMLInputElement>('#reader-search input[type=search]')!;
    const results = reader.querySelector<HTMLElement>('[data-reader-results]')!;
    const fields = Array.from(reader.querySelectorAll<HTMLInputElement | HTMLSelectElement>('#reader-appearance input:not([type=hidden]), #reader-appearance select'));
    const contents = reader.querySelector<HTMLElement>('[data-reader-contents-list]')!;
    const contentsPanel = reader.querySelector<HTMLElement>('#reader-contents')!;
    const contentsTriggers = reader.querySelectorAll('[popovertarget=reader-contents]:not([popovertargetaction])');
    const sidebar = reader.querySelector<HTMLElement>('[data-reader-sidebar]')!;
    const canvas = reader.querySelector<HTMLElement>('.reader-preview__canvas')!;
    const desktop = matchMedia('(min-width: 1000px)');
    const bookmarks = new Set<number>();
    let current = 0;

    // Presets are values, never separate Readers.
    const book = Object.fromEntries(fields.filter(field => field.name !== 'Preset').map(field =>
        [field.name, field instanceof HTMLInputElement && field.type === 'checkbox' ? field.checked : field.value]));
    const presets: Record<string, Record<string, string | boolean>> = {
        book,
        'light-novel': { ...book, Layout: 'single', Illustrations: true, LineHeight: '175', TextWidth: '74' },
        manga: { ...book, ContentMode: 'pages', Illustrations: true, Decorations: false, ReaderTheme: 'app', Direction: 'rtl' }
    };
    const appearance = (): void => {
        const value = (name: string): string => fields.find(field => field.name === name)!.value;
        const checked = (name: string): string => String(fields.some(field => field.name === name && field instanceof HTMLInputElement && field.checked));
        reader.style.setProperty('--reader-font-size', value('FontSize') + 'px');
        reader.style.setProperty('--reader-line-height', String(Number(value('LineHeight')) / 100));
        reader.style.setProperty('--reader-measure', value('TextWidth'));
        reader.style.setProperty('--reader-font', value('FontFamily') === 'sans' ? 'system-ui, sans-serif' : 'Georgia, serif');
        reader.dataset.readerLayout = value('Layout');
        reader.dataset.readerMode = value('ContentMode');
        reader.dataset.readerDirection = value('Direction');
        reader.dataset.readerTheme = value('ReaderTheme');
        reader.dataset.readerIllustrations = checked('Illustrations');
        reader.dataset.readerDecorations = checked('Decorations');
        chapters.forEach((chapter, index) => chapter.hidden = value('Layout') !== 'scroll' && index !== current);
    };
    const contentsState = (): void => contentsTriggers.forEach(trigger => trigger.setAttribute('aria-expanded',
        String(desktop.matches ? !reader.classList.contains('reader-preview--contents-hidden') : contentsPanel.matches(':popover-open'))));
    const placeContents = (): void => {
        if (contentsPanel.matches(':popover-open')) contentsPanel.hidePopover();
        (desktop.matches ? sidebar : contentsPanel).append(contents);
        contentsState();
    };
    contentsPanel.addEventListener('toggle', contentsState);
    desktop.addEventListener('change', placeContents);
    placeContents();
    appearance();

    const showChapter = (index: number): void => {
        current = Math.max(0, Math.min(chapters.length - 1, index));
        chapters.forEach((chapter, position) => chapter.hidden = reader.dataset.readerLayout !== 'scroll' && position !== current);
        progress.value = String(current);
        reader.querySelector('[data-reader-position]')!.textContent = reader.dataset.positionTemplate!.replace('{0}', String(current + 1)).replace('{1}', String(chapters.length));
        reader.querySelectorAll<HTMLButtonElement>('[data-reader-step]').forEach(button => button.disabled = current + Number(button.dataset.readerStep) < 0 || current + Number(button.dataset.readerStep) >= chapters.length);
        reader.querySelectorAll('[data-reader-jump]').forEach(button => button.setAttribute('aria-current', Number((button as HTMLElement).dataset.readerJump) === current ? 'page' : 'false'));
        search.value = '';
        results.replaceChildren();
        reader.querySelector('[data-reader-title]')!.textContent = chapters[current].querySelector('h1')!.textContent;
        reader.querySelector('[data-reader-bookmark]')!.setAttribute('aria-pressed', String(bookmarks.has(current)));
        if (reader.dataset.readerLayout === 'scroll') chapters[current].scrollIntoView({ block: 'start' });
        else canvas.scrollTop = 0;
    };
    progress.addEventListener('input', () => showChapter(Number(progress.value)));

    // Local content inspection, no progress writes.
    reader.addEventListener('click', async event => {
        if (!(event.target instanceof Element)) return;
        const button = event.target.closest<HTMLElement>('button');
        if (!button) return;
        if (button.getAttribute('popovertarget') === 'reader-contents' && !button.hasAttribute('popovertargetaction') && desktop.matches) {
            event.preventDefault();
            reader.classList.toggle('reader-preview--contents-hidden');
            contentsState();
        }
        if (button.dataset.readerStep) showChapter(current + Number(button.dataset.readerStep));
        if (button.dataset.readerJump) {
            showChapter(Number(button.dataset.readerJump));
            if (contentsPanel.matches(':popover-open')) contentsPanel.hidePopover();
        }
        if (button.hasAttribute('data-reader-bookmark')) {
            if (bookmarks.has(current)) bookmarks.delete(current);
            else bookmarks.add(current);
            button.setAttribute('aria-pressed', String(bookmarks.has(current)));
        }
        if (button.hasAttribute('data-reader-clean') || button.hasAttribute('data-reader-restore')) {
            const clean = button.hasAttribute('data-reader-clean');
            reader.querySelectorAll<HTMLElement>('[popover]:popover-open').forEach(panel => panel.hidePopover());
            reader.classList.toggle('reader-preview--clean', clean);
            reader.querySelector<HTMLElement>('[data-reader-restore]')!.hidden = !clean;
            reader.querySelector('[data-reader-clean]')!.setAttribute('aria-pressed', String(clean));
            reader.querySelector<HTMLElement>(clean ? '[data-reader-restore]' : '[popovertarget=reader-more]:not([popovertargetaction])')!.focus({ preventScroll: true });
        }
        if (button.hasAttribute('data-reader-fullscreen')) {
            try {
                if (document.fullscreenElement) await document.exitFullscreen();
                else await reader.requestFullscreen();
            } catch {
                const message = reader.querySelector<HTMLElement>('[data-reader-message]')!;
                message.textContent = reader.dataset.fullscreenError!;
                message.hidden = false;
            }
        }
    });

    // The same typed controls, local appearance.
    reader.addEventListener('input', event => {
        const field = event.target;
        if (!(field instanceof HTMLInputElement || field instanceof HTMLSelectElement) || !field.validity.valid) return;
        if (field.name === 'Preset') fields.forEach(control => {
            const value = presets[field.value][control.name];
            if (value === undefined) return;
            if (control instanceof HTMLInputElement && control.type === 'checkbox') control.checked = Boolean(value);
            else control.value = String(value);
        });
        if (field.closest('#reader-appearance')) appearance();
    });

    // Search only the illustrative chapter.
    search.addEventListener('input', () => {
        results.replaceChildren();
        const query = search.value.trim().toLocaleLowerCase();
        if (!query) return;
        if (reader.dataset.readerMode === 'pages') {
            results.textContent = reader.dataset.pageSearchNotice!;
            return;
        }
        chapters[current].querySelectorAll('p').forEach(paragraph => {
            if (!paragraph.textContent.toLocaleLowerCase().includes(query)) return;
            const result = document.createElement('button');
            result.type = 'button';
            result.className = 'button';
            result.textContent = paragraph.textContent;
            result.addEventListener('click', () => {
                reader.querySelector<HTMLElement>('#reader-search')!.hidePopover();
                paragraph.scrollIntoView({ block: 'center', behavior: 'instant' });
            });
            results.append(result);
        });
        if (!results.childElementCount) results.textContent = reader.dataset.searchEmpty!;
    });
}
