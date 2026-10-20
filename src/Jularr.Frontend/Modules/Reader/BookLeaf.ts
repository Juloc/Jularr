import { curlPaper, foldPaper, paperSegments, paperShadow, shadowPenumbra, simplifyOutline, type Point } from './BookGeometry';
import { paintPaperRim } from './BookSolid';

export interface LandingPaper {
    outline: Point[]; height: number; lift: number; footerRise: number; lighting: string;
}

export interface LeafOptions {
    width: number; height: number; gripY: number; middle: boolean; automatic: boolean;
    outline: Point[]; angle: number; illumination: number; forward: boolean;
    lift: number; footerRise: number; lighting: string; showThrough: boolean; paperEdges: boolean; front?: Element; back?: Element;
    landing: LandingPaper;
}

const polygon = (points: Point[]) => points.length < 3 ? 'polygon(0px 0px,0px 0px,0px 0px)'
    : 'polygon(' + points.map(p => `${p.x.toFixed(3)}px ${p.y.toFixed(3)}px`).join(',') + ')';
const matrix = (values: number[]) => `matrix(${values.map(value => value.toFixed(6)).join(',')})`;
let shadowIdentity = 0;

// One decorative leaf, shared by direct grips and concurrent button flights.
// It owns no paging, timers, document data, preferences or application writes.
export class BookLeaf {
    readonly view: HTMLElement;
    readonly width: number;
    readonly height: number;
    readonly gripY: number;
    readonly middle: boolean;
    point: Point;
    renderedPoint: Point;
    progress = 0;
    dirty = true;
    private readonly front: HTMLElement;
    private readonly back: HTMLElement;
    private readonly backPrint: HTMLElement;
    private backNumber: HTMLElement | null;
    private readonly sourceLight: HTMLElement;
    private readonly landingLight: HTMLElement;
    private readonly landingGutter: HTMLElement;
    private landing!: LandingPaper;
    private outline: Point[] = [];
    private landingOutline: Point[] = [];
    private readonly shadow: HTMLElement;
    private readonly contact: HTMLElement;
    private readonly bands: HTMLElement[];
    private readonly bandInk: { face: boolean; front?: Element; back?: Element }[] = [];
    private reverseInk?: Element;
    private readonly shadowOutline: SVGPathElement;
    private readonly shadowLayers: SVGUseElement[];
    private readonly shadowFade: SVGStopElement[];
    private readonly shadowGradient: SVGLinearGradientElement;
    private readonly bendSubstrate: SVGSVGElement;
    private readonly bendOutline: SVGPathElement;
    private bundleView?: HTMLElement;
    private bundlePaths: SVGPathElement[] = [];
    private bundleDepth = 0;
    private readonly shades: string[] = [];
    private sourceRank = 0;
    private landingRank = 0;

