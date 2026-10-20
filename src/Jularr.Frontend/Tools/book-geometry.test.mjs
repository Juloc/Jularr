import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import ts from 'typescript';

const source = readFileSync(resolve(import.meta.dirname, '../Modules/Reader/BookGeometry.ts'), 'utf8');
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText;
const { foldPaper, curlPaper, paperSegments, paperShadow, bookDimensions, paperColour, paperProfile, paperOutline, simplifyOutline, shadowPenumbra } = await import('data:text/javascript;base64,' + Buffer.from(compiled).toString('base64'));
const area = polygon => Math.abs(polygon.reduce((sum, a, i) => {
    const b = polygon[(i + 1) % polygon.length];
    return sum + a.x * b.y - a.y * b.x;
}, 0)) / 2;

test('shallow button turns retain a sub-pixel bow with a bounded facet budget', () => {
    for (const width of [180, 250, 300, 430, 600, 700, 1200, 2000]) for (const angle of [4, 14, 24]) {
        const count = paperSegments(width, true, angle), radius = .15 + width * .022;
        assert.equal(count % 2, 0); assert.ok(count >= 4);
        const deviation = radius * (Math.PI / count) ** 2 / 8 * Math.hypot(1, Math.sin(angle * Math.PI / 180));
        assert.ok(deviation <= .900001, `Maximum projected cylinder deviation ${deviation} at width ${width}/${angle}.`);
        for (const orientation of [0, Math.PI / 4, Math.PI / 2]) for (let index = 0; index < count; index++) {
            const theta0 = index / count * Math.PI, theta1 = (index + 1) / count * Math.PI;
            const projected = theta => ({ x: radius * Math.sin(theta) * Math.cos(orientation),
                y: radius * Math.sin(theta) * Math.sin(orientation) - radius * (1 - Math.cos(theta)) * Math.sin(angle * Math.PI / 180) });
            const a = projected(theta0), b = projected(theta1);
            for (const t of [.1, .25, .5, .75, .9]) {
                const p = projected(theta0 + (theta1 - theta0) * t);
                assert.ok(Math.hypot(p.x - a.x - (b.x - a.x) * t, p.y - a.y - (b.y - a.y) * t) <= .900001,
                    'Independently sampled projected bend stays within the sub-pixel bound.');
            }
        }
    }
    assert.equal(paperSegments(430, true), 4);
    assert.equal(paperSegments(300, false), 10); assert.equal(paperSegments(430, false), 16);
});

test('the cast shadow has a bounded penumbra on both sides of its footprint', () => {
    const shape = [{ x: 100, y: 20 }, { x: 260, y: 30 }, { x: 245, y: 560 }, { x: 95, y: 550 }];
    const shadow = shadowPenumbra(shape, 24);
    assert.equal(shadow.layers.length, 8);
    assert.equal(shadow.strength, 1);
    assert.ok(shadow.edgeFade > 0 && shadow.edgeFade < .12, 'The broader fade removes hard end caps while retaining most of the shadow centre.');
    assert.ok(shadow.layers[0].matrix[0] > 1 && shadow.layers.at(-1).matrix[0] < 1,
        'Softness extends outward and inward, not only as strokes outside a hard filled rectangle.');
    assert.ok(shadow.layers.every(layer => layer.matrix.every(Number.isFinite)));
    assert.ok(shadow.layers.every(layer => Math.abs(layer.matrix[0] - 1) > .001));
    assert.ok(shadow.layers.reduce((sum, layer) => sum + layer.opacity, 0) < .34);
    const tiny = shadowPenumbra([{ x: 0, y: 0 }, { x: .1, y: 0 }, { x: .1, y: 600 }, { x: 0, y: 600 }], 24);
    assert.ok(tiny.strength < .01 && tiny.layers.every(layer => layer.matrix[0] > 0));
    assert.equal(shadowPenumbra([], 24).strength, 0);
});

test('the broad contact shadow survives late holds and disappears only at contact', () => {
    const width = 430, height = 610;
    for (const progress of [.1, .5, .85, .95, .98]) {
        const fold = foldPaper(width, height, { x: width * (1 - 2 * progress), y: height / 2 }, height / 2);
        const paper = curlPaper(width, height, fold, .001 + Math.sin(progress * Math.PI) * width * .05, 14, 16);
        const shadow = paperShadow(paper.backHeight, width);
        assert.ok(shadow.opacity > .45, `Visible lifted paper retains a shadow at ${progress}.`);
        assert.ok(shadow.contactWidth >= width * .045, 'Late holds retain a broad contact footprint, not a vanishing hairline.');
    }
    assert.equal(paperShadow(0, width).opacity, 0);
    const laid = curlPaper(width, height, foldPaper(width, height, { x: -width, y: height / 2 }, height / 2), 0, 14);
    assert.equal(laid.backHeight, 0, 'A fully laid leaf has no artificial residual radius or terminal shadow flash.');
    assert.ok(paperShadow(.001, width).opacity < .02, 'The final microscopic clearance settles smoothly.');
});

