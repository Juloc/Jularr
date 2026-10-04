// Canonical reflow runtime shared by Books and Novel/Light-Novel readers.
// Source adapters own persistence, translation, annotations and presentation;
// this module owns layout-independent reflow navigation semantics.
const clamp = (value, min, max) =>
    Math.min(max, Math.max(min, Number(value) || 0));

export const ReflowReadingMode = Object.freeze({
    continuous: "continuous",
    paged: "paged"
});

export const normalizeReadingMode = value =>
    value === ReflowReadingMode.paged
        ? ReflowReadingMode.paged
        : ReflowReadingMode.continuous;

export const permilleForIndex = (index, count) => {
    const total = Math.max(1, Number(count) || 1);
    if (total <= 1) return 0;
    return Math.round(clamp(index, 0, total - 1) / (total - 1) * 1000);
};

export const indexForPermille = (permille, count) => {
    const total = Math.max(1, Number(count) || 1);
    if (total <= 1) return 0;
    return Math.round(clamp(permille, 0, 1000) / 1000 * (total - 1));
};

export const scrollPermille = (scrollTop, scrollHeight, viewportHeight) => {
    const max = Math.max(0, Number(scrollHeight) - Number(viewportHeight));
    return max <= 0
        ? 0
        : Math.round(clamp(scrollTop, 0, max) / max * 1000);
};

export const scrollTopForPermille = (permille, scrollHeight, viewportHeight) => {
    const max = Math.max(0, Number(scrollHeight) - Number(viewportHeight));
    return max * clamp(permille, 0, 1000) / 1000;
};

const paragraphIndex = paragraph => {
    const value = paragraph?.dataset?.index ?? paragraph?.dataset?.bookParagraph;
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
};

const paragraphLanguage = paragraph =>
    paragraph?.dataset?.language || null;

export const captureContinuousAnchor = (
    paragraphs,
    targetY,
    { language = null } = {}) => {
    const items = Array.from(paragraphs || [])
        .filter(paragraph => !language || paragraphLanguage(paragraph) === language);
    if (!items.length) return null;

    const target = Number.isFinite(Number(targetY)) ? Number(targetY) : 0;
    let selected = items[0];

    for (const paragraph of items) {
        const rect = paragraph.getBoundingClientRect();
        if (rect.top <= target) selected = paragraph;
        if (rect.top <= target && rect.bottom >= target) break;
        if (rect.top > target) break;
    }

    const rect = selected.getBoundingClientRect();
    const length = selected.textContent?.length || 0;
    const fraction = rect.height <= 0
        ? 0
        : clamp((target - rect.top) / rect.height, 0, 1);

    return {
        paragraph: selected,
        index: paragraphIndex(selected),
        offset: Math.round(length * fraction),
        language: paragraphLanguage(selected)
    };
};

export const capturePagedRectAnchor = (
    paragraphs,
    currentPage,
    pageOfRect,
    { language = null } = {}) => {
    const items = Array.from(paragraphs || [])
        .filter(paragraph => !language || paragraphLanguage(paragraph) === language);
    if (!items.length) return null;

    const page = Math.max(0, Number(currentPage) || 0);
    for (let index = 0; index < items.length; index++) {
        const paragraph = items[index];
        const rects = Array.from(paragraph.getClientRects());
        const part = rects.findIndex(rect => pageOfRect(rect) === page);
        if (part < 0) continue;

        const next = items[index + 1];
        const nextFirst = next?.getClientRects?.()[0];
        if (part > 0 && nextFirst && pageOfRect(nextFirst) === page) {
            return {
                paragraph: next,
                index: paragraphIndex(next),
                part: 0,
                offset: 0,
                language: paragraphLanguage(next)
            };
        }

        return {
            paragraph,
            index: paragraphIndex(paragraph),
            part,
            offset: 0,
            language: paragraphLanguage(paragraph)
        };
    }

    const fallback = items[0];
    return {
        paragraph: fallback,
        index: paragraphIndex(fallback),
        part: 0,
        offset: 0,
        language: paragraphLanguage(fallback)
    };
};

