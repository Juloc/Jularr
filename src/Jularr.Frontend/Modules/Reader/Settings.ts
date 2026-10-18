import { contrast } from '../Appearance/Palette';

export interface ReaderSettings {
    size: number;
    spacing: number;
    mode: 'paged' | 'scroll';
    layout: 'auto' | 'single' | 'spread';
    direction: 'ltr' | 'rtl';
    surface: 'paper' | 'clean';
    colour: string;
    animation: 'system' | 'on' | 'off';
    preset: 'book' | 'novel' | 'manga';
}

const Key = 'jularr.reader';
export function readerSettings(value: unknown, rtl = false): ReaderSettings {
    const stored = value && typeof value === 'object' ? value as Partial<ReaderSettings> & { theme?: unknown } : {};
    const number = (value: unknown, minimum: number, maximum: number, fallback: number) =>
        typeof value === 'number' && Number.isFinite(value) ? Math.min(maximum, Math.max(minimum, value)) : fallback;
    return {
        // Existing reader sizes were rem; keep that device preference when opening the new controls.
        size: number(typeof stored.size === 'number' && stored.size > 0 && stored.size <= 3 ? Math.round(stored.size * 16) : stored.size, 14, 40, 20),
        spacing: number(stored.spacing, 120, 220, 165),
        mode: stored.mode === 'scroll' ? 'scroll' : 'paged',
        layout: stored.layout === 'single' || stored.layout === 'spread' ? stored.layout : 'auto',
        direction: stored.direction === 'ltr' || stored.direction === 'rtl' ? stored.direction : rtl ? 'rtl' : 'ltr',
        surface: stored.surface === 'clean' ? 'clean' : 'paper',
        colour: typeof stored.colour === 'string' && /^#[a-f\d]{6}$/i.test(stored.colour) ? stored.colour
            : stored.theme === 'dark' ? '#141414' : stored.theme === 'sepia' ? '#f4ecd8' : '#f7f3ec',
        animation: stored.animation === 'on' || stored.animation === 'off' ? stored.animation : 'system',
        preset: stored.preset === 'novel' || stored.preset === 'manga' ? stored.preset : 'book'
    };
}

export function initializeReaderSettings(reader: HTMLElement, changed: () => void): ReaderSettings {
    let stored: unknown;
    try { stored = JSON.parse(localStorage.getItem(Key) ?? 'null'); } catch { /* Device preferences are optional. */ }
    const settings = readerSettings(stored, reader.dataset.direction === 'rtl');
    const fields = Array.from(reader.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[data-panel=settings] input, [data-panel=settings] select'));
    const names: Record<string, keyof ReaderSettings> = { Size: 'size', Spacing: 'spacing', Mode: 'mode', Layout: 'layout', Direction: 'direction', Surface: 'surface', Colour: 'colour', Animation: 'animation', Preset: 'preset' };
    const apply = (): void => {
        reader.dataset.mode = settings.mode;
        reader.dataset.layout = settings.layout;
        reader.dataset.direction = settings.direction;
        reader.dataset.surface = settings.surface;
        reader.dataset.animation = settings.animation;
        reader.style.setProperty('--reader-size', `${settings.size}px`);
        reader.style.setProperty('--reader-spacing', String(settings.spacing / 100));
        reader.style.setProperty('--reader-paper', settings.colour);
        reader.style.setProperty('--reader-ink', contrast(settings.colour, '#000000') > contrast(settings.colour, '#ffffff') ? '#181818' : '#fafafa');
        fields.forEach(field => { const key = names[field.name]; if (key) field.value = String(settings[key]); });
    };
    apply();
    reader.addEventListener('input', event => {
        const field = event.target;
        if (!(field instanceof HTMLInputElement || field instanceof HTMLSelectElement) || !field.closest('[data-panel=settings]') || !field.validity.valid) return;
        const key = names[field.name];
        if (!key) return;
        const next = { ...settings, [key]: key === 'size' || key === 'spacing' ? Number(field.value) : field.value };
        if (key === 'preset') {
            next.spacing = next.preset === 'novel' ? 175 : 165;
            next.surface = next.preset === 'manga' ? 'clean' : 'paper';
            next.layout = 'auto';
        }
        Object.assign(settings, readerSettings(next));
        apply();
        try { localStorage.setItem(Key, JSON.stringify(settings)); } catch { /* Controls still work without storage. */ }
        changed();
    });
    reader.querySelector('[data-reader-select]')?.addEventListener('click', event => {
        const selected = reader.dataset.selecting !== 'true';
        reader.dataset.selecting = String(selected);
        (event.currentTarget as HTMLElement).setAttribute('aria-pressed', String(selected));
        if (!selected) getSelection()?.removeAllRanges();
        changed();
    });
    reader.querySelectorAll<HTMLButtonElement>('[data-action]').forEach(button => button.addEventListener('click', () => {
        const name = button.dataset.action!;
        reader.querySelectorAll<HTMLElement>('[data-panel]').forEach(panel => {
            if (panel.dataset.panel === 'contents') return;
            const open = panel.dataset.panel === name && panel.hidden;
            panel.hidden = !open;
            reader.querySelector(`[data-action=${panel.dataset.panel}]`)?.setAttribute('aria-expanded', String(open));
        });
        if (name === 'contents') {
            reader.dataset.contents = reader.dataset.contents === 'false' ? 'true' : 'false';
            button.setAttribute('aria-expanded', String(reader.dataset.contents === 'true'));
            changed();
        }
    }));
    const desktop = matchMedia('(min-width: 1000px)');
    reader.dataset.contents = String(desktop.matches);
    desktop.addEventListener('change', () => {
        reader.dataset.contents = String(desktop.matches);
        reader.querySelector('[data-action=contents]')?.setAttribute('aria-expanded', reader.dataset.contents);
        changed();
    });
    const topbar = reader.querySelector<HTMLElement>('.reader__top')!;
    new ResizeObserver(() => reader.style.setProperty('--reader-header-height', `${topbar.getBoundingClientRect().height}px`)).observe(topbar);
    reader.querySelector<HTMLButtonElement>('[data-action=contents]')?.setAttribute('aria-expanded', reader.dataset.contents);
    reader.querySelector<HTMLElement>('[data-panel=contents]')?.removeAttribute('hidden');
    reader.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        reader.querySelectorAll<HTMLElement>('[data-panel]:not([data-panel=contents])').forEach(panel => panel.hidden = true);
        reader.querySelectorAll('[data-action]:not([data-action=contents])').forEach(button => button.setAttribute('aria-expanded', 'false'));
    });
    reader.querySelector('[data-reader-fullscreen]')?.addEventListener('click', async () => {
        try { if (document.fullscreenElement) await document.exitFullscreen(); else await reader.requestFullscreen(); }
        catch { reader.querySelector<HTMLElement>('[data-role=fullscreen-error]')?.removeAttribute('hidden'); }
    });
    return settings;
}
