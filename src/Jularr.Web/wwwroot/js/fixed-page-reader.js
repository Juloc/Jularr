(() => {
    "use strict";

    const clamp = (value, min, max) => Math.max(min, Math.min(max, Number(value) || 0));

    const createPageMap = (pageCount, pageItems) => {
        const total = Math.max(1, Math.round(Number(pageCount) || 1));
        const items = Array.from(pageItems || []);
        const itemCount = Math.max(1, items.length);
        const exact = items.length === total;

        return Object.freeze({
            positionForPage(page) {
                const number = clamp(Math.round(page), 1, total);
                if (exact) return { itemId: items[number - 1], positionPermille: 0 };
                const at = (number - 0.5) * itemCount / total;
                const index = clamp(Math.floor(at), 0, itemCount - 1);
                return {
                    itemId: items[index],
                    positionPermille: clamp(Math.round((at - index) * 1000), 0, 1000)
                };
            },
            pageForPosition(itemId, positionPermille) {
                const wanted = String(itemId || "").toLowerCase();
                const index = items.findIndex(id => String(id).toLowerCase() === wanted);
                if (index < 0) return null;
                if (exact) return index + 1;
                const at = (index + clamp(positionPermille, 0, 1000) / 1000) * total / itemCount;
                return clamp(Math.floor(at) + 1, 1, total);
            }
        });
    };

    const createSpreadProjection = (pageCount, perView, firstPageAlone = true) => {
        const total = Math.max(1, Math.round(Number(pageCount) || 1));
        const pages = Math.max(1, Math.round(Number(perView) || 1));
        const paired = pages > 1;

        const viewOfPage = page => {
            const number = clamp(Math.round(page), 1, total);
            if (!paired) return number - 1;
            if (!firstPageAlone) return Math.floor((number - 1) / pages);
            return number === 1 ? 0 : 1 + Math.floor((number - 2) / pages);
        };

        const pagesOfView = index => {
            const view = Math.max(0, Math.round(Number(index) || 0));
            if (!paired) {
                const page = view + 1;
                return page <= total ? [page] : [];
            }
            if (firstPageAlone && view === 0) return [1];

            const first = firstPageAlone
                ? 2 + (view - 1) * pages
                : 1 + view * pages;
            return Array.from({ length: pages }, (_, offset) => first + offset)
                .filter(page => page <= total);
        };

        let count = 0;
        while (pagesOfView(count).length) count++;

        return Object.freeze({
            count: Math.max(1, count),
            viewOfPage,
            pagesOfView
        });
    };

    const createState = ({ pageCount = 1, page = 1, mode = "paged", perView = 1, firstPageAlone = true } = {}) => {
        let total = Math.max(1, Math.round(Number(pageCount) || 1));
        let currentPage = clamp(Math.round(page), 1, total);
        let readingMode = mode === "continuous" ? "continuous" : "paged";
        let pagesPerView = Math.max(1, Math.round(Number(perView) || 1));
        let currentView = 0;

        const projection = () => createSpreadProjection(total, pagesPerView, firstPageAlone);
        const syncView = () => {
            currentView = projection().viewOfPage(currentPage);
            return currentView;
        };
        syncView();

        const setPageCount = value => {
            total = Math.max(1, Math.round(Number(value) || 1));
            currentPage = clamp(currentPage, 1, total);
            syncView();
            return total;
        };

        const setLayout = (nextMode, nextPerView) => {
            readingMode = nextMode === "continuous" ? "continuous" : "paged";
            pagesPerView = Math.max(1, Math.round(Number(nextPerView) || 1));
            syncView();
            return { mode: readingMode, perView: pagesPerView };
        };

        const setPage = value => {
            currentPage = clamp(Math.round(value), 1, total);
            syncView();
            return currentPage;
        };

        const setView = value => {
            const next = clamp(Math.round(value), 0, projection().count - 1);
            const pages = projection().pagesOfView(next);
            currentView = next;
            if (pages.length) currentPage = pages[0];
            return currentView;
        };

        const visiblePages = () => readingMode === "paged"
            ? projection().pagesOfView(currentView)
            : [currentPage];

        const targetForTurn = direction => {
            const delta = Math.sign(Number(direction) || 0);
            if (!delta) return { kind: "none", page: currentPage, view: currentView };
            if (readingMode !== "paged") return { kind: "scroll", direction: delta, page: currentPage, view: currentView };
            const next = currentView + delta;
            if (next < 0 || next >= projection().count) return { kind: "edge", direction: delta, page: currentPage, view: currentView };
            return { kind: "view", direction: delta, view: next, page: projection().pagesOfView(next)[0] };
        };

        return Object.freeze({
            get pageCount() { return total; },
            get page() { return currentPage; },
            get view() { return currentView; },
            get mode() { return readingMode; },
            get perView() { return pagesPerView; },
            get isPaged() { return readingMode === "paged"; },
            setPageCount,
            setLayout,
            setPage,
            setView,
            viewOfPage: pageNumber => projection().viewOfPage(pageNumber),
            viewCount: () => projection().count,
            pagesOfView: index => projection().pagesOfView(index),
            visiblePages,
            targetForTurn
        });
    };

    window.JularrFixedPage = Object.freeze({
        clamp,
        createPageMap,
        createSpreadProjection,
        createState
    });
})();