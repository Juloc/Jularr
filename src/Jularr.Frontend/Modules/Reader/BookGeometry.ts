export interface Point { x: number; y: number }

// Shallow automatic turns use the fewest even cylinder facets whose interpolation
// error is < 1 CSS px at maximum bow, including the viewing projection. Direct grips
// retain their fine tessellation. The .9 px bound leaves a rounding margin.
export function paperSegments(width: number, automatic: boolean, angle = 24): number {
    if (!automatic) return width < 380 ? 10 : 16;
    const radius = .15 + Math.max(0, width) * .022;
    const projection = Math.hypot(1, Math.sin(angle * Math.PI / 180));
    // Linear interpolation error <= max(|curve''|) * step² / 8.
    const minimum = Math.ceil(Math.PI * Math.sqrt(radius * projection / (8 * .9)));
    return Math.max(4, minimum + minimum % 2);
}

export interface PaperFold {
    point: Point;
    front: Point[];
    back: Point[];
    reflection: number[];
    crease: Point[];
    progress: number;
}

const dot = (a: Point, b: Point) => a.x * b.x + a.y * b.y;
const subtract = (a: Point, b: Point): Point => ({ x: a.x - b.x, y: a.y - b.y });

// A bounded area-light approximation. Nested homothetic contours distribute the
// edge transition both outside AND inside the cast footprint. No hard core sits
// on its original boundary, and no full-page blur/filter runs during a gesture.
export function shadowPenumbra(points: Point[], radius: number, fadeLength?: number) {
    const xs = points.map(p => p.x), ys = points.map(p => p.y);
    const width = Math.max(...xs) - Math.min(...xs), height = Math.max(...ys) - Math.min(...ys);
    if (!points.length || width < .001 || height < .001) return { strength: 0, edgeFade: 0, layers: [] };
    const x = (Math.max(...xs) + Math.min(...xs)) / 2, y = (Math.max(...ys) + Math.min(...ys)) / 2;
    // A finite area light retains a broad penumbra near the receiving page.
    // Do not collapse this to a hairline as the free edge approaches contact.
    const softness = 5 + radius * 1.3;
    const weights = [.003, .008, .015, .021, .021, .015, .008, .003].map(value => value * 3.6);
    const layers = weights.map((opacity, index) => {
        const outset = softness * (2 - index * 4 / (weights.length - 1));
        const sx = Math.max(.05, 1 + outset * 2 / width), sy = Math.max(.05, 1 + outset * 2 / height);
        return { opacity, matrix: [sx, 0, 0, sy, x * (1 - sx), y * (1 - sy)] };
    });
    // A vanishingly narrow strip cannot cast the same energy as a broad page.
    return { layers, edgeFade: Math.min(.3, softness * 1.5 / Math.max(.001, fadeLength ?? height)),
        strength: Math.min(1, width / (softness * 2), height / (softness * 2)) };
}

// Contact, not progress, controls disappearance. Multiplying by sin(progress*pi)
// again used to erase the shadow while a late-held leaf was still visibly lifted.
export function paperShadow(height: number, width: number) {
    const clearance = Math.max(0, height);
    return { opacity: 1 - Math.exp(-clearance / .055),
        contactWidth: Math.min(width * .14, width * .045 + clearance * 2.6) };
}

// Keep a sampled curved edge within a sub-pixel error, instead of sending every
// nearly collinear sample through every moving clip and CSS polygon parser.
// This runs once per leaf, never once per animation frame.
export function simplifyOutline(points: Point[], tolerance = .08): Point[] {
    const keep = new Set([0, points.length - 1]);
    const split = (first: number, last: number) => {
        const a = points[first], b = points[last], dx = b.x - a.x, dy = b.y - a.y;
        const length = dx * dx + dy * dy;
        let furthest = -1, maximum = tolerance * tolerance;
        for (let index = first + 1; index < last; index++) {
            const p = points[index];
            const t = length ? Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / length)) : 0;
            const distance = (p.x - a.x - t * dx) ** 2 + (p.y - a.y - t * dy) ** 2;
            if (distance > maximum) { maximum = distance; furthest = index; }
        }
        if (furthest < 0) return;
        keep.add(furthest); split(first, furthest); split(furthest, last);
    };
    if (points.length < 4) return points;
    split(0, points.length - 1);
    return Array.from(keep).sort((a, b) => a - b).map(index => points[index]);
}

