import { bookDimensions, paperProfile, paperOutline, printPlacement, type Point } from './BookGeometry';
import { BookLeaf } from './BookLeaf';
import { paintBookSolid, paperLighting } from './BookSolid';
import { pagePrint } from './PagePrint';

export interface BookGroup { source: HTMLElement; page: number; count: number }
export interface BookAdapter {
    current: number; columns: number; minimum: number; maximum: number;
    group(at: number): BookGroup | undefined;
    next(at: number, delta: number): number;
    number(at: number): number;
    commit(at: number): void;
}
interface Flight {
    leaf: BookLeaf; from: number; to: number; delta: number;
    intent: 'holding' | 'commit' | 'return'; finished: boolean; bundled?: boolean; printingDirty?: boolean;
    animation?: { start: number; from: Point; to: Point; duration: number; released: boolean };
}

// One decorative renderer for prose and images. The adapters alone commit
// pagination/progress. Idle/held paper schedules no frames; live leaves <= 3.
export function initializeBookRenderer(reader: HTMLElement) {
    const root = reader.querySelector<HTMLElement>('.reader-book')!;
    const paper = reader.querySelector<HTMLElement>('.reader__paper')!;
    const host = root.querySelector<HTMLElement>('[data-book-leaves]')!;
    const template = root.querySelector<HTMLTemplateElement>('[data-book-leaf-template]')!;
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    const flights: Flight[] = [];
    const prints = new Map<string, HTMLElement>();
    let adapter: BookAdapter;
    let page = 0, target = 0, columns = 1, frame = 0, width = 0, height = 0;
    let ready = false, committing = false;
    let drag: { id: number; x: number; y: number; scale: number; started: number; flight: Flight } | undefined;
    const clamp = (at: number) => Math.max(adapter.minimum, Math.min(adapter.maximum, at));
    const enabled = () => ready && reader.dataset.mode === 'paged' && reader.dataset.selecting !== 'true';
    const motion = () => reader.dataset.animation === 'on' || reader.dataset.animation === 'system' && !reduced.matches;
    const direction = () => reader.dataset.direction === 'rtl' ? -1 : 1;
    const number = (name: string, fallback: number) => Number(reader.dataset[name] ?? fallback);
    const dimensions = (at: number) => bookDimensions(number('bookPageCount', 0), number('paperMicrons', 100),
        number('bookPageBefore', 0) + adapter.number(at), number('paperBoard', 22));
    const profile = (at: number, side: string) => {
        const body = dimensions(at);
        return paperProfile(side === 'left' ? body.leftMm : body.rightMm, body.centreMm, number('paperCurve', 24), number('paperAngle', 14));
    };
    const state = () => {
        for (const [key, value] of Object.entries({ bookState: drag ? 'holding' : flights.length ? 'settling' : 'idle',
            bookPendingFrame: String(Boolean(frame)), bookActiveLeaves: String(flights.length), bookTarget: String(target),
            bookBundled: String(flights.some(flight => flight.bundled)) }))
            if (reader.dataset[key] !== value) reader.dataset[key] = value;
        if (adapter && ready) reader.querySelectorAll<HTMLButtonElement>('[data-turn]').forEach(button => {
            button.disabled = Number(button.dataset.turn) < 0 ? target <= adapter.minimum : target >= adapter.maximum;
        });
    };
    const cancel = () => {
        const id = drag?.id; drag = undefined;
        if (id !== undefined && root.hasPointerCapture(id)) root.releasePointerCapture(id);
        cancelAnimationFrame(frame); frame = 0;
        flights.splice(0).forEach(flight => flight.leaf.dispose()); target = page; state();
    };
    const fail = () => {
        cancel(); ready = false; root.hidden = true; reader.dataset.bookReady = 'false';
        reader.querySelector<HTMLElement>('[data-role=animation-error]')!.hidden = false;
    };
    const printing = (at: number, offset: number): HTMLElement | undefined => {
        const group = adapter.group(at);
        if (!group || group.page + offset >= group.count) return;
        const key = at + ':' + offset;
        let print = prints.get(key);
        if (!print) {
            const gap = parseFloat(getComputedStyle(group.source).columnGap) || 0;
            print = pagePrint(group.source, group.page + offset, width * columns, height, (width * columns - gap * (columns - 1)) / columns, gap, direction() < 0);
            const footer = document.createElement('small'); footer.className = 'reader-book__page-number';
            footer.dataset.bookNumber = ''; footer.textContent = String(number('bookPageBefore', 0) + adapter.number(at) + offset);
            if (reader.dataset.format !== 'comic') print.append(footer);
            prints.set(key, print);
            // Prints contain only visible runs. No chapter or unbounded jump queue is cached.
            if (prints.size > 12) prints.delete(prints.keys().next().value!);
        }
        return print.cloneNode(true) as HTMLElement;
    };
    const face = (at: number, side: string) => {
        const print = printing(at, columns === 1 || side === 'left' ? 0 : 1);
        if (!print) return;
        const place = printPlacement(profile(at, side), side, width / 148);
        const gap = columns === 2 && side === 'right' ? (parseFloat(getComputedStyle(adapter.group(at)!.source).columnGap) || 0) / 2 : 0;
        const printOffset = gap + place.shift;
        print.style.left = `${printOffset}px`;
        print.style.top = `${place.top}px`;
        // Ink follows the curved paper's print origin; the folio number must stay
        // centred on the visible sheet, not follow the displaced text origin.
        const number = print.querySelector<HTMLElement>('.reader-book__page-number');
        if (number) number.style.left = `${-printOffset}px`;
        return print;
    };
    const exposure = () => {
        const live = flights.filter(flight => !flight.finished || flight.intent === 'commit');
        const forward = live[0]?.delta;
        const exposed = forward === 1 ? Math.max(page, ...live.map(flight => flight.to))
            : forward === -1 ? Math.min(page, ...live.map(flight => flight.to)) : page;
        for (const side of ['left', 'right']) {
            const at = side === 'left' && forward === -1 || side === 'right' && forward === 1 ? exposed : page;
            const container = root.querySelector<HTMLElement>(`[data-book-page=${side}]`)!;
            const key = at + ':' + side;
            if (container.dataset.print !== key) {
                const print = columns === 1 && side === 'left' ? undefined : face(at, side);
                container.replaceChildren(...print ? [print] : []); container.dataset.print = key;
            }
        }
        flights.forEach((flight, index) => flight.leaf.stack(flights.length - index, index));
    };
    const paintBook = () => {
        const body = dimensions(page);
        root.style.setProperty('--paper', getComputedStyle(reader).getPropertyValue('--reader-paper'));
        root.style.setProperty('--paper-ink', getComputedStyle(reader).getPropertyValue('--reader-ink'));
        root.style.setProperty('--texture', reader.dataset.surface === 'clean' ? '0' : String(number('paperTexture', 30) / 100));
        root.style.setProperty('--book-scale', String(210 / (210 + body.totalMm * .45)));
        root.dataset.bookColumns = String(columns); root.dataset.bookDirection = reader.dataset.direction;
        root.dataset.bookBinding = reader.dataset.paperBinding ?? 'hard';
        root.dataset.paperGrain = reader.dataset.paperGrain ?? 'fine'; root.dataset.paperEdge = reader.dataset.paperEdge ?? 'normal';
        root.style.setProperty('--cover', reader.dataset.paperCover ?? '#84b6cb');
        root.style.setProperty('--lining', reader.dataset.paperLining ?? reader.dataset.paperCover ?? '#84b6cb');
        root.style.setProperty('--edge-colour', reader.dataset.paperEdgeColour ?? '#9b8054');
        root.style.setProperty('--edge-strength', root.dataset.paperEdge === 'normal' ? '0' : String(number('paperEdgeStrength', 45) / 100));
        paintBookSolid(root, body, number('paperCurve', 24), number('paperAngle', 14), number('paperLight', 75) / 100, width / 148);
        if (reader.dataset.bookPageCount) reader.dataset.bookThickness = body.totalMm.toFixed(2);
        else delete reader.dataset.bookThickness;
        exposure(); state();
    };
    const landing = (to: number, forward: boolean) => {
        const side = forward ? 'left' : 'right', section = profile(to, side);
        return { outline: paperOutline(section), height: width * (section.height + section.lift) / 148,
            lift: section.lift / section.height * 100, footerRise: section.footerLift * width / 148,
            lighting: paperLighting(section, side, number('paperLight', 75) / 100, number('paperAngle', 14), true) };
    };
    const create = (from: number, to: number, grip: number, middle: boolean, automatic: boolean): Flight => {
        const delta = Math.sign(to - from), side = delta > 0 || columns === 1 ? 'right' : 'left';
        const section = profile(from, side), leafHeight = width * (section.height + section.lift) / 148;
        const leaf = new BookLeaf(host, template, { width, height: leafHeight, gripY: grip * leafHeight, middle, automatic,
            outline: paperOutline(section).map(point => ({ x: point.x * width, y: point.y * leafHeight })),
            angle: number('paperAngle', 14), illumination: number('paperLight', 75) / 100, forward: delta > 0,
            lift: section.lift / section.height * 100, footerRise: section.footerLift * width / 148,
            paperEdges: root.dataset.paperEdge !== 'normal', showThrough: false,
            lighting: root.querySelector<HTMLElement>(`[data-book-sheet=${side}]`)!.style.getPropertyValue('--paper-cross-light'),
            landing: landing(to, delta > 0), front: face(from, side), back: face(to, delta > 0 ? 'left' : 'right') });
        const flight: Flight = { leaf, from, to, delta, intent: 'holding', finished: false };
        leaf.view.dataset.leafFrom = String(from); leaf.view.dataset.leafTo = String(to); leaf.view.dataset.leafIntent = 'holding';
        flights.push(flight); leaf.stack(flights.length, flights.length - 1); return flight;
    };
    const animate = (flight: Flight, commit: boolean, now: number, duration: number, start = now) => {
        const leaf = flight.leaf, released = flight.intent === 'holding';
        if (leaf.dirty) leaf.draw();
        flight.intent = commit ? 'commit' : 'return'; flight.finished = false;
        leaf.view.dataset.leafIntent = flight.intent; leaf.view.dataset.leafTo = String(flight.to);
        const to = { x: commit ? -leaf.width : leaf.width, y: leaf.gripY };
        flight.animation = { start, from: { ...leaf.renderedPoint }, to, duration, released };
    };
    const pump = (now: number) => {
        if (drag) return;
        let last = flights.filter(flight => flight.intent === 'commit').at(-1);
        if (last && (target - last.from) * last.delta > 0 && (last.bundled || Math.abs(target - last.to) > 8)) {
            if (last.to !== target || !last.bundled) { last.to = target; last.bundled = last.printingDirty = true; }
        }
        while (last && (target - last.to) * last.delta < 0) {
            animate(last, false, now, 200); last = flights.filter(flight => flight.intent === 'commit').at(-1);
        }
        let from = flights.filter(flight => flight.intent === 'commit').at(-1)?.to ?? page;
        while (from !== target) {
            const delta = Math.sign(target - from);
            const returning = flights.find(flight => flight.intent === 'return' && flight.from === from && flight.delta === delta);
            if (returning) { animate(returning, true, now, 220); from = returning.to; continue; }
            if (flights.some(flight => flight.intent === 'return') || flights.length >= 3) break;
            const to = delta > 0 ? Math.min(target, adapter.next(from, 1)) : Math.max(target, adapter.next(from, -1));
            if (to === from) break;
            const previous = flights.at(-1)?.animation;
            const start = Math.max(now, previous ? previous.start + 60 : now);
            const duration = Math.max(220, previous ? previous.start + previous.duration + 35 - start : 220);
            animate(create(from, to, 1, false, true), true, now, duration, start); from = to;
        }
        state();
    };
    const retire = () => {
        let changed = false;
        while (flights[0]?.finished) {
            const first = flights[0];
            if (first.intent === 'return') {
                if (!flights.every(flight => flight.finished && flight.intent === 'return')) break;
                flights.splice(0).forEach(flight => flight.leaf.dispose()); changed = true; break;
            }
            page = first.to; first.leaf.dispose(); flights.shift(); changed = true;
            committing = true;
            try { adapter.commit(page); } finally { committing = false; }
        }
        if (changed) paintBook();
    };
    const schedule = () => { if (!frame && !document.hidden) { frame = requestAnimationFrame(tick); state(); } };
    const tick = (time: number) => {
        frame = 0;
        try {
            for (const flight of flights) {
                if (flight.printingDirty) {
                    flight.leaf.setLanding(landing(flight.to, flight.delta > 0));
                    flight.leaf.bundle(face(flight.to, flight.delta > 0 ? 'left' : 'right'), Math.max(0, Math.abs(flight.to - flight.from) - 1));
                    flight.printingDirty = false;
                }
                const animation = flight.animation;
                if (animation && time >= animation.start && !flight.finished) {
                    const progress = Math.min(1, (time - animation.start) / animation.duration);
                    const eased = animation.released ? progress * progress * (3 - 2 * progress) : 1 - (1 - progress) ** 3;
                    flight.leaf.point = { x: animation.from.x + (animation.to.x - animation.from.x) * eased,
                        y: animation.from.y + (animation.to.y - animation.from.y) * eased };
                    flight.leaf.draw();
                    if (progress === 1) { flight.finished = true; flight.animation = undefined; }
                } else if (flight.leaf.dirty) flight.leaf.draw();
            }
            retire(); pump(time); exposure();
            if (flights.some(flight => !flight.finished && flight.animation)) schedule(); state();
        } catch { fail(); }
    };
    const turn = (delta: number): boolean => {
        if (!enabled() || !motion()) return false;
        try {
            if (drag) { cancel(); paintBook(); }
            target = clamp(adapter.next(target, delta)); pump(performance.now());
            if (flights.length) schedule(); return true;
        } catch { fail(); return false; }
    };
    const update = (next: BookAdapter, active: boolean): boolean => {
        adapter = next;
        if (committing && ready && active && columns === next.columns) return true;
        cancel(); prints.clear();
        root.querySelectorAll<HTMLElement>('[data-book-page]').forEach(element => delete element.dataset.print);
        page = target = next.current; columns = next.columns;
        const bounds = paper.getBoundingClientRect(); width = bounds.width / columns; height = bounds.height;
        ready = active; root.hidden = !active; reader.dataset.bookReady = String(active);
        if (active) { try { paintBook(); } catch { fail(); return false; } }
        return true;
    };
    root.addEventListener('pointerdown', event => {
        if (!enabled() || event.button !== 0 || flights.length || !(event.target as Element).closest('[data-book-sheet]')) return;
        const right = root.querySelector<HTMLElement>('[data-book-sheet=right]')!.getBoundingClientRect();
        const x = direction() > 0 ? (event.clientX - right.left) / right.width : (right.right - event.clientX) / right.width;
        const delta = columns === 1 ? x >= .5 ? 1 : -1 : x >= 0 ? 1 : -1;
        const to = clamp(adapter.next(page, delta)); if (to === page) return;
        const source = root.querySelector<HTMLElement>(`[data-book-sheet=${delta > 0 || columns === 1 ? 'right' : 'left'}]`)!.getBoundingClientRect();
        const y = Math.max(0, Math.min(1, (event.clientY - source.top) / source.height)), middle = y > .2 && y < .8;
        try {
            const flight = create(page, to, middle ? y : y < .5 ? 0 : 1, middle, false);
            exposure(); if (!motion()) flight.leaf.view.hidden = true;
            event.preventDefault();
            drag = { id: event.pointerId, x: event.clientX, y: event.clientY, scale: source.width / width, started: performance.now(), flight };
            root.setPointerCapture(event.pointerId); state();
        } catch { fail(); }
    });
    root.addEventListener('pointermove', event => {
        if (drag?.id !== event.pointerId) return;
        const leaf = drag.flight.leaf;
        leaf.point = { x: leaf.width - Math.max(0, (event.clientX - drag.x) * -drag.flight.delta * direction() / drag.scale),
            y: leaf.gripY + (event.clientY - drag.y) / drag.scale };
        leaf.dirty = true; if (motion()) schedule();
    });
    root.addEventListener('pointerup', event => {
        if (drag?.id !== event.pointerId) return;
        const flight = drag.flight, distance = (event.clientX - drag.x) * -flight.delta * direction() / drag.scale;
        const commit = distance > flight.leaf.width * .42 || distance > 45 && performance.now() - drag.started < 260;
        drag = undefined; root.releasePointerCapture(event.pointerId); target = commit ? flight.to : page;
        if (motion()) { animate(flight, commit, performance.now(), 310); schedule(); }
        else { if (commit) adapter.commit(target); cancel(); paintBook(); }
        state();
    });
    const restore = () => { cancel(); if (ready) { try { paintBook(); } catch { fail(); } } };
    root.addEventListener('pointercancel', restore);
    root.addEventListener('lostpointercapture', () => { if (drag) restore(); });
    reader.addEventListener('keydown', event => { if (event.key === 'Escape') restore(); });
    document.addEventListener('visibilitychange', () => { if (document.hidden) restore(); });
    window.addEventListener('pagehide', cancel);
    new MutationObserver(() => {
        if (!ready || flights.length) return;
        prints.clear(); root.querySelectorAll<HTMLElement>('[data-book-page]').forEach(element => delete element.dataset.print);
        try { paintBook(); } catch { fail(); }
    }).observe(reader, { attributes: true, attributeFilter: ['data-book-page-count', 'data-book-page-before'] });
    return { update, turn, enabled };
}
