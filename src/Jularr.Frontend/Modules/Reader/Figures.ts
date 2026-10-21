// Figures open in a full-screen view: a tap or click on the picture, Enter or Space on it. The wheel, a pinch, a double tap and the buttons zoom; dragging moves a zoomed picture.
// A page only the original PDF shows stands in the text as a figure without a picture; it names the page and links to it.
const MaximumScale = 8;
const TapDistance = 8;
const TapDuration = 600;

export function initializeFigures(reader: HTMLElement, content: HTMLElement): void {
    const dialog = reader.querySelector<HTMLDialogElement>('dialog.reader-figure');
    if (!dialog) return;
    const stage = dialog.querySelector<HTMLElement>('.reader-figure__stage')!;
    const image = dialog.querySelector<HTMLImageElement>('.reader-figure__image')!;
    describeStandIns(reader, content);
    describePictures(reader, content);

    let scale = 1, x = 0, y = 0;
    const pointers = new Map<number, { x: number; y: number }>();
    let pinch = 0;
    let moved = false;

    // How far the zoomed picture may move: its overhang beyond the stage on each side.
    const limit = (axis: 'width' | 'height') => Math.max(0, ((axis === 'width' ? image.clientWidth : image.clientHeight) * scale - (axis === 'width' ? stage.clientWidth : stage.clientHeight)) / 2);
    const paint = (): void => {
        x = Math.max(-limit('width'), Math.min(limit('width'), x));
        y = Math.max(-limit('height'), Math.min(limit('height'), y));
        stage.style.setProperty('--figure-scale', String(scale));
        stage.style.setProperty('--figure-x', `${x}px`);
        stage.style.setProperty('--figure-y', `${y}px`);
        stage.dataset.zoomed = String(scale > 1);
    };
    const zoom = (next: number, centreX = 0, centreY = 0): void => {
        const target = Math.max(1, Math.min(MaximumScale, next));
        const ratio = target / scale;
        // Keep the point under the pointer where it is, measured from the stage's centre.
        x = centreX - (centreX - x) * ratio;
        y = centreY - (centreY - y) * ratio;
        scale = target;
        if (scale === 1) { x = 0; y = 0; }
        paint();
    };
    const fromCentre = (event: { clientX: number; clientY: number }) => {
        const box = stage.getBoundingClientRect();
        return { x: event.clientX - box.left - box.width / 2, y: event.clientY - box.top - box.height / 2 };
    };
    const open = (source: HTMLImageElement): void => {
        image.src = source.currentSrc || source.src;
        image.alt = source.alt || content.querySelector<HTMLImageElement>(`img[src="${CSS.escape(source.getAttribute('src') ?? '')}"]`)?.alt || '';
        scale = 1; x = 0; y = 0;
        paint();
        if (!dialog.open) dialog.showModal();
    };

    // Taps and clicks on pictures, in the text and on the paper of the book view.
    let down: { image: HTMLImageElement; x: number; y: number; at: number } | undefined;
    reader.addEventListener('pointerdown', event => {
        const picture = zoomable(event.target) ?? printedPicture(reader, event);
        down = picture && event.button === 0 && reader.dataset.selecting !== 'true' ? { image: picture, x: event.clientX, y: event.clientY, at: performance.now() } : undefined;
    }, true);
    reader.addEventListener('pointerup', event => {
        const start = down; down = undefined;
        if (!start || Math.hypot(event.clientX - start.x, event.clientY - start.y) > TapDistance || performance.now() - start.at > TapDuration || getSelection()?.toString()) return;
        open(start.image);
    }, true);
    reader.addEventListener('pointercancel', () => { down = undefined; }, true);

    // The printed pages of the book view take no pointer events; a figure on them shows its cursor by position.
    reader.addEventListener('pointermove', event => {
        const over = event.pointerType === 'mouse' && printedPicture(reader, event) !== null;
        if ((reader.dataset.figureHover === 'true') !== over) reader.dataset.figureHover = String(over);
    });
    content.addEventListener('keydown', event => {
        const picture = zoomable(event.target);
        if (picture && (event.key === 'Enter' || event.key === ' ')) {
            event.preventDefault();
            open(picture);
        }
    });

    dialog.addEventListener('click', event => { if (event.target === dialog) dialog.close(); });
    dialog.querySelector('[data-figure-close]')!.addEventListener('click', () => dialog.close());
    dialog.querySelectorAll<HTMLElement>('[data-figure-zoom]').forEach(button => button.addEventListener('click', () => {
        const mode = button.dataset.figureZoom;
        if (mode === 'fit') zoom(1);
        else zoom(scale * (mode === 'in' ? 1.5 : 1 / 1.5));
    }));
    dialog.addEventListener('close', () => { image.removeAttribute('src'); pointers.clear(); });
    dialog.addEventListener('keydown', event => {
        if (event.key === '+' || event.key === '=') zoom(scale * 1.5);
        else if (event.key === '-') zoom(scale / 1.5);
        else if (event.key === '0') zoom(1);
        else if (event.key.startsWith('Arrow') && scale > 1) {
            const step = 60;
            x += event.key === 'ArrowLeft' ? step : event.key === 'ArrowRight' ? -step : 0;
            y += event.key === 'ArrowUp' ? step : event.key === 'ArrowDown' ? -step : 0;
            paint();
        } else return;
        event.preventDefault();
    });

    // Wheel and trackpad pinch zoom around the pointer; two fingers pinch; one finger or the mouse drags a zoomed picture.
    stage.addEventListener('wheel', event => {
        event.preventDefault();
        const centre = fromCentre(event);
        zoom(scale * Math.exp(-event.deltaY * (event.ctrlKey ? 0.01 : 0.0018)), centre.x, centre.y);
    }, { passive: false });
    stage.addEventListener('dblclick', event => {
        const centre = fromCentre(event);
        if (scale > 1) zoom(1); else zoom(2.5, centre.x, centre.y);
    });
    stage.addEventListener('pointerdown', event => {
        stage.setPointerCapture(event.pointerId);
        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        moved = false;
        pinch = pointers.size === 2 ? distance() : 0;
    });
    stage.addEventListener('pointermove', event => {
        const previous = pointers.get(event.pointerId);
        if (!previous) return;
        const next = { x: event.clientX, y: event.clientY };
        pointers.set(event.pointerId, next);
        if (pointers.size === 2 && pinch > 0) {
            const now = distance();
            const [a, b] = [...pointers.values()];
            const centre = fromCentre({ clientX: (a.x + b.x) / 2, clientY: (a.y + b.y) / 2 });
            zoom(scale * now / pinch, centre.x, centre.y);
            pinch = now;
            moved = true;
        } else if (pointers.size === 1 && scale > 1) {
            x += next.x - previous.x;
            y += next.y - previous.y;
            moved = true;
            paint();
        }
    });
    const release = (event: PointerEvent) => {
        pointers.delete(event.pointerId);
        pinch = pointers.size === 2 ? distance() : 0;
        // A tap on the empty stage outside the picture closes the view.
        const box = image.getBoundingClientRect();
        const inside = event.clientX >= box.left && event.clientX <= box.right && event.clientY >= box.top && event.clientY <= box.bottom;
        if (pointers.size === 0 && !moved && !inside) dialog.close();
    };
    stage.addEventListener('pointerup', release);
    stage.addEventListener('pointercancel', release);
    const distance = () => { const [a, b] = [...pointers.values()]; return a && b ? Math.hypot(a.x - b.x, a.y - b.y) : 0; };
}

