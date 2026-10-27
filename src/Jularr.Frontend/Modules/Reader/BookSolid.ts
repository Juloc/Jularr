import { paperProfile, paperOutline, type Point } from './BookGeometry';

type Dimensions = { leftMm: number; rightMm: number; centreMm: number; boardMm: number };
const svgNamespace = 'http://www.w3.org/2000/svg';
const rounded = (number: number) => number.toFixed(3);

// Shared by resting paper and a leaf relaxing onto that same receiving surface.
export function paperLighting(profile: ReturnType<typeof paperProfile>, side: string, strength: number, angle: number, reverse = false): string {
    const stops = [0, .01, .03, .06, .1, .16, .24, .35, .5, .7, 1].map(u => {
        const index = Math.round(u * 40), a = profile.points[Math.max(0, index - 1)], b = profile.points[Math.min(40, index + 1)];
        const slope = -(b.y - a.y) / Math.sin(angle * Math.PI / 180) / (b.x - a.x);
        const nx = (side === 'left' ? 1 : -1) * slope;
        const diffuse = (nx * -.35 + .82) / Math.hypot(1, nx);
        const shade = ((.82 - diffuse) * .8 + .13 * Math.exp(-u * 24)) * strength;
        return `color-mix(in srgb,${shade >= 0 ? 'var(--paper-shade)' : 'var(--paper-bounce)'} ${Math.min(shade >= 0 ? .30 : .10, Math.abs(shade)) * 100}%,transparent) ${u * 100}%`;
    });
    return `linear-gradient(${(side === 'left') !== reverse ? 270 : 90}deg,${stops.join(',')})`;
}

// Open contour: exposed top/outer/bottom edges, never a stained line at the spine.
// The same decoration stays attached to both large faces of a moving leaf.
export function paintPaperRim(surface: HTMLElement, outline: Point[], enabled: boolean): void {
    let rim = surface.querySelector<SVGSVGElement>(':scope > .reader-book__paper-rim');
    if (!enabled) { rim?.remove(); return; }
    if (!rim) {
        rim = document.createElementNS(svgNamespace, 'svg');
        rim.classList.add('reader-book__paper-rim'); rim.setAttribute('viewBox', '0 0 148 210');
        rim.setAttribute('preserveAspectRatio', 'none'); rim.setAttribute('aria-hidden', 'true');
        for (let index = 0; index < 2; index++) rim.append(document.createElementNS(svgNamespace, 'path'));
        surface.append(rim);
    }
    const path = outline.map((point, index) => `${index ? 'L' : 'M'}${rounded(point.x * 148)},${rounded(point.y * 210)}`).join(' ');
    for (const line of Array.from(rim.children)) if (line.getAttribute('d') !== path) line.setAttribute('d', path);
}