export const capturePagedTextAnchor = (
    paragraphs,
    currentPage,
    pageOfLeft,
    characterLeft,
    { language = null } = {}) => {
    const items = Array.from(paragraphs || [])
        .filter(paragraph => !language || paragraphLanguage(paragraph) === language);
    if (!items.length) return null;

    const page = Math.max(0, Number(currentPage) || 0);
    const paragraph = items.find(item =>
        Array.from(item.getClientRects())
            .some(rect => rect.width > 0 && pageOfLeft(rect.left) >= page))
        || items[0];

    const first = paragraph.getClientRects()[0];
    if (!first || pageOfLeft(first.left) >= page) {
        return {
            paragraph,
            index: paragraphIndex(paragraph),
            offset: 0,
            language: paragraphLanguage(paragraph)
        };
    }

    const length = paragraph.textContent?.length || 0;
    let low = 0;
    let high = length;
    while (low < high) {
        const middle = Math.floor((low + high) / 2);
        const left = characterLeft(paragraph, middle, length);
        if (left !== null && pageOfLeft(left) >= page) high = middle;
        else low = middle + 1;
    }

    return {
        paragraph,
        index: paragraphIndex(paragraph),
        offset: Math.min(low, Math.max(0, length - 1)),
        language: paragraphLanguage(paragraph)
    };
};

export const createReflowTextRenderer = adapter => {
    if (!adapter || typeof adapter !== "object") {
        throw new TypeError("ReflowTextRenderer requires an adapter.");
    }

    let mode = normalizeReadingMode(adapter.initialMode);
    let pageIndex = 0;
    let pageCount = 1;

    const setPageState = (index, count) => {
        pageCount = Math.max(1, Number(count) || 1);
        pageIndex = clamp(index, 0, pageCount - 1);
        return { pageIndex, pageCount };
    };

    const setMode = value => {
        mode = normalizeReadingMode(value);
        adapter.onModeChange?.(mode);
        return mode;
    };

    const progressPermille = () =>
        mode === ReflowReadingMode.paged
            ? permilleForIndex(pageIndex, pageCount)
            : clamp(adapter.getScrollPermille?.() ?? 0, 0, 1000);

    const turn = direction => {
        if (mode !== ReflowReadingMode.paged) return false;
        const delta = Math.sign(Number(direction) || 0);
        if (!delta) return false;
        const next = clamp(pageIndex + delta, 0, pageCount - 1);
        if (next === pageIndex) {
            adapter.onPageEdge?.(delta);
            return false;
        }

        pageIndex = next;
        adapter.goToPage?.(pageIndex);
        adapter.onLocation?.({
            mode,
            pageIndex,
            pageCount,
            progressPermille: progressPermille()
        });
        return true;
    };

    const seekPermille = value => {
        if (mode === ReflowReadingMode.paged) {
            pageIndex = indexForPermille(value, pageCount);
            adapter.goToPage?.(pageIndex);
        } else {
            adapter.scrollToPermille?.(clamp(value, 0, 1000));
        }

        adapter.onLocation?.({
            mode,
            pageIndex,
            pageCount,
            progressPermille: progressPermille()
        });
    };

    const captureAnchor = () =>
        adapter.captureAnchor?.({
            mode,
            pageIndex,
            pageCount
        }) ?? null;

    const restoreAnchor = anchor =>
        adapter.restoreAnchor?.(anchor, {
            mode,
            pageIndex,
            pageCount
        });

    return Object.freeze({
        get mode() { return mode; },
        get pageIndex() { return pageIndex; },
        get pageCount() { return pageCount; },
        setMode,
        setPageState,
        progressPermille,
        turn,
        seekPermille,
        captureAnchor,
        restoreAnchor
    });
};