test('moving curved edges discard redundant samples without exceeding 0.08 CSS pixels', () => {
    for (const width of [240, 430, 700]) for (const depth of [0, 12, 100]) for (const curve of [0, 24, 100]) {
        const profile = paperProfile(depth, depth * .2, curve, 24), height = width * (profile.height + profile.lift) / 148;
        const full = paperOutline(profile).map(p => ({ x: p.x * width, y: p.y * height }));
        const lean = simplifyOutline(full);
        assert.ok(lean.length < full.length, 'Redundant vertices are removed without relaxing the geometric error bound.');
        if (width <= 430 && curve <= 24 && depth <= 12) assert.ok(lean.length < full.length / 2,
            'The normal viewport/material cases need fewer than half the original samples.');
        for (const p of full) {
            const error = Math.min(...lean.map((a, i) => {
                const b = lean[(i + 1) % lean.length], dx = b.x - a.x, dy = b.y - a.y;
                const t = Math.max(0, Math.min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / (dx * dx + dy * dy)));
                return Math.hypot(p.x - a.x - t * dx, p.y - a.y - t * dy);
            }));
            assert.ok(error <= .080001, `No curved-edge point may move by a perceptible amount: ${width}/${depth}/${curve}, error=${error}, vertices=${lean.length}.`);
        }
        assert.ok(Math.abs(area(lean) - area(full)) / area(full) < .0005);
    }
});