// A static vector silhouette, updated only for settings/position/resize. No canvas,
// filter, lighting loop or per-leaf node. Printed HTML stays in the accessibility
// tree; this decorative solid is always aria-hidden.
export function paintBookSolid(study: HTMLElement, dimensions: Dimensions, curve: number, angle: number, strength: number, pixelScale: number): void {
    const host = study.querySelector<HTMLElement>('[data-book-solid]')!;
    const preview = study;
    const left = paperProfile(dimensions.leftMm, curve, angle);
    const right = paperProfile(dimensions.rightMm, curve, angle);
    const height = right.height;
    const board = dimensions.boardMm * Math.sin(angle * Math.PI / 180);
    const svg = host.querySelector('svg') ?? document.createElementNS(svgNamespace, 'svg');
    svg.setAttribute('viewBox', `0 0 296 ${height}`);
    svg.setAttribute('preserveAspectRatio', 'none');
    svg.setAttribute('aria-hidden', 'true');
    const cursors = new Map<Element, number>();
    const element = (name: string, attributes: Record<string, string | number>, parent: Element = svg) => {
        const index = cursors.get(parent) ?? 0;
        cursors.set(parent, index + 1);
        const previous = parent.children[index];
        const node = previous?.localName === name ? previous : document.createElementNS(svgNamespace, name);
        if (node !== previous) { if (previous) previous.replaceWith(node); else parent.append(node); }
        for (const key of node.getAttributeNames()) if (!(key in attributes)) node.removeAttribute(key);
        for (const [key, value] of Object.entries(attributes)) if (node.getAttribute(key) !== String(value)) node.setAttribute(key, String(value));
        return node;
    };
    const defs = element('defs', {});
    const gradient = (id: string, stops: [number, string, number][], x2 = '100%', y2 = '0%') => {
        const paint = element('linearGradient', { id: `book-${id}`, x1: '0%', y1: '0%', x2, y2 }, defs);
        for (const [offset, colour, opacity] of stops) element('stop', { offset: `${offset}%`, 'stop-color': colour, 'stop-opacity': opacity * strength }, paint);
    };
    gradient('cut', [[0, '#514537', .23], [20, '#514537', .10], [75, '#514537', .17], [100, '#514537', .30]], '0%', '100%');
    const surfaces = [['left', left], ['right', right]] as const;
    const overhang = study.dataset.bookBinding === 'soft' ? .7 : 2.4;
    // The stack's cut edge hangs below the page. A paperback has no gap behind its pages: each stack keeps its own flat edge, thins to nothing in a V at the fold
    // as wide as the flare at its outer corner, and the thicker one steps down in a short soft corner on its own side of the fold. A hardcover leaves the space between case and
    // book block: both stacks meet at the spine, the thicker one flat, the thinner one rising softly to its own level.
    const soft = study.dataset.bookBinding === 'soft';
    const notchWidth = 10;
    const stackBase = (x: number) => {
        const thick = Math.max(left.drop, right.drop), thin = Math.min(left.drop, right.drop);
        const onThickSide = (right.drop >= left.drop) === x >= 148;
        if (soft) {
            const near = Math.max(0, Math.min(1, Math.abs(x - 148) / 12));
            return onThickSide ? thin + (thick - thin) * near * near * (3 - 2 * near) : thin;
        }
        const own = x < 148 ? left.drop : right.drop;
        const near = 1 - Math.max(0, Math.min(1, Math.abs(x - 148) / Math.min(90, Math.max(30, (thick - own) * 1.6))));
        return own + (thick - own) * near * near * (3 - 2 * near);
    };
    // Every leaf is bound at the spine, so a paperback's stack thins to nothing in a V at the fold.
    const stackAt = (x: number) => stackBase(x) * (soft ? Math.min(1, Math.abs(x - 148) / notchWidth) : 1);
    const trace = (shape: (x: number) => number, from: number, to: number, extra: number, ratio = 1) => {
        const steps = Math.max(2, Math.round(Math.abs(to - from) / 6)), low = Math.min(from, to), high = Math.max(from, to);
        const xs = Array.from({ length: steps + 1 }, (_, index) => from + (to - from) * index / steps);
        if (soft) for (const x of [-1, -.75, -.5, -.25, 0, .25, .5, .75, 1].map(step => 148 + step * notchWidth)) if (x > low && x < high) xs.push(x);
        return xs.sort((a, b) => (a - b) * Math.sign(to - from)).map(x => `L${rounded(x)},${rounded(height + shape(x) * ratio + extra)}`).join(' ');
    };
    const along = (from: number, to: number, extra: number, ratio = 1) => trace(stackAt, from, to, extra, ratio);
    // A paperback's stack flares out a little at its outer bottom corners, in a soft curve rather than a hard corner.
    const flareWidth = (outer: number) => soft && stackAt(outer) >= 1 ? Math.min(notchWidth, stackAt(outer) * .5) : 0;
    const flareEdge = (outer: number, sign: number) => {
        const drop = stackAt(outer), flare = flareWidth(outer);
        return `M${outer},${rounded(height)} C${outer},${rounded(height + drop * .5)} ${rounded(outer + sign * flare * .2)},${rounded(height + drop * .85)} ${rounded(outer + sign * flare)},${rounded(height + drop)}`;
    };
    const projectX = (side: string, profile: typeof left, point: { x: number }, ratio = 1) => {
        const depth = side === 'left' ? dimensions.leftMm : dimensions.rightMm;
        const fan = depth > 0 ? Math.min(overhang * .65, .35 + depth * .025) : 0;
        return 148 + (side === 'left' ? -1 : 1) * (point.x + (fan * (1 - ratio) - profile.inset * ratio) * (point.x / 148) ** 3);
    };
    const facePath = (side: string, profile: typeof left) =>
        profile.points.map((point, index) => `${index ? 'L' : 'M'}${rounded(projectX(side, profile, point))},${rounded(point.y)}`).join(' ')
        + [...profile.points].reverse().map(point => `L${rounded(projectX(side, profile, point))},${rounded(height + point.y)}`).join(' ') + 'Z';
    // Nested low-opacity strokes approximate a static soft contact shadow without
    // a live blur filter. The upper silhouette also casts onto the empty desk.
    for (const [side, profile] of surfaces) for (const [width, opacity] of [[6, .006], [4, .012], [2.5, .018], [1.4, .03], [.65, .04]])
        element('path', { d: facePath(side, profile), fill: 'none', stroke: '#30271e', 'stroke-width': width, 'stroke-opacity': opacity,
            'stroke-linejoin': 'round', transform: 'translate(.6,.8)' });
    // One chosen cover colour by default; a separate endpaper colour is optional.
    const cover = `M${-overhang},${-overhang} H143 Q148,${-overhang + .8} 153,${-overhang} H${296 + overhang} V${rounded(height + stackAt(296) + overhang)}
        ${along(296 + overhang, -overhang, overhang)} Z`;
    // The hanging stack casts its own soft shadow along the silhouette; the box behind only covers the page area.
    if (Math.max(left.drop, right.drop) > 1) for (const [width, opacity] of [[9, .014], [5, .024], [2.6, .04]])
        element('path', { d: cover, fill: 'none', stroke: '#30271e', 'stroke-width': width, 'stroke-opacity': opacity, 'stroke-linejoin': 'round', transform: 'translate(1.2,3)' });
    element('path', { d: cover, fill: 'var(--cover)', stroke: 'color-mix(in srgb,var(--cover) 74%,#302c27)', 'stroke-width': .35 });
    element('path', { d: `M${-overhang},${rounded(height + stackAt(0) + overhang)} ${along(-overhang, 296 + overhang, overhang)}
        V${rounded(height + stackAt(296) + overhang + board)} ${along(296 + overhang, -overhang, overhang + board)} Z`,
        fill: 'color-mix(in srgb,var(--cover) 75%,#302c27)' });
    element('path', { d: `M${-overhang + .6},${-overhang + .6} H${296 + overhang - .6} V${rounded(height + overhang - .6)} H${-overhang + .6} Z`, fill: 'var(--lining)' });
    for (const [outer, sign] of [[0, -1], [296, 1]] as const) if (flareWidth(outer) > 0) {
        const edge = flareEdge(outer, sign);
        element('path', { d: `${edge} L${outer},${rounded(height + stackAt(outer))} Z`, fill: 'var(--paper-cut)', 'data-book-cut': `${sign < 0 ? 'left' : 'right'}-flare` });
        element('path', { d: `${edge} L${outer},${rounded(height + stackAt(outer))}`, fill: 'none', stroke: '#504639', 'stroke-opacity': .5, 'stroke-width': .3 });
    }
    element('path', { d: `M147,-1 Q145,${height / 2} 147,${height + 1} L149,${height + 1} Q151,${height / 2} 149,-1 Z`,
        fill: 'color-mix(in srgb,var(--cover) 58%,#413a31)' });
    element('path', { d: `M147,${height + .2} Q148,${height + 1.8} 149,${height + .2}`, fill: 'none', stroke: 'var(--cover)', 'stroke-width': .65 });
    // Both curved faces meet here. Their independently antialiased edges must
    // not expose the dark cover below as a new line when a moving leaf retires.
    // Keep the actual bound paper under that joint; the shared gutter light still
    // supplies the visible crevice, including its paper-coloured interreflection.
    element('path', { d: `M147.5,${left.points[0].y} H148.5 V${height + left.points[0].y} H147.5 Z`, fill: 'var(--paper)' });
    for (const [side, profile] of surfaces) {
        const x = (point: { x: number }, ratio = 1) => projectX(side, profile, point, ratio);
        const line = (base: number, ratio: number) => profile.points.map((point, index) => `${index ? 'L' : 'M'}${rounded(x(point, ratio))},${rounded(base + point.y * ratio)}`).join(' ');
        const outside = profile.points[40], outsideX = x(outside, 0);
        const sideCut = `M${x(outside)},${outside.y} L${outsideX},0 V${height} L${x(outside)},${height + outside.y}Z`;
        element('path', { d: facePath(side, profile), fill: 'none', stroke: '#30271e', 'stroke-width': .9, 'stroke-opacity': .12, transform: 'translate(.15,.25)' });
        element('path', { d: sideCut, fill: 'var(--paper-cut)', 'data-book-cut': `${side}-outside` });
        element('path', { d: sideCut, fill: 'url(#book-cut)' });
        const sideCount = Math.min(12, Math.max(1, Math.round(profile.inset * pixelScale / .7)));
        for (let index = 1; index < sideCount; index++) {
            const ratio = index / sideCount;
            element('path', { d: `M${x(outside, ratio)},${outside.y * ratio} V${height + outside.y * ratio}`, fill: 'none',
                stroke: index % 3 === 0 ? 'color-mix(in srgb,var(--paper-cut) 68%,#514637)' : 'color-mix(in srgb,var(--paper-cut) 78%,#fffdf2)', 'stroke-opacity': .4, 'stroke-width': .16 });
        }
        const [from, to] = side === 'left' ? [0, 148] : [148, 296];
        const cut = `M${from},${height} L${to},${height} ${along(to, from, 0)} Z`;
        element('path', { d: cut, fill: 'var(--paper-cut)', 'data-book-cut': `${side}-bottom` });
        element('path', { d: cut, fill: 'url(#book-cut)' });
        // Optical groups of leaves; density tracks the stack's height, never page count. The lines stay level and simply end
        // where the V at the fold and the flared outer corner cut through the stack.
        const outer = side === 'left' ? 0 : 296, sign = side === 'left' ? -1 : 1;
        const stackClip = element('clipPath', { id: `book-stack-${side}` }, defs);
        element('path', { d: cut }, stackClip);
        element('path', { d: flareWidth(outer) > 0 ? `${flareEdge(outer, sign)} L${outer},${rounded(height + stackAt(outer))} Z` : 'M0,0Z' }, stackClip);
        const [lineFrom, lineTo] = side === 'left' ? [-flareWidth(0), 148] : [148, 296 + flareWidth(296)];
        const count = Math.min(24, Math.max(1, Math.round(profile.drop * pixelScale / .85)));
        for (let index = 1; index < count; index++) {
            const ratio = index / count;
            element('path', { d: `M${rounded(lineFrom)},${rounded(height + stackBase(lineFrom) * ratio)} ${trace(stackBase, lineFrom, lineTo, 0, ratio)}`, 'clip-path': `url(#book-stack-${side})`, fill: 'none', stroke: index % 3 === 0 ? 'color-mix(in srgb,var(--paper-cut) 68%,#514637)' : 'color-mix(in srgb,var(--paper-cut) 78%,#fffdf2)',
                'stroke-opacity': index % 3 === 0 ? .42 : .55, 'stroke-width': .2 + index % 3 * .04 });
        }
        element('path', { d: `M${from},${rounded(height + stackAt(from))} ${along(from, to, 0)}`, fill: 'none', stroke: '#504639', 'stroke-opacity': .5, 'stroke-width': .3 });
        element('path', { d: line(height, 1), fill: 'none', stroke: '#504639', 'stroke-opacity': .22, 'stroke-width': .18 });
        const outline = facePath(side, profile);
        element('path', { d: outline, fill: 'var(--paper)' });
        // A fine edge highlight/contact line, not a dark rectangular page border.
        element('path', { d: line(height, 1), fill: 'none', stroke: 'color-mix(in srgb,var(--paper) 78%,#584a38)', 'stroke-width': .2 });
        const sheet = study.querySelector<HTMLElement>(`[data-book-sheet=${side}]`)!;
        // Light follows the cross-section's actual slope, with a narrow contact
        // occlusion at the fold. It is not a fixed dark rail over a flat rectangle.
        const lighting = paperLighting(profile, side, strength, angle);
        sheet.style.setProperty('--paper-cross-light', lighting);
        if (side === 'right') preview.style.setProperty('--paper-cross-light', lighting);
        const contour = paperOutline(profile).map(point => ({ x: side === 'left' ? 1 - point.x : point.x, y: point.y }));
        paintPaperRim(sheet, contour, preview.dataset.paperEdge !== 'normal');
        const clip = contour.map(point => `${point.x * 100}% ${point.y * 100}%`);
        sheet.style.clipPath = `polygon(${clip.join(',')})`;
        sheet.style.setProperty('--page-lift', `${profile.lift / height * 100}%`);
    }
    // The fold runs on into the stack down to the spine, where both stacks are bound.
    element('path', { d: `M148,${rounded(height)} V${rounded(height + stackAt(148))}`, fill: 'none', stroke: '#30271e', 'stroke-opacity': .42, 'stroke-width': .45 });
    for (const [parent, count] of cursors) while (parent.children.length > count) parent.lastElementChild!.remove();
    if (svg.parentElement !== host) host.replaceChildren(svg);
    preview.style.setProperty('--page-height', String(height));
    preview.style.setProperty('--right-lift', `${right.lift / height * 100}%`);
    preview.style.setProperty('--spine-lift', '0%');
    preview.style.setProperty('--light', String(strength));
    study.dataset.bookSolidNodes = String(svg.querySelectorAll('*').length);
}
