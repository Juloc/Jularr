import { contrast } from '../Appearance/Palette';
import { initializeRanges } from '../../Framework/Components/Range';
import { initializeOverflow } from './Overflow';

export interface ReaderSettings {
    size: number;
    spacing: number;
    paragraph: number;
    font: 'serif' | 'sans';
    width: number;
    indent: boolean;
    hyphenation: boolean;
    illustrations: boolean;
    mode: 'paged' | 'scroll';
    layout: 'auto' | 'single' | 'spread';
    direction: 'ltr' | 'rtl';
    surface: 'paper' | 'clean';
    colour: string;
    animation: 'system' | 'on' | 'off';
    preset: 'book' | 'novel' | 'manga';
    fit: 'page' | 'width';
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
        paragraph: number(stored.paragraph, 0, 200, 100),
        font: stored.font === 'sans' ? 'sans' : 'serif',
        width: number(stored.width, 40, 90, 68),
        indent: stored.indent === true,
        hyphenation: stored.hyphenation !== false,
        illustrations: stored.illustrations !== false,
        mode: stored.mode === 'scroll' ? 'scroll' : 'paged',
        layout: stored.layout === 'single' || stored.layout === 'spread' ? stored.layout : 'auto',
        direction: stored.direction === 'ltr' || stored.direction === 'rtl' ? stored.direction : rtl ? 'rtl' : 'ltr',
        surface: stored.surface === 'clean' ? 'clean' : 'paper',
        colour: typeof stored.colour === 'string' && /^#[a-f\d]{6}$/i.test(stored.colour) ? stored.colour
            : stored.theme === 'dark' ? '#141414' : stored.theme === 'sepia' ? '#f4ecd8' : '#f7f3ec',
        animation: stored.animation === 'on' || stored.animation === 'off' ? stored.animation : 'system',
        preset: stored.preset === 'novel' || stored.preset === 'manga' ? stored.preset : 'book',
        fit: stored.fit === 'width' ? 'width' : 'page'
    };
}

// What the profile keeps for every device: the look of the text. The rest of the settings stays with the device.
const ProfileKeys: (keyof ReaderSettings)[] = ['font', 'size', 'spacing', 'paragraph', 'width', 'hyphenation'];