    constructor(host: HTMLElement, template: HTMLTemplateElement, private readonly options: LeafOptions) {
        this.width = options.width; this.height = options.height;
        this.gripY = options.gripY; this.middle = options.middle;
        this.point = { x: this.width, y: this.gripY };
        this.renderedPoint = { ...this.point };
        this.view = template.content.firstElementChild!.cloneNode(true) as HTMLElement;
        this.view.hidden = false;
        this.view.classList.toggle('reader-book__turn--backward', !options.forward);
        this.view.style.top = `${-options.lift}%`;
        // The substrate, printed faces and clip geometry need the identical box.
        // A percentage-height box can cut off fractional pixels of a diagonal
        // reflection even though its clip polygon extends beyond that box.
        this.view.style.width = `${this.width}px`; this.view.style.height = `${this.height}px`;
        this.view.style.bottom = 'auto';
        this.view.style.setProperty('--page-number-rise', `${options.footerRise}px`);
        this.view.style.setProperty('--paper-cross-light', options.lighting);
        this.front = this.view.querySelector('[data-book-front]')!;
        this.back = this.view.querySelector('[data-book-back]')!;
        this.backPrint = this.view.querySelector('[data-book-back-print]')!;
        this.imprint(this.front, options.front, options.back);
        this.imprint(this.backPrint, options.back, options.front);
        for (const ink of Array.from(this.front.children) as HTMLElement[]) ink.style.height = `${this.height}px`;
        this.backNumber = this.backPrint.querySelector('[data-book-number]');
        const light = (className: string) => {
            const layer = document.createElement('div'); layer.className = className; return layer;
        };
        this.sourceLight = light('reader-book__back-light');
        this.landingLight = light('reader-book__back-light reader-book__back-light--landing');
        this.landingGutter = light('reader-book__landing-gutter');
        this.back.prepend(this.sourceLight, this.landingLight, this.landingGutter);
        this.reverseInk = options.back;
        const contour = options.outline.map(point => ({ x: options.forward ? point.x / this.width : 1 - point.x / this.width, y: point.y / this.height }));
        paintPaperRim(this.front, contour, options.paperEdges); paintPaperRim(this.back, contour, options.paperEdges);
        this.shadow = this.view.querySelector('[data-book-fold-shadow]')!;
        this.contact = this.view.querySelector('[data-book-fold-light]')!;
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('preserveAspectRatio', 'none');
        svg.setAttribute('viewBox', `0 0 ${this.width} ${this.height}`);
        const defs = document.createElementNS('http://www.w3.org/2000/svg', 'defs');
        this.shadowOutline = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        this.shadowOutline.id = 'book-leaf-shadow-' + ++shadowIdentity;
        defs.append(this.shadowOutline); svg.append(defs);
        const fade = document.createElementNS('http://www.w3.org/2000/svg', 'linearGradient');
        this.shadowGradient = fade;
        fade.id = this.shadowOutline.id + '-ink'; fade.setAttribute('gradientUnits', 'userSpaceOnUse');
        this.shadowFade = Array.from({ length: 4 }, (_, index) => {
            const stop = document.createElementNS('http://www.w3.org/2000/svg', 'stop');
            stop.setAttribute('stop-color', 'var(--paper-shade)'); stop.setAttribute('stop-opacity', String([0, 1, .75, 0][index]));
            stop.setAttribute('offset', index < 2 ? '0' : '1'); fade.append(stop); return stop;
        });
        defs.append(fade);
        this.shadowLayers = Array.from({ length: 8 }, () => {
            const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
            use.setAttribute('href', '#' + this.shadowOutline.id); use.setAttribute('fill', `url(#${fade.id})`);
            svg.append(use); return use;
        });
        this.shadow.append(svg);
        const curl = this.view.querySelector('[data-book-curl]')!;
        // One continuous opaque substrate beneath the tessellation. Independent
        // CSS clips antialias both sides of an internal seam; without this shared
        // paper footprint the lower page's ink bleeds through those half pixels.
        this.bendSubstrate = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        this.bendSubstrate.classList.add('reader-book__bend-substrate');
        this.bendSubstrate.setAttribute('viewBox', `0 0 ${this.width} ${this.height}`);
        this.bendSubstrate.setAttribute('preserveAspectRatio', 'none');
        this.bendOutline = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        this.bendSubstrate.append(this.bendOutline); curl.append(this.bendSubstrate);
        const count = paperSegments(this.width, options.automatic, options.angle);
        this.bands = Array.from({ length: count }, (_, index) => {
            const band = document.createElement('div'); band.className = 'reader-book__curl-band';
            const print = document.createElement('div');
            print.className = index >= count / 2 ? 'reader-book__back-print' : 'reader-book__band-print';
            print.style.height = `${index >= count / 2 ? options.landing.height : this.height}px`;
            const content = index >= count / 2 ? options.back : options.front;
            const ink = content?.cloneNode(true) as Element | undefined;
            if (ink) print.append(ink);
            const reverse = index >= count / 2;
            this.bandInk.push({ face: reverse, front: reverse ? undefined : ink, back: reverse ? ink : undefined });
            const light = document.createElement('div'); light.className = 'reader-book__bend-light';
            band.append(print, light); curl.append(band); return band;
        });
        this.setLanding(options.landing); host.append(this.view);
        this.draw();
    }