// A moving point and its resting point determine the perpendicular-bisector fold.
// Both faces use that same line: no independently rotating hinges or strip seams.
export function foldPaper(width: number, height: number, point: Point, gripY: number, outline?: Point[]): PaperFold {
    const resting = { x: width, y: gripY };
    const moving = { x: Math.max(-width, Math.min(width - .001, point.x)), y: Math.max(-height * .25, Math.min(height * 1.25, point.y)) };
    // Nearest point in both binding disks. Clamping Y at a fixed X suppresses a
    // vertical corner peel almost completely. Project onto the valid disk arc;
    // horizontal pulls inside both disks remain unchanged.
    const topRadius = Math.hypot(width, gripY), bottomRadius = Math.hypot(width, height - gripY);
    const valid = (p: Point) => Math.hypot(p.x, p.y) <= topRadius + 1e-7 && Math.hypot(p.x, p.y - height) <= bottomRadius + 1e-7;
    if (!valid(moving)) {
        const candidates: Point[] = [{ x: width, y: gripY }, { x: -width, y: gripY }];
        for (const [cy, radius] of [[0, topRadius], [height, bottomRadius]]) {
            const distance = Math.hypot(moving.x, moving.y - cy);
            const projected = { x: moving.x * radius / distance, y: cy + (moving.y - cy) * radius / distance };
            if (valid(projected)) candidates.push(projected);
        }
        const projected = candidates.reduce((best, p) => Math.hypot(p.x - moving.x, p.y - moving.y) < Math.hypot(best.x - moving.x, best.y - moving.y) ? p : best);
        moving.x = projected.x; moving.y = projected.y;
    }
    const difference = subtract(resting, moving);
    const length = Math.max(.001, Math.hypot(difference.x, difference.y));
    const normal = { x: difference.x / length, y: difference.y / length };
    const middle = { x: (resting.x + moving.x) / 2, y: (resting.y + moving.y) / 2 };
    const rectangle = outline ?? [{ x: 0, y: 0 }, { x: width, y: 0 }, { x: width, y: height }, { x: 0, y: height }];
    const crease: Point[] = [];
    const signed = (p: Point) => dot(subtract(p, middle), normal);
    const clip = (side: number): Point[] => {
        const polygon: Point[] = [];
        for (let index = 0; index < rectangle.length; index++) {
            const a = rectangle[index], b = rectangle[(index + 1) % rectangle.length];
            const da = signed(a), db = signed(b);
            if (da * side >= 0) polygon.push(a);
            if ((da < 0) !== (db < 0)) {
                const fraction = da / (da - db);
                const intersection = { x: a.x + (b.x - a.x) * fraction, y: a.y + (b.y - a.y) * fraction };
                polygon.push(intersection);
                if (side === 1) crease.push(intersection);
            }
        }
        return polygon;
    };
    const offset = 2 * dot(middle, normal);
    const front = clip(-1), back = clip(1);
    return {
        point: moving,
        front, back, crease: crease.length <= 2 ? crease : [...crease].sort((a, b) => dot(a, { x: -normal.y, y: normal.x }) - dot(b, { x: -normal.y, y: normal.x })).filter((_, index) => index === 0 || index === crease.length - 1),
        reflection: [1 - 2 * normal.x ** 2, -2 * normal.x * normal.y, -2 * normal.x * normal.y,
            1 - 2 * normal.y ** 2, offset * normal.x, offset * normal.y],
        progress: Math.max(0, Math.min(1, (width - moving.x) / (2 * width)))
    };
}

export function bookDimensions(printedPages: number, microns: number, currentPage: number, boardTenths: number) {
    const leaves = Math.ceil(printedPages / 2);
    const readLeaves = Math.min(leaves, Math.max(0, Math.floor(currentPage / 2)));
    const leftMm = readLeaves * microns / 1000, rightMm = (leaves - readLeaves) * microns / 1000;
    const centreMm = Math.min(leftMm, rightMm) * .35;
    return { leaves, readLeaves, leftMm, rightMm, centreMm,
        leftAngle: Math.atan2(leftMm - centreMm, 148) * 180 / Math.PI,
        rightAngle: -Math.atan2(rightMm - centreMm, 148) * 180 / Math.PI, boardMm: boardTenths / 10,
        totalMm: leaves * microns / 1000 + 2 * boardTenths / 10 };
}

