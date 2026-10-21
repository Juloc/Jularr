// The arithmetic of the original pages: how big a page shows, how zoom and turns move, where a position is kept. No document, no pdf.js: everything here is a function of numbers.
export type Fit = 'page' | 'width';
export interface Size { width: number; height: number }

export const MinimumZoom = 1;
export const MaximumZoom = 5;
const ZoomStep = 1.25;
const Swipe = { distance: 50, slope: 1.4 };

export const clampZoom = (zoom: number): number => Number.isFinite(zoom) ? Math.min(MaximumZoom, Math.max(MinimumZoom, zoom)) : MinimumZoom;

// A button press zooms a step in or out.
export const stepZoom = (zoom: number, direction: 1 | -1): number => clampZoom(direction > 0 ? zoom * ZoomStep : zoom / ZoomStep);

// The wheel zooms a little per notch; a trackpad pinch arrives as a wheel with the control key and zooms faster.
export const wheelZoom = (zoom: number, deltaY: number, pinch: boolean): number => clampZoom(zoom * Math.exp(-deltaY * (pinch ? 0.01 : 0.0018)));

// Two fingers zoom by how far their distance changed since the pinch began.
export const pinchZoom = (zoom: number, start: number, now: number): number => start > 0 && now > 0 ? clampZoom(zoom * now / start) : zoom;

// What a page takes on screen, in CSS pixels: the whole page in the viewport, or its width across the viewport, times the zoom.
export function pageSize(viewport: Size, page: Size, fit: Fit, zoom: number): Size {
    if (viewport.width <= 0 || viewport.height <= 0 || page.width <= 0 || page.height <= 0) return { width: 0, height: 0 };
    const ratio = fit === 'width' ? viewport.width / page.width : Math.min(viewport.width / page.width, viewport.height / page.height);
    const scale = ratio * clampZoom(zoom);
    return { width: page.width * scale, height: page.height * scale };
}

// The scale pdf.js draws a page at so it is sharp on the device without making an unbounded bitmap.
export function renderScale(cssWidth: number, page: Size, pixelRatio: number, maximumPixels: number): number {
    if (cssWidth <= 0 || page.width <= 0 || page.height <= 0) return 1;
    const wanted = cssWidth * Math.max(1, Math.min(3, pixelRatio)) / page.width;
    const allowed = Math.sqrt(maximumPixels / (page.width * page.height));
    return Math.max(0.1, Math.min(wanted, allowed));
}

export const clampPage = (page: number, count: number): number => Math.min(Math.max(1, count), Math.max(1, Math.trunc(Number.isFinite(page) ? page : 1)));

// A turn by one page or more, stopping at the first and last page.
export const turnTarget = (page: number, count: number, delta: number): number => clampPage(page + delta, count);

// A horizontal drag is a swipe: the way to turn (1 forward, -1 back), 0 for a tap, a scroll or a short drag. Right to left books turn the other way.
export function swipeDelta(dx: number, dy: number, rtl: boolean): number {
    if (Math.abs(dx) < Swipe.distance || Math.abs(dx) < Math.abs(dy) * Swipe.slope) return 0;
    return (dx < 0 ? 1 : -1) * (rtl ? -1 : 1);
}

// Where the position of a PDF is kept: p12 is page 12. Anything else is another format's position.
export function locatorPage(locator: string | null | undefined): number | null {
    const match = /^p(\d{1,6})$/.exec(locator ?? '');
    return match && Number(match[1]) >= 1 ? Number(match[1]) : null;
}

export const pageLocator = (page: number): string => `p${page}`;

// How far through the book the page is; the last page completes it.
export const progression = (page: number, count: number): number => count <= 0 ? 0 : Number(Math.min(1, page / count).toFixed(4));

// The first words of a page, to find a note's page again by what it says.
export function pageAnchor(items: ReadonlyArray<string>, maximum = 120): string {
    const text = items.join(' ').replace(/\s+/g, ' ').trim();
    return text.length <= maximum ? text : text.slice(0, maximum).replace(/\s\S*$/, '').trim();
}
