// Manga reader on the shared reader frame (docs/UNIFIED_READER.md "Reader frame").
// reader-shell.js owns the bars, menus, contents panel, slider and fullscreen;
// this script renders the pages, keeps MangaProgress (zero-based page index)
// and bookmarks in sync and applies the manga view settings.
(() => {
    const root = document.querySelector("[data-manga-reader]");
    if (!root) return;

    const readJson = (selector, fallback) => {
        try {
            return JSON.parse(root.querySelector(selector)?.textContent || "") ?? fallback;
        } catch {
            return fallback;
        }
    };

    const text = readJson("[data-manga-reader-text]", {});
    const format = (value, values) => {
        let result = value;
        for (const [name, replacement] of Object.entries(values)) {
            result = result.replaceAll("{" + name + "}", String(replacement));
        }
        return result;
    };
    const t = (key, fallback, values = {}) =>
        format(text["manga.reader." + key] || fallback, values);

    const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

    const chapters = readJson("[data-manga-chapters]", []);
    const chapterOrder = new Map(chapters.map((item, index) => [item.id, index]));
    const chapterNumber = id => chapters.find(item => item.id === id)?.number ?? "";
    const pageCount = Math.max(1, Number(root.dataset.pageCount) || 1);
    const chapterId = root.dataset.chapterId;
    const seriesId = root.dataset.seriesId;
    const pageUrl = index => (root.dataset.pageUrl || "?handler=Page&page=") + index;
    const previousHref = root.dataset.previousHref || "";
    const nextHref = root.dataset.nextHref || "";
    const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
    const scrollBehavior = () => reducedMotion.matches ? "auto" : "smooth";

    const stage = root.querySelector("[data-manga-stage]");
    const spread = root.querySelector("[data-manga-spread]");
    const slots = Array.from(root.querySelectorAll("[data-manga-slot]"));
    const strip = root.querySelector("[data-manga-strip]");
    const continueLink = root.querySelector("[data-manga-continue]");
    const ticks = root.querySelector("[data-manga-ticks]");
    const preferenceForm = root.querySelector("[data-preference-form]");
    const resetPreferenceForm = root.querySelector("[data-reset-preference-form]");
    const progressForm = root.querySelector("[data-progress-form]");
    const bookmarkForm = root.querySelector("[data-bookmark-form]");
    const removeBookmarkForm = root.querySelector("[data-remove-bookmark-form]");
    const bookmarkList = root.querySelector("[data-manga-bookmark-list]");
    const bookmarksEmpty = root.querySelector("[data-manga-bookmarks-empty]");
    const noteList = root.querySelector("[data-manga-note-list]");
    const notesEmpty = root.querySelector("[data-manga-notes-empty]");
    const noteForm = root.querySelector("[data-manga-note-form]");
    const noteInput = root.querySelector("[data-manga-note-input]");
    const searchInput = root.querySelector("[data-manga-search-input]");
    const searchResults = root.querySelector("[data-manga-search-results]");
    const searchStatus = root.querySelector("[data-manga-search-status]");
    const resetSeriesButton = root.querySelector("[data-manga-reset-series]");

    const toast = message => {
        if (root.readerShell) {
            root.readerShell.toast(message);
            return;
        }
        const element = root.querySelector("[data-reader-toast]");
        if (!element) return;
        element.textContent = message;
        element.hidden = false;
        window.clearTimeout(toast.timer);
        toast.timer = window.setTimeout(() => {
            element.hidden = true;
        }, 2600);
    };

    // ---- Settings -------------------------------------------------------------
    // The view mode is stored server-side (single/double/continuous, shared
    // ReaderPreferences). Everything else is a per-device preference.

    const settingsKey = `anilingo.reader.manga.${root.dataset.profile || "default"}`;
    const scrollModes = ["continuous", "horizontal", "webtoon"];
    const schemes = ["auto", "light", "sepia", "dark"];

    const loadSettings = () => {
        let stored = {};
        try {
            stored = JSON.parse(localStorage.getItem(settingsKey) || "{}") || {};
        } catch {
            stored = {};
        }
        // Older builds stored fit ("width"/"height") and zoom as a factor.
        const zoomPercent = Number(stored.zoomPercent) ||
            (Number(stored.zoom) > 0 ? Number(stored.zoom) * 100 : 100);
        return {
            fitWidth: typeof stored.fitWidth === "boolean" ? stored.fitWidth : stored.fit === "width",
            zoomPercent: clamp(Math.round(zoomPercent / 10) * 10, 50, 300),
            scrollMode: scrollModes.includes(stored.scrollMode) ? stored.scrollMode : "continuous",
            firstPageAlone: stored.firstPageAlone === true,
            autoNext: stored.autoNext !== false,
            scheme: schemes.includes(stored.scheme) ? stored.scheme : "auto",
            sharpen: stored.sharpen === true,
            crop: stored.crop === true,
            gap: clamp(Number.isFinite(Number(stored.gap)) ? Number(stored.gap) : 8, 0, 48),
            directions: stored.directions && typeof stored.directions === "object" ? stored.directions : {}
        };
    };

    const settings = loadSettings();
    const saveSettings = () => {
        try {
            localStorage.setItem(settingsKey, JSON.stringify(settings));
        } catch {
        }
    };

    const serverMode = root.dataset.defaultMode;
    let mode = serverMode === "continuous"
        ? settings.scrollMode
        : serverMode === "double" ? "double" : "single";
    let direction = settings.directions[seriesId] === "ltr" || settings.directions[seriesId] === "rtl"
        ? settings.directions[seriesId]
        : root.dataset.seriesDirection === "ltr" ? "ltr" : "rtl";

    const isPaged = () => mode === "single" || mode === "double";
    const coarseMode = value => value === "single" || value === "double" ? value : "continuous";

    // ---- Pages ----------------------------------------------------------------

    const spreadStart = index => {
        if (mode !== "double") return index;
        if (settings.firstPageAlone) return index === 0 ? 0 : index - ((index - 1) % 2);
        return index - (index % 2);
    };

    const spreadPages = start => {
        if (mode !== "double") return [start];
        if (settings.firstPageAlone && start === 0) return [0];
        return start + 1 < pageCount ? [start, start + 1] : [start];
    };

    const visiblePages = () => isPaged() ? spreadPages(page) : [page];

    let page = clamp(Number(root.dataset.page) || 0, 0, pageCount - 1);
    page = spreadStart(page);

    // Border cropping (single and double page): trims plain white or black
    // margins on a small sample, then cuts the full image once. Pages come from
    // the same origin, so the canvas stays readable.
    const cropCache = new Map();
    const cropOrder = [];

    const cropPage = async index => {
        const url = pageUrl(index);
        const image = new Image();
        image.decoding = "async";
        image.src = url;
        await image.decode();
        const width = image.naturalWidth;
        const height = image.naturalHeight;
        if (!width || !height) return url;

        const scale = Math.min(1, 200 / Math.max(width, height));
        const w = Math.max(1, Math.round(width * scale));
        const h = Math.max(1, Math.round(height * scale));
        const sample = document.createElement("canvas");
        sample.width = w;
        sample.height = h;
        const context = sample.getContext("2d", { willReadFrequently: true });
        context.drawImage(image, 0, 0, w, h);
        const data = context.getImageData(0, 0, w, h).data;
        const at = (x, y) => (y * w + x) * 4;
        const corners = [at(0, 0), at(w - 1, 0), at(0, h - 1), at(w - 1, h - 1)]
            .map(i => (data[i] + data[i + 1] + data[i + 2]) / 3);
        const background = corners.reduce((sum, value) => sum + value, 0) / corners.length;
        if (!(background > 215 || background < 40) ||
            corners.some(value => Math.abs(value - background) > 30)) {
            return url;
        }

        const differs = i =>
            Math.abs(data[i] - background) + Math.abs(data[i + 1] - background) +
            Math.abs(data[i + 2] - background) > 72;
        const rowHasInk = y => {
            let count = 0;
            for (let x = 0; x < w; x++) if (differs(at(x, y))) count++;
            return count > w * .01;
        };
        const columnHasInk = x => {
            let count = 0;
            for (let y = 0; y < h; y++) if (differs(at(x, y))) count++;
            return count > h * .01;
        };

        let top = 0;
        while (top < h - 1 && !rowHasInk(top)) top++;
        let bottom = h - 1;
        while (bottom > top && !rowHasInk(bottom)) bottom--;
        let left = 0;
        while (left < w - 1 && !columnHasInk(left)) left++;
        let right = w - 1;
        while (right > left && !columnHasInk(right)) right--;

        top = Math.max(0, top - 1);
        left = Math.max(0, left - 1);
        bottom = Math.min(h - 1, bottom + 1);
        right = Math.min(w - 1, right + 1);

        const sx = Math.floor(left / scale);
        const sy = Math.floor(top / scale);
        const sw = Math.min(width - sx, Math.ceil((right - left + 1) / scale));
        const sh = Math.min(height - sy, Math.ceil((bottom - top + 1) / scale));
        if (sw * sh > width * height * .96 || sw < width * .3 || sh < height * .3) {
            return url;
        }

        const output = document.createElement("canvas");
        output.width = sw;
        output.height = sh;
        output.getContext("2d").drawImage(image, sx, sy, sw, sh, 0, 0, sw, sh);
        const blob = await new Promise(resolve => output.toBlob(resolve, "image/webp", .92));
        return blob ? URL.createObjectURL(blob) : url;
    };

    const croppedSource = index => {
        if (cropCache.has(index)) return cropCache.get(index);
        const promise = cropPage(index).catch(() => pageUrl(index));
        cropCache.set(index, promise);
        cropOrder.push(index);
        while (cropOrder.length > 12) {
            const oldest = cropOrder.shift();
            const stale = cropCache.get(oldest);
            cropCache.delete(oldest);
            void stale?.then(url => {
                const shown = slots.some(slot => slot.getAttribute("src") === url);
                if (url.startsWith("blob:") && !shown) URL.revokeObjectURL(url);
            });
        }
        return promise;
    };

    const clearCrops = () => {
        for (const promise of cropCache.values()) {
            void promise.then(url => {
                if (url.startsWith("blob:")) URL.revokeObjectURL(url);
            });
        }
        cropCache.clear();
        cropOrder.length = 0;
    };

    const assignSource = (image, index) => {
        const token = (image.mangaToken || 0) + 1;
        image.mangaToken = token;
        image.dataset.pageIndex = String(index);
        if (!settings.crop) {
            const url = pageUrl(index);
            if (image.getAttribute("src") !== url) image.src = url;
            image.style.visibility = "";
            return;
        }
        image.style.visibility = "hidden";
        void croppedSource(index).then(url => {
            if (image.mangaToken !== token) return;
            if (image.getAttribute("src") !== url) image.src = url;
            image.style.visibility = "";
        });
    };

    const renderSpread = () => {
        const pages = spreadPages(page);
        const visual = direction === "rtl" ? [...pages].reverse() : pages;
        root.style.setProperty("--manga-pages", String(visual.length));
        slots.forEach((image, slot) => {
            const index = visual[slot];
            if (index === undefined) {
                image.hidden = true;
                image.mangaToken = (image.mangaToken || 0) + 1;
                image.removeAttribute("src");
                delete image.dataset.pageIndex;
                return;
            }
            image.hidden = false;
            image.alt = t("pageAlt", "Page {page}", { page: index + 1 });
            assignSource(image, index);
        });
        if (stage) {
            stage.scrollTo({ top: 0, left: direction === "rtl" ? stage.scrollWidth : 0 });
        }
    };

    let figures = [];
    const buildStrip = () => {
        if (!strip || figures.length) return;
        const fragment = document.createDocumentFragment();
        for (let index = 0; index < pageCount; index++) {
            const figure = document.createElement("figure");
            figure.dataset.page = String(index);
            const image = document.createElement("img");
            image.loading = Math.abs(index - page) <= 2 ? "eager" : "lazy";
            image.decoding = "async";
            image.draggable = false;
            image.alt = t("pageAlt", "Page {page}", { page: index + 1 });
            image.addEventListener("load", () => figure.classList.add("is-loaded"), { once: true });
            image.src = pageUrl(index);
            figure.append(image);
            fragment.append(figure);
        }
        strip.append(fragment);
        figures = Array.from(strip.querySelectorAll("figure"));
    };

    // Programmatic scrolls must not count as "reached the end by reading".
    let programmaticUntil = 0;
    const scrollToPage = (index, behavior = scrollBehavior()) => {
        const figure = figures[index];
        if (!figure) return;
        programmaticUntil = performance.now() + (behavior === "smooth" ? 900 : 120);
        figure.scrollIntoView({ block: "start", inline: "center", behavior });
    };

    const prefetch = () => {
        if (!isPaged()) return;
        const next = page + spreadPages(page).length;
        const after = next < pageCount ? next + spreadPages(next).length : pageCount;
        const indexes = [
            ...(next < pageCount ? spreadPages(next) : []),
            ...(after < pageCount ? spreadPages(after) : []),
            ...(page > 0 ? spreadPages(spreadStart(page - 1)) : [])
        ];
        for (const index of indexes) {
            if (settings.crop) {
                void croppedSource(index);
            } else {
                const image = new Image();
                image.decoding = "async";
                image.src = pageUrl(index);
            }
        }
    };

    // ---- Bookmarks and notes ----------------------------------------------------

    const bookmarks = readJson("[data-manga-bookmarks]", [])
        .map(item => ({
            id: String(item.id),
            chapterId: String(item.chapterId),
            pageIndex: Number(item.pageIndex) || 0,
            label: item.label || ""
        }));

    const locationText = item => t(
        "bookmarkLocation",
        "Chapter {chapter} · page {page}",
        { chapter: chapterNumber(item.chapterId), page: item.pageIndex + 1 });

    const sortBookmarks = items => items.slice().sort((a, b) =>
        (chapterOrder.get(a.chapterId) ?? 0) - (chapterOrder.get(b.chapterId) ?? 0) ||
        a.pageIndex - b.pageIndex);

    const currentPlainBookmark = () => {
        const shown = visiblePages();
        return bookmarks.find(item =>
            !item.label && item.chapterId === chapterId && shown.includes(item.pageIndex));
    };

    const fillList = (list, items, isNote) => {
        if (!list) return;
        const removeLabel = text["common.remove"] || "Remove";
        list.replaceChildren(...sortBookmarks(items).map(item => {
            const row = document.createElement("li");
            row.className = "reader-contents-note";
            const link = document.createElement("a");
            link.className = "reader-contents-row";
            const here = item.chapterId === chapterId;
            link.href = here
                ? `?page=${item.pageIndex}`
                : `/Manga/Read/${encodeURIComponent(item.chapterId)}?page=${item.pageIndex}`;
            if (here) link.dataset.mangaJump = String(item.pageIndex);
            if (isNote) {
                const quote = document.createElement("q");
                quote.textContent = item.label;
                const where = document.createElement("small");
                where.textContent = locationText(item);
                link.append(quote, where);
            } else {
                const title = document.createElement("span");
                title.className = "reader-contents-title";
                title.textContent = locationText(item);
                link.append(title);
            }
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "reader-contents-delete";
            remove.dataset.mangaRemove = item.id;
            remove.textContent = removeLabel;
            remove.setAttribute("aria-label", `${removeLabel}: ${isNote ? item.label : locationText(item)}`);
            row.append(link, remove);
            return row;
        }));
    };

    const renderTicks = () => {
        if (!ticks) return;
        ticks.replaceChildren(...bookmarks
            .filter(item => item.chapterId === chapterId)
            .map(item => {
                const button = document.createElement("button");
                button.type = "button";
                button.dataset.mangaJump = String(item.pageIndex);
                button.style.setProperty(
                    "--tick",
                    String(pageCount <= 1 ? 0 : item.pageIndex / (pageCount - 1)));
                const label = item.label || t("pageAlt", "Page {page}", { page: item.pageIndex + 1 });
                button.title = label;
                button.setAttribute("aria-label", label);
                return button;
            }));
    };

    const renderBookmarks = () => {
        const plain = bookmarks.filter(item => !item.label);
        const notes = bookmarks.filter(item => item.label);
        fillList(bookmarkList, plain, false);
        fillList(noteList, notes, true);
        if (bookmarksEmpty) bookmarksEmpty.hidden = plain.length > 0;
        if (notesEmpty) notesEmpty.hidden = notes.length > 0;
        renderTicks();
        syncBookmarkButtons();
    };

    const syncBookmarkButtons = () => {
        const active = Boolean(currentPlainBookmark());
        root.querySelectorAll("[data-manga-bookmark]").forEach(button => {
            button.setAttribute("aria-pressed", active ? "true" : "false");
        });
    };

    const postForm = async (form, mutate, keepalive = false) => {
        const data = new FormData(form);
        mutate?.(data);
        const response = await fetch(form.action, {
            method: "POST",
            body: data,
            credentials: "same-origin",
            headers: { "X-Requested-With": "fetch" },
            keepalive
        });
        if (!response.ok) throw new Error(response.statusText || "Request failed");
        return response.headers.get("content-type")?.includes("application/json")
            ? response.json()
            : null;
    };

    const addBookmark = async (label = "") => {
        const saved = await postForm(bookmarkForm, data => {
            data.set("pageIndex", String(page));
            data.set("label", label);
        });
        bookmarks.push({
            id: String(saved.id),
            chapterId: String(saved.chapterId),
            pageIndex: Number(saved.pageIndex) || 0,
            label: saved.label || ""
        });
        renderBookmarks();
    };

    const removeBookmark = async id => {
        await postForm(removeBookmarkForm, data => data.set("bookmarkId", id));
        const index = bookmarks.findIndex(item => item.id === id);
        if (index >= 0) bookmarks.splice(index, 1);
        renderBookmarks();
    };

    const toggleBookmark = async () => {
        try {
            const existing = currentPlainBookmark();
            if (existing) {
                await removeBookmark(existing.id);
                toast(t("toast.bookmarkRemoved", "Bookmark removed."));
            } else {
                await addBookmark();
                toast(t("toast.bookmarkSaved", "Bookmark saved."));
            }
        } catch {
            toast(t("toast.saveFailed", "Could not save. Please try again."));
        }
    };

    // ---- Progress ---------------------------------------------------------------

    let progressTimer = 0;
    let progressDirty = false;
    const saveProgress = (keepalive = false) => {
        window.clearTimeout(progressTimer);
        if (!progressForm || !progressDirty) return;
        progressDirty = false;
        void postForm(progressForm, data => data.set("pageIndex", String(page)), keepalive)
            .catch(() => {
                progressDirty = true;
            });
    };
    const queueProgress = () => {
        progressDirty = true;
        window.clearTimeout(progressTimer);
        progressTimer = window.setTimeout(() => saveProgress(), 350);
    };

    // ---- Chrome -----------------------------------------------------------------

    const syncControls = () => {
        root.querySelectorAll("[data-manga-view]").forEach(button => {
            const attribute = button.getAttribute("role") === "menuitemradio" ? "aria-checked" : "aria-pressed";
            button.setAttribute(attribute, button.dataset.mangaView === mode ? "true" : "false");
        });
        root.querySelectorAll("[data-manga-scheme]").forEach(button => {
            const attribute = button.getAttribute("role") === "menuitemradio" ? "aria-checked" : "aria-pressed";
            button.setAttribute(attribute, button.dataset.mangaScheme === settings.scheme ? "true" : "false");
        });
        root.querySelectorAll("[data-manga-modes]").forEach(element => {
            element.hidden = !element.dataset.mangaModes.split(" ").includes(mode);
        });

        const values = {
            zoom: settings.zoomPercent,
            gap: settings.gap,
            firstPageAlone: settings.firstPageAlone,
            rightToLeft: direction === "rtl",
            autoNext: settings.autoNext,
            sharpen: settings.sharpen,
            crop: settings.crop,
            fitWidth: settings.fitWidth
        };
        root.querySelectorAll("[data-manga-setting]").forEach(control => {
            const value = values[control.dataset.mangaSetting];
            if (control.type === "checkbox") control.checked = Boolean(value);
            else control.value = String(value);
        });
        root.querySelectorAll("[data-manga-setting-output]").forEach(output => {
            output.textContent = output.dataset.mangaSettingOutput === "zoom"
                ? `${settings.zoomPercent}%`
                : `${settings.gap} px`;
        });
    };

    const updateLocation = () => {
        const shown = visiblePages();
        const label = shown.length > 1
            ? t("pageRange", "{first}–{last} / {total}", {
                first: shown[0] + 1,
                last: shown[shown.length - 1] + 1,
                total: pageCount
            })
            : t("pagePosition", "{page} / {total}", { page: shown[0] + 1, total: pageCount });
        root.dispatchEvent(new CustomEvent("jularr:reader-location", {
            detail: {
                max: pageCount - 1,
                value: page,
                text: label,
                valueText: t("pageOf", "Page {page} of {total}", { page: shown[0] + 1, total: pageCount })
            }
        }));

        if (noteInput) {
            const placeholder = t("notePlaceholder", "Note for page {page}", { page: page + 1 });
            noteInput.placeholder = placeholder;
            noteInput.setAttribute("aria-label", placeholder);
        }

        if (continueLink && isPaged()) {
            continueLink.hidden = page + spreadPages(page).length < pageCount;
        }
        syncBookmarkButtons();
    };

    const applyView = () => {
        root.dataset.mangaMode = mode;
        root.dataset.direction = direction;
        root.dataset.fit = settings.fitWidth ? "width" : "height";
        root.dataset.scheme = settings.scheme;
        root.dataset.sharpen = settings.sharpen ? "true" : "false";
        root.dataset.readerPanGesture = settings.zoomPercent > 100 ? "true" : "false";
        root.style.setProperty("--manga-zoom", String(settings.zoomPercent / 100));
        root.toggleAttribute("data-zoomed", settings.zoomPercent !== 100);
        root.style.setProperty("--manga-gap", `${settings.gap}px`);
        root.dispatchEvent(new CustomEvent("jularr:reader-mode", {
            detail: {
                readingMode: isPaged() ? "paged" : "continuous",
                pageDirection: direction,
                immersive: true
            }
        }));
    };

    const render = () => {
        applyView();
        if (isPaged()) {
            if (strip) strip.hidden = true;
            if (spread) spread.hidden = false;
            if (continueLink && continueLink.parentElement !== stage) stage.append(continueLink);
            renderSpread();
        } else {
            if (spread) spread.hidden = true;
            if (strip) strip.hidden = false;
            buildStrip();
            if (continueLink) {
                if (continueLink.parentElement !== strip) strip.append(continueLink);
                continueLink.hidden = false;
            }
            scrollToPage(page, "auto");
        }
        syncControls();
        updateLocation();
        prefetch();
    };

    const goTo = target => {
        let next = clamp(Math.round(Number(target) || 0), 0, pageCount - 1);
        if (isPaged()) next = spreadStart(next);
        if (next === page) return;
        page = next;
        if (isPaged()) renderSpread();
        else scrollToPage(page);
        updateLocation();
        queueProgress();
        prefetch();
    };

    const chapterEnd = () => {
        if (!nextHref) {
            toast(t("toast.lastChapter", "This is the last chapter."));
        } else if (settings.autoNext) {
            saveProgress(true);
            window.location.assign(nextHref);
        } else {
            toast(t("toast.chapterEnd", "End of the chapter."));
        }
    };

    const forward = () => {
        const next = isPaged() ? page + spreadPages(page).length : page + 1;
        if (next < pageCount) goTo(next);
        else chapterEnd();
    };

    const back = () => {
        if (page > 0) {
            goTo(isPaged() ? spreadStart(page - 1) : page - 1);
        } else if (previousHref) {
            saveProgress(true);
            window.location.assign(previousHref);
        }
    };

    const setChrome = visible => {
        root.classList.remove("manga-chrome-hidden");
        root.dispatchEvent(new CustomEvent("jularr:reader-chrome", {
            detail: { visible }
        }));
    };

    // ---- Mode and settings changes ------------------------------------------

    const savePreference = scope => postForm(preferenceForm, data => {
        data.set("scope", scope);
        data.set("mode", coarseMode(mode));
    });

    const setMode = (next, { persist = true } = {}) => {
        if (!["single", "double", ...scrollModes].includes(next) || next === mode) return;
        mode = next;
        if (scrollModes.includes(mode)) {
            settings.scrollMode = mode;
            saveSettings();
        }
        if (isPaged()) page = spreadStart(page);
        render();
        queueProgress();
        if (!persist) return;
        void savePreference("series")
            .then(() => {
                if (resetSeriesButton) resetSeriesButton.hidden = false;
            })
            .catch(() => toast(t("toast.saveFailed", "Could not save. Please try again.")));
    };

    const changeSetting = (key, value) => {
        switch (key) {
            case "zoom":
                settings.zoomPercent = clamp(Math.round(Number(value) / 10) * 10, 50, 300);
                break;
            case "gap":
                settings.gap = clamp(Number(value) || 0, 0, 48);
                break;
            case "rightToLeft":
                direction = value ? "rtl" : "ltr";
                if (direction === root.dataset.seriesDirection) delete settings.directions[seriesId];
                else settings.directions[seriesId] = direction;
                break;
            case "firstPageAlone":
                settings.firstPageAlone = Boolean(value);
                page = spreadStart(page);
                break;
            case "crop":
                settings.crop = Boolean(value);
                if (!settings.crop) clearCrops();
                break;
            case "autoNext":
            case "sharpen":
            case "fitWidth":
                settings[key] = Boolean(value);
                break;
            default:
                return;
        }
        saveSettings();
        applyView();
        syncControls();
        if (isPaged() && ["rightToLeft", "firstPageAlone", "crop"].includes(key)) {
            renderSpread();
            updateLocation();
            prefetch();
        } else if (!isPaged() && ["zoom", "gap", "fitWidth"].includes(key)) {
            scrollToPage(page, "auto");
        }
    };

    root.addEventListener("input", event => {
        const control = event.target instanceof Element ? event.target.closest("[data-manga-setting]") : null;
        if (control?.type === "range") changeSetting(control.dataset.mangaSetting, control.value);
    });

    root.addEventListener("change", event => {
        const control = event.target instanceof Element ? event.target.closest("[data-manga-setting]") : null;
        if (control?.type === "checkbox") changeSetting(control.dataset.mangaSetting, control.checked);
    });

    root.addEventListener("click", event => {
        const target = event.target instanceof Element ? event.target : null;
        if (!target) return;

        const modeButton = target.closest("[data-manga-view]");
        if (modeButton) {
            setMode(modeButton.dataset.mangaView);
            return;
        }

        const schemeButton = target.closest("[data-manga-scheme]");
        if (schemeButton) {
            settings.scheme = schemes.includes(schemeButton.dataset.mangaScheme)
                ? schemeButton.dataset.mangaScheme
                : "auto";
            saveSettings();
            applyView();
            syncControls();
            return;
        }

        const zoomStep = target.closest("[data-manga-zoom-step]");
        if (zoomStep) {
            changeSetting("zoom", settings.zoomPercent + Number(zoomStep.dataset.mangaZoomStep));
            return;
        }

        if (target.closest("[data-manga-bookmark]")) {
            void toggleBookmark();
            return;
        }

        const jump = target.closest("[data-manga-jump]");
        if (jump) {
            event.preventDefault();
            goTo(Number(jump.dataset.mangaJump));
            if (root.readerShell && jump.closest(".reader-contents.is-overlay")) {
                root.querySelector("[data-reader-contents-close]")?.click();
            }
            return;
        }

        const remove = target.closest("[data-manga-remove]");
        if (remove) {
            void removeBookmark(remove.dataset.mangaRemove)
                .then(() => toast(t("toast.bookmarkRemoved", "Bookmark removed.")))
                .catch(() => toast(t("toast.saveFailed", "Could not save. Please try again.")));
            return;
        }

        if (target.closest("[data-manga-save-default]")) {
            void savePreference("media")
                .then(() => toast(t("toast.defaultSaved", "Mode saved for all manga.")))
                .catch(() => toast(t("toast.saveFailed", "Could not save. Please try again.")));
            return;
        }

        if (target.closest("[data-manga-reset-series]")) {
            void postForm(resetPreferenceForm)
                .then(result => {
                    if (resetSeriesButton) resetSeriesButton.hidden = true;
                    const fallback = result?.mode === "continuous"
                        ? settings.scrollMode
                        : result?.mode === "double" ? "double" : "single";
                    setMode(fallback, { persist: false });
                    toast(t("toast.usingDefault", "Using the manga default."));
                })
                .catch(() => toast(t("toast.saveFailed", "Could not save. Please try again.")));
        }
    });

    noteForm?.addEventListener("submit", event => {
        event.preventDefault();
        const label = (noteInput?.value || "").trim();
        if (!label) return;
        void addBookmark(label)
            .then(() => {
                noteInput.value = "";
                toast(t("toast.noteSaved", "Note saved."));
            })
            .catch(() => toast(t("toast.saveFailed", "Could not save. Please try again.")));
    });

    // ---- Chapter search -------------------------------------------------------

    const chapterLabel = item => t("chapterNumber", "Chapter {chapter}", { chapter: item.number });

    const renderSearch = () => {
        if (!searchInput || !searchResults) return [];
        const query = searchInput.value.trim().toLocaleLowerCase();
        if (!query) {
            searchResults.replaceChildren();
            if (searchStatus) searchStatus.hidden = true;
            return [];
        }
        const matches = chapters.filter(item =>
            String(item.number).startsWith(query) ||
            chapterLabel(item).toLocaleLowerCase().includes(query) ||
            (item.title || "").toLocaleLowerCase().includes(query)).slice(0, 60);
        searchResults.replaceChildren(...matches.map(item => {
            const row = document.createElement("li");
            const link = document.createElement("a");
            link.href = `/Manga/Read/${encodeURIComponent(item.id)}`;
            if (item.id === chapterId) link.setAttribute("aria-current", "page");
            const name = document.createElement("strong");
            name.textContent = chapterLabel(item);
            link.append(name);
            if (item.title) link.append(document.createTextNode(item.title));
            row.append(link);
            return row;
        }));
        if (searchStatus) searchStatus.hidden = matches.length > 0;
        return matches;
    };

    searchInput?.addEventListener("input", renderSearch);
    root.querySelector("[data-manga-search-form]")?.addEventListener("submit", event => {
        event.preventDefault();
        const first = renderSearch()[0];
        if (first) window.location.assign(`/Manga/Read/${encodeURIComponent(first.id)}`);
    });

    // ---- Navigation input -------------------------------------------------------

    root.addEventListener("jularr:reader-page-edge", event => {
        if ((Number(event.detail?.direction) || 0) > 0) forward();
        else back();
    });

    root.addEventListener("jularr:reader-seek", event => {
        goTo(Number(event.detail?.value));
    });

    root.addEventListener("jularr:reader-menu", event => {
        if (event.detail?.open) setChrome(true);
    });

    root.addEventListener("jularr:reader-contents", () => setChrome(true));

    root.addEventListener("jularr:reader-bookmark", () => {
        void toggleBookmark();
    });

    // Horizontal view: a mouse wheel scrolls along the reading direction.
    stage?.addEventListener("wheel", event => {
        if (mode !== "horizontal" || Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return;
        event.preventDefault();
        stage.scrollBy({ left: (direction === "rtl" ? -1 : 1) * event.deltaY });
    }, { passive: false });

    // ---- Scroll views: follow the page in view and continue at the end -------

    let readingInput = false;
    for (const type of ["wheel", "touchstart", "pointerdown", "keydown"]) {
        (type === "keydown" ? document : stage)?.addEventListener(type, () => {
            readingInput = true;
        }, { passive: true });
    }

    let autoNextTimer = 0;
    const continueVisible = () => {
        if (!continueLink || continueLink.hidden || !stage) return false;
        const link = continueLink.getBoundingClientRect();
        const view = stage.getBoundingClientRect();
        return mode === "horizontal"
            ? link.left >= view.left - 1 && link.right <= view.right + 1
            : link.top >= view.top - 1 && link.bottom <= view.bottom + 1;
    };

    const trackScroll = () => {
        if (!stage || !figures.length) return;
        const view = stage.getBoundingClientRect();
        const horizontal = mode === "horizontal";
        const probe = horizontal ? view.left + view.width / 2 : view.top + view.height * .35;
        let current = page;
        for (const figure of figures) {
            const box = figure.getBoundingClientRect();
            const start = horizontal ? box.left : box.top;
            const end = horizontal ? box.right : box.bottom;
            if (probe >= start && probe < end) {
                current = Number(figure.dataset.page);
                break;
            }
        }
        const atEnd = horizontal
            // Right-to-left rows run from the right edge towards scrollLeft 0.
            ? (direction === "rtl" ? stage.scrollLeft <= 2 : stage.scrollLeft + stage.clientWidth >= stage.scrollWidth - 2)
            : stage.scrollTop + stage.clientHeight >= stage.scrollHeight - 2;
        if (atEnd) current = pageCount - 1;
        if (current !== page) {
            page = current;
            updateLocation();
            queueProgress();
        }

        window.clearTimeout(autoNextTimer);
        if (settings.autoNext && nextHref && readingInput &&
            performance.now() > programmaticUntil && continueVisible()) {
            autoNextTimer = window.setTimeout(() => {
                if (continueVisible()) {
                    saveProgress(true);
                    window.location.assign(nextHref);
                }
            }, 900);
        }
    };

    let scrollFrame = 0;
    stage?.addEventListener("scroll", () => {
        if (isPaged() || scrollFrame) return;
        scrollFrame = requestAnimationFrame(() => {
            scrollFrame = 0;
            trackScroll();
        });
    }, { passive: true });

    // Page sizes follow the stage size; keep the current page in view when the
    // contents panel, fullscreen or the window change it.
    if (stage && "ResizeObserver" in window) {
        let size = "";
        let resizeTimer = 0;
        new ResizeObserver(entries => {
            const box = entries[0]?.contentRect;
            if (!box) return;
            const next = `${Math.round(box.width)}x${Math.round(box.height)}`;
            if (next === size) return;
            const first = size === "";
            size = next;
            if (first || isPaged()) return;
            window.clearTimeout(resizeTimer);
            resizeTimer = window.setTimeout(() => scrollToPage(page, "auto"), 120);
        }).observe(stage);
    }

    window.addEventListener("pagehide", () => saveProgress(true));
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "hidden") saveProgress(true);
    });

    renderBookmarks();
    render();
    queueProgress();
})();