export function paperColour(base: string, warmth: number, brightness: number): string {
    const rgb = [1, 3, 5].map(start => parseInt(base.slice(start, start + 2), 16));
    return '#' + rgb.map((value, index) => Math.round(Math.max(0, Math.min(255,
        (value + ([0, -14, -39][index] * warmth / 100)) * brightness / 100))).toString(16).padStart(2, '0')).join('');
}

// Orthographic, almost-overhead projection of a page, in A5 millimetres. The page itself stays as flat as a
// bare leaf (only the small inner crown remains, so the text never bends); the thickness of the stack is
// `drop`, the height of its cut edge below the page. More leaves add physical height, not extra DOM.
export function paperProfile(depth: number, curve: number, angle: number) {
    const radians = angle * Math.PI / 180;
    const height = 210 * Math.cos(radians);
    const points = Array.from({ length: 41 }, (_, index) => {
        const u = index / 40;
        const crown = 4 * u * (1 - u) * Math.exp(-3 * u) / .3557 * curve / 100 * 8;
        return { x: u * 148, y: -crown * Math.sin(radians) };
    });
    const inset = .25;
    const projectedX = (p: Point) => p.x - inset * (p.x / 148) ** 3;
    const index = points.findIndex(p => projectedX(p) >= 74);
    const a = points[index - 1], b = points[index];
    const fraction = (74 - projectedX(a)) / (projectedX(b) - projectedX(a));
    const footerLift = -(a.y + (b.y - a.y) * fraction);
    return { points, height, inset, footerLift, lift: Math.max(...points.map(point => -point.y)), drop: depth * Math.sin(radians) };
}

// Where a page's printed text box sits on its own paper. The stack beside the spine lifts the sheet and pulls the outer
// edge inwards, more the thicker the stack: the print rises with the paper's edge at the horizontal centre (as the page
// number always did) and moves to the middle of the paper's narrower width, so padding above, below, at the spine and at
// the outer edge keep the same ratio on both pages whatever has been read. A left page is the mirror of a right one: its
// spine is on the right, so its shift has the opposite sign.
export function printPlacement(profile: ReturnType<typeof paperProfile>, side: string, pixelScale: number) {
    return { top: (profile.lift - profile.footerLift) * pixelScale,
        shift: (side === 'left' ? 1 : -1) * profile.inset * pixelScale / 2 };
}

export function paperOutline(profile: ReturnType<typeof paperProfile>): Point[] {
    const x = (value: number) => (value - profile.inset * (value / 148) ** 3) / 148;
    return [...profile.points.map(point => ({ x: x(point.x), y: (point.y + profile.lift) / (profile.height + profile.lift) })),
        ...[...profile.points].reverse().map(point => ({ x: x(point.x), y: (profile.height + point.y + profile.lift) / (profile.height + profile.lift) }))];
}

export interface CurledBand {
    polygon: Point[]; matrix: number[]; back: boolean; shade: number; height: number;
    shadeEdges: [number, number]; lightAxis: [number, number, number, number];
}

const bendProfiles = new Map<string, { angle: number; x: number; z: number }[]>();
function bendProfile(segments: number, soft: boolean) {
    const key = segments + ':' + soft;
    const cached = bendProfiles.get(key);
    if (cached) return cached;
    const angle = (t: number) => Math.PI * (soft ? t * t * (3 - 2 * t) : t);
    const points = [{ angle: 0, x: 0, z: 0 }];
    for (let index = 1; index <= segments; index++) {
        const a = (index - 1) / segments, b = index / segments;
        // Fixed quadrature is cached once per detail level, not run per frame.
        const steps = 16, dt = (b - a) / steps;
        let dx = 0, dz = 0;
        for (let sample = 0; sample <= steps; sample++) {
            const weight = sample === 0 || sample === steps ? 1 : sample % 2 ? 4 : 2;
            const theta = angle(a + dt * sample);
            dx += Math.cos(theta) * weight; dz += Math.sin(theta) * weight;
        }
        const previous = points[index - 1];
        points.push({ angle: angle(b), x: index === segments ? 0 : previous.x + dx * dt / 3 * Math.PI,
            z: previous.z + dz * dt / 3 * Math.PI });
    }
    if (!soft) points.forEach((p, index) => { p.x = Math.sin(index / segments * Math.PI); p.z = 1 - Math.cos(index / segments * Math.PI); });
    bendProfiles.set(key, points); return points;
}