    setLanding(landing: LandingPaper): void {
        this.landing = landing;
        const source = this.options.outline;
        const target = landing.outline.map(p => ({ x: p.x * this.width, y: p.y * this.height }));
        // Source and receiver share physical sample positions. Retain the union
        // of their simplification indices once, not a new tessellation per frame.
        const indices = new Set([...simplifyOutline(source).map(p => source.indexOf(p)), ...simplifyOutline(target).map(p => target.indexOf(p))]);
        const ordered = [...indices].sort((a, b) => a - b);
        this.outline = ordered.map(i => source[i]); this.landingOutline = ordered.map(i => target[i]);
        // Allocate both page extents once. Do not resize all cloned text boxes
        // during settling: clips/transforms carry the geometric relaxation.
        this.view.style.height = `${Math.max(this.height, landing.height)}px`;
        this.backPrint.style.height = `${landing.height}px`;
        this.sourceLight.style.height = `${this.height}px`; this.landingLight.style.height = `${landing.height}px`;
        this.landingGutter.style.height = `${landing.height}px`;
        this.bendSubstrate.style.height = `${this.height}px`; this.shadow.style.height = `${this.height}px`;
        this.front.style.setProperty('--face-height', `${this.height}px`); this.back.style.setProperty('--face-height', `${landing.height}px`);
        this.bands.forEach((band, index) => (band.firstElementChild as HTMLElement).style.height = `${this.bandInk[index].face ? landing.height : this.height}px`);
        this.landingLight.style.setProperty('--paper-cross-light', landing.lighting); this.dirty = true;
    }

    private imprint(target: HTMLElement, ink?: Element, reverse?: Element): void {
        target.replaceChildren();
        if (this.options.showThrough && reverse) {
            const ghost = document.createElement('div'); ghost.className = 'reader-book__bleed';
            ghost.inert = true; ghost.setAttribute('aria-hidden', 'true');
            ghost.append(reverse.cloneNode(true)); target.append(ghost);
        }
        if (ink) target.append(ink);
    }

    stack(sourceRank: number, landingRank: number): void {
        if (this.sourceRank === sourceRank && this.landingRank === landingRank && this.front.style.zIndex) return;
        this.sourceRank = sourceRank; this.landingRank = landingRank;
        this.front.style.zIndex = String(1000 + sourceRank);
        this.shadow.style.zIndex = '1500'; this.contact.style.zIndex = '1501';
        this.dirty = true;
    }

    // Long jumps show a bounded paper packet, not a hidden queue of document pages.
    bundle(back: Element | undefined, skipped: number): void {
        if (this.options.showThrough) {
            const rim = this.front.querySelector(':scope > .reader-book__paper-rim');
            this.imprint(this.front, this.options.front, back);
            if (rim) this.front.append(rim);
            for (const ink of Array.from(this.front.children) as HTMLElement[]) if (!ink.classList.contains('reader-book__paper-rim')) ink.style.height = `${this.height}px`;
        }
        this.imprint(this.backPrint, back, this.options.front);
        this.backNumber = this.backPrint.querySelector('[data-book-number]');
        this.reverseInk = back;
        this.bands.forEach((band, index) => {
            const ink = this.bandInk[index];
            ink.back = ink.face ? back?.cloneNode(true) as Element | undefined : undefined;
            if (ink.face) band.firstElementChild!.replaceChildren(...ink.back ? [ink.back] : []);
        });
        if (!this.bundleView) {
            this.bundleView = document.createElement('div'); this.bundleView.className = 'reader-book__bundle';
            this.bundleView.style.height = `${this.height}px`;
            const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            svg.setAttribute('viewBox', `0 0 ${this.width} ${this.height}`); svg.setAttribute('preserveAspectRatio', 'none');
            this.bundlePaths = Array.from({ length: 3 }, () => {
                const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
                path.setAttribute('fill', 'var(--paper)'); path.setAttribute('stroke', 'color-mix(in srgb,var(--paper) 70%,#514637)');
                path.setAttribute('stroke-width', '.5'); svg.append(path); return path;
            });
            this.bundleView.append(svg); this.view.append(this.bundleView);
        }
        this.bundleDepth = Math.min(3, .6 + Math.log2(skipped + 1) * .4);
        this.view.dataset.leafSkipped = String(skipped); this.dirty = true;
    }

