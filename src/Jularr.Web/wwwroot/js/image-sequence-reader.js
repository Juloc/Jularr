// Canonical image-sequence runtime for Manga/comics and future page-image documents.
// The source adapter renders pixels and persists progress; this module owns logical
// page/spread/mode/direction state so new image readers do not copy Manga behavior.

export const ImageSequenceMode = Object.freeze({
    single: "single",
    double: "double",
    continuous: "continuous",
    horizontal: "horizontal",
    webtoon: "webtoon"
});

export const normalizeImageSequenceMode = value =>
    Object.values(ImageSequenceMode).includes(value)
        ? value
        : ImageSequenceMode.single;

export const normalizePageDirection = value =>
    value === "rtl" ? "rtl" : "ltr";

export const isPagedImageMode = value =>
    value === ImageSequenceMode.single || value === ImageSequenceMode.double;

export const spreadStartFor = (
    index,
    mode,
    firstPageAlone = false) => {
    const page = Math.max(0, Math.round(Number(index) || 0));
    if (mode !== ImageSequenceMode.double) return page;
    if (firstPageAlone) {
        return page === 0 ? 0 : page - ((page - 1) % 2);
    }
    return page - (page % 2);
};

export const spreadPagesFor = (
    start,
    pageCount,
    mode,
    firstPageAlone = false) => {
    const total = Math.max(1, Math.round(Number(pageCount) || 1));
    const first = Math.min(
        total - 1,
        spreadStartFor(start, mode, firstPageAlone));
    if (mode !== ImageSequenceMode.double) return [first];
    if (firstPageAlone && first === 0) return [0];
    return first + 1 < total ? [first, first + 1] : [first];
};

export const createImageSequenceRenderer = ({
    pageCount,
    initialPage = 0,
    initialMode = ImageSequenceMode.single,
    initialDirection = "ltr",
    firstPageAlone = false
}) => {
    const total = Math.max(1, Math.round(Number(pageCount) || 1));
    let mode = normalizeImageSequenceMode(initialMode);
    let direction = normalizePageDirection(initialDirection);
    let soloFirst = Boolean(firstPageAlone);
    let page = Math.min(
        total - 1,
        spreadStartFor(initialPage, mode, soloFirst));

    const normalizeCurrentPage = () => {
        page = Math.min(
            total - 1,
            spreadStartFor(page, mode, soloFirst));
        return page;
    };

    const setPage = value => {
        const next = Math.min(total - 1, Math.max(0, Math.round(Number(value) || 0)));
        page = isPagedImageMode(mode)
            ? spreadStartFor(next, mode, soloFirst)
            : next;
        return page;
    };

    const setMode = value => {
        mode = normalizeImageSequenceMode(value);
        normalizeCurrentPage();
        return mode;
    };

    const setDirection = value => {
        direction = normalizePageDirection(value);
        return direction;
    };

    const setFirstPageAlone = value => {
        soloFirst = Boolean(value);
        normalizeCurrentPage();
        return soloFirst;
    };

    const visiblePages = () =>
        isPagedImageMode(mode)
            ? spreadPagesFor(page, total, mode, soloFirst)
            : [page];

    const targetForMove = delta => {
        const directionDelta = Math.sign(Number(delta) || 0);
        if (!directionDelta) {
            return { kind: "none", page };
        }

        if (directionDelta > 0) {
            const step = isPagedImageMode(mode)
                ? visiblePages().length
                : 1;
            const next = page + step;
            return next < total
                ? { kind: "page", page: isPagedImageMode(mode)
                    ? spreadStartFor(next, mode, soloFirst)
                    : next }
                : { kind: "edge", direction: 1, page };
        }

        if (page <= 0) {
            return { kind: "edge", direction: -1, page };
        }

        const next = isPagedImageMode(mode)
            ? spreadStartFor(page - 1, mode, soloFirst)
            : page - 1;
        return { kind: "page", page: Math.max(0, next) };
    };

    return Object.freeze({
        get pageCount() { return total; },
        get page() { return page; },
        get mode() { return mode; },
        get direction() { return direction; },
        get firstPageAlone() { return soloFirst; },
        get isPaged() { return isPagedImageMode(mode); },
        setPage,
        setMode,
        setDirection,
        setFirstPageAlone,
        visiblePages,
        targetForMove
    });
};