test('one fold partitions the paper without lost or overlapping area', () => {
    for (const width of [180, 330, 520]) for (const grip of [0, .5, 1]) for (let i = 0; i <= 30; i++) {
        const height = width * 210 / 148;
        const fold = foldPaper(width, height, { x: width - i / 30 * width * 2, y: grip * height + Math.sin(i) * height * .18 }, grip * height);
        assert.ok(Math.abs(area(fold.front) + area(fold.back) - width * height) < .00001);
        const [a, b, c, d, x, y] = fold.reflection;
        assert.ok(Math.abs(a * d - b * c + 1) < .00001, 'Back is a reflection, not a second arbitrary plane.');
        for (const point of fold.crease) {
            assert.ok(Math.hypot(a * point.x + c * point.y + x - point.x, b * point.x + d * point.y + y - point.y) < .00001);
        }
        assert.ok(fold.reflection.every(Number.isFinite));
    }
});
test('printed sides produce half as many leaves; reading position redistributes one stable block', () => {
    const start = bookDimensions(320, 100, 1, 22), middle = bookDimensions(320, 100, 160, 22), end = bookDimensions(320, 100, 320, 22);
    assert.equal(start.leaves, 160);
    assert.equal(start.totalMm, 20.4);
    assert.equal(middle.leftMm, 8); assert.equal(middle.rightMm, 8);
    assert.equal(end.leftMm, 16); assert.equal(end.rightMm, 0);
    assert.equal(start.totalMm, end.totalMm);
    assert.equal(bookDimensions(321, 40, 100, 4).leaves, 161);
    assert.ok(bookDimensions(640, 100, 1, 22).totalMm > start.totalMm);
});
test('paper tint is deterministic; brightness changes the material, not the app theme', () => {
    assert.equal(paperColour('#ffffff', 0, 100), '#ffffff');
    assert.equal(paperColour('#ffffff', 100, 100), '#fff1d8');
    assert.match(paperColour('#ffffff', 38, 18), /^#[0-9a-f]{6}$/);
    assert.notEqual(paperColour('#ffffff', 38, 96), paperColour('#ffffff', 38, 18));
});
test('curved paper and cut edge share one physical cross-section and a continuous gutter', () => {
    for (const count of [16, 320, 2000]) for (const read of [1, count / 2, count]) for (const angle of [4, 14, 24]) {
        const dimensions = bookDimensions(count, 100, read, 22);
        const left = paperProfile(dimensions.leftMm, dimensions.centreMm, 24, angle);
        const right = paperProfile(dimensions.rightMm, dimensions.centreMm, 24, angle);
        assert.equal(left.points[0].y, right.points[0].y, 'Both paper blocks meet at the same spine elevation.');
        assert.equal(left.height, right.height);
        assert.ok(right.points.every(point => Number.isFinite(point.y) && point.y <= 0));
        assert.ok(Math.abs(right.points[40].y + dimensions.rightMm * Math.sin(angle * Math.PI / 180)) < 1e-8);
        assert.equal(right.points.length, 41, 'Geometry complexity does not grow with leaf count.');
    }
    assert.ok(paperProfile(12, 2, 100, 14).lift > paperProfile(12, 2, 0, 14).lift);
    const steep = paperProfile(12, 2, 0, 14);
    const rise = index => (steep.points[0].y - steep.points[index].y) / (steep.points[0].y - steep.points[40].y);
    assert.ok(rise(4) > .5 && rise(12) > .9, 'The block rises immediately at the spine, reaching almost full thickness within the inner third.');
});

test('page-number rise follows the actual curved paper edge at the horizontal centre', () => {
    for (const depth of [0, 3.2, 28.8, 100]) for (const curve of [0, 24, 100]) for (const angle of [4, 14, 24]) {
        const profile = paperProfile(depth, depth * .35, curve, angle);
        const lower = paperOutline(profile).slice(41).reverse();
        const i = lower.findIndex(p => p.x >= .5), a = lower[i - 1], b = lower[i];
        const y = a.y + (b.y - a.y) * (.5 - a.x) / (b.x - a.x);
        const edge = y * (profile.height + profile.lift) - profile.lift;
        assert.ok(Math.abs(profile.height - edge - profile.footerLift) < 1e-8,
            'Footer translation is measured from the paper contour, not the rectangular page box.');
        assert.ok(Number.isFinite(profile.footerLift) && profile.footerLift >= 0);
    }
});
test('rounded bend partitions the leaf and joins every projected patch without a hinge gap', () => {
    const width = 430, height = 610;
    const mapped = (matrix, point) => ({ x: matrix[0] * point.x + matrix[2] * point.y + matrix[4], y: matrix[1] * point.x + matrix[3] * point.y + matrix[5] });
    for (const grip of [0, height / 2, height]) for (let step = 1; step < 20; step++) {
        const fold = foldPaper(width, height, { x: width - step / 20 * width * 2, y: grip + Math.sin(step) * 40 }, grip);
        const paper = curlPaper(width, height, fold, 18, 14);
        const sum = area(paper.front) + area(paper.back) + paper.bands.reduce((total, band) => total + area(band.polygon), 0);
        assert.ok(Math.abs(sum - width * height) < 1e-6, 'Every part of the original leaf belongs to exactly one surface.');
        paper.bands.forEach((band, index) => {
            assert.ok(band.matrix.every(Number.isFinite));
            const [a, b, c, d] = band.matrix;
            assert.equal(band.back, a * d - b * c < 0, 'Ink side follows the actual projected face, not the unprojected 90-degree midpoint.');
            const next = paper.bands[index + 1];
            if (!next) return;
            for (const point of band.polygon) if (next.polygon.some(p => Math.hypot(p.x - point.x, p.y - point.y) < 1e-6)) {
                const a = mapped(band.matrix, point), b = mapped(next.matrix, point);
                assert.ok(Math.hypot(a.x - b.x, a.y - b.y) < 1e-6, 'Adjacent strips sample the same cylinder, not independent rotations.');
            }
        });
    }
});
test('a lifted leaf retains the same curved silhouette as the resting paper', () => {
    const width = 430, height = 610;
    const outline = paperOutline(paperProfile(25, 2, 60, 20)).map(point => ({ x: point.x * width, y: point.y * height }));
    for (const fraction of [.01, .25, .5, .75, .99]) {
        const fold = foldPaper(width, height, { x: width * (1 - fraction * 2), y: height * .8 }, height, outline);
        const curl = curlPaper(width, height, fold, 12, 20, 16, outline);
        const sum = area(curl.front) + area(curl.back) + curl.bands.reduce((total, band) => total + area(band.polygon), 0);
        assert.ok(Math.abs(sum - area(outline)) < 1e-6);
        assert.ok(area(outline) < width * height, 'The profile is a shaped sheet, not its rectangular bounding box.');
    }
});

test('matte bend lighting follows the crease while the world light is not mirrored', () => {
    const width = 430, height = 610;
    const fold = foldPaper(width, height, { x: 140, y: height * .7 }, height);
    const forward = curlPaper(width, height, fold, 20, 14, 16, undefined, 1);
    const reverse = curlPaper(width, height, fold, 20, 14, 16, undefined, -1);
    assert.ok(forward.bands.every(b => b.shade >= -.07 && b.shade <= .30));
    assert.ok(forward.bands.some((b, i) => Math.abs(b.shade - reverse.bands[i].shade) > .05),
        'A mirrored grip exposes a different normal to the same upper-left light.');
    assert.ok(forward.bands.some((b, i) => Math.abs(b.shade - forward.bands[15 - i].shade) > .05),
        'Front/back lighting is directional, not identical fixed dark bands.');
    const changed = curlPaper(width, height, foldPaper(width, height, { x: 140, y: height * .9 }, height), 20, 14, 16);
    assert.ok(forward.bands.some((b, i) => Math.abs(b.shade - changed.bands[i].shade) > .01));
    for (let index = 0; index < forward.bands.length - 1; index++) {
        assert.equal(forward.bands[index].shadeEdges[1], forward.bands[index + 1].shadeEdges[0],
            'Adjacent patches share exactly the same diffuse light at their boundary.');
    }
    assert.ok(forward.bands.every(b => b.lightAxis.every(Number.isFinite)));
});
test('corner bends taper while all patch/front/back boundaries stay continuous', () => {
    const apply = (m, p) => ({ x: m[0] * p.x + m[2] * p.y + m[4], y: m[1] * p.x + m[3] * p.y + m[5] });
    const identity = [1, 0, 0, 1, 0, 0];
    for (const width of [250, 430]) for (const corner of [0, 1]) for (const fraction of [.02, .2, .5, .8, .98]) {
        const height = width * 210 / 148, gripY = height * corner;
        const outline = paperOutline(paperProfile(20, 2, 35, 14)).map(p => ({ x: p.x * width, y: p.y * height }));
        const fold = foldPaper(width, height, { x: width * (1 - fraction * 2), y: gripY + (corner ? -1 : 1) * height * .2 }, gripY, outline);
        const paper = curlPaper(width, height, fold, width * .05, 14, 16, outline);
        const sum = area(paper.front) + area(paper.back) + paper.bands.reduce((total, band) => total + area(band.polygon), 0);
        assert.ok(Math.abs(sum - area(outline)) < 1e-6);
        assert.equal(Math.min(...paper.radiusRange), 0, 'The rounded fold has zero height at the bound edge.');
        assert.ok(Math.max(...paper.radiusRange) > 0, 'The free part still has an actual bend.');
        const surfaces = [{ polygon: paper.front, matrix: identity }, ...paper.bands, { polygon: paper.back, matrix: paper.reflection }];
        surfaces.forEach((surface, index) => {
            assert.ok(surface.matrix.every(Number.isFinite));
            const next = surfaces[index + 1];
            if (!next) return;
            for (const point of surface.polygon) if (next.polygon.some(p => Math.hypot(p.x - point.x, p.y - point.y) < 1e-6)) {
                const a = apply(surface.matrix, point), b = apply(next.matrix, point);
                assert.ok(Math.hypot(a.x - b.x, a.y - b.y) < 1e-6, 'Taper must not tear the crease or leave a stepped boundary.');
            }
        });
    }
});
test('diagonal drags cannot lift the flat fold across either end of the binding', () => {
    const width = 400, height = 570;
    for (const grip of [0, height * .25, height / 2, height * .75, height]) {
        for (let x = -width * 1.4; x <= width * 1.4; x += 40) for (let y = -height; y <= height * 2; y += 57) {
            const fold = foldPaper(width, height, { x, y }, grip);
            const nx = Math.sqrt(Math.max(0, (1 - fold.reflection[0]) / 2));
            const ny = nx > 1e-6 ? -fold.reflection[1] / (2 * nx) : 1;
            const offset = (fold.reflection[4] * nx + fold.reflection[5] * ny) / 2;
            for (const boundY of [0, height / 2, height])
                assert.ok(ny * boundY - offset <= .00001, 'The base crease must stay on the free side of the whole bound edge.');
        }
    }
});

test('vertical peels remain responsive and the rounded fold retains every bound point', () => {
    const width = 430, height = 610;
    const inside = (polygon, p) => {
        let sign = 0;
        for (let i = 0; i < polygon.length; i++) {
            const a = polygon[i], b = polygon[(i + 1) % polygon.length];
            const cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            if (Math.abs(cross) < 1e-5) continue;
            if (sign && Math.sign(cross) !== sign) return false;
            sign = Math.sign(cross);
        }
        return polygon.length >= 3;
    };
    for (const grip of [0, height]) {
        const fold = foldPaper(width, height, { x: width, y: grip + (grip ? -1 : 1) * height * .55 }, grip);
        assert.ok(fold.progress > .02, 'A vertical pull follows a valid binding arc instead of stalling at the free-edge X.');
    }
    for (const grip of [0, height / 2, height]) for (const x of [width, width * .9, width * .2, -width * .9]) for (const dy of [-height * .6, 0, height * .6]) {
        const fold = foldPaper(width, height, { x, y: grip + dy }, grip);
        const paper = curlPaper(width, height, fold, 24, 14, 16);
        for (const y of [0, height * .25, height / 2, height * .75, height])
            assert.ok(inside(paper.front, { x: 0, y }), 'Even the rounded bend, not just its centre crease, must keep the entire bound edge down.');
    }
    const fold = foldPaper(width, height, { x: width * .2, y: height * .8 }, height);
    const soft = curlPaper(width, height, fold, 24, 14, 16);
    const round = curlPaper(width, height, fold, 24, 14, 16, undefined, 1, false);
    assert.ok(soft.rise < round.rise * .85, 'The held-paper shoulder is flatter than a semicircular roller.');
});