// The figure under the pointer on the printed pages of the book view, found by position.
function printedPicture(reader: HTMLElement, event: { clientX: number; clientY: number }): HTMLImageElement | null {
    for (const picture of Array.from(reader.querySelectorAll<HTMLImageElement>('.reader__page-print img[data-figure]'))) {
        const box = picture.getBoundingClientRect();
        if (event.clientX >= box.left && event.clientX <= box.right && event.clientY >= box.top && event.clientY <= box.bottom) return picture;
    }
    return null;
}

// The picture under a tap or key press, when it is a figure or a large picture of the text.
function zoomable(target: EventTarget | null): HTMLImageElement | null {
    const picture = target instanceof HTMLImageElement ? target : null;
    if (!picture || picture.closest('a[href], button, dialog') || !(picture.closest('[data-figure]') || picture.dataset.figure)) return null;
    return picture;
}

// Pictures that open the view can be reached and used with the keyboard.
function describePictures(reader: HTMLElement, content: HTMLElement): void {
    const label = reader.dataset.figureLabel ?? '';
    content.querySelectorAll<HTMLImageElement>('figure[data-figure] img, img[data-figure]').forEach(picture => {
        picture.tabIndex = 0;
        picture.setAttribute('role', 'button');
        if (!picture.alt) picture.setAttribute('aria-label', label);
    });
}

