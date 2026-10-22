import { visibleCount } from './OverflowFit';

// What the title keeps at least while the tools of the header make room for themselves.
const TitleMinimum = 96;

// A bar whose tools may leave it: they carry data-overflow (their name) and data-overflow-rank (the lower, the longer they stay).
// The widths decide, not breakpoints: whatever does not fit goes to the More menu, where a stand-in of the same name appears.
export function initializeOverflow(reader: HTMLElement): void {
    const header = reader.querySelector<HTMLElement>('.reader__top');
    const footer = reader.querySelector<HTMLElement>('.reader__footer');
    const more = reader.querySelector<HTMLElement>('[data-overflow-more]');
    const menu = reader.querySelector<HTMLElement>('[data-panel=more]');
    if (!header || !menu) return;
    const entries = Array.from(menu.querySelectorAll<HTMLElement>('[data-overflow-for]'));
    // What only the menu holds; empty placeholders are not entries.
    const permanent = Array.from(menu.querySelectorAll<HTMLElement>('.reader__menu > :not([data-overflow-for])')).filter(entry => entry.textContent?.trim());
    let frame = 0;

    const tools = (bar: HTMLElement | null) => bar ? Array.from(bar.querySelectorAll<HTMLElement>('[data-overflow]')) : [];
    const gapOf = (element: Element) => parseFloat(getComputedStyle(element).columnGap) || 0;
    const paddingOf = (element: Element) => parseFloat(getComputedStyle(element).paddingLeft) + parseFloat(getComputedStyle(element).paddingRight);

    // Sorting by rank keeps the tools of the lowest rank; the others leave first.
    const decide = (bar: HTMLElement, space: number, gap: number, reserve: boolean): Set<string> => {
        const items = tools(bar).sort((a, b) => Number(a.dataset.overflowRank) - Number(b.dataset.overflowRank));
        const widths = items.map(item => item.getBoundingClientRect().width);
        const fits = visibleCount(widths, space, gap, reserve && more ? more.getBoundingClientRect().width : 0, reserve);
        return new Set(items.slice(fits).map(item => item.dataset.overflow!));
    };

    const arrange = (): void => {
        frame = 0;
        // Natural widths first: everything is shown, measured and then reduced within the same frame.
        const all = [...tools(header), ...tools(footer)];
        all.forEach(item => { item.hidden = false; });
        if (more) more.hidden = false;
        const hidden = new Set<string>();
        const hasPermanent = permanent.some(entry => !entry.hidden);

        const identity = header.querySelector<HTMLElement>('.reader__identity');
        const back = identity?.firstElementChild as HTMLElement | null;
        const cover = identity?.querySelector<HTMLElement>('.reader__cover');
        const tail = header.querySelector<HTMLElement>('.reader__tools');
        if (identity && tail) {
            const identityMinimum = (back?.getBoundingClientRect().width ?? 0) + (cover?.getBoundingClientRect().width ?? 0) + gapOf(identity) * 2 + TitleMinimum;
            // The chapter switch sits beside the tools and leaves first; it shares the space with them.
            const space = header.clientWidth - paddingOf(header) - identityMinimum - gapOf(header) * Math.max(0, header.children.length - 1);
            decide(header, space, gapOf(tail), true).forEach(name => hidden.add(name));
        }

        const settings = footer?.querySelector<HTMLElement>('[data-action=settings]');
        const centre = footer?.querySelector<HTMLElement>('.reader__page-controls');
        const tail2 = footer?.querySelector<HTMLElement>('.reader__view-tools');
        if (footer && settings && tail2) {
            const centreWidth = centre && !centre.hidden ? centre.getBoundingClientRect().width : 0;
            const side = (footer.clientWidth - paddingOf(footer) - centreWidth) / 2 - gapOf(footer);
            // The settings button never leaves: it takes its width first.
            decide(footer, side - settings.getBoundingClientRect().width - gapOf(tail2), gapOf(tail2), false).forEach(name => hidden.add(name));
        }

        for (const item of all) item.hidden = hidden.has(item.dataset.overflow!);
        for (const entry of entries) entry.hidden = !hidden.has(entry.dataset.overflowFor!);
        if (more) more.hidden = !hasPermanent && !hidden.size;
        // An open menu that has nothing left to show closes with its button.
        if (more?.hidden) menu.hidden = true;
    };

    const schedule = (): void => { if (!frame) frame = requestAnimationFrame(arrange); };
    new ResizeObserver(schedule).observe(header);
    if (footer) new ResizeObserver(schedule).observe(footer);
    void document.fonts.ready.then(schedule);
    new MutationObserver(schedule).observe(reader, { attributes: true, attributeFilter: ['data-view', 'data-mode'] });
    schedule();

    // A stand-in does what its tool does, and the menu closes behind it.
    menu.addEventListener('click', event => {
        const entry = (event.target as Element).closest<HTMLElement>('[data-overflow-for]');
        if (!entry || entry.matches('a') || entry.hasAttribute('data-reader-command')) return;
        menu.hidden = true;
        more?.setAttribute('aria-expanded', 'false');
        reader.querySelector<HTMLElement>(`[data-overflow=${entry.dataset.overflowFor}]`)?.click();
    });
}
