import { PageFlip } from 'page-flip';

export interface CurlGroup { source: HTMLElement; page: number; count: number }

// Decorative, bounded page faces; canonical text and position remain in PaperPages.
export function initializePaperCurl(reader: HTMLElement, moved: (delta: number) => void) {
    const canvas = reader.querySelector<HTMLElement>('.reader__canvas')!;
    const paper = reader.querySelector<HTMLElement>('.reader__paper')!;
    const host = document.createElement('div');
    host.className = 'reader__curl';
    host.setAttribute('aria-hidden', 'true');
    canvas.append(host);
    let book: PageFlip | undefined;
    let current = 0, columns = 1, pending = 0;
    let updating = false;
    let revision = 0;
    let hasPrevious = false, hasNext = false;
    let drag: { id: number; x: number; y: number; started: number; moved: boolean } | undefined;
    const enabled = (): boolean => reader.dataset.curl === 'true' && reader.dataset.selecting !== 'true'
        && reader.dataset.mode === 'paged' && reader.dataset.animation !== 'off';
    const point = (x: number, y: number) => {
        const box = host.getBoundingClientRect();
        return { x: reader.dataset.direction === 'rtl' ? box.right - x : x - box.left, y: y - box.top };
    };
    const cancel = (): void => {
        revision++;
        drag = undefined;
        if (!book) return;
        updating = true;
        book.userStop({ x: 0, y: 0 }, true);
        book.getFlipController().stopMove();
        book.getRender().finishAnimation();
        book.turnToPage(current);
        pending = 0;
        updating = false;
        reader.dataset.curlState = 'read';
    };
    const update = (groups: (CurlGroup | undefined)[], count: number, active: boolean): void => {
        cancel();
        reader.dataset.curl = String(active);
        host.hidden = !active;
        if (!active) return;
        columns = count;
        hasPrevious = Boolean(groups[0]); hasNext = Boolean(groups[2]);
        current = hasPrevious ? columns : 0;
        const width = paper.clientWidth, height = paper.clientHeight;
        const gap = parseFloat(getComputedStyle(groups[1]!.source).columnGap);
        const columnWidth = (width - gap * (columns - 1)) / columns;
        const leafWidth = width / columns;
        const faces: HTMLElement[] = [];
        for (const group of groups) {
            if (!group) continue;
            for (let offset = 0; offset < columns; offset++) {
                const face = document.createElement('div');
                face.className = 'reader__leaf';
                const viewport = document.createElement('div');
                viewport.className = 'reader__leaf-viewport';
                viewport.style.width = `${columnWidth}px`;
                viewport.style.left = `${columns === 2 && offset === 1 ? gap / 2 : 0}px`;
                if (group.page + offset < group.count) {
                    const snapshot = group.source.cloneNode(true) as HTMLElement;
                    snapshot.hidden = false;
                    snapshot.inert = true;
                    snapshot.removeAttribute('data-role');
                    snapshot.removeAttribute('data-pad');
                    snapshot.querySelectorAll('[id]').forEach(element => element.removeAttribute('id'));
                    snapshot.style.width = `${width}px`;
                    snapshot.style.height = `${height}px`;
                    snapshot.style.position = 'absolute';
                    const shift = (group.page + offset) * (columnWidth + gap);
                    snapshot.style.left = `${reader.dataset.direction === 'rtl' ? columnWidth - width + shift : -shift}px`;
                    snapshot.style.direction = reader.dataset.direction === 'rtl' ? 'rtl' : 'ltr';
                    viewport.append(snapshot);
                }
                face.append(viewport);
                faces.push(face);
            }
        }
        updating = true;
        if (!book) {
            book = new PageFlip(host, {
                width: leafWidth, height, size: 'stretch', minWidth: leafWidth, maxWidth: leafWidth,
                minHeight: height, maxHeight: height, usePortrait: columns === 1, autoSize: false,
                useMouseEvents: false, showPageCorners: false, drawShadow: true,
                maxShadowOpacity: .28, flippingTime: 520, startPage: current
            });
            book.on('flip', event => { if (!updating) pending = (Number(event.data) - current) / columns; });
            book.on('changeState', event => {
                reader.dataset.curlState = String(event.data);
                if (event.data === 'read' && pending && !updating) {
                    const delta = pending;
                    pending = 0;
                    const committed = revision;
                    requestAnimationFrame(() => { if (revision === committed && enabled()) moved(delta); });
                }
            });
            book.loadFromHTML(faces);
        } else {
            Object.assign(book.getSettings(), { width: leafWidth, height, minWidth: leafWidth, maxWidth: leafWidth,
                minHeight: height, maxHeight: height, usePortrait: columns === 1 });
            book.turnToPage(0);
            book.updateFromHtml(faces);
        }
        host.style.minWidth = '0';
        host.style.minHeight = '0';
        book.update();
        book.turnToPage(current);
        updating = false;
    };
    const turn = (delta: number): boolean => {
        if (!enabled() || !book) return false;
        if (book.getState() !== 'read') return true;
        if (delta < 0 ? hasPrevious : hasNext) delta > 0 ? book.flipNext() : book.flipPrev();
        return true;
    };

    host.addEventListener('pointerdown', event => {
        if (!enabled() || event.button !== 0 || !book || book.getState() !== 'read') return;
        event.preventDefault();
        drag = { id: event.pointerId, x: event.clientX, y: event.clientY, started: performance.now(), moved: false };
        host.setPointerCapture(event.pointerId);
        book.startUserTouch(point(event.clientX, event.clientY));
    });
    host.addEventListener('pointermove', event => {
        if (drag?.id === event.pointerId) {
            if (Math.hypot(event.clientX - drag.x, event.clientY - drag.y) > 6) drag.moved = true;
            book!.userMove(point(event.clientX, event.clientY), event.pointerType === 'touch');
        }
    });
    host.addEventListener('pointerup', event => {
        if (drag?.id !== event.pointerId) return;
        const start = drag;
        drag = undefined;
        const dx = event.clientX - start.x, dy = event.clientY - start.y;
        const fast = performance.now() - start.started < 250 && Math.abs(dx) > 40 && Math.abs(dx) > Math.abs(dy) * 1.4;
        const stationary = !start.moved;
        const release = point(event.clientX, event.clientY);
        // A single-page viewport has no off-screen spine the finger can cross.
        const forward = dx * (reader.dataset.direction === 'rtl' ? -1 : 1) < -host.clientWidth * .5;
        if (columns === 1 && !fast && forward && hasNext) book!.userMove({ x: -1, y: release.y }, false);
        book!.userStop(release, fast || stationary);
        if (fast) {
            const delta = (dx < 0 ? 1 : -1) * (reader.dataset.direction === 'rtl' ? -1 : 1);
            if (delta > 0 ? hasNext : hasPrevious) delta > 0 ? book!.flipNext() : book!.flipPrev();
            else cancel();
        }
        host.releasePointerCapture(event.pointerId);
    });
    host.addEventListener('pointercancel', cancel);
    host.addEventListener('lostpointercapture', () => { if (drag) cancel(); });
    reader.addEventListener('keydown', event => { if (event.key === 'Escape') cancel(); });
    document.addEventListener('visibilitychange', () => { if (document.hidden) cancel(); });
    return { update, turn, enabled };
}