// The stand-in for a page that only the original shows: a note with the page and a link to it in the original pages, until the page itself is drawn in its place.
function describeStandIns(reader: HTMLElement, content: HTMLElement): void {
    const url = reader.dataset.originalUrl;
    const template = reader.dataset.originalLabel;
    if (!url || !template) return;
    const figures = Array.from(content.querySelectorAll<HTMLElement>('figure[data-figure=original]')).filter(figure => {
        const page = Number(figure.dataset.page);
        return Number.isInteger(page) && page >= 1 && !figure.firstChild;
    });
    for (const figure of figures) {
        const page = Number(figure.dataset.page);
        const text = document.createElement('p');
        text.textContent = template.replace('{0}', String(page)) + ' ';
        const link = document.createElement('a');
        link.href = `${url}&pg=${page}`;
        link.textContent = reader.dataset.originalLink ?? '';
        text.append(link);
        figure.append(text);
    }

    if (figures.length > 0 && reader.dataset.fileUrl) void drawStandIns(reader, content, figures);
}

// A page that only the original shows is drawn in the text by the browser: the whole page as a picture that opens full screen like any figure. Pages are drawn as they come near, a bounded number per section.
const MaximumDrawn = 40;
const DrawnWidth = 1200;
async function drawStandIns(reader: HTMLElement, content: HTMLElement, figures: HTMLElement[]): Promise<void> {
    let engine: typeof import('./PdfEngine');
    try { engine = await import('./PdfEngine'); } catch { return; }
    const alt = reader.dataset.originalAlt ?? '';
    const state = new WeakSet<HTMLElement>();
    let drawn = 0;
    let chain = Promise.resolve();
    const draw = (figure: HTMLElement): void => {
        if (state.has(figure) || drawn >= MaximumDrawn) return;
        state.add(figure);
        drawn++;
        chain = chain.then(async () => {
            const page = Number(figure.dataset.page);
            try {
                const document = await engine.openPdf(reader.dataset.fileUrl!);
                const proxy = await document.getPage(page);
                const natural = proxy.getViewport({ scale: 1 });
                const canvas = window.document.createElement('canvas');
                await engine.drawPage(proxy, canvas, DrawnWidth / natural.width).promise;
                const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.86));
                if (!blob) return;
                const picture = new Image();
                picture.src = URL.createObjectURL(blob);
                picture.alt = alt.replace('{0}', String(page));
                picture.dataset.figure = 'page';
                picture.tabIndex = 0;
                picture.setAttribute('role', 'button');
                const link = figure.querySelector('a');
                const caption = window.document.createElement('figcaption');
                if (link) caption.append(link);
                figure.replaceChildren(picture, caption);
                figure.dataset.state = 'ready';
            } catch { /* The note and its link stay: the original pages show the page. */ }
        });
    };
    if (!('IntersectionObserver' in window)) { figures.forEach(draw); return; }
    const observer = new IntersectionObserver(entries => entries.forEach(entry => { if (entry.isIntersecting) { draw(entry.target as HTMLElement); observer.unobserve(entry.target); } }),
        { root: content, rootMargin: '600px 2000px' });
    figures.forEach(figure => observer.observe(figure));
}