    draw(): void {
        let fold = foldPaper(this.width, this.height, this.point, this.gripY, this.outline);
        this.progress = fold.progress;
        const approach = Math.max(0, Math.min(1, (fold.progress - .6) / .4));
        const landed = approach * approach * (3 - 2 * approach);
        const mix = (a: number, b: number) => a + (b - a) * landed;
        const outline = landed ? this.outline.map((p, i) => ({ x: mix(p.x, this.landingOutline[i].x), y: mix(p.y, this.landingOutline[i].y) })) : this.outline;
        if (landed) fold = foldPaper(this.width, this.height, this.point, this.gripY, outline);
        this.renderedPoint = { ...fold.point };
        const scaleY = mix(this.height, this.landing.height) / this.height;
        if (this.options.paperEdges) {
            const contour = outline.map(p => ({ x: this.options.forward ? p.x / this.width : 1 - p.x / this.width, y: p.y / this.height }));
            for (const face of [this.front, this.back]) {
                paintPaperRim(face, contour, true);
                (face.querySelector(':scope > .reader-book__paper-rim') as SVGSVGElement).style.height = `${this.height * scaleY}px`;
            }
        }
        this.view.style.top = `${-mix(this.options.lift, this.landing.lift)}%`;
        this.sourceLight.style.opacity = String(1 - landed); this.landingLight.style.opacity = String(landed);
        this.landingGutter.style.opacity = String(landed * this.options.illumination);
        const footer = `translateY(${this.height * scaleY - this.landing.height - mix(this.options.footerRise, this.landing.footerRise)}px)`;
        if (this.backNumber) this.backNumber.style.transform = footer;
        this.view.dataset.leafLanding = landed.toFixed(4);
        const bend = this.options.automatic ? .022 : this.middle ? .05 : .04;
        const radius = Math.sin(fold.progress * Math.PI) * this.width * bend;
        const paper = curlPaper(this.width, this.height, fold, radius, this.options.angle, this.bands.length,
            outline, this.options.forward ? 1 : -1, !this.options.automatic);
        // Mirror the geometry, not the leaf root: its planes must stay in the
        // shared painter order when several sheets overlap.
        const mirror = (p: Point): Point => this.options.forward ? p : { x: this.width - p.x, y: p.y };
        const display = (p: Point): Point => ({ x: p.x, y: p.y * scaleY });
        const transform = ([a, b, c, d, e, f]: number[]) => {
            const m = this.options.forward ? [a, b, c, d, e, f] : [a, -b, -c, d, this.width - a * this.width - e, b * this.width + f];
            return [m[0], m[1] * scaleY, m[2] / scaleY, m[3], m[4], m[5] * scaleY];
        };
        this.front.style.clipPath = polygon(paper.front.map(mirror).map(display));
        this.back.style.clipPath = polygon(paper.back.map(mirror).map(display)); this.back.style.transform = matrix(transform(paper.reflection));
        const backOrder = 2000 + Math.round(paper.backHeight * 100) + this.landingRank;
        this.back.style.zIndex = String(backOrder);
        const bendPaths: string[] = [];
        let substrateOrder = backOrder;
        if (this.bundleView) {
            const reflected = paper.back.map(p => display(mirror({ x: paper.reflection[0] * p.x + paper.reflection[2] * p.y + paper.reflection[4],
                y: paper.reflection[1] * p.x + paper.reflection[3] * p.y + paper.reflection[5] })));
            this.bundleView.style.zIndex = String(backOrder - 1);
            this.bundlePaths.forEach((path, index) => path.setAttribute('d', reflected.length < 3 ? '' : reflected.map((p, i) =>
                `${i ? 'L' : 'M'}${p.x.toFixed(3)},${(p.y + this.bundleDepth * (3 - index) / 3).toFixed(3)}`).join(' ') + 'Z'));
        }
        paper.bands.forEach((band, index) => {
            const element = this.bands[index];
            const hidden = band.polygon.length < 3;
            // Retain printing layout, but don't change inherited light properties
            // on a surface outside the current paper outline.
            const visibility = hidden ? 'hidden' : 'visible';
            if (element.style.visibility !== visibility) element.style.visibility = visibility;
            if (hidden) return;
            const projected = band.polygon.map(p => display(mirror({ x: band.matrix[0] * p.x + band.matrix[2] * p.y + band.matrix[4],
                y: band.matrix[1] * p.x + band.matrix[3] * p.y + band.matrix[5] })));
            // Consistent winding fills the union even where front- and back-facing
            // patches overlap in projection. No holes from opposite path winding.
            if (band.back !== !this.options.forward) projected.reverse();
            bendPaths.push(projected.map((p, i) => `${i ? 'L' : 'M'}${p.x.toFixed(3)},${p.y.toFixed(3)}`).join(' ') + 'Z');
            substrateOrder = Math.min(substrateOrder, 2000 + Math.round(band.height * 100) + this.sourceRank);
            const ink = this.bandInk[index];
            if (ink.face !== band.back) {
                const print = element.firstElementChild as HTMLElement;
                if (band.back && !ink.back) ink.back = this.reverseInk?.cloneNode(true) as Element | undefined;
                if (!band.back && !ink.front) ink.front = this.options.front?.cloneNode(true) as Element | undefined;
                const content = band.back ? ink.back : ink.front;
                print.className = band.back ? 'reader-book__back-print' : 'reader-book__band-print';
                print.style.height = `${band.back ? this.landing.height : this.height}px`;
                print.replaceChildren(...content ? [content] : []); ink.face = band.back;
            }
            const number = (band.back ? ink.back : ink.front)?.querySelector<HTMLElement>('[data-book-number]');
            if (number) number.style.transform = band.back ? footer : `translateY(${-this.options.footerRise}px)`;
            // Quantise imperceptible light changes, without freezing a diagonal
            // grip's lighting at its initial crease direction.
            const colour = (value: number) => {
                const shade = Math.round(value * this.options.illumination * 100) / 100;
                return `color-mix(in srgb,var(--paper-${shade >= 0 ? 'shade' : 'bounce'}) ${Math.abs(shade) * 100}%,transparent)`;
            };
            const [axisX, axisY, start, end] = band.lightAxis;
            const nx = axisX * (this.options.forward ? 1 : -1), ny = axisY;
            const origin = (this.width * nx + this.height * ny - this.width * Math.abs(nx) - this.height * Math.abs(ny)) / 2;
            const mirrorOffset = this.options.forward ? 0 : axisX * this.width;
            const lighting = `linear-gradient(${(Math.atan2(ny, nx) * 180 / Math.PI + 90).toFixed(2)}deg,${colour(band.shadeEdges[0])} ${(start - mirrorOffset - origin).toFixed(2)}px,${colour(band.shadeEdges[1])} ${(end - mirrorOffset - origin).toFixed(2)}px)`;
            // Updating an empty overlay avoids cascading a new custom property
            // through the full cloned article on every pointer frame.
            if (this.shades[index] !== lighting) { (element.lastElementChild as HTMLElement).style.backgroundImage = lighting; this.shades[index] = lighting; }
            element.style.clipPath = polygon(band.polygon.map(mirror).map(display)); element.style.transform = matrix(transform(band.matrix));
            element.style.zIndex = String(2000 + Math.round(band.height * 100) + this.sourceRank);
        });
        this.bendOutline.setAttribute('d', bendPaths.join(' '));
        this.bendSubstrate.style.zIndex = String(substrateOrder - 1);
        const shadow = paperShadow(paper.backHeight, this.width);
        const projection = Math.sin(this.options.angle * Math.PI / 180);
        const castPoint = (point: Point) => {
            const x = fold.reflection[0] * point.x + fold.reflection[2] * point.y + fold.reflection[4];
            const y = fold.reflection[1] * point.x + fold.reflection[3] * point.y + fold.reflection[5];
            const liftedY = paper.reflection[1] * point.x + paper.reflection[3] * point.y + paper.reflection[5];
            const elevation = Math.max(0, (y - liftedY) / projection);
            return { x: mirror({ x, y }).x + elevation * .35 / .82, y: (y + elevation * .45 / .82) * scaleY };
        };
        const cast = paper.back.map(castPoint);
        const silhouette = cast.length < 3 ? '' : cast.map((p, i) => `${i ? 'L' : 'M'}${p.x.toFixed(3)},${p.y.toFixed(3)}`).join(' ') + 'Z';
        this.shadowOutline.setAttribute('d', silhouette);
        // Shadow density follows the actual lifting direction, not screen Y.
        // The inverse projected basis maps height (source X) to a world gradient;
        // a higher free edge is lighter than the near-contact part of the same leaf.
        const origin = castPoint({ x: 0, y: 0 });
        const px = castPoint({ x: this.width, y: 0 }), py = castPoint({ x: 0, y: this.height });
        const ux = px.x - origin.x, uy = px.y - origin.y, vx = py.x - origin.x, vy = py.y - origin.y;
        const determinant = ux * vy - uy * vx;
        const gx = Math.abs(determinant) > 1e-8 ? vy / determinant : 1 / this.width;
        const gy = Math.abs(determinant) > 1e-8 ? -vx / determinant : 0;
        const norm = gx * gx + gy * gy;
        const values = paper.back.map(p => p.x / this.width);
        const lower = values.length ? Math.min(...values) : 0, upper = values.length ? Math.max(...values) : 1;
        const start = { x: origin.x + gx * lower / norm, y: origin.y + gy * lower / norm };
        const end = { x: origin.x + gx * upper / norm, y: origin.y + gy * upper / norm };
        for (const [name, value] of Object.entries({ x1: start.x, y1: start.y, x2: end.x, y2: end.y })) this.shadowGradient.setAttribute(name, value.toFixed(3));
        const penumbra = shadowPenumbra(cast, radius, Math.hypot(end.x - start.x, end.y - start.y));
        this.shadowFade[1].setAttribute('offset', String(penumbra.edgeFade));
        this.shadowFade[2].setAttribute('offset', String(1 - penumbra.edgeFade));
        this.shadowLayers.forEach((layer, index) => {
            const step = penumbra.layers[index];
            if (!step) { layer.setAttribute('opacity', '0'); return; }
            layer.setAttribute('transform', matrix(step.matrix)); layer.setAttribute('opacity', String(step.opacity));
        });
        this.shadow.style.opacity = String(shadow.opacity * this.options.illumination * penumbra.strength);
        if (fold.crease.length === 2) {
            const [a, b] = fold.crease.map(mirror).map(display);
            // Taper only immediately at the bound tip, not across the whole page:
            // otherwise a crease near the gutter becomes an invisible hairline.
            const ra = Math.min(1, fold.crease[0].x / (this.width * .04)) * 50;
            const rb = Math.min(1, fold.crease[1].x / (this.width * .04)) * 50;
            this.contact.style.clipPath = `polygon(${50 - ra}% 0%,${50 + ra}% 0%,${50 + rb}% 100%,${50 - rb}% 100%)`;
            this.contact.style.visibility = 'visible'; this.contact.style.width = `${shadow.contactWidth}px`;
            this.contact.style.height = `${Math.hypot(b.x - a.x, b.y - a.y)}px`;
            this.contact.style.transform = `translate(${a.x}px,${a.y}px) rotate(${Math.atan2(b.y - a.y, b.x - a.x) * 180 / Math.PI - 90}deg) translateX(${-shadow.contactWidth / 2}px)`;
            this.contact.style.opacity = String(shadow.opacity * .9 * this.options.illumination);
        } else this.contact.style.visibility = 'hidden';
        this.view.dataset.leafProgress = this.progress.toFixed(4); this.dirty = false;
    }

    dispose(): void { this.view.remove(); }
}