export function initializeReaderSettings(reader: HTMLElement, changed: () => void): ReaderSettings {
    let stored: unknown, profile: unknown;
    try { stored = JSON.parse(localStorage.getItem(Key) ?? 'null'); } catch { /* Device preferences are optional. */ }
    try { profile = JSON.parse(reader.dataset.profileSettings ?? 'null'); } catch { /* A profile without choices leaves the device's. */ }
    const settings = readerSettings({ ...(stored as object | null), ...(profile as object | null) }, reader.dataset.direction === 'rtl');
    let saving = 0;
    const saveProfile = (now = false): void => {
        const url = reader.dataset.settingsUrl;
        if (!url) return;
        window.clearTimeout(saving);
        const send = (): void => {
            saving = 0;
            void fetch(url, {
                method: 'PUT', credentials: 'same-origin', keepalive: true,
                headers: { 'Content-Type': 'application/json', RequestVerificationToken: reader.dataset.antiforgery! },
                body: JSON.stringify({ fontTypeId: settings.font === 'sans' ? 2 : 1, size: settings.size, lineSpacing: settings.spacing, paragraphSpacing: settings.paragraph,
                    textWidth: settings.width, hyphenation: settings.hyphenation })
            }).catch(() => { /* The device keeps its copy; the next change tries again. */ });
        };
        if (now) send(); else saving = window.setTimeout(send, 600);
    };
    window.addEventListener('pagehide', () => { if (saving) saveProfile(true); });
    const fields = Array.from(reader.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[data-panel=settings] input:not([type=hidden]), [data-panel=settings] select'));
    const names: Record<string, keyof ReaderSettings> = { Size: 'size', Spacing: 'spacing', Paragraph: 'paragraph', FontFamily: 'font', TextWidth: 'width', Indent: 'indent', Hyphenation: 'hyphenation', Illustrations: 'illustrations',
        Mode: 'mode', Layout: 'layout', Direction: 'direction', Surface: 'surface', Colour: 'colour', Animation: 'animation', Preset: 'preset', Fit: 'fit' };
    const apply = (): void => {
        reader.dataset.mode = settings.mode;
        reader.dataset.layout = settings.layout;
        reader.dataset.direction = settings.direction;
        reader.dataset.surface = settings.surface;
        reader.dataset.animation = settings.animation;
        reader.dataset.indent = String(settings.indent);
        reader.dataset.hyphenation = String(settings.hyphenation);
        reader.dataset.illustrations = String(settings.illustrations);
        reader.style.setProperty('--reader-font', settings.font === 'sans' ? "'Segoe UI', system-ui, sans-serif" : "Georgia, 'Iowan Old Style', serif");
        reader.style.setProperty('--reader-text-width', `${settings.width}ch`);
        reader.style.setProperty('--reader-size', `${settings.size}px`);
        reader.style.setProperty('--reader-spacing', String(settings.spacing / 100));
        reader.style.setProperty('--reader-paragraph', String(settings.paragraph / 100));
        reader.style.setProperty('--reader-paper', settings.colour);
        reader.style.setProperty('--reader-ink', contrast(settings.colour, '#000000') > contrast(settings.colour, '#ffffff') ? '#181818' : '#fafafa');
        fields.forEach(field => {
            const key = names[field.name];
            if (!key) return;
            if (field instanceof HTMLInputElement && field.type === 'checkbox') field.checked = Boolean(settings[key]);
            else field.value = String(settings[key]);
        });
        initializeRanges(reader);
    };
    apply();
    reader.addEventListener('input', event => {
        const field = event.target;
        if (!(field instanceof HTMLInputElement || field instanceof HTMLSelectElement) || !field.closest('[data-panel=settings]') || !field.validity.valid) return;
        const key = names[field.name];
        if (!key) return;
        const value = field instanceof HTMLInputElement && field.type === 'checkbox' ? field.checked
            : key === 'size' || key === 'spacing' || key === 'paragraph' || key === 'width' ? Number(field.value) : field.value;
        const next = { ...settings, [key]: value };
        if (key === 'preset') {
            next.spacing = next.preset === 'novel' ? 175 : 165;
            next.surface = next.preset === 'manga' ? 'clean' : 'paper';
            next.layout = 'auto';
        }
        Object.assign(settings, readerSettings(next));
        apply();
        try { localStorage.setItem(Key, JSON.stringify(settings)); } catch { /* Controls still work without storage. */ }
        if (ProfileKeys.includes(key) || key === 'preset') saveProfile();
        changed();
    });
    reader.querySelector('[data-reader-select]')?.addEventListener('click', event => {
        const selected = reader.dataset.selecting !== 'true';
        reader.dataset.selecting = String(selected);
        (event.currentTarget as HTMLElement).setAttribute('aria-pressed', String(selected));
        if (!selected) getSelection()?.removeAllRanges();
        changed();
    });
    const openers = new Map<string, HTMLElement>();
    const disclosureState = new Map<HTMLDetailsElement, boolean>();
    const panelButtons = '[data-action], [data-reader-open-panel], [data-reader-open-settings]';
    const action = (button: HTMLElement) => button.hasAttribute('data-reader-open-settings') ? 'settings' : button.dataset.readerOpenPanel ?? button.dataset.action;
    const expanded = (name: string, open: boolean) => reader.querySelectorAll<HTMLElement>(panelButtons).forEach(button => {
        if (action(button) === name) button.setAttribute('aria-expanded', String(open));
    });
    const opener = (name: string) => {
        const preferred = openers.get(name);
        if (preferred?.getClientRects().length) return preferred;
        const canonical = reader.querySelector<HTMLElement>(`[data-action=${name}]`);
        return canonical?.getClientRects().length ? canonical : reader.querySelector<HTMLElement>('[data-action=more]');
    };
    reader.addEventListener('click', event => {
        const button = (event.target as Element).closest<HTMLElement>(panelButtons);
        if (!button || !reader.contains(button)) return;
        const name = action(button)!;
        const requested = button.dataset.readerOpenSettings ?? 'all';
        reader.querySelectorAll<HTMLElement>('[data-panel]').forEach(panel => {
            if (panel.dataset.panel === 'contents') return;
            const open = panel.dataset.panel === name && (panel.hidden || name === 'settings' && panel.dataset.currentGroup !== requested);
            panel.hidden = !open;
            expanded(panel.dataset.panel!, open);
            if (open) openers.set(name, button);
            if (open && name === 'settings') {
                panel.dataset.currentGroup = requested;
                if (requested !== 'all' && !disclosureState.size) panel.querySelectorAll<HTMLDetailsElement>('[data-reader-group]').forEach(group => disclosureState.set(group, group.open));
                panel.querySelectorAll<HTMLDetailsElement>('[data-reader-group]').forEach(group => {
                    group.hidden = requested !== 'all' && group.dataset.readerGroup !== requested;
                    if (requested !== 'all' && !group.hidden) group.open = true;
                    if (requested === 'all' && disclosureState.has(group)) group.open = disclosureState.get(group)!;
                });
                if (requested === 'all') disclosureState.clear();
            }
        });
        if (name === 'contents') {
            reader.dataset.contents = reader.dataset.contents === 'false' ? 'true' : 'false';
            button.setAttribute('aria-expanded', String(reader.dataset.contents === 'true'));
            if (reader.dataset.contents === 'true') openers.set(name, button);
        } else {
            reader.dataset.contents = 'false';
            reader.querySelector('[data-action=contents]')?.setAttribute('aria-expanded', 'false');
        }
    });
    reader.dataset.contents = 'false';
    const topbar = reader.querySelector<HTMLElement>('.reader__top')!;
    const identity = reader.querySelector<HTMLElement>('.reader__identity')!;
    const headerSize = new ResizeObserver(() => reader.style.setProperty('--reader-header-height', `${topbar.getBoundingClientRect().height || identity.getBoundingClientRect().height}px`));
    headerSize.observe(topbar);
    headerSize.observe(identity);
    reader.querySelector<HTMLButtonElement>('[data-action=contents]')?.setAttribute('aria-expanded', reader.dataset.contents);
    reader.querySelector<HTMLElement>('[data-panel=contents]')?.removeAttribute('hidden');
    reader.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        const trigger = reader.dataset.contents === 'true' ? 'contents'
            : reader.querySelector<HTMLElement>('[data-panel]:not([data-panel=contents]):not([hidden])')?.dataset.panel;
        if (trigger) {
            event.preventDefault();
            opener(trigger)?.focus({ preventScroll: true });
        }
        reader.dataset.contents = 'false';
        reader.querySelectorAll<HTMLElement>('[data-panel]:not([data-panel=contents])').forEach(panel => panel.hidden = true);
        reader.querySelectorAll(panelButtons).forEach(button => button.setAttribute('aria-expanded', 'false'));
    });
    reader.querySelectorAll<HTMLButtonElement>('[data-reader-close]').forEach(button => button.addEventListener('click', () => {
        if (button.dataset.readerClose === 'contents') reader.dataset.contents = 'false';
        else reader.querySelector<HTMLElement>(`[data-panel=${button.dataset.readerClose}]`)!.hidden = true;
        const name = button.dataset.readerClose!;
        const trigger = opener(name);
        expanded(name, false);
        trigger?.focus({ preventScroll: true });
    }));
    initializeReaderChrome(reader);
    return settings;
}

