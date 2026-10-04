(() => {
    "use strict";

    // PDF pages for the Books reader (docs/UNIFIED_READER.md "PDF books").
    // books-reader.js owns the frame (settings, bookmarks, contents, search UI,
    // progress queue); this adapter owns the document: pdf.js (wwwroot/lib/pdfjs),
    // the paper sheets, page spreads or the scrolled page column, zoom, the text
    // layer that selection, search and read-aloud use, the outline and the
    // mapping between PDF pages and the work's page chapters.

    const MAX_CANVAS_PIXELS = 4096 * 2048;
    const KEEP_RENDERED = 8;
    const ZOOM_STEPS = [0.5, 0.67, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3];
    // An image covering most of a page that looks like paper is the page itself
    // (a scan) and is themed like text. Other images keep their own colours.
    const PAGE_IMAGE_SHARE = 0.6;
    const VIEW_KEY = "jularr:book-pdf-view";
    const SEARCH_HIT_LIMIT = 60;

    const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
    const frame = () => new Promise(resolve => requestAnimationFrame(() => resolve()));

    // The worker asks the page for cMaps, standard fonts and image decoders.
    // The vendored cMaps carry ".bin" because the static file server only
    // serves known file types.
    class StaticBinaryData {
        constructor({ cMapUrl = null, standardFontDataUrl = null, wasmUrl = null }) {
            this.urls = { cMapUrl, standardFontDataUrl, wasmUrl };
        }

        async fetch({ kind, filename }) {
            const base = this.urls[kind];
            if (!base) throw new Error(`No URL for ${kind}.`);
            const url = base + filename + (kind === "cMapUrl" ? ".bin" : "");
            const response = await fetch(url, { credentials: "same-origin" });
            if (!response.ok) throw new Error(`Unable to load ${url}.`);
            return new Uint8Array(await response.arrayBuffer());
        }
    }

    // pageChapters lists the chapter of every physical page (BookCatalogService.GetPdfPageChapterIdsAsync):
    // a page is stored as its chapter at position 0, pages that repeat an earlier page share its chapter,
    // and a chapter opens at its first page (findIndex). If the import could not read every page, pages
    // map linearly onto the chapters (page centre -> permille).
    const pageMap = (pageCount, pageChapters) => {
        const total = Math.max(1, pageCount);
        const chapters = Math.max(1, pageChapters.length);
        const exact = chapters === total;
        return Object.freeze({
            positionForPage(page) {
                const number = clamp(Math.round(page), 1, total);
                if (exact) return { chapterId: pageChapters[number - 1], positionPermille: 0 };
                const at = (number - 0.5) * chapters / total;
                const index = clamp(Math.floor(at), 0, chapters - 1);
                return {
                    chapterId: pageChapters[index],
                    positionPermille: clamp(Math.round((at - index) * 1000), 0, 1000)
                };
            },
            pageForPosition(chapterId, positionPermille) {
                const wanted = String(chapterId || "").toLowerCase();
                const index = pageChapters.findIndex(id => String(id).toLowerCase() === wanted);
                if (index < 0) return null;
                if (exact) return index + 1;
                const at = (index + clamp(Number(positionPermille) || 0, 0, 1000) / 1000) * total / chapters;
                return clamp(Math.floor(at) + 1, 1, total);
            }
        });
    };

    // Book spreads: the first page (the cover) stands alone, then 2–3, 4–5, …
    const spreads = (pageCount, perView) => Object.freeze({
        count: perView === 1 ? pageCount : Math.floor(pageCount / 2) + 1,
        viewOfPage: page => perView === 1 ? page - 1 : Math.floor(page / 2),
        pagesOfView: index => {
            if (perView === 1) return [index + 1];
            if (index === 0) return [1];
            return [index * 2, index * 2 + 1].filter(page => page <= pageCount);
        }
    });

    // Case-insensitive matches in one page's text layer text. Offsets are into
    // `text`, so a hit can be marked in the rendered layer; the snippet collapses
    // white space around it.
    const findMatches = (text, query, page, limit) => {
        const hits = [];
        const clean = String(query || "").trim();
        if (clean.length < 2) return hits;
        const lower = text.toLocaleLowerCase();
        // Lower-casing may change the length of rare characters; then the
        // offsets would not match the text layer, so such pages match exactly.
        const sameLength = lower.length === text.length;
        const haystack = sameLength ? lower : text;
        const needle = sameLength ? clean.toLocaleLowerCase() : clean;
        const collapse = value => value.replace(/\s+/g, " ");
        let from = 0;
        while (hits.length < limit) {
            const start = haystack.indexOf(needle, from);
            if (start < 0) break;
            const end = start + needle.length;
            const snippetStart = Math.max(0, start - 48);
            const snippetEnd = Math.min(text.length, end + 64);
            const before = (snippetStart > 0 ? "…" : "") + collapse(text.slice(snippetStart, start));
            const match = collapse(text.slice(start, end));
            hits.push({
                page,
                start,
                end,
                snippet: before + match + collapse(text.slice(end, snippetEnd)) + (snippetEnd < text.length ? "…" : ""),
                matchStart: before.length,
                matchLength: match.length
            });
            from = end;
        }
        return hits;
    };

    const readView = () => {
        try {
            const stored = JSON.parse(window.localStorage.getItem(VIEW_KEY) || "{}") || {};
            return {
                fit: stored.fit === "width" ? "width" : "page",
                zoom: ZOOM_STEPS.includes(Number(stored.zoom)) ? Number(stored.zoom) : 1
            };
        } catch {
            return { fit: "page", zoom: 1 };
        }
    };

    const create = options => {
        const {
            root,
            stage,
            container,
            status,
            urls,
            pageChapters,
            t
        } = options;
        const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
        const absolute = path => new URL(path, window.location.href).href;

        let lib = null;
        let doc = null;
        let pageCount = 0;
        let failed = false;
        let settings = options.settings || {};
        let mode = "paged";
        let perView = 1;
        let currentView = 0;
        let currentPage = 1;
        let view = readView();
        let outline = null;
        let outlineLoading = null;
        let layoutTimer = 0;
        let useClock = 0;
        let scrollFrame = 0;
        let observer = null;
        let turnTimer = 0;
        let searchHighlight = null;

        const sizes = new Map();
        const entries = new Map();
        const texts = new Map();
        let defaultSize = { width: 612, height: 792 };

        const setStatus = message => {
            if (!status) return;
            status.textContent = message || "";
            status.hidden = !message;
        };

        // ---- Pages and chapters ------------------------------------------------------

        let chapterMap = pageMap(pageChapters.length, pageChapters);
        const positionForPage = page => chapterMap.positionForPage(page);
        const pageForPosition = (chapterId, positionPermille) => chapterMap.pageForPosition(chapterId, positionPermille);

        // ---- Layout ----------------------------------------------------------------------

        const paged = () => mode === "paged";

        const viewOfPage = page => spreads(pageCount, perView).viewOfPage(page);
        const viewCount = () => spreads(pageCount, perView).count;
        const pagesOfView = index => spreads(pageCount, perView).pagesOfView(index);

        const sizeOf = page => sizes.get(page) || defaultSize;

        const stageBox = () => {
            const styles = getComputedStyle(stage);
            return {
                width: Math.max(160, stage.clientWidth - parseFloat(styles.paddingLeft) - parseFloat(styles.paddingRight)),
                height: Math.max(160, stage.clientHeight - parseFloat(styles.paddingTop) - parseFloat(styles.paddingBottom))
            };
        };

        const visibleHeight = () => {
            const top = root.querySelector("[data-reader-chrome-primary]")?.getBoundingClientRect().bottom || 0;
            const bottom = root.querySelector(".reader-frame-bottom")?.getBoundingClientRect().top || window.innerHeight;
            return Math.max(200, Math.min(window.innerHeight, bottom) - Math.max(0, top) - 32);
        };

        const spreadScale = pages => {
            const box = stageBox();
            const width = pages.reduce((sum, page) => sum + sizeOf(page).width, 0);
            const height = Math.max(...pages.map(page => sizeOf(page).height));
            const fit = view.fit === "width"
                ? box.width / width
                : Math.min(box.width / width, box.height / height);
            return Math.max(0.1, fit * view.zoom);
        };

        const columnScale = page => {
            const box = stageBox();
            const size = sizeOf(page);
            const fit = view.fit === "width"
                ? box.width / size.width
                : Math.min(box.width / size.width, visibleHeight() / size.height);
            return Math.max(0.1, fit * view.zoom);
        };

        const entryFor = page => {
            let entry = entries.get(page);
            if (!entry) {
                const element = document.createElement("div");
                element.className = "book-pdf-page";
                element.dataset.pdfPage = String(page);
                const text = document.createElement("div");
                text.className = "textLayer book-pdf-text";
                // The page's text is one read-aloud paragraph (reader-tts.js).
                text.dataset.readerParagraph = String(page - 1);
                element.append(text);
                entry = { page, element, text, scale: 0, token: 0, ready: null, task: null, layer: null, used: 0 };
                entries.set(page, entry);
            }
            entry.used = ++useClock;
            return entry;
        };

        const sizeEntry = (entry, scale) => {
            const size = sizeOf(entry.page);
            entry.element.style.width = Math.floor(size.width * scale) + "px";
            entry.element.style.height = Math.floor(size.height * scale) + "px";
            entry.targetScale = scale;
        };

        // ---- Rendering ---------------------------------------------------------------------

        const release = entry => {
            entry.token++;
            entry.task?.cancel();
            entry.layer?.cancel();
            entry.task = null;
            entry.layer = null;
            entry.ready = null;
            entry.scale = 0;
            entry.element.querySelectorAll("canvas").forEach(canvas => {
                canvas.width = 0;
                canvas.height = 0;
                canvas.remove();
            });
            entry.text.replaceChildren();
            delete entry.element.dataset.rendered;
        };

        const trimRendered = keep => {
            const rendered = Array.from(entries.values())
                .filter(entry => entry.ready && !keep.has(entry.page))
                .sort((a, b) => a.used - b.used);
            while (rendered.length > Math.max(0, KEEP_RENDERED - keep.size)) {
                release(rendered.shift());
            }
        };

        // A scanned page is a light, nearly colourless image; a full-page picture
        // (a cover, a plate) is not. Judged from a 24×24 sample of the region.
        const looksLikePaper = (canvas, sx, sy, sw, sh) => {
            const sample = document.createElement("canvas");
            sample.width = 24;
            sample.height = 24;
            const context = sample.getContext("2d", { willReadFrequently: true });
            context.drawImage(canvas, sx, sy, sw, sh, 0, 0, 24, 24);
            const data = context.getImageData(0, 0, 24, 24).data;
            let light = 0;
            let colour = 0;
            for (let index = 0; index < data.length; index += 4) {
                const r = data[index];
                const g = data[index + 1];
                const b = data[index + 2];
                light += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
                colour += (Math.max(r, g, b) - Math.min(r, g, b)) / 255;
            }
            const pixels = data.length / 4;
            return light / pixels > 0.7 && colour / pixels < 0.12;
        };

        // Images drawn on the page keep their colours in every paper style: they
        // are copied, untouched, above the themed page (book-reader.css).
        const imageOverlays = (canvas, coordinates) => {
            const overlays = [];
            if (!coordinates?.length) return overlays;
            for (let index = 0; index + 5 < coordinates.length; index += 6) {
                const xs = [coordinates[index], coordinates[index + 2], coordinates[index + 4],
                    coordinates[index + 2] + coordinates[index + 4] - coordinates[index]];
                const ys = [coordinates[index + 1], coordinates[index + 3], coordinates[index + 5],
                    coordinates[index + 3] + coordinates[index + 5] - coordinates[index + 1]];
                const left = clamp(Math.min(...xs), 0, 1);
                const right = clamp(Math.max(...xs), 0, 1);
                const top = clamp(Math.min(...ys), 0, 1);
                const bottom = clamp(Math.max(...ys), 0, 1);
                const share = (right - left) * (bottom - top);
                if (share < 0.0004) continue;
                const sx = Math.floor(left * canvas.width);
                const sy = Math.floor(top * canvas.height);
                const sw = Math.ceil((right - left) * canvas.width);
                const sh = Math.ceil((bottom - top) * canvas.height);
                if (sw < 2 || sh < 2) continue;
                if (share >= PAGE_IMAGE_SHARE && looksLikePaper(canvas, sx, sy, sw, sh)) continue;
                const overlay = document.createElement("canvas");
                overlay.className = "book-pdf-image";
                overlay.setAttribute("aria-hidden", "true");
                overlay.width = sw;
                overlay.height = sh;
                overlay.getContext("2d").drawImage(canvas, sx, sy, sw, sh, 0, 0, sw, sh);
                overlay.style.left = (left * 100) + "%";
                overlay.style.top = (top * 100) + "%";
                overlay.style.width = ((right - left) * 100) + "%";
                overlay.style.height = ((bottom - top) * 100) + "%";
                overlays.push(overlay);
            }
            return overlays;
        };

        // pdf.js puts a <br> after each line; a space keeps words apart for
        // read-aloud, search and copied text.
        const spaceLineEnds = container => {
            container.querySelectorAll("br").forEach(br => br.after(document.createTextNode(" ")));
        };

        const renderText = async (entry, page, viewport, token) => {
            const text = entry.text;
            text.style.setProperty("--total-scale-factor", String(viewport.scale));
            if (entry.layer) {
                entry.layer.update({ viewport });
                return;
            }
            const layer = new lib.TextLayer({
                textContentSource: page.streamTextContent(),
                container: text,
                viewport
            });
            entry.layer = layer;
            try {
                await layer.render();
            } catch {
                return;
            }
            if (token !== entry.token) return;
            spaceLineEnds(text);
        };

        const render = (entry, scale) => {
            if (entry.ready && Math.abs(entry.scale - scale) < 0.001) return entry.ready;
            entry.task?.cancel();
            const token = ++entry.token;
            entry.scale = scale;
            entry.ready = (async () => {
                const page = await doc.getPage(entry.page);
                if (token !== entry.token) return entry;
                const viewport = page.getViewport({ scale });
                const pixels = viewport.width * viewport.height;
                const ratio = Math.min(window.devicePixelRatio || 1, Math.sqrt(MAX_CANVAS_PIXELS / Math.max(1, pixels)));
                const canvas = document.createElement("canvas");
                canvas.className = "book-pdf-canvas";
                canvas.setAttribute("aria-hidden", "true");
                canvas.width = Math.max(1, Math.floor(viewport.width * ratio));
                canvas.height = Math.max(1, Math.floor(viewport.height * ratio));
                const task = page.render({
                    canvasContext: canvas.getContext("2d", { alpha: false }),
                    viewport,
                    transform: Math.abs(ratio - 1) < 0.001 ? null : [ratio, 0, 0, ratio, 0, 0],
                    recordImages: true
                });
                entry.task = task;
                const textDone = renderText(entry, page, viewport, token);
                try {
                    await task.promise;
                } catch (error) {
                    canvas.width = 0;
                    if (error?.name === "RenderingCancelledException") return entry;
                    throw error;
                }
                if (token !== entry.token) {
                    canvas.width = 0;
                    return entry;
                }
                const overlays = imageOverlays(canvas, page.imageCoordinates);
                entry.element.querySelectorAll("canvas").forEach(old => {
                    old.width = 0;
                    old.remove();
                });
                entry.element.prepend(canvas, ...overlays);
                entry.element.dataset.rendered = "true";
                await textDone;
                return entry;
            })().catch(error => {
                console.warn(error);
                if (token === entry.token) entry.ready = null;
                return entry;
            });
            return entry.ready;
        };

        const knowSize = async page => {
            if (sizes.has(page)) return;
            const proxy = await doc.getPage(page);
            const viewport = proxy.getViewport({ scale: 1 });
            sizes.set(page, { width: viewport.width, height: viewport.height });
        };

        // ---- Paged mode ----------------------------------------------------------------------

        const idle = callback =>
            (window.requestIdleCallback || (fn => window.setTimeout(fn, 120)))(callback, { timeout: 600 });

        const prefetch = index => {
            idle(async () => {
                if (!paged() || index !== currentView) return;
                const around = [index + 1, index - 1]
                    .filter(value => value >= 0 && value < viewCount());
                for (const next of around) {
                    const pages = pagesOfView(next);
                    await Promise.all(pages.map(knowSize));
                    if (!paged() || index !== currentView) return;
                    const scale = spreadScale(pages);
                    for (const page of pages) {
                        const entry = entryFor(page);
                        sizeEntry(entry, scale);
                        await render(entry, scale);
                        if (!paged() || index !== currentView) return;
                    }
                }
            });
        };

        let viewRun = 0;
        const showView = async (index, { animate = false, direction = 0 } = {}) => {
            const run = ++viewRun;
            currentView = clamp(index, 0, Math.max(0, viewCount() - 1));
            const pages = pagesOfView(currentView);
            currentPage = pages[0];
            emitLocation();
            await Promise.all(pages.map(knowSize));
            if (run !== viewRun) return;
            const scale = spreadScale(pages);
            const shown = pages.map(entryFor);
            shown.forEach((entry, position) => {
                sizeEntry(entry, scale);
                entry.element.classList.toggle("is-left", shown.length === 2 && position === 0);
                entry.element.classList.toggle("is-right", shown.length === 2 && position === 1);
            });
            container.replaceChildren(...shown.map(entry => entry.element));
            stage.scrollTo({ top: 0, left: 0 });
            syncOverflow();

            window.clearTimeout(turnTimer);
            container.classList.remove("is-turning-next", "is-turning-back", "is-fading");
            const transition = reduceMotion.matches ? "none" : settings.pageTransition;
            if (animate && direction && transition !== "none") {
                void container.offsetWidth;
                container.classList.add(transition === "fade"
                    ? "is-fading"
                    : direction > 0 ? "is-turning-next" : "is-turning-back");
                turnTimer = window.setTimeout(() => {
                    container.classList.remove("is-turning-next", "is-turning-back", "is-fading");
                }, 320);
            }

            trimRendered(new Set(pages));
            await Promise.all(shown.map(entry => render(entry, scale)));
            if (run !== viewRun) return;
            applySearchHighlight();
            prefetch(currentView);
        };

        const syncOverflow = () => {
            const horizontal = stage.scrollWidth > stage.clientWidth + 1;
            const vertical = stage.scrollHeight > stage.clientHeight + 1;
            root.dataset.pdfOverflow = horizontal ? "both" : vertical ? "vertical" : "none";
        };

        // ---- Scroll mode -----------------------------------------------------------------------

        const readingLine = () => {
            const top = root.querySelector("[data-reader-chrome-primary]")?.getBoundingClientRect().bottom || 0;
            return Math.max(0, top) + window.innerHeight * 0.2;
        };

        // Walks from the current page, so a scroll frame reads only a few rects.
        const pageAtLine = () => {
            const line = readingLine();
            const top = page => entries.get(page)?.element.getBoundingClientRect().top ?? 0;
            let page = clamp(currentPage, 1, pageCount);
            while (page > 1 && top(page) > line) page--;
            while (page < pageCount && top(page + 1) <= line) page++;
            return page;
        };

        const scrollAnchor = () => {
            const page = pageAtLine();
            const rect = entries.get(page)?.element.getBoundingClientRect();
            return {
                page,
                fraction: rect && rect.height ? clamp((readingLine() - rect.top) / rect.height, 0, 1) : 0
            };
        };

        const scrollToPage = (page, fraction = 0) => {
            const rect = entries.get(page)?.element.getBoundingClientRect();
            if (!rect) return;
            const top = root.querySelector("[data-reader-chrome-primary]")?.getBoundingClientRect().bottom || 0;
            const target = fraction > 0
                ? window.scrollY + rect.top + rect.height * fraction - readingLine()
                : window.scrollY + rect.top - Math.max(0, top) - 12;
            window.scrollTo({ top: Math.max(0, target), behavior: "auto" });
        };

        const layoutColumn = anchor => {
            if (observer) observer.disconnect();
            intersecting.clear();
            const elements = [];
            for (let page = 1; page <= pageCount; page++) {
                const entry = entryFor(page);
                entry.element.classList.remove("is-left", "is-right");
                sizeEntry(entry, columnScale(page));
                elements.push(entry.element);
            }
            container.replaceChildren(...elements);
            root.dataset.pdfOverflow = "none";
            observer = new IntersectionObserver(onIntersect, { rootMargin: "120% 0px 160% 0px" });
            elements.forEach(element => observer.observe(element));
            scrollToPage(anchor.page, anchor.fraction);
            currentPage = anchor.page;
            emitLocation();
        };

        // Pages near the viewport render; the rest stay empty paper of the right size.
        const intersecting = new Set();

        const showColumnPage = page => knowSize(page).then(() => {
            if (paged()) return null;
            const entry = entryFor(page);
            const scale = columnScale(page);
            const before = entry.element.getBoundingClientRect();
            sizeEntry(entry, scale);
            // Keep the reading position when a page above it turns out to have
            // another size than estimated.
            const delta = entry.element.getBoundingClientRect().height - before.height;
            if (delta && before.bottom < readingLine()) window.scrollBy(0, delta);
            return render(entry, scale);
        }).then(entry => {
            if (!entry || paged()) return;
            trimRendered(new Set([...intersecting, ...nearPages()]));
            applySearchHighlight();
        });

        const onIntersect = records => {
            for (const record of records) {
                const page = Number(record.target.dataset.pdfPage);
                if (!record.isIntersecting) {
                    intersecting.delete(page);
                    continue;
                }
                intersecting.add(page);
                void showColumnPage(page);
            }
        };

        const nearPages = () => {
            const pages = [];
            for (let page = Math.max(1, currentPage - 2); page <= Math.min(pageCount, currentPage + 3); page++) {
                pages.push(page);
            }
            return pages;
        };

        const onScroll = () => {
            if (paged() || !doc || scrollFrame) return;
            scrollFrame = requestAnimationFrame(() => {
                scrollFrame = 0;
                const page = pageAtLine();
                if (page === currentPage) return;
                currentPage = page;
                emitLocation();
                for (const near of nearPages()) {
                    if (!entries.get(near)?.ready) void showColumnPage(near);
                }
            });
        };
        window.addEventListener("scroll", onScroll, { passive: true });

        // ---- Location ------------------------------------------------------------------------

        const visiblePages = () => {
            if (!pageCount) return [currentPage, currentPage];
            if (paged()) {
                const pages = pagesOfView(currentView);
                return [pages[0], pages.at(-1)];
            }
            return [currentPage, currentPage];
        };

        function emitLocation() {
            if (!pageCount) return;
            const [first, last] = visiblePages();
            options.onLocation?.({ first, last, total: pageCount, section: sectionFor(first) });
        }

        // ---- Navigation ----------------------------------------------------------------------

        const goToPage = (page, { animate = false } = {}) => {
            if (!pageCount) return Promise.resolve();
            const target = clamp(Math.round(page), 1, pageCount);
            if (paged()) {
                const index = viewOfPage(target);
                const direction = Math.sign(index - currentView);
                return showView(index, { animate, direction });
            }
            scrollToPage(target);
            currentPage = target;
            emitLocation();
            return Promise.resolve();
        };

        const turn = direction => {
            if (!pageCount) return;
            if (!paged()) {
                window.scrollBy({
                    top: direction * visibleHeight() * 0.9,
                    behavior: reduceMotion.matches ? "auto" : "smooth"
                });
                return;
            }
            // Zoomed in, a page turn first moves through the enlarged page.
            if (root.dataset.pdfOverflow !== "none") {
                const vertical = stage.scrollHeight > stage.clientHeight + 1;
                const room = direction > 0
                    ? (vertical ? stage.scrollHeight - stage.clientHeight - stage.scrollTop : stage.scrollWidth - stage.clientWidth - stage.scrollLeft)
                    : (vertical ? stage.scrollTop : stage.scrollLeft);
                if (room > 2) {
                    const step = direction * (vertical ? stage.clientHeight : stage.clientWidth) * 0.85;
                    stage.scrollBy({
                        top: vertical ? step : 0,
                        left: vertical ? 0 : step,
                        behavior: reduceMotion.matches ? "auto" : "smooth"
                    });
                    return;
                }
            }
            const next = currentView + direction;
            if (next < 0 || next >= viewCount()) return;
            void showView(next, { animate: true, direction });
        };

        // The slider runs from 0 to the page count; its value is the last page on
        // screen, so its fill matches the percentage in the label.
        const seek = value => goToPage(Math.max(1, Number(value) || 1));

        // ---- Zoom ----------------------------------------------------------------------------

        const storeView = () => {
            try {
                window.localStorage.setItem(VIEW_KEY, JSON.stringify(view));
            } catch {
            }
        };

        const syncZoomControls = () => {
            root.querySelectorAll("[data-book-pdf-fit]").forEach(button => {
                button.setAttribute("aria-pressed", button.dataset.bookPdfFit === view.fit && view.zoom === 1 ? "true" : "false");
            });
            root.querySelectorAll("[data-book-pdf-zoom]").forEach(button => {
                const step = Number(button.dataset.bookPdfZoom);
                const index = ZOOM_STEPS.indexOf(view.zoom);
                button.disabled = step > 0 ? index >= ZOOM_STEPS.length - 1 : index <= 0;
            });
            root.querySelectorAll("[data-book-pdf-zoom-value]").forEach(output => {
                output.textContent = Math.round(view.zoom * 100) + "%";
            });
        };

        const setView = next => {
            view = next;
            storeView();
            syncZoomControls();
            relayout();
        };

        const zoom = direction => {
            const index = ZOOM_STEPS.indexOf(view.zoom);
            const next = ZOOM_STEPS[clamp(index + Math.sign(direction), 0, ZOOM_STEPS.length - 1)];
            if (next !== view.zoom) setView({ ...view, zoom: next });
        };

        const fit = value => setView({ fit: value === "width" ? "width" : "page", zoom: 1 });

        root.querySelectorAll("[data-book-pdf-fit]").forEach(button => {
            button.addEventListener("click", () => fit(button.dataset.bookPdfFit));
        });
        root.querySelectorAll("[data-book-pdf-zoom]").forEach(button => {
            button.addEventListener("click", () => zoom(Number(button.dataset.bookPdfZoom)));
        });
        syncZoomControls();

        // ---- Settings and relayout -------------------------------------------------------------

        const layoutNow = () => {
            if (!doc) return;
            const nextMode = settings.readingMode === "continuous" ? "continuous" : "paged";
            const box = stageBox();
            const nextPerView = nextMode === "paged" && settings.twoPageSpread !== false && box.width >= 860 && box.height >= 420 ? 2 : 1;
            const anchorPage = paged() ? pagesOfView(currentView)[0] : null;
            const columnAnchor = !paged() && container.childElementCount ? scrollAnchor() : null;
            const modeChanged = nextMode !== mode || !container.childElementCount;
            mode = nextMode;
            perView = nextPerView;
            laidOutSpread = settings.twoPageSpread !== false;
            root.dataset.bookLayout = paged() ? "paged" : "scroll";
            root.dataset.pages = String(perView);
            root.classList.toggle("reader-frame-fixed", paged());

            if (paged()) {
                if (observer) {
                    observer.disconnect();
                    observer = null;
                }
                window.scrollTo({ top: 0, behavior: "auto" });
                void showView(viewOfPage(anchorPage ?? currentPage));
                return;
            }
            const anchor = columnAnchor && !modeChanged ? columnAnchor : { page: anchorPage ?? currentPage, fraction: 0 };
            layoutColumn(anchor);
        };

        function relayout() {
            window.clearTimeout(layoutTimer);
            layoutTimer = window.setTimeout(layoutNow, 90);
        }

        // Paper, transition and theme settings are CSS only; mode and spread lay out
        // again. The settings object is updated in place, so the spread choice is
        // compared with the last one laid out.
        let laidOutSpread = settings.twoPageSpread !== false;
        const apply = next => {
            settings = next || settings;
            if (!doc) return;
            const nextMode = settings.readingMode === "continuous" ? "continuous" : "paged";
            const spread = settings.twoPageSpread !== false;
            if (nextMode !== mode) {
                window.clearTimeout(layoutTimer);
                layoutNow();
            } else if (spread !== laidOutSpread) {
                relayout();
            }
        };

        // ---- Outline (contents) -----------------------------------------------------------------

        const resolveDestination = async destination => {
            try {
                const explicit = typeof destination === "string"
                    ? await doc.getDestination(destination)
                    : destination;
                const target = Array.isArray(explicit) ? explicit[0] : null;
                if (Number.isInteger(target)) return target + 1;
                if (target && typeof target === "object") return (await doc.getPageIndex(target)) + 1;
            } catch {
            }
            return null;
        };

        const loadOutline = () => {
            if (outline) return Promise.resolve(outline);
            if (outlineLoading) return outlineLoading;
            outlineLoading = (async () => {
                const items = [];
                const walk = async (nodes, depth) => {
                    for (const node of nodes || []) {
                        if (items.length >= 600) return;
                        const page = node.dest ? await resolveDestination(node.dest) : null;
                        const title = String(node.title || "").trim();
                        if (page && title) items.push({ title, page, depth });
                        if (depth < 3) await walk(node.items, depth + 1);
                    }
                };
                try {
                    await walk(await doc.getOutline(), 0);
                } catch (error) {
                    console.warn(error);
                }
                outline = items;
                emitLocation();
                options.onOutline?.(outline);
                return outline;
            })();
            return outlineLoading;
        };

        function sectionFor(page) {
            if (!outline?.length) return null;
            let section = null;
            for (const item of outline) {
                if (item.page <= page) section = item;
                else if (item.page > page && section) break;
            }
            return section;
        }

        // Without an outline the contents list offers page ranges.
        const pageRanges = () => {
            if (pageCount <= 30) {
                return Array.from({ length: pageCount }, (_, index) => ({
                    title: t("books.read.pageNumber", "Page {number}", { number: index + 1 }),
                    page: index + 1,
                    depth: 0
                }));
            }
            const steps = [5, 10, 20, 25, 50, 100, 200, 250, 500, 1000];
            const step = steps.find(value => pageCount / value <= 30) || Math.ceil(pageCount / 30);
            const ranges = [];
            for (let first = 1; first <= pageCount; first += step) {
                const last = Math.min(pageCount, first + step - 1);
                ranges.push({
                    title: t("books.read.pageRange", "Pages {first}–{last}", { first, last }),
                    page: first,
                    depth: 0
                });
            }
            return ranges;
        };

        const contents = async query => {
            if (!doc) return [];
            const items = (await loadOutline()).length ? outline : pageRanges();
            const clean = String(query || "").trim();
            if (!clean) return items;
            const number = /^\d+$/.test(clean) ? Number(clean) : null;
            const needle = clean.toLocaleLowerCase();
            const matches = items.filter(item => item.title.toLocaleLowerCase().includes(needle));
            if (number && number >= 1 && number <= pageCount) {
                matches.unshift({
                    title: t("books.read.pageNumber", "Page {number}", { number }),
                    page: number,
                    depth: 0
                });
            }
            return matches;
        };

        const stepSection = direction => {
            if (!pageCount) return;
            const [first, last] = visiblePages();
            if (outline?.length) {
                const current = sectionFor(first);
                const index = current ? outline.indexOf(current) : -1;
                const target = direction > 0
                    ? outline.slice(index + 1).find(item => item.page > last)
                    : current && current.page < first
                        ? current
                        : outline.slice(0, Math.max(0, index)).reverse().find(item => item.page < first);
                if (target) void goToPage(target.page, { animate: true });
                else if (direction < 0) void goToPage(1, { animate: true });
                return;
            }
            turn(direction);
        };

        // ---- Search through the text layer ------------------------------------------------

        const pageText = async page => {
            if (texts.has(page)) return texts.get(page);
            const content = await (await doc.getPage(page)).getTextContent();
            // Same order as the rendered text layer: each item, then a space at line ends.
            const value = content.items
                .filter(item => item.str !== undefined)
                .map(item => item.str + (item.hasEOL ? " " : ""))
                .join("");
            texts.set(page, value);
            return value;
        };

        // Searches page by page; a newer search makes an older one return null.
        let searchRun = 0;
        const search = async query => {
            const run = ++searchRun;
            if (!doc || String(query || "").trim().length < 2) return [];
            const hits = [];
            for (let page = 1; page <= pageCount && hits.length < SEARCH_HIT_LIMIT; page++) {
                let value;
                try {
                    value = await pageText(page);
                } catch {
                    continue;
                }
                if (run !== searchRun) return null;
                hits.push(...findMatches(value, query, page, SEARCH_HIT_LIMIT - hits.length));
            }
            return run === searchRun ? hits : null;
        };

        const rangeIn = (element, start, end) => {
            const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
            const range = document.createRange();
            let position = 0;
            let started = false;
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                const length = node.data.length;
                if (!started && start < position + length) {
                    range.setStart(node, start - position);
                    started = true;
                }
                if (started && end <= position + length) {
                    range.setEnd(node, end - position);
                    return range;
                }
                position += length;
            }
            return null;
        };

        function applySearchHighlight() {
            if (!searchHighlight || typeof Highlight !== "function" || !CSS.highlights) return;
            const entry = entries.get(searchHighlight.page);
            if (!entry?.element.isConnected || !entry.text.childNodes.length) return;
            const range = rangeIn(entry.text, searchHighlight.start, searchHighlight.end);
            if (!range) return;
            CSS.highlights.set("book-pdf-search", new Highlight(range));
            const rect = range.getBoundingClientRect();
            if (paged() && root.dataset.pdfOverflow !== "none") {
                const box = stage.getBoundingClientRect();
                stage.scrollBy({ top: rect.top - box.top - box.height / 3, left: rect.left - box.left - box.width / 3 });
            } else if (!paged() && (rect.top < readingLine() - 40 || rect.bottom > window.innerHeight - 80)) {
                window.scrollBy({ top: rect.top - readingLine() });
            }
        }

        const showHit = hit => {
            searchHighlight = { page: hit.page, start: hit.start, end: hit.end };
            CSS.highlights?.delete("book-pdf-search");
            void goToPage(hit.page).then(() => {
                const entry = entryFor(hit.page);
                return entry.ready || render(entry, entry.targetScale || (paged() ? spreadScale(pagesOfView(currentView)) : columnScale(hit.page)));
            }).then(applySearchHighlight);
        };

        // ---- Read-aloud across pages ----------------------------------------------------
        // reader-tts.js reads the text layers on screen; at their end it asks for
        // the next page, which is shown and resolved once its text layer exists.

        root.addEventListener("jularr:reader-tts-next-page", event => {
            const request = event.detail;
            if (!request || !pageCount) return;
            const after = request.after?.closest?.("[data-pdf-page]");
            const last = after ? Number(after.dataset.pdfPage) : visiblePages()[1];
            const next = last + 1;
            if (next > pageCount) return;
            request.ready = (async () => {
                await goToPage(next, { animate: true });
                const entry = entryFor(next);
                await (entry.ready || render(entry, entry.targetScale || 1));
                await frame();
                return entry.element.isConnected && entry.text.textContent.trim() ? entry.text : null;
            })();
        });

        // ---- Start ---------------------------------------------------------------------------

        const start = async () => {
            setStatus(t("books.read.loading", "Loading…"));
            try {
                lib = await import(urls.lib);
                lib.GlobalWorkerOptions.workerSrc = absolute(urls.worker);
                const task = lib.getDocument({
                    url: absolute(urls.file),
                    cMapUrl: absolute(urls.cmaps),
                    cMapPacked: true,
                    standardFontDataUrl: absolute(urls.fonts),
                    wasmUrl: absolute(urls.wasm),
                    BinaryDataFactory: StaticBinaryData,
                    enableXfa: false
                });
                doc = await task.promise;
                pageCount = doc.numPages;
                chapterMap = pageMap(pageCount, pageChapters);
                currentPage = clamp(pageForPosition(options.startChapterId, options.startPermille) || 1, 1, pageCount);
                const first = await doc.getPage(currentPage);
                const viewport = first.getViewport({ scale: 1 });
                defaultSize = { width: viewport.width, height: viewport.height };
                sizes.set(currentPage, defaultSize);
            } catch (error) {
                console.warn(error);
                failed = true;
                setStatus(t("books.read.pdfFailed", "This book could not be opened."));
                options.onReady?.(false);
                return;
            }
            setStatus("");
            root.dataset.pdfReady = "true";
            mode = "";
            layoutNow();
            options.onReady?.(true);
            idle(() => void loadOutline());
        };

        // In Scroll mode the stage grows with the page column, so only its width
        // counts; in Pages mode the frame gives it a fixed size.
        let observedWidth = 0;
        let observedHeight = 0;
        new ResizeObserver(([record]) => {
            const { width, height } = record.contentRect;
            const changed = Math.abs(width - observedWidth) > 1 || (paged() && Math.abs(height - observedHeight) > 1);
            observedWidth = width;
            observedHeight = height;
            if (doc && changed) relayout();
        }).observe(stage);

        void start();

        return Object.freeze({
            apply,
            relayout,
            turn,
            seek,
            goToPage,
            first: () => goToPage(1, { animate: true }),
            last: () => goToPage(pageCount, { animate: true }),
            isPaged: paged,
            isReady: () => Boolean(doc) && !failed,
            refresh: emitLocation,
            pageCount: () => pageCount,
            visiblePages,
            positionForPage,
            pageForPosition,
            currentPosition: () => positionForPage(visiblePages()[0]),
            contents,
            sectionFor,
            stepSection,
            search,
            showHit,
            zoom,
            fit
        });
    };

    window.JularrBookPdf = Object.freeze({ create, pageMap, spreads, findMatches });
})();