// A shared rounded bend, optionally tapered toward an unheld corner. Angular
// patches are secants of the same surface, not independently rotated hinges.
export function curlPaper(width: number, height: number, fold: PaperFold, radius: number, viewAngle: number, segments = 20, outline?: Point[], lightSide = 1, soft = true) {
    const [aa, ab, , , tx, ty] = fold.reflection;
    const normal = { x: Math.sqrt(Math.max(0, (1 - aa) / 2)), y: 0 };
    normal.y = normal.x > .000001 ? -ab / (2 * normal.x) : 1;
    const offset = (tx * normal.x + ty * normal.y) / 2;
    const project = Math.sin(viewAngle * Math.PI / 180);
    const rectangle = outline ?? [{ x: 0, y: 0 }, { x: width, y: 0 }, { x: width, y: height }, { x: 0, y: height }];
    // A conical bend anchored to the entire bound edge, not a cylinder that can
    // lift the spine. Radius is an affine field: zero at X=0, largest at the free edge.
    const radiusX = radius / width;
    const profile = bendProfile(segments, soft), lift = profile[segments].z;
    const clip = (polygon: Point[], limit: number, sign: number) => {
        const result: Point[] = [];
        polygon.forEach((a, index) => {
            const b = polygon[(index + 1) % polygon.length];
            const da = dot(a, normal) - offset - limit * radiusX * a.x;
            const db = dot(b, normal) - offset - limit * radiusX * b.x;
            if (da * sign >= 0) result.push(a);
            if ((da < 0) !== (db < 0)) {
                const t = da / (da - db);
                result.push({ x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t });
            }
        });
        return result;
    };
    const front = clip(rectangle, -Math.PI / 2, -1), back = clip(rectangle, Math.PI / 2, 1);
    const bands: CurledBand[] = [];
    // Approximate the two-sided diffuse scattering of matte fibres. Unlike a
    // specular strip or a clamped front/back normal, this stays continuous where
    // the viewer sees the paper edge-on. World light does not mirror with a grip.
    const matte = (theta: number) => (.82 - Math.abs(Math.sin(theta) * (normal.x * lightSide * .35 + normal.y * .45) + Math.cos(theta) * .82)) * .36;
    for (let index = 0; index < segments; index++) {
        const theta0 = profile[index].angle, theta1 = profile[index + 1].angle;
        const start = index / segments * Math.PI - Math.PI / 2, end = (index + 1) / segments * Math.PI - Math.PI / 2;
        const q0 = -Math.PI / 2 + profile[index].x, q1 = -Math.PI / 2 + profile[index + 1].x;
        const z0 = profile[index].z, z1 = profile[index + 1].z;
        const slope = (q1 - q0) / (end - start), rise = (z1 - z0) / (end - start);
        const intercept = q0 - slope * start, elevationIntercept = z0 - rise * start;
        const nx = normal.x, ny = normal.y;
        const qx = (slope - 1) * nx + intercept * radiusX;
        const qy = (slope - 1) * ny;
        const zx = rise * nx + elevationIntercept * radiusX;
        const zy = rise * ny;
        const a = 1 + nx * qx, c = nx * qy;
        const b = ny * qx - project * zx, d = 1 + ny * qy - project * zy;
        const constant = -(slope - 1) * offset;
        const elevation = -rise * offset;
        const polygon = clip(clip(rectangle, start, 1), end, -1);
        const theta = (theta0 + theta1) / 2;
        const representative = fold.crease.length ? fold.crease.reduce((sum, p) => sum + p.x, 0) / fold.crease.length : width / 2;
        const localRadius = radiusX * representative;
        bands.push({ polygon, matrix: [a, b, c, d, constant * nx, constant * ny - project * elevation],
            // The viewing projection moves the visible front/back transition.
            // Splitting at 90 degrees alone paints mirrored reverse ink on a
            // still-front-facing patch during vertical/diagonal peels.
            back: a * d - b * c < 0, shade: matte(theta), shadeEdges: [matte(theta0), matte(theta1)],
            lightAxis: [nx, ny, offset + start * localRadius, offset + end * localRadius],
            height: (z0 + z1) * localRadius / 2 });
    }
    const reflection = [...fold.reflection];
    reflection[1] -= project * radiusX * lift;
    const representative = fold.crease.length ? fold.crease.reduce((sum, p) => sum + p.x, 0) / fold.crease.length : width / 2;
    return { front, back, bands, reflection, rise: project * radius * lift,
        backHeight: radiusX * representative * lift, radiusRange: [0, radiusX * Math.max(...rectangle.map(p => p.x))] };
}