// Transient chrome only: the same frame/content and canonical locator survive every view.
export function initializeReaderChrome(reader: HTMLElement): void {
    const top = reader.querySelector<HTMLElement>('.reader__top')!;
    const footer = reader.querySelector<HTMLElement>('.reader__footer')!;
    const theater = reader.querySelector<HTMLButtonElement>('[data-reader-theater]')!;
    const fullscreen = reader.querySelector<HTMLButtonElement>('[data-reader-fullscreen]')!;
    const reveal = Array.from(reader.querySelectorAll<HTMLButtonElement>('[data-reader-reveal]'));
    let previous: 'normal' | 'theater' = 'normal';
    reader.dataset.view = 'normal';
    const closePanels = (): void => {
        reader.querySelectorAll<HTMLElement>('[data-panel]:not([data-panel=contents])').forEach(panel => panel.hidden = true);
        reader.querySelectorAll('[data-action]:not([data-action=contents]), [data-reader-open-panel], [data-reader-open-settings]').forEach(button => button.setAttribute('aria-expanded', 'false'));
    };
    const chrome = (visible: boolean): void => {
        const immersive = reader.dataset.view === 'fullscreen';
        reader.dataset.chrome = visible ? 'visible' : 'hidden';
        top.inert = footer.inert = immersive && !visible;
        reveal.forEach(button => {
            button.hidden = !immersive;
            button.setAttribute('aria-expanded', String(visible));
            button.setAttribute('aria-label', (visible ? button.dataset.hideLabel : button.dataset.showLabel)!);
        });
        if (!visible) {
            closePanels();
            reader.dataset.contents = 'false';
            reader.querySelector('[data-action=contents]')?.setAttribute('aria-expanded', 'false');
            if (top.contains(document.activeElement) || footer.contains(document.activeElement)) reader.querySelector<HTMLElement>('.reader__canvas')!.focus({ preventScroll: true });
        }
    };
    const view = (value: 'normal' | 'theater' | 'fullscreen'): void => {
        const fromPanel = document.activeElement?.closest('[data-panel]');
        closePanels();
        reader.dataset.view = value;
        reader.dataset.contents = 'false';
        reader.querySelector('[data-action=contents]')?.setAttribute('aria-expanded', String(reader.dataset.contents === 'true'));
        theater.setAttribute('aria-pressed', String(value === 'theater'));
        fullscreen.setAttribute('aria-pressed', String(value === 'fullscreen'));
        reader.querySelectorAll<HTMLElement>('[data-reader-command]').forEach(button => button.setAttribute('aria-pressed', String(value === button.dataset.readerCommand)));
        chrome(value !== 'fullscreen');
        if (fromPanel) reader.querySelector<HTMLElement>('.reader__canvas')!.focus({ preventScroll: true });
    };
    const toggleTheater = async () => {
        if (reader.dataset.view === 'fullscreen') {
            previous = 'theater';
            try { await document.exitFullscreen(); }
            catch { reader.querySelector<HTMLElement>('[data-role=fullscreen-error]')?.removeAttribute('hidden'); }
        } else {
            view(reader.dataset.view === 'theater' ? 'normal' : 'theater');
        }
    };
    const toggleFullscreen = async () => {
        const failure = reader.querySelector<HTMLElement>('[data-role=fullscreen-error]');
        if (failure) failure.hidden = true;
        try {
            if (document.fullscreenElement === reader) await document.exitFullscreen();
            else {
                previous = reader.dataset.view === 'theater' ? 'theater' : 'normal';
                await reader.requestFullscreen();
            }
        } catch { if (failure) failure.hidden = false; }
    };
    theater.addEventListener('click', toggleTheater);
    fullscreen.addEventListener('click', toggleFullscreen);
    reader.addEventListener('click', event => {
        const button = (event.target as Element).closest<HTMLElement>('[data-reader-command]');
        if (!button || !reader.contains(button)) return;
        if (button.dataset.readerCommand === 'theater') void toggleTheater();
        if (button.dataset.readerCommand === 'fullscreen') void toggleFullscreen();
    });
    document.addEventListener('fullscreenchange', () => {
        if (document.fullscreenElement === reader) view('fullscreen');
        else if (reader.dataset.view === 'fullscreen') {
            view(previous);
            (fullscreen.getClientRects().length ? fullscreen : reader.querySelector<HTMLElement>('[data-action=more]') ?? reader.querySelector<HTMLElement>('.reader__canvas'))?.focus({ preventScroll: true });
        }
    });
    reveal.forEach(button => button.addEventListener('click', () => chrome(reader.dataset.chrome !== 'visible')));
    initializeOverflow(reader);
    reader.addEventListener('keydown', event => {
        if (event.key === 'Escape' && !event.defaultPrevented && reader.dataset.view === 'fullscreen' && reader.dataset.chrome === 'visible') {
            chrome(false);
        }
    });
}
